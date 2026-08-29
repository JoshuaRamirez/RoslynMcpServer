using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.FileSystem;
using RoslynMcp.Core.Refactoring.Base;
using RoslynMcp.Core.Refactoring.Utilities;
using RoslynMcp.Core.Workspace;

namespace RoslynMcp.Core.Refactoring.Generate;

/// <summary>
/// Adds null-check statements to method or constructor parameters.
/// </summary>
public sealed class AddNullChecksOperation : RefactoringOperationBase<AddNullChecksParams>
{
    /// <inheritdoc />
    public AddNullChecksOperation(WorkspaceContext context) : base(context)
    {
    }

    /// <inheritdoc />
    protected override void ValidateParams(AddNullChecksParams @params) => Validate(@params);

    /// <summary>
    /// Validates add-null-checks parameters. Internal so tests can
    /// exercise input rules without loading a workspace.
    /// </summary>
    internal static void Validate(AddNullChecksParams @params)
    {
        if (string.IsNullOrWhiteSpace(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "sourceFile is required.");

        if (string.IsNullOrWhiteSpace(@params.MethodName))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "methodName is required.");

        if (!PathResolver.IsAbsolutePath(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be an absolute path.");

        if (!PathResolver.IsValidCSharpFilePath(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be a .cs file.");

        if (@params.Line.HasValue && @params.Line.Value < 1)
            throw new RefactoringException(ErrorCodes.InvalidLineNumber, "Line number must be >= 1.");

        if (@params.Column.HasValue && @params.Column.Value < 1)
            throw new RefactoringException(ErrorCodes.InvalidColumnNumber, "column must be >= 1.");

        if (!File.Exists(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.SourceFileNotFound, $"Source file not found: {@params.SourceFile}");
    }

    /// <inheritdoc />
    protected override async Task<RefactoringResult> ExecuteCoreAsync(
        Guid operationId,
        AddNullChecksParams @params,
        CancellationToken cancellationToken)
    {
        var document = GetDocumentOrThrow(@params.SourceFile);
        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);

        if (root == null || semanticModel == null)
            throw new RefactoringException(ErrorCodes.RoslynError, "Could not parse file.");

        var useThrowIfNull = string.IsNullOrWhiteSpace(@params.Style) ||
                             string.Equals(@params.Style, "throw", StringComparison.OrdinalIgnoreCase);

        // Find the method or constructor
        var methodNode = FindMethod(root, @params.MethodName, @params.Line, @params.Column);
        if (methodNode == null)
            throw new RefactoringException(ErrorCodes.MethodNotFound, $"Method '{@params.MethodName}' not found.");

        // Get parameters from the method symbol
        var methodSymbol = semanticModel.GetDeclaredSymbol(methodNode, cancellationToken) as IMethodSymbol;
        if (methodSymbol == null)
            throw new RefactoringException(ErrorCodes.RoslynError, "Could not resolve method symbol.");

        // Find nullable reference type parameters that need checks
        var paramsToCheck = methodSymbol.Parameters
            .Where(NullCheckGenerator.ShouldCheckForNull)
            .ToList();

        if (paramsToCheck.Count == 0)
            throw new RefactoringException(ErrorCodes.NoMembersToGenerate, "No parameters require null checks.");

        // Generate null-check statements
        var nullChecks = new List<StatementSyntax>();
        foreach (var param in paramsToCheck)
        {
            var check = useThrowIfNull
                ? NullCheckGenerator.GenerateThrowIfNull(param.Name)
                : NullCheckGenerator.GenerateGuardClause(param.Name);
            nullChecks.Add(check);
        }

        if (@params.Preview)
        {
            var code = string.Join("\n", nullChecks.Select(s => s.NormalizeWhitespace().ToFullString()));
            var pendingChanges = new List<PendingChange>
            {
                new()
                {
                    File = @params.SourceFile,
                    ChangeType = Contracts.Enums.ChangeKind.Modify,
                    Description = $"Add null checks to {@params.MethodName}",
                    BeforeSnippet = $"// Method '{@params.MethodName}' (no null checks)",
                    AfterSnippet = code
                }
            };
            return RefactoringResult.PreviewResult(operationId, pendingChanges);
        }

        // Insert null checks at the beginning of the method body
        var body = GetBody(methodNode);
        if (body == null)
            throw new RefactoringException(ErrorCodes.RoslynError, "Method has no body to insert null checks into.");

        var newStatements = nullChecks.Concat(body.Statements);
        var newBody = body.WithStatements(SyntaxFactory.List(newStatements));
        var newMethodNode = ReplaceBody(methodNode, newBody);

        var newRoot = root.ReplaceNode(methodNode, newMethodNode);
        var newDocument = document.WithSyntaxRoot(newRoot);
        var commitResult = await CommitChangesAsync(newDocument.Project.Solution, cancellationToken);

        return RefactoringResult.Succeeded(operationId,
            new FileChanges { FilesModified = commitResult.FilesModified, FilesCreated = commitResult.FilesCreated, FilesDeleted = commitResult.FilesDeleted },
            new Contracts.Models.SymbolInfo { Name = @params.MethodName, FullyQualifiedName = @params.MethodName, Kind = Contracts.Enums.SymbolKind.Method },
            0, 0);
    }

    /// <summary>
    /// Finds a method or constructor. Omitted <paramref name="column"/> keeps
    /// today's MethodName + optional Line start-line pick, including today's
    /// silent <c>First()</c> fallback when line misses. When set with
    /// <paramref name="line"/>, picks the smallest method or constructor
    /// whose identifier or declaration span covers that 1-based column
    /// (same exclusive-end coverage as
    /// <c>ChangeSignatureOperation.SpanCoversColumn</c>). Prefer the
    /// identifier hit, then the smallest containing declaration. Do not
    /// require the declaration to start on <paramref name="line"/> when
    /// column is set — a split signature may put the identifier on a
    /// continuation line. Column without line cannot disambiguate
    /// same-indent same-name members across lines — keep today's first-match
    /// rather than substituting each candidate's own start line.
    /// </summary>
    internal static SyntaxNode? FindMethod(SyntaxNode root, string methodName, int? line, int? column)
    {
        var candidates = root.DescendantNodes()
            .Where(n => n is MethodDeclarationSyntax m && m.Identifier.Text == methodName ||
                        n is ConstructorDeclarationSyntax c && c.Identifier.Text == methodName)
            .ToList();

        if (candidates.Count == 0)
            return null;

        // Column without line is not a source position: substituting each
        // candidate's own start line would match every equally-aligned
        // same-name method and could silently pick the shortest. Keep
        // today's First() after the methodName filter.
        if (column.HasValue && !line.HasValue)
            return candidates.First();

        if (column.HasValue)
        {
            // Do not require the declaration to start on `line` — a split
            // signature's identifier may live on a continuation line whose
            // declaration span still covers that column. Prefer the
            // identifier hit, then the smallest containing declaration.
            // Do not silently pick the first when a covering node exists
            // elsewhere — scan every candidate, including those that do
            // not start on `line`. If nothing covers this position, keep
            // today's not-found (null) rather than inventing a first-match.
            return candidates
                .Where(n => MemberCoversColumn(n, line!.Value, column.Value))
                .OrderBy(n => IdentifierCoversColumn(n, line!.Value, column.Value) ? 0 : 1)
                .ThenBy(n => n.Span.Length)
                .FirstOrDefault();
        }

        if (line.HasValue)
        {
            return candidates.FirstOrDefault(c => StartLine(c) == line.Value)
                ?? candidates.First();
        }

        return candidates.First();
    }

    private static int StartLine(SyntaxNode node) =>
        node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

    private static bool MemberCoversColumn(SyntaxNode node, int line, int column) =>
        IdentifierCoversColumn(node, line, column) ||
        SpanCoversColumn(node.GetLocation().GetLineSpan(), line, column);

    private static bool IdentifierCoversColumn(SyntaxNode node, int line, int column)
    {
        var identifier = GetIdentifier(node);
        return identifier != default &&
               SpanCoversColumn(identifier.GetLocation().GetLineSpan(), line, column);
    }

    private static SyntaxToken GetIdentifier(SyntaxNode node) => node switch
    {
        MethodDeclarationSyntax method => method.Identifier,
        ConstructorDeclarationSyntax constructor => constructor.Identifier,
        _ => default
    };

    /// <summary>
    /// 1-based line/column coverage. <see cref="FileLinePositionSpan.EndLinePosition"/>
    /// is exclusive, so <paramref name="column"/> must be strictly before the
    /// exclusive end (reject <c>column &gt;= endCol</c>). Treating the end as
    /// inclusive would let the first character of an adjacent method also
    /// match the previous declaration. Same helper as
    /// <c>ChangeSignatureOperation.SpanCoversColumn</c>.
    /// </summary>
    internal static bool SpanCoversColumn(FileLinePositionSpan span, int line, int column)
    {
        var startLine = span.StartLinePosition.Line + 1;
        var endLine = span.EndLinePosition.Line + 1;
        var startCol = span.StartLinePosition.Character + 1;
        var endCol = span.EndLinePosition.Character + 1;

        if (line < startLine || line > endLine)
            return false;
        if (line == startLine && column < startCol)
            return false;
        if (line == endLine && column >= endCol)
            return false;
        return true;
    }

    private static BlockSyntax? GetBody(SyntaxNode node) => node switch
    {
        MethodDeclarationSyntax m => m.Body,
        ConstructorDeclarationSyntax c => c.Body,
        _ => null
    };

    private static SyntaxNode ReplaceBody(SyntaxNode node, BlockSyntax newBody) => node switch
    {
        MethodDeclarationSyntax m => m.WithBody(newBody),
        ConstructorDeclarationSyntax c => c.WithBody(newBody),
        _ => node
    };
}

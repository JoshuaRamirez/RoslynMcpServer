using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using RoslynMcp.Contracts.Enums;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.FileSystem;
using RoslynMcp.Core.Refactoring.Base;
using RoslynMcp.Core.Workspace;

namespace RoslynMcp.Core.Refactoring.Extract;

/// <summary>
/// Promotes a local variable to a method parameter and updates call sites.
/// Honors optional <c>column</c> to disambiguate same-line multi-declarators
/// and continuation-line identifiers (identifier preferred, then smallest
/// covering declarator). Omitted column keeps today's start-line equality
/// on the local declaration statement, then <c>variableName</c>
/// <c>FirstOrDefault</c>. Do not force column 1 when omitted. Do not
/// rewrite line-only to covering-span. After the rewrite, keep the
/// selected declarator — do not rematch by name, stale SpanStart, or line.
/// </summary>
public sealed class IntroduceParameterOperation : RefactoringOperationBase<IntroduceParameterParams>
{
    /// <inheritdoc />
    public IntroduceParameterOperation(WorkspaceContext context) : base(context)
    {
    }

    /// <inheritdoc />
    protected override void ValidateParams(IntroduceParameterParams @params) => Validate(@params);

    /// <summary>
    /// Validates introduce-parameter parameters. Internal so tests can
    /// exercise input rules without loading a workspace.
    /// </summary>
    internal static void Validate(IntroduceParameterParams @params)
    {
        if (string.IsNullOrWhiteSpace(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "sourceFile is required.");

        if (string.IsNullOrWhiteSpace(@params.VariableName))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "variableName is required.");

        if (!PathResolver.IsAbsolutePath(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be an absolute path.");

        if (!PathResolver.IsValidCSharpFilePath(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be a .cs file.");

        if (@params.Line < 1)
            throw new RefactoringException(ErrorCodes.InvalidLineNumber, "line must be >= 1.");

        if (@params.Column.HasValue && @params.Column.Value < 1)
            throw new RefactoringException(ErrorCodes.InvalidColumnNumber, "column must be >= 1.");

        if (!File.Exists(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.SourceFileNotFound, $"Source file not found: {@params.SourceFile}");
    }

    /// <inheritdoc />
    protected override async Task<RefactoringResult> ExecuteCoreAsync(
        Guid operationId,
        IntroduceParameterParams @params,
        CancellationToken cancellationToken)
    {
        var document = GetDocumentOrThrow(@params.SourceFile);
        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);

        if (root == null || semanticModel == null)
            throw new RefactoringException(ErrorCodes.RoslynError, "Could not parse file.");

        // Find the local variable declaration. Omitted column keeps
        // today's start-line + name FirstOrDefault. Column set picks the
        // covering declarator (identifier preferred, then smallest).
        var localDecl = FindLocalDeclarator(root, @params.VariableName, @params.Line, @params.Column);

        if (localDecl == null)
        {
            throw new RefactoringException(ErrorCodes.SymbolNotFound,
                $"Local variable '{@params.VariableName}' not found at line {@params.Line}.");
        }

        // Get the containing method
        var containingMethod = localDecl.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();
        if (containingMethod == null)
        {
            throw new RefactoringException(ErrorCodes.CannotConvert,
                "Local variable must be inside a method to be promoted to a parameter.");
        }

        var methodSymbol = semanticModel.GetDeclaredSymbol(containingMethod, cancellationToken);
        if (methodSymbol == null)
            throw new RefactoringException(ErrorCodes.RoslynError, "Could not resolve method symbol.");

        // Get the variable's type
        var localDeclStatement = localDecl.Ancestors().OfType<LocalDeclarationStatementSyntax>().First();
        var declaredType = localDeclStatement.Declaration.Type;

        // If using 'var', resolve the actual type
        TypeSyntax paramType;
        if (declaredType.IsVar)
        {
            var typeInfo = semanticModel.GetTypeInfo(declaredType, cancellationToken);
            if (typeInfo.Type != null)
            {
                paramType = SyntaxFactory.ParseTypeName(typeInfo.Type.ToDisplayString());
            }
            else
            {
                paramType = declaredType;
            }
        }
        else
        {
            paramType = declaredType;
        }

        // Get the initializer expression (used as default value at call sites)
        var initializer = localDecl.Initializer?.Value;

        if (@params.Preview)
        {
            var beforeSig = containingMethod.Identifier.Text + "(" +
                            string.Join(", ", containingMethod.ParameterList.Parameters.Select(p => p.ToString())) + ")";
            var afterSig = containingMethod.Identifier.Text + "(" +
                           string.Join(", ", containingMethod.ParameterList.Parameters.Select(p => p.ToString())) +
                           (containingMethod.ParameterList.Parameters.Count > 0 ? ", " : "") +
                           $"{paramType.NormalizeWhitespace()} {@params.VariableName})";

            var pendingChanges = new List<PendingChange>
            {
                new()
                {
                    File = @params.SourceFile,
                    ChangeType = ChangeKind.Modify,
                    Description = $"Promote '{@params.VariableName}' to parameter of '{containingMethod.Identifier.Text}'",
                    BeforeSnippet = beforeSig,
                    AfterSnippet = afterSig
                }
            };
            return RefactoringResult.PreviewResult(operationId, pendingChanges);
        }

        // 1. Add parameter to method signature
        var newParam = SyntaxFactory.Parameter(SyntaxFactory.Identifier(@params.VariableName))
            .WithType(paramType.WithTrailingTrivia(SyntaxFactory.Space));

        var newParameterList = containingMethod.ParameterList.AddParameters(newParam);

        // 2. Remove the selected local declarator (do not rematch by name /
        // stale SpanStart / line after rewrite — keep today's selected node).
        var declarationStatement = localDecl.Ancestors().OfType<LocalDeclarationStatementSyntax>().First();
        var newBody = containingMethod.Body!;

        if (declarationStatement.Declaration.Variables.Count == 1)
        {
            // Remove entire statement
            newBody = newBody.RemoveNode(declarationStatement, SyntaxRemoveOptions.KeepNoTrivia)!;
        }
        else
        {
            // Remove just this variable from the multi-variable declaration
            var newDeclaration = declarationStatement.Declaration.RemoveNode(localDecl, SyntaxRemoveOptions.KeepNoTrivia)!;
            var newDeclStatement = declarationStatement.WithDeclaration(newDeclaration);
            newBody = (BlockSyntax)newBody.ReplaceNode(declarationStatement, newDeclStatement);
        }

        var newMethod = containingMethod
            .WithParameterList(newParameterList)
            .WithBody(newBody);

        var newRoot = root.ReplaceNode(containingMethod, newMethod);
        var newSolution = document.WithSyntaxRoot(newRoot).Project.Solution;

        // 3. Update call sites: add the initializer value as argument
        if (initializer != null)
        {
            var references = await SymbolFinder.FindReferencesAsync(
                methodSymbol, newSolution, cancellationToken);

            foreach (var reference in references.SelectMany(r => r.Locations))
            {
                var refDoc = newSolution.GetDocument(reference.Document.Id);
                if (refDoc == null) continue;

                var refRoot = await refDoc.GetSyntaxRootAsync(cancellationToken);
                if (refRoot == null) continue;

                var refNode = refRoot.FindNode(reference.Location.SourceSpan);
                var invocation = refNode.Ancestors().OfType<InvocationExpressionSyntax>().FirstOrDefault();

                if (invocation != null)
                {
                    var newArgument = SyntaxFactory.Argument(initializer);
                    var newArgList = invocation.ArgumentList.AddArguments(newArgument);
                    var newInvocation = invocation.WithArgumentList(newArgList);
                    var newRefRoot = refRoot.ReplaceNode(invocation, newInvocation);
                    newSolution = refDoc.WithSyntaxRoot(newRefRoot).Project.Solution;
                }
            }
        }

        var commitResult = await CommitChangesAsync(newSolution, cancellationToken);

        return RefactoringResult.Succeeded(operationId,
            new FileChanges { FilesModified = commitResult.FilesModified, FilesCreated = commitResult.FilesCreated, FilesDeleted = commitResult.FilesDeleted },
            new Contracts.Models.SymbolInfo { Name = containingMethod.Identifier.Text, FullyQualifiedName = $"{methodSymbol.ContainingType.ToDisplayString()}.{containingMethod.Identifier.Text}", Kind = Contracts.Enums.SymbolKind.Method },
            0, 0);
    }

    /// <summary>
    /// Resolves the local variable declarator. Omitted <paramref name="column"/>
    /// keeps today's start-line equality on the local declaration statement,
    /// then <paramref name="variableName"/> <c>FirstOrDefault</c>. When set,
    /// picks the matching <c>VariableDeclaratorSyntax</c> whose identifier or
    /// declaration span covers that 1-based column (identifier preferred,
    /// then smallest covering declarator). A continuation-line identifier is
    /// eligible — do not require the declaration statement to start on
    /// <paramref name="line"/>. If nothing covers, or the covering declarator
    /// is not <paramref name="variableName"/>, return null
    /// (<c>SymbolNotFound</c>) rather than falling back to FirstOrDefault.
    /// </summary>
    internal static VariableDeclaratorSyntax? FindLocalDeclarator(
        SyntaxNode root,
        string variableName,
        int line,
        int? column)
    {
        if (!column.HasValue)
        {
            // Today's pick exactly: start-line equality on the local
            // declaration statement, then variableName FirstOrDefault.
            // Do not force column 1. Do not rewrite to covering-span.
            var targetLine = line - 1;
            return root.DescendantNodes()
                .OfType<LocalDeclarationStatementSyntax>()
                .Where(l => l.GetLocation().GetLineSpan().StartLinePosition.Line == targetLine)
                .SelectMany(l => l.Declaration.Variables)
                .FirstOrDefault(v => v.Identifier.Text == variableName);
        }

        // Column set: do not require the declaration statement to start
        // on `line`. Scan every local declarator, prefer the identifier
        // hit, then the smallest covering declarator. If the covering
        // declarator is not variableName, return null rather than
        // falling back to FirstOrDefault.
        var covering = root.DescendantNodes()
            .OfType<VariableDeclaratorSyntax>()
            .Where(declarator => declarator.Parent?.Parent is LocalDeclarationStatementSyntax)
            .Where(declarator => DeclaratorCoversColumn(declarator, line, column.Value))
            .OrderBy(declarator => IdentifierCoversColumn(declarator, line, column.Value) ? 0 : 1)
            .ThenBy(declarator => SmallestCoveringSpanLength(declarator, line, column.Value))
            .FirstOrDefault();

        if (covering == null || covering.Identifier.Text != variableName)
            return null;

        return covering;
    }

    private static bool DeclaratorCoversColumn(VariableDeclaratorSyntax declarator, int line, int column) =>
        IdentifierCoversColumn(declarator, line, column) ||
        SpanCoversColumn(declarator.GetLocation().GetLineSpan(), line, column) ||
        (GetLocalDeclaration(declarator) is { } local &&
         SpanCoversColumn(local.GetLocation().GetLineSpan(), line, column));

    private static bool IdentifierCoversColumn(VariableDeclaratorSyntax declarator, int line, int column) =>
        SpanCoversColumn(declarator.Identifier.GetLocation().GetLineSpan(), line, column);

    private static int SmallestCoveringSpanLength(VariableDeclaratorSyntax declarator, int line, int column)
    {
        var smallest = int.MaxValue;
        if (SpanCoversColumn(declarator.GetLocation().GetLineSpan(), line, column))
            smallest = Math.Min(smallest, declarator.Span.Length);

        if (GetLocalDeclaration(declarator) is { } local &&
            SpanCoversColumn(local.GetLocation().GetLineSpan(), line, column))
        {
            smallest = Math.Min(smallest, local.Span.Length);
        }

        return smallest;
    }

    private static LocalDeclarationStatementSyntax? GetLocalDeclaration(VariableDeclaratorSyntax declarator) =>
        declarator.Parent?.Parent as LocalDeclarationStatementSyntax;

    /// <summary>
    /// 1-based line/column coverage. <see cref="FileLinePositionSpan.EndLinePosition"/>
    /// is exclusive, so <paramref name="column"/> must be strictly before the
    /// exclusive end (reject <c>column &gt;= endCol</c>). Treating the end as
    /// inclusive would let the first character of an adjacent declarator also
    /// match the previous one. Same helper as
    /// <c>EncapsulateFieldOperation.SpanCoversColumn</c>.
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
}

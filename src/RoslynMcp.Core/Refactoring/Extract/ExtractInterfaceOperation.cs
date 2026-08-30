using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using RoslynMcp.Contracts.Enums;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.FileSystem;
using RoslynMcp.Core.Refactoring.Base;
using RoslynMcp.Core.Refactoring.Generate;
using RoslynMcp.Core.Refactoring.Utilities;
using RoslynMcp.Core.Resolution;
using RoslynMcp.Core.Workspace;

namespace RoslynMcp.Core.Refactoring.Extract;

/// <summary>
/// Extracts an interface from a class's public members.
/// Honors optional <c>line</c> to disambiguate same-named types in one
/// file (identifier preferred, then smallest containing type).
/// Omitted line keeps today's <c>TypeDeclarationSyntax</c>
/// <c>FirstOrDefault</c> pick (enum and
/// <c>DelegateDeclarationSyntax</c> do not participate).
/// When line is set, a covering enum or delegate is included so it
/// reaches <c>InvalidSymbolKind</c> rather than retargeting a later class.
/// After same-file extract and/or <c>addInterfaceToType</c> rewrites the
/// source document, the selected declaration is recovered by a
/// per-execution syntax annotation (stripped before commit).
/// </summary>
public sealed class ExtractInterfaceOperation : RefactoringOperationBase<ExtractInterfaceParams>
{
    /// <summary>
    /// Creates a new extract interface operation.
    /// </summary>
    public ExtractInterfaceOperation(WorkspaceContext context) : base(context)
    {
    }

    /// <inheritdoc />
    protected override void ValidateParams(ExtractInterfaceParams @params) => Validate(@params);

    /// <summary>
    /// Validates extract-interface parameters. Internal so tests can exercise
    /// input rules without loading a workspace.
    /// </summary>
    internal static void Validate(ExtractInterfaceParams @params)
    {
        if (string.IsNullOrWhiteSpace(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "sourceFile is required.");

        if (string.IsNullOrWhiteSpace(@params.TypeName))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "typeName is required.");

        if (string.IsNullOrWhiteSpace(@params.InterfaceName))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "interfaceName is required.");

        if (!PathResolver.IsAbsolutePath(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be an absolute path.");

        if (!PathResolver.IsValidCSharpFilePath(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be a .cs file.");

        if (@params.Line.HasValue && @params.Line.Value < 1)
            throw new RefactoringException(ErrorCodes.InvalidLineNumber, "Line number must be >= 1.");

        if (!File.Exists(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.SourceFileNotFound, $"Source file not found: {@params.SourceFile}");

        if (!IsValidIdentifier(@params.InterfaceName))
            throw new RefactoringException(ErrorCodes.InvalidSymbolName, $"Invalid interface name: {@params.InterfaceName}");

        if (@params.TargetFile != null)
        {
            if (!PathResolver.IsAbsolutePath(@params.TargetFile))
                throw new RefactoringException(ErrorCodes.InvalidTargetPath, "targetFile must be an absolute path.");

            if (!PathResolver.IsValidCSharpFilePath(@params.TargetFile))
                throw new RefactoringException(ErrorCodes.InvalidTargetPath, "targetFile must be a .cs file.");
        }
    }

    /// <inheritdoc />
    protected override async Task<RefactoringResult> ExecuteCoreAsync(
        Guid operationId,
        ExtractInterfaceParams @params,
        CancellationToken cancellationToken)
    {
        var document = GetDocumentOrThrow(@params.SourceFile);
        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);

        if (root == null || semanticModel == null)
        {
            throw new RefactoringException(ErrorCodes.RoslynError, "Could not parse file.");
        }

        // Optional line disambiguates same-named types. Omitted keeps
        // today's TypeDeclarationSyntax FirstOrDefault pick (enum and
        // DelegateDeclarationSyntax do not participate). Line set also
        // includes a covering enum or delegate so it reaches
        // InvalidSymbolKind instead of retargeting a later class.
        var found = FindTypeDeclaration(root, @params.TypeName, @params.Line);

        if (found == null)
        {
            throw new RefactoringException(
                ErrorCodes.TypeNotFound,
                $"Type '{@params.TypeName}' not found in file.");
        }

        // Get the type symbol
        var typeSymbol = semanticModel.GetDeclaredSymbol(found, cancellationToken) as INamedTypeSymbol;
        if (typeSymbol == null)
        {
            throw new RefactoringException(ErrorCodes.RoslynError, "Could not resolve type symbol.");
        }

        if (found is not TypeDeclarationSyntax typeDeclaration)
        {
            throw new RefactoringException(
                ErrorCodes.InvalidSymbolKind,
                $"Type '{typeSymbol.Name}' is not a supported target for extract_interface.");
        }

        // Check for static class
        if (typeSymbol.IsStatic)
        {
            throw new RefactoringException(
                ErrorCodes.CannotExtractFromStatic,
                "Cannot extract interface from static class.");
        }

        // Check if interface name already exists
        var existingInterface = await TypeResolver.FindTypeByNameAsync(
            $"{typeSymbol.ContainingNamespace}.{@params.InterfaceName}",
            cancellationToken);

        if (existingInterface != null)
        {
            throw new RefactoringException(
                ErrorCodes.InterfaceAlreadyExists,
                $"Interface '{@params.InterfaceName}' already exists.");
        }

        // Get members to extract
        var allExtractable = MemberAnalyzer.GetExtractableMembers(typeSymbol).ToList();
        var membersToExtract = FilterMembers(allExtractable, @params.Members);

        if (membersToExtract.Count == 0)
        {
            throw new RefactoringException(
                ErrorCodes.NoExtractableMembers,
                "No extractable public members found.");
        }

        // Generate interface declaration
        var interfaceDecl = SyntaxGenerationHelper.CreateInterfaceDeclaration(
            @params.InterfaceName,
            membersToExtract);

        // Get namespace
        var namespaceName = typeSymbol.ContainingNamespace?.ToDisplayString();

        // Explicit targetFile always wins. separateFile=true with no targetFile
        // writes {InterfaceName}.cs next to the source.
        var targetFile = ResolveTargetFile(@params);
        ThrowIfSiblingTargetExists(@params, targetFile);

        // If preview mode, return without applying
        if (@params.Preview)
        {
            return CreatePreviewResult(operationId, @params, membersToExtract, interfaceDecl, namespaceName, targetFile);
        }

        // Fresh instance per execution. A static annotation is shared
        // across operations; after CommitChanges the in-memory solution
        // can still carry it, so a later extract on another type would
        // recover the stale node via FirstOrDefault.
        SyntaxAnnotation? targetTypeAnnotation = null;
        var willRewriteSource = targetFile == @params.SourceFile || @params.AddInterfaceToType;
        if (willRewriteSource)
        {
            // Annotate before the rewrite. Same-file extract inserts the
            // interface before the selected type and addInterfaceToType
            // rematches the source type afterward — both shift later
            // same-named types. Do not re-find with stale SpanStart or
            // line. Today's AddInterfaceToBaseListAsync FirstOrDefault
            // by typeName is not enough.
            targetTypeAnnotation = new SyntaxAnnotation("extract-interface-target-type");
            root = root.ReplaceNode(
                typeDeclaration,
                typeDeclaration.WithAdditionalAnnotations(targetTypeAnnotation));
            document = document.WithSyntaxRoot(root);
            typeDeclaration = root.GetAnnotatedNodes(targetTypeAnnotation)
                .OfType<TypeDeclarationSyntax>()
                .FirstOrDefault()
                ?? throw new RefactoringException(ErrorCodes.TypeNotFound, $"Type '{@params.TypeName}' not found in file.");
        }

        // Apply changes
        Solution newSolution;
        if (targetFile != @params.SourceFile)
        {
            // Create new file with interface
            newSolution = await CreateInterfaceInNewFileAsync(
                document.Project.Solution,
                document.Project,
                targetFile,
                interfaceDecl,
                namespaceName,
                root,
                cancellationToken);
        }
        else
        {
            // Add interface to same file
            newSolution = AddInterfaceToSameFile(
                document,
                root,
                typeDeclaration,
                interfaceDecl);
        }

        // Add interface to type's base list if requested
        if (@params.AddInterfaceToType)
        {
            var updatedDoc = newSolution.GetDocument(document.Id);
            if (updatedDoc != null)
            {
                newSolution = await AddInterfaceToBaseListAsync(
                    newSolution,
                    updatedDoc,
                    targetTypeAnnotation!,
                    @params.TypeName,
                    @params.InterfaceName,
                    cancellationToken);
            }
        }
        else if (targetTypeAnnotation != null)
        {
            newSolution = await StripTargetTypeAnnotationAsync(
                newSolution,
                document.Id,
                targetTypeAnnotation,
                cancellationToken);
        }

        // Commit changes
        var commitResult = await CommitChangesAsync(newSolution, cancellationToken);

        return RefactoringResult.Succeeded(
            operationId,
            new FileChanges
            {
                FilesModified = commitResult.FilesModified,
                FilesCreated = commitResult.FilesCreated,
                FilesDeleted = commitResult.FilesDeleted
            },
            new Contracts.Models.SymbolInfo
            {
                Name = @params.InterfaceName,
                FullyQualifiedName = string.IsNullOrEmpty(namespaceName)
                    ? @params.InterfaceName
                    : $"{namespaceName}.{@params.InterfaceName}",
                Kind = Contracts.Enums.SymbolKind.Interface
            },
            0,
            0);
    }

    private static List<ISymbol> FilterMembers(
        List<ISymbol> allMembers,
        IReadOnlyList<string>? requestedMembers)
    {
        if (requestedMembers == null || requestedMembers.Count == 0)
        {
            return allMembers;
        }

        var requestedSet = new HashSet<string>(requestedMembers);
        // Indexers match metadata name (Item), Roslyn name (this[]), and
        // conventional display (this[int i]) — same identity forms as
        // implement_interface.
        var filtered = allMembers
            .Where(m => ImplementInterfaceOperation.MatchesRequestedMember(m, requestedSet))
            .ToList();

        var notFound = requestedMembers
            .Where(n => !allMembers.Any(m =>
                ImplementInterfaceOperation.MatchesRequestedMember(m, new HashSet<string> { n })))
            .ToList();

        if (notFound.Count > 0)
        {
            throw new RefactoringException(
                ErrorCodes.MemberNotFound,
                $"Members not found or not extractable: {string.Join(", ", notFound)}");
        }

        return filtered;
    }

    private Task<Solution> CreateInterfaceInNewFileAsync(
        Solution solution,
        Project project,
        string targetFile,
        InterfaceDeclarationSyntax interfaceDecl,
        string? namespaceName,
        SyntaxNode sourceRoot,
        CancellationToken cancellationToken)
    {
        // Build compilation unit with usings from source
        var usings = sourceRoot.DescendantNodes()
            .OfType<UsingDirectiveSyntax>()
            .ToList();

        MemberDeclarationSyntax wrappedInterface;
        if (!string.IsNullOrEmpty(namespaceName))
        {
            wrappedInterface = SyntaxFactory.FileScopedNamespaceDeclaration(
                    SyntaxFactory.ParseName(namespaceName))
                .WithMembers(SyntaxFactory.SingletonList<MemberDeclarationSyntax>(interfaceDecl));
        }
        else
        {
            wrappedInterface = interfaceDecl;
        }

        var compilationUnit = SyntaxFactory.CompilationUnit()
            .WithUsings(SyntaxFactory.List(usings))
            .WithMembers(SyntaxFactory.SingletonList(wrappedInterface))
            .NormalizeWhitespace();

        // Create new document
        var newDoc = project.AddDocument(
            Path.GetFileName(targetFile),
            compilationUnit,
            filePath: targetFile);

        return Task.FromResult(newDoc.Project.Solution);
    }

    private static Solution AddInterfaceToSameFile(
        Document document,
        SyntaxNode root,
        TypeDeclarationSyntax typeDeclaration,
        InterfaceDeclarationSyntax interfaceDecl)
    {
        // Find insertion point - before the class
        var newInterface = interfaceDecl
            .WithLeadingTrivia(SyntaxFactory.CarriageReturnLineFeed)
            .WithTrailingTrivia(SyntaxFactory.CarriageReturnLineFeed, SyntaxFactory.CarriageReturnLineFeed);

        var newRoot = root.InsertNodesBefore(typeDeclaration, new[] { newInterface });
        var newDoc = document.WithSyntaxRoot(newRoot);

        return newDoc.Project.Solution;
    }

    private static async Task<Solution> AddInterfaceToBaseListAsync(
        Solution solution,
        Document document,
        SyntaxAnnotation targetTypeAnnotation,
        string typeName,
        string interfaceName,
        CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken);
        if (root == null) return solution;

        // Recover the selected declaration from the per-execution
        // annotation. Do not rematch FirstOrDefault by typeName — that
        // attaches to an earlier same-named type after the rewrite.
        var typeDeclaration = root.GetAnnotatedNodes(targetTypeAnnotation)
            .OfType<TypeDeclarationSyntax>()
            .FirstOrDefault()
            ?? throw new RefactoringException(
                ErrorCodes.TypeNotFound,
                $"Type '{typeName}' not found in file.");

        var baseType = SyntaxFactory.SimpleBaseType(SyntaxFactory.ParseTypeName(interfaceName));

        TypeDeclarationSyntax newTypeDeclaration;
        if (typeDeclaration.BaseList == null)
        {
            newTypeDeclaration = typeDeclaration.WithBaseList(
                SyntaxFactory.BaseList(SyntaxFactory.SingletonSeparatedList<BaseTypeSyntax>(baseType)));
        }
        else
        {
            var newBaseList = typeDeclaration.BaseList.AddTypes(baseType);
            newTypeDeclaration = typeDeclaration.WithBaseList(newBaseList);
        }

        // Strip the per-execution annotation so it does not linger in the
        // workspace after commit.
        newTypeDeclaration = (TypeDeclarationSyntax)newTypeDeclaration.WithoutAnnotations(targetTypeAnnotation);

        var newRoot = root.ReplaceNode(typeDeclaration, newTypeDeclaration);
        var newDoc = document.WithSyntaxRoot(newRoot);

        return newDoc.Project.Solution;
    }

    private static async Task<Solution> StripTargetTypeAnnotationAsync(
        Solution solution,
        DocumentId documentId,
        SyntaxAnnotation targetTypeAnnotation,
        CancellationToken cancellationToken)
    {
        var document = solution.GetDocument(documentId);
        if (document == null)
            return solution;

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        if (root == null)
            return solution;

        var annotated = root.GetAnnotatedNodes(targetTypeAnnotation).FirstOrDefault();
        if (annotated == null)
            return solution;

        var newRoot = root.ReplaceNode(annotated, annotated.WithoutAnnotations(targetTypeAnnotation));
        return document.WithSyntaxRoot(newRoot).Project.Solution;
    }

    /// <summary>
    /// Resolves the destination for the extracted interface.
    /// Explicit <see cref="ExtractInterfaceParams.TargetFile"/> always wins.
    /// </summary>
    private static string ResolveTargetFile(ExtractInterfaceParams @params)
    {
        if (!string.IsNullOrWhiteSpace(@params.TargetFile))
            return @params.TargetFile;

        if (!@params.SeparateFile)
            return @params.SourceFile;

        var directory = Path.GetDirectoryName(@params.SourceFile);
        if (string.IsNullOrEmpty(directory))
        {
            throw new RefactoringException(
                ErrorCodes.InvalidSourcePath,
                "sourceFile must have a parent directory.");
        }

        return PathResolver.Combine(directory, @params.InterfaceName + ".cs");
    }

    private static void ThrowIfSiblingTargetExists(ExtractInterfaceParams @params, string targetFile)
    {
        // Explicit targetFile keeps today's path; only the computed sibling is rejected.
        if (!string.IsNullOrWhiteSpace(@params.TargetFile) || !@params.SeparateFile)
            return;

        if (!File.Exists(targetFile))
            return;

        throw new RefactoringException(
            ErrorCodes.TargetFileExists,
            $"Destination file already exists: {targetFile}");
    }

    private static RefactoringResult CreatePreviewResult(
        Guid operationId,
        ExtractInterfaceParams @params,
        List<ISymbol> members,
        InterfaceDeclarationSyntax interfaceDecl,
        string? namespaceName,
        string targetFile)
    {
        var memberNames = string.Join(", ", members.Select(m => m.Name));
        var interfaceCode = interfaceDecl.NormalizeWhitespace().ToFullString();

        var isNewFile = targetFile != @params.SourceFile;

        var pendingChanges = new List<PendingChange>
        {
            new()
            {
                File = targetFile,
                ChangeType = isNewFile ? ChangeKind.Create : ChangeKind.Modify,
                Description = $"Extract interface {@params.InterfaceName} with members: {memberNames}",
                BeforeSnippet = isNewFile ? "// (new file)" : $"// Before type '{@params.TypeName}'",
                AfterSnippet = interfaceCode
            }
        };

        if (@params.AddInterfaceToType)
        {
            pendingChanges.Add(new PendingChange
            {
                File = @params.SourceFile,
                ChangeType = ChangeKind.Modify,
                Description = $"Add {@params.InterfaceName} to base list of {@params.TypeName}",
                BeforeSnippet = $"class {@params.TypeName}",
                AfterSnippet = $"class {@params.TypeName} : {@params.InterfaceName}"
            });
        }

        return RefactoringResult.PreviewResult(operationId, pendingChanges);
    }

    private static bool IsValidIdentifier(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        if (!char.IsLetter(name[0]) && name[0] != '_') return false;
        return name.All(c => char.IsLetterOrDigit(c) || c == '_');
    }

    /// <summary>
    /// Finds a type by <paramref name="typeName"/>. Omitted <paramref name="line"/>
    /// keeps today's <c>TypeDeclarationSyntax</c> <c>FirstOrDefault</c>
    /// pick, including when several same-named types exist (nested vs outer
    /// or two namespaces). Enums and delegates are not in that set, so
    /// omitted line still picks a later same-named class rather than an
    /// earlier enum or <c>DelegateDeclarationSyntax</c>. When set, picks the
    /// type whose identifier or declaration span covers that 1-based line
    /// (same exclusive-end coverage as
    /// <c>GenerateToStringOperation.SpanCoversLine</c> /
    /// <c>ImplementAbstractOperation.SpanCoversLine</c>). Prefer the identifier
    /// hit, then the smallest containing type. Nested types, enums, and
    /// <c>DelegateDeclarationSyntax</c> participate when line is set so a
    /// covering enum or delegate still reaches <c>InvalidSymbolKind</c>
    /// rather than retargeting a later class. Do not require the declaration
    /// to start on <paramref name="line"/> — a split declaration may put the
    /// identifier on a continuation line. If nothing covers this line, keep
    /// today's first-match rather than inventing a not-found. After same-file
    /// extract and/or <c>addInterfaceToType</c>, recover the selected type
    /// from the per-execution syntax annotation — do not reuse a pre-rewrite
    /// SpanStart or line.
    /// </summary>
    internal static MemberDeclarationSyntax? FindTypeDeclaration(
        SyntaxNode root,
        string typeName,
        int? line)
    {
        var typeCandidates = root.DescendantNodes()
            .OfType<TypeDeclarationSyntax>()
            .Where(t => t.Identifier.Text == typeName)
            .ToList();

        if (!line.HasValue)
            return typeCandidates.FirstOrDefault();

        // Line set: include BaseTypeDeclarationSyntax (enum) and
        // DelegateDeclarationSyntax in the covering-line set. Do not
        // require the declaration to start on `line` — a split type's
        // identifier may live on a continuation line whose declaration
        // span still covers that line. Prefer the identifier hit, then
        // the smallest containing type (nested over outer). Include enum
        // and delegate candidates. Do not silently pick the first when a
        // covering node exists elsewhere — scan every candidate. If
        // nothing covers this line, keep today's TypeDeclarationSyntax
        // first-match rather than inventing a not-found (enums and
        // delegates stay out of that omitted-line fallback).
        var lineCandidates = root.DescendantNodes()
            .OfType<BaseTypeDeclarationSyntax>()
            .Where(t => t.Identifier.Text == typeName)
            .Cast<MemberDeclarationSyntax>()
            .Concat(root.DescendantNodes()
                .OfType<DelegateDeclarationSyntax>()
                .Where(d => d.Identifier.Text == typeName))
            .ToList();

        if (lineCandidates.Count == 0)
            return null;

        return lineCandidates
            .Where(t => TypeCoversLine(t, line.Value))
            .OrderBy(t => IdentifierCoversLine(t, line.Value) ? 0 : 1)
            .ThenBy(t => t.Span.Length)
            .FirstOrDefault()
            ?? typeCandidates.FirstOrDefault();
    }

    private static bool TypeCoversLine(MemberDeclarationSyntax type, int line) =>
        IdentifierCoversLine(type, line) ||
        SpanCoversLine(type.GetLocation().GetLineSpan(), line);

    private static bool IdentifierCoversLine(MemberDeclarationSyntax type, int line)
    {
        var identifier = GetTypeIdentifier(type);
        return identifier != default
            && SpanCoversLine(identifier.GetLocation().GetLineSpan(), line);
    }

    private static SyntaxToken GetTypeIdentifier(MemberDeclarationSyntax type) => type switch
    {
        BaseTypeDeclarationSyntax named => named.Identifier,
        DelegateDeclarationSyntax del => del.Identifier,
        _ => default
    };

    /// <summary>
    /// 1-based line coverage. <see cref="FileLinePositionSpan.EndLinePosition"/>
    /// is exclusive, so a span that ends at the start of a line does not
    /// cover that line. Treating the end as inclusive would let the first
    /// line of an adjacent type also match the previous declaration. Same
    /// exclusive-end idea as <c>GenerateToStringOperation.SpanCoversLine</c>.
    /// </summary>
    internal static bool SpanCoversLine(FileLinePositionSpan span, int line)
    {
        var startLine = span.StartLinePosition.Line + 1;
        var endLine = span.EndLinePosition.Line + 1;

        if (line < startLine || line > endLine)
            return false;
        if (line == endLine && span.EndLinePosition.Character == 0)
            return false;
        return true;
    }
}

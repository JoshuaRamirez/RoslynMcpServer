using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using RoslynMcp.Contracts.Enums;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.FileSystem;
using RoslynMcp.Core.Refactoring.Base;
using RoslynMcp.Core.Refactoring.Utilities;
using RoslynMcp.Core.Workspace;

namespace RoslynMcp.Core.Refactoring.Generate;

/// <summary>
/// Generates a ToString() override for a type.
/// Honors optional <c>line</c> / <c>column</c> to disambiguate same-named
/// types in one file (identifier preferred, then smallest containing type).
/// Omitted line keeps today's <c>TypeDeclarationSyntax</c>
/// <c>FirstOrDefault</c> pick (enum and
/// <c>DelegateDeclarationSyntax</c> do not participate).
/// When line is set, a covering enum or delegate is included so it
/// reaches <c>InvalidSymbolKind</c> rather than retargeting a later class.
/// Honors <c>format</c> for interpolated vs StringBuilder bodies,
/// <c>includeProperties</c> when collecting ToString members,
/// <c>includeInheritedMembers</c> to append accessible base-type members,
/// <c>replaceExisting</c> to remove an existing non-generic parameterless
/// ToString (instance or static) before generating a fresh override,
/// and <c>callSuper</c> to fold the immediate base type's ToString into the body.
/// Honors optional <c>allFiles</c> to walk every C# document (or the
/// optional single <c>sourceFile</c>) and generate ToString on every
/// eligible <see cref="TypeDeclarationSyntax"/>.
/// </summary>
public sealed class GenerateToStringOperation : RefactoringOperationBase<GenerateToStringParams>
{
    private const string InterpolatedFormat = "interpolated";
    private const string StringBuilderFormat = "stringbuilder";

    /// <inheritdoc />
    public GenerateToStringOperation(WorkspaceContext context) : base(context)
    {
    }

    /// <inheritdoc />
    protected override void ValidateParams(GenerateToStringParams @params) => Validate(@params);

    /// <summary>
    /// Validates generate-tostring parameters. Internal so tests can exercise
    /// input rules without loading a workspace.
    /// </summary>
    internal static void Validate(GenerateToStringParams @params)
    {
        if (@params.AllFiles)
        {
            if (!string.IsNullOrWhiteSpace(@params.TypeName) ||
                @params.Fields != null ||
                @params.Line.HasValue ||
                @params.Column.HasValue)
            {
                throw new RefactoringException(
                    ErrorCodes.MissingRequiredParam,
                    "allFiles cannot be combined with typeName, fields, line, or column.");
            }

            ValidateFormat(@params.Format);
            return;
        }

        if (string.IsNullOrWhiteSpace(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "sourceFile is required.");

        if (string.IsNullOrWhiteSpace(@params.TypeName))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "typeName is required.");

        var sourceFile = @params.SourceFile!;

        if (!PathResolver.IsAbsolutePath(sourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be an absolute path.");

        if (!PathResolver.IsValidCSharpFilePath(sourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be a .cs file.");

        ValidateFormat(@params.Format);

        if (@params.Line.HasValue && @params.Line.Value < 1)
            throw new RefactoringException(ErrorCodes.InvalidLineNumber, "Line number must be >= 1.");

        if (@params.Column.HasValue && @params.Column.Value < 1)
            throw new RefactoringException(ErrorCodes.InvalidColumnNumber, "column must be >= 1.");

        if (!File.Exists(sourceFile))
            throw new RefactoringException(ErrorCodes.SourceFileNotFound, $"Source file not found: {sourceFile}");
    }

    /// <inheritdoc />
    protected override async Task<RefactoringResult> ExecuteCoreAsync(
        Guid operationId,
        GenerateToStringParams @params,
        CancellationToken cancellationToken)
    {
        if (@params.AllFiles)
            return await ExecuteAllFilesAsync(operationId, @params, cancellationToken);

        var sourceFile = @params.SourceFile!;
        var typeName = @params.TypeName!;
        var document = GetDocumentOrThrow(sourceFile);
        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);

        if (root == null || semanticModel == null)
            throw new RefactoringException(ErrorCodes.RoslynError, "Could not parse file.");

        // Optional line/column disambiguates same-named types. Omitted
        // column keeps today's TypeDeclarationSyntax FirstOrDefault
        // pick (enum and DelegateDeclarationSyntax do not participate).
        // Line set also includes a covering enum or delegate so it
        // reaches InvalidSymbolKind instead of retargeting a later class.
        var found = FindTypeDeclaration(root, typeName, @params.Line, @params.Column);

        if (found == null)
            throw new RefactoringException(ErrorCodes.TypeNotFound, $"Type '{typeName}' not found.");

        var typeSymbol = semanticModel.GetDeclaredSymbol(found, cancellationToken) as INamedTypeSymbol;
        if (typeSymbol == null)
            throw new RefactoringException(ErrorCodes.RoslynError, "Could not resolve type symbol.");

        if (found is not TypeDeclarationSyntax typeDecl)
        {
            throw new RefactoringException(
                ErrorCodes.InvalidSymbolKind,
                $"Type '{typeSymbol.Name}' is not a supported target for generate_tostring.");
        }

        if (@params.CallSuper && IsObjectOrValueTypeBase(typeSymbol))
        {
            throw new RefactoringException(
                ErrorCodes.CallSuperOnObjectBase,
                "callSuper cannot be used when the immediate base type is System.Object or System.ValueType.");
        }

        if (@params.CallSuper && HasAbstractBaseToString(typeSymbol))
        {
            throw new RefactoringException(
                ErrorCodes.CallSuperOnAbstractBase,
                "callSuper cannot be used when the immediate base type's ToString() is abstract.");
        }

        if (!@params.ReplaceExisting && HasExistingToStringOverride(typeSymbol))
            throw new RefactoringException(ErrorCodes.AlreadyHasOverride, "Type already has a ToString override.");

        var members = CollectToStringMembers(typeSymbol, @params);
        if (members.Count == 0 && !@params.CallSuper)
            throw new RefactoringException(ErrorCodes.NoMembersToGenerate, "No fields or properties available for ToString generation.");

        var toStringMethod = GenerateToString(typeName, members, @params.Format, @params.CallSuper);

        if (@params.Preview)
        {
            var code = toStringMethod.NormalizeWhitespace().ToFullString();
            var pendingChanges = new List<PendingChange>
            {
                new()
                {
                    File = sourceFile,
                    ChangeType = ChangeKind.Modify,
                    Description = BuildDescription(
                        typeName,
                        members,
                        @params.Format,
                        @params.IncludeInheritedMembers,
                        @params.ReplaceExisting,
                        @params.CallSuper),
                    BeforeSnippet = @params.ReplaceExisting
                        ? $"// Type '{typeName}' (replacing existing ToString)"
                        : $"// Type '{typeName}' (no ToString)",
                    AfterSnippet = code
                }
            };
            return RefactoringResult.PreviewResult(operationId, pendingChanges);
        }

        var solution = await ApplyToStringToSolutionAsync(
            document.Project.Solution,
            document,
            typeDecl,
            typeSymbol,
            toStringMethod,
            @params.ReplaceExisting,
            typeName,
            cancellationToken);
        var commitResult = await CommitChangesAsync(solution, cancellationToken);

        return RefactoringResult.Succeeded(operationId,
            new FileChanges { FilesModified = commitResult.FilesModified, FilesCreated = commitResult.FilesCreated, FilesDeleted = commitResult.FilesDeleted },
            new Contracts.Models.SymbolInfo { Name = typeName, FullyQualifiedName = typeName, Kind = Contracts.Enums.SymbolKind.Class },
            0, 0);
    }

    /// <summary>
    /// Walks every C# document (<c>FilePath</c> ends with <c>.cs</c>; same
    /// document filter as <c>FormatDocumentOperation.ExecuteAllFilesAsync</c>
    /// / <c>GenerateEqualsHashCodeOperation.ExecuteAllFilesAsync</c>
    /// / <c>ImplementAbstractOperation.ExecuteAllFilesAsync</c>
    /// / <c>GenerateConstructorOperation.ExecuteAllFilesAsync</c>
    /// / <c>InlineConstantOperation.ExecuteAllFilesAsync</c>
    /// / <c>MakeStaticOperation.ExecuteAllFilesAsync</c>
    /// / <c>MakeNonStaticOperation.ExecuteAllFilesAsync</c>
    /// / <c>EncapsulateFieldOperation.ExecuteAllFilesAsync</c>) and generates
    /// ToString on every eligible
    /// <see cref="TypeDeclarationSyntax"/> (class / struct / record /
    /// record struct / interface, including nested — same node kind as
    /// today's <see cref="FindTypeDeclaration"/>). Optional
    /// <c>sourceFile</c> limits the walk to that one file. Interface,
    /// no-members when <c>callSuper</c> is false, existing-ToString
    /// collisions when <c>replaceExisting</c> is false,
    /// sealed inherited parameterless instance ToString (CS0239; skipped
    /// even when <c>replaceExisting</c> unless this type itself declares
    /// the parameterless ToString being replaced),
    /// <c>CallSuperOnObjectBase</c> / <c>CallSuperOnAbstractBase</c>,
    /// uneditable, parse/symbol failures, and otherwise ineligible types
    /// are skipped rather than failing the walk. When a later rewrite
    /// conflicts with an earlier one, the later claim is skipped. When
    /// every type is a no-op, succeeds with empty changes.
    /// </summary>
    private async Task<RefactoringResult> ExecuteAllFilesAsync(
        Guid operationId,
        GenerateToStringParams @params,
        CancellationToken cancellationToken)
    {
        var originalSolution = Context.Solution;
        var currentSolution = originalSolution;
        var allDocuments = originalSolution.Projects
            .SelectMany(p => p.Documents)
            .Where(d => d.FilePath != null && d.FilePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            .OrderBy(d => d.FilePath, StringComparer.Ordinal)
            .ToList();

        if (!string.IsNullOrWhiteSpace(@params.SourceFile))
            allDocuments = FilterDocumentsBySourceFile(allDocuments, @params.SourceFile!);

        var generatedCountByDoc = new Dictionary<DocumentId, int>();
        var processedTypes = new HashSet<string>(StringComparer.Ordinal);

        foreach (var document in allDocuments)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!IsDocumentEditable(document, Context.Workspace))
                continue;

            while (true)
            {
                var currentDocument = currentSolution.GetDocument(document.Id);
                if (currentDocument == null || !IsDocumentEditable(currentDocument, Context.Workspace))
                    break;

                var root = await currentDocument.GetSyntaxRootAsync(cancellationToken);
                var semanticModel = await currentDocument.GetSemanticModelAsync(cancellationToken);
                if (root == null || semanticModel == null)
                    break;

                Solution? updated = null;
                foreach (var typeDeclaration in CollectTypeDeclarations(root))
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var typeSymbol = semanticModel.GetDeclaredSymbol(typeDeclaration, cancellationToken) as INamedTypeSymbol;
                    if (typeSymbol == null)
                        continue;

                    var typeKey = TypeWalkKey(currentDocument.Project.Id, typeSymbol);
                    if (!processedTypes.Add(typeKey))
                        continue;

                    try
                    {
                        updated = await TryGenerateOneAsync(
                            currentDocument,
                            typeDeclaration,
                            typeSymbol,
                            @params,
                            cancellationToken);
                    }
                    catch (RefactoringException)
                    {
                        // Skip interface / no-members / already-has-override
                        // / CallSuperOnObjectBase / CallSuperOnAbstractBase
                        // / uneditable / parse/symbol failures rather than
                        // failing the walk.
                        updated = null;
                    }

                    if (updated != null)
                        break;
                }

                if (updated == null)
                    break;

                currentSolution = updated;
                generatedCountByDoc[document.Id] =
                    generatedCountByDoc.GetValueOrDefault(document.Id) + 1;
            }
        }

        var documentsToCompare = originalSolution.Projects
            .SelectMany(p => p.Documents)
            .Where(d => d.FilePath != null && d.FilePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            .OrderBy(d => d.FilePath, StringComparer.Ordinal)
            .ToList();

        var allPendingChanges = new List<PendingChange>();
        var anyChanged = false;

        foreach (var document in documentsToCompare)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var originalDocument = originalSolution.GetDocument(document.Id);
            var currentDocument = currentSolution.GetDocument(document.Id);
            if (originalDocument == null || currentDocument == null)
                continue;

            var beforeText = await originalDocument.GetTextAsync(cancellationToken);
            var afterText = await currentDocument.GetTextAsync(cancellationToken);
            if (beforeText.ContentEquals(afterText))
                continue;

            if (@params.Preview)
            {
                var originalRoot = await originalDocument.GetSyntaxRootAsync(cancellationToken);
                var currentRoot = await currentDocument.GetSyntaxRootAsync(cancellationToken);
                if (originalRoot == null || currentRoot == null)
                    continue;

                var span = originalRoot.GetLocation().GetLineSpan();
                var generatedCount = generatedCountByDoc.GetValueOrDefault(document.Id);
                allPendingChanges.Add(new PendingChange
                {
                    File = originalDocument.FilePath!,
                    ChangeType = ChangeKind.Modify,
                    Description = generatedCount > 0
                        ? BuildAllFilesDescription(generatedCount)
                        : "Update ToString generated in other files",
                    BeforeSnippet = originalRoot.NormalizeWhitespace().ToFullString().Trim(),
                    AfterSnippet = currentRoot.NormalizeWhitespace().ToFullString().Trim(),
                    StartLine = span.StartLinePosition.Line + 1,
                    EndLine = span.EndLinePosition.Line + 1
                });
                continue;
            }

            anyChanged = true;
        }

        if (@params.Preview)
            return RefactoringResult.PreviewResult(operationId, allPendingChanges);

        if (anyChanged)
        {
            var commitResult = await CommitChangesAsync(currentSolution, cancellationToken);
            return RefactoringResult.Succeeded(operationId,
                new FileChanges
                {
                    FilesModified = commitResult.FilesModified,
                    FilesCreated = commitResult.FilesCreated,
                    FilesDeleted = commitResult.FilesDeleted
                },
                null, 0, 0);
        }

        return RefactoringResult.Succeeded(operationId,
            new FileChanges { FilesModified = [], FilesCreated = [], FilesDeleted = [] },
            null, 0, 0);
    }

    /// <summary>
    /// De-dupes types within one project across rematches and partials.
    /// Includes <paramref name="projectId"/> so two projects that both
    /// declare <c>TestApp.Widget</c> are not collapsed onto one walk key.
    /// File-local types (<see cref="INamedTypeSymbol.IsFileLocal"/>) also
    /// include a file-local marker and declaring file so two
    /// <c>file class Worker</c> hosts that share
    /// <see cref="SymbolDisplayFormat.FullyQualifiedFormat"/> are not
    /// skipped as if they were one partial. Genuine partials
    /// (<c>IsFileLocal</c> false, multiple declaring syntax refs) still
    /// collapse to one walk.
    /// </summary>
    internal static string TypeWalkKey(ProjectId projectId, INamedTypeSymbol typeSymbol)
    {
        var fqn = typeSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        if (!typeSymbol.IsFileLocal)
            return TypeWalkKey(projectId, fqn);

        var declaringFile = typeSymbol.DeclaringSyntaxReferences
            .Select(reference => reference.SyntaxTree.FilePath)
            .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path));

        return TypeWalkKey(projectId, fqn, declaringFile);
    }

    /// <summary>
    /// Same key shape as a non-file-local
    /// <see cref="TypeWalkKey(ProjectId, INamedTypeSymbol)"/> for tests
    /// that do not have a compilation symbol.
    /// </summary>
    internal static string TypeWalkKey(ProjectId projectId, string fullyQualifiedTypeName) =>
        $"{projectId.Id:D}\0{fullyQualifiedTypeName}";

    /// <summary>
    /// File-local walk key: project id + FQN plus a <c>file</c> marker and
    /// declaring path so same-named file-local types in different files
    /// stay distinct. Ordinary (non-file-local) callers should use
    /// <see cref="TypeWalkKey(ProjectId, string)"/>.
    /// </summary>
    internal static string TypeWalkKey(ProjectId projectId, string fullyQualifiedTypeName, string? fileLocalDeclaringPath)
    {
        var key = TypeWalkKey(projectId, fullyQualifiedTypeName);
        if (string.IsNullOrWhiteSpace(fileLocalDeclaringPath))
            return $"{key}\0file";

        string normalized;
        try
        {
            normalized = PathResolver.NormalizePath(fileLocalDeclaringPath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            normalized = fileLocalDeclaringPath;
        }

        return $"{key}\0file\0{normalized}";
    }

    /// <summary>
    /// Preview description for a file that generated ToString on
    /// <paramref name="generatedCount"/> types.
    /// </summary>
    internal static string BuildAllFilesDescription(int generatedCount) =>
        generatedCount == 1
            ? "Generate ToString"
            : $"Generate ToString on {generatedCount} types";

    /// <summary>
    /// Collects every <see cref="TypeDeclarationSyntax"/> in
    /// <paramref name="root"/> (class / struct / interface / record /
    /// record struct, including nested — same node kind as today's
    /// <see cref="FindTypeDeclaration"/>). Deterministic
    /// <c>SpanStart</c> then span-length order.
    /// </summary>
    internal static IReadOnlyList<TypeDeclarationSyntax> CollectTypeDeclarations(SyntaxNode root) =>
        root.DescendantNodes()
            .OfType<TypeDeclarationSyntax>()
            .OrderBy(type => type.SpanStart)
            .ThenBy(type => type.Span.Length)
            .ToList();

    private async Task<Solution?> TryGenerateOneAsync(
        Document document,
        TypeDeclarationSyntax typeDecl,
        INamedTypeSymbol typeSymbol,
        GenerateToStringParams @params,
        CancellationToken cancellationToken)
    {
        if (!IsDocumentEditable(document, Context.Workspace))
            return null;

        if (typeSymbol.TypeKind == TypeKind.Interface || typeDecl is InterfaceDeclarationSyntax)
            return null;

        if (@params.CallSuper && IsObjectOrValueTypeBase(typeSymbol))
            return null;

        if (@params.CallSuper && HasAbstractBaseToString(typeSymbol))
            return null;

        if (!@params.ReplaceExisting && HasExistingToStringOverride(typeSymbol))
            return null;

        // Sealed inherited ToString is CS0239 if we emit override.
        // replaceExisting cannot unseal an inherited method — only skip
        // this guard when THIS type already declares the parameterless
        // ToString being replaced.
        if (HasSealedInheritedToString(typeSymbol) &&
            !(@params.ReplaceExisting && HasExistingToStringOverride(typeSymbol)))
        {
            return null;
        }

        List<ISymbol> members;
        try
        {
            members = CollectToStringMembers(typeSymbol, @params);
        }
        catch (RefactoringException)
        {
            return null;
        }

        if (members.Count == 0 && !@params.CallSuper)
            return null;

        var typeName = typeDecl.Identifier.Text;
        var toStringMethod = GenerateToString(typeName, members, @params.Format, @params.CallSuper);

        try
        {
            return await ApplyToStringToSolutionAsync(
                document.Project.Solution,
                document,
                typeDecl,
                typeSymbol,
                toStringMethod,
                @params.ReplaceExisting,
                typeName,
                cancellationToken);
        }
        catch (RefactoringException)
        {
            return null;
        }
    }

    private static async Task<Solution> ApplyToStringToSolutionAsync(
        Solution solution,
        Document document,
        TypeDeclarationSyntax typeDecl,
        INamedTypeSymbol typeSymbol,
        MethodDeclarationSyntax toStringMethod,
        bool replaceExisting,
        string typeName,
        CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken)
            ?? throw new RefactoringException(ErrorCodes.RoslynError, "Could not parse file.");
        // Fresh instance per execution. A static annotation is shared
        // across operations; after CommitChanges the in-memory solution
        // can still carry it, so a later replaceExisting on another type
        // would recover the stale node via FirstOrDefault.
        SyntaxAnnotation? targetTypeAnnotation = null;
        if (replaceExisting)
        {
            // Annotate before the rewrite. Removing ToString from an
            // earlier same-file partial shifts both SpanStart and the
            // physical line of a later selected partial — do not re-find
            // with those stale values. Today's FindTypeDeclaration(root,
            // typeName, preferredSpanStart) is not enough.
            targetTypeAnnotation = new SyntaxAnnotation("generate-tostring-target-type");
            root = root.ReplaceNode(
                typeDecl,
                typeDecl.WithAdditionalAnnotations(targetTypeAnnotation));
            document = document.WithSyntaxRoot(root);
            solution = document.Project.Solution;

            solution = await RemoveExistingToStringOverridesAcrossPartialsAsync(
                solution, typeSymbol, cancellationToken);
            document = solution.GetDocument(document.Id)
                ?? throw new RefactoringException(
                    ErrorCodes.DocumentNotEditable,
                    $"Could not locate the document for type '{typeName}'.");
            root = await document.GetSyntaxRootAsync(cancellationToken)
                ?? throw new RefactoringException(ErrorCodes.RoslynError, "Could not parse file.");
            typeDecl = root.GetAnnotatedNodes(targetTypeAnnotation)
                .OfType<TypeDeclarationSyntax>()
                .FirstOrDefault()
                ?? throw new RefactoringException(ErrorCodes.TypeNotFound, $"Type '{typeName}' not found.");
        }

        var newTypeDecl = typeDecl.AddMembers(
            toStringMethod.WithLeadingTrivia(SyntaxFactory.CarriageReturnLineFeed, SyntaxFactory.CarriageReturnLineFeed));
        // Strip the per-execution annotation so it does not linger in the
        // workspace after commit.
        if (targetTypeAnnotation != null)
            newTypeDecl = (TypeDeclarationSyntax)newTypeDecl.WithoutAnnotations(targetTypeAnnotation);

        var newRoot = root.ReplaceNode(typeDecl, newTypeDecl);
        return document.WithSyntaxRoot(newRoot).Project.Solution;
    }

    private static List<Document> FilterDocumentsBySourceFile(List<Document> documents, string sourceFile)
    {
        string wanted;
        try
        {
            wanted = PathResolver.NormalizePath(sourceFile);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            wanted = sourceFile;
        }

        return documents
            .Where(d => string.Equals(
                PathResolver.NormalizePath(d.FilePath!),
                wanted,
                StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    /// <summary>
    /// Returns whether <paramref name="document"/> can receive source edits
    /// (skip not throw — same checks as sibling AllFiles operations).
    /// </summary>
    internal static bool IsDocumentEditable(Document document, Microsoft.CodeAnalysis.Workspace workspace)
    {
        if (document is SourceGeneratedDocument)
            return false;

        if (string.IsNullOrWhiteSpace(document.FilePath) || !File.Exists(document.FilePath))
            return false;

        return workspace.CanApplyChange(ApplyChangesKind.ChangeDocument);
    }

    internal static void ValidateFormat(string? format)
    {
        if (string.IsNullOrWhiteSpace(format))
            return;

        var normalized = format.Trim();
        if (normalized.Equals(InterpolatedFormat, StringComparison.OrdinalIgnoreCase)
            || normalized.Equals(StringBuilderFormat, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        throw new RefactoringException(
            ErrorCodes.InvalidToStringFormat,
            $"Invalid format: '{format}'. Expected \"interpolated\" or \"stringbuilder\".");
    }

    internal static bool IsStringBuilderFormat(string? format) =>
        !string.IsNullOrWhiteSpace(format)
        && format.Trim().Equals(StringBuilderFormat, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Collects ToString members, then drops any named <c>ToString</c> so the
    /// generated override cannot hide <c>this.ToString</c> (recursive / CS0119).
    /// </summary>
    internal static List<ISymbol> CollectToStringMembers(INamedTypeSymbol typeSymbol, GenerateToStringParams @params)
    {
        var members = EqualityMemberCollector.CollectMembers(
            typeSymbol, @params.Fields, @params.IncludeProperties, @params.IncludeInheritedMembers);

        return members.Where(m => !string.Equals(m.Name, "ToString", StringComparison.Ordinal)).ToList();
    }

    internal static bool HasExistingToStringOverride(INamedTypeSymbol typeSymbol) =>
        typeSymbol.GetMembers("ToString").OfType<IMethodSymbol>().Any(IsParameterlessToString);

    internal static string BuildDescription(
        string typeName,
        IReadOnlyList<ISymbol> members,
        string? format,
        bool includeInheritedMembers,
        bool replaceExisting = false,
        bool callSuper = false)
    {
        var verb = replaceExisting ? "Replace" : "Generate";
        var formatName = IsStringBuilderFormat(format) ? StringBuilderFormat : InterpolatedFormat;
        var notes = new List<string>();
        if (callSuper)
            notes.Add("base ToString");
        if (includeInheritedMembers)
            notes.Add("inherited members");
        var extraNote = notes.Count > 0 ? " including " + string.Join(" and ", notes) : "";
        var memberList = members.Count > 0
            ? ": " + string.Join(", ", members.Select(m => m.Name))
            : "";
        return $"{verb} ToString ({formatName}){extraNote} for {typeName}{memberList}";
    }

    private static async Task<Solution> RemoveExistingToStringOverridesAcrossPartialsAsync(
        Solution solution,
        INamedTypeSymbol typeSymbol,
        CancellationToken cancellationToken)
    {
        // Match by span/kind, not SyntaxNode reference — same seam as Equals.
        var membersByTreeAndPart = new Dictionary<SyntaxTree, Dictionary<int, HashSet<(int Start, int End, SyntaxKind Kind)>>>();

        foreach (var method in CollectToStringOverridesToReplace(typeSymbol))
        {
            foreach (var reference in method.DeclaringSyntaxReferences)
            {
                var syntax = await reference.GetSyntaxAsync(cancellationToken);
                if (syntax.Parent is not TypeDeclarationSyntax part)
                    continue;

                if (!membersByTreeAndPart.TryGetValue(syntax.SyntaxTree, out var byPart))
                {
                    byPart = new Dictionary<int, HashSet<(int Start, int End, SyntaxKind Kind)>>();
                    membersByTreeAndPart[syntax.SyntaxTree] = byPart;
                }

                if (!byPart.TryGetValue(part.SpanStart, out var keys))
                {
                    keys = new HashSet<(int Start, int End, SyntaxKind Kind)>();
                    byPart[part.SpanStart] = keys;
                }

                keys.Add((syntax.SpanStart, syntax.Span.End, syntax.Kind()));
            }
        }

        foreach (var (tree, byPart) in membersByTreeAndPart)
        {
            var document = GetDocumentForTree(solution, tree, typeSymbol.Name);
            var root = await document.GetSyntaxRootAsync(cancellationToken)
                ?? throw new RefactoringException(ErrorCodes.RoslynError, "Could not parse file.");

            var replacements = new Dictionary<TypeDeclarationSyntax, TypeDeclarationSyntax>();
            foreach (var reference in typeSymbol.DeclaringSyntaxReferences)
            {
                if (!SameSyntaxTree(reference.SyntaxTree, tree))
                    continue;
                if (await reference.GetSyntaxAsync(cancellationToken) is not TypeDeclarationSyntax originalPart)
                    continue;
                // The solution root may already carry a target-type
                // annotation (new tree). Rematch by span — annotation does
                // not change SpanStart — so ReplaceNodes sees nodes from
                // this root and keeps the annotation on the selected type.
                var part = RematchTypeDeclaration(root, originalPart);
                if (part == null)
                    continue;
                if (!byPart.TryGetValue(part.SpanStart, out var keys) || keys.Count == 0)
                    continue;

                var remainingMembers = part.Members
                    .Where(m => !keys.Contains((m.SpanStart, m.Span.End, m.Kind())))
                    .ToArray();
                replacements[part] = part.WithMembers(SyntaxFactory.List(remainingMembers));
            }

            if (replacements.Count == 0)
                continue;

            var newRoot = root.ReplaceNodes(replacements.Keys, (original, _) => replacements[original]);
            solution = solution.WithDocumentSyntaxRoot(document.Id, newRoot);
        }

        return solution;
    }

    /// <summary>
    /// Non-generic parameterless ToString (instance or static). Generic
    /// <c>ToString&lt;T&gt;()</c> can coexist with the generated override and
    /// is not treated as existing / not replaced.
    /// </summary>
    private static bool IsParameterlessToString(IMethodSymbol method) =>
        !method.IsImplicitlyDeclared && method.Arity == 0 && method.Parameters.Length == 0;

    private static IEnumerable<IMethodSymbol> CollectToStringOverridesToReplace(INamedTypeSymbol typeSymbol) =>
        typeSymbol.GetMembers("ToString").OfType<IMethodSymbol>().Where(IsParameterlessToString);

    private static Document GetDocumentForTree(Solution solution, SyntaxTree tree, string typeName)
    {
        var document = solution.GetDocument(tree);
        if (document != null)
            return document;

        if (!string.IsNullOrEmpty(tree.FilePath))
        {
            foreach (var id in solution.GetDocumentIdsWithFilePath(tree.FilePath))
            {
                document = solution.GetDocument(id);
                if (document != null)
                    return document;
            }
        }

        throw new RefactoringException(
            ErrorCodes.DocumentNotEditable,
            $"Could not locate a declaring document for type '{typeName}'.");
    }

    private static bool SameSyntaxTree(SyntaxTree left, SyntaxTree right) =>
        left == right
        || (!string.IsNullOrEmpty(left.FilePath)
            && string.Equals(left.FilePath, right.FilePath, StringComparison.OrdinalIgnoreCase));

    private static TypeDeclarationSyntax? RematchTypeDeclaration(SyntaxNode root, TypeDeclarationSyntax original) =>
        root.DescendantNodes()
            .OfType<TypeDeclarationSyntax>()
            .FirstOrDefault(t => t.SpanStart == original.SpanStart && t.Identifier.Text == original.Identifier.Text);

    /// <summary>
    /// Finds a type by <paramref name="typeName"/>. Omitted
    /// <paramref name="column"/> keeps today's typeName + optional
    /// <paramref name="line"/> pick, including omitted-line
    /// <c>TypeDeclarationSyntax</c> <c>FirstOrDefault</c> (enum and
    /// <c>DelegateDeclarationSyntax</c> do not participate) and
    /// line-only exclusive-end coverage (<see cref="SpanCoversLine"/>).
    /// Do not force column 1 when omitted. Do not change
    /// omitted-line/omitted-column to <c>BaseTypeDeclarationSyntax</c>
    /// FirstOrDefault. Do not add enums or delegates to the omitted-line
    /// set. Column without line keeps today's first-match after the
    /// typeName filter (<c>TypeDeclarationSyntax</c> only) rather than
    /// substituting each candidate's own start line. When column is set
    /// with line, picks the type whose identifier or declaration span
    /// covers that 1-based column (same exclusive-end coverage as
    /// <c>GenerateConstructorOperation.SpanCoversColumn</c> /
    /// <c>ImplementAbstractOperation.SpanCoversColumn</c> /
    /// <c>GenerateOverridesOperation.SpanCoversColumn</c>). Prefer the
    /// identifier hit, then the smallest containing type. Nested types,
    /// enums, and <c>DelegateDeclarationSyntax</c> participate when line
    /// is set so a covering enum or delegate still reaches
    /// <c>InvalidSymbolKind</c> rather than retargeting a later class. Do
    /// not require the declaration to start on <paramref name="line"/>
    /// when column is set — a split declaration may put the identifier on
    /// a continuation line. If column is set with line and nothing covers
    /// that position, return null (TypeNotFound) rather than falling back
    /// to first-match. After
    /// <see cref="RemoveExistingToStringOverridesAcrossPartialsAsync"/>,
    /// recover the selected type from the per-execution syntax annotation
    /// — do not reuse a pre-rewrite SpanStart or line.
    /// </summary>
    internal static MemberDeclarationSyntax? FindTypeDeclaration(
        SyntaxNode root,
        string typeName,
        int? line,
        int? column = null)
    {
        var typeCandidates = root.DescendantNodes()
            .OfType<TypeDeclarationSyntax>()
            .Where(t => t.Identifier.Text == typeName)
            .ToList();

        // Line set (including column+line) uses BaseTypeDeclarationSyntax
        // (enum) plus DelegateDeclarationSyntax so a covering enum or
        // delegate reaches InvalidSymbolKind rather than retargeting a
        // later class. Omitted-line / column-without-line stay
        // TypeDeclarationSyntax only — do not switch that set to
        // BaseTypeDeclarationSyntax (that would add enums).
        var lineCandidates = line.HasValue
            ? root.DescendantNodes()
                .OfType<BaseTypeDeclarationSyntax>()
                .Where(t => t.Identifier.Text == typeName)
                .Cast<MemberDeclarationSyntax>()
                .Concat(root.DescendantNodes()
                    .OfType<DelegateDeclarationSyntax>()
                    .Where(d => d.Identifier.Text == typeName))
                .ToList()
            : typeCandidates.Cast<MemberDeclarationSyntax>().ToList();

        // Column without line is not a source position: substituting each
        // candidate's own start line would match every equally-aligned
        // same-name type and could silently pick the shortest. Keep
        // today's FirstOrDefault after the typeName filter
        // (TypeDeclarationSyntax only).
        if (column.HasValue && !line.HasValue)
            return typeCandidates.FirstOrDefault();

        if (column.HasValue)
        {
            // Do not require the declaration to start on `line` — a split
            // type's identifier may live on a continuation line whose
            // declaration span still covers that column. Prefer the
            // identifier hit, then the smallest containing type (nested
            // over outer). Include enum and delegate candidates so a
            // covering enum or delegate still reaches InvalidSymbolKind.
            // Do not silently pick the first when a covering node exists
            // elsewhere — scan every candidate. If nothing covers this
            // position, keep today's not-found (null) rather than
            // inventing a first-match.
            return lineCandidates
                .Where(t => TypeCoversColumn(t, line!.Value, column.Value))
                .OrderBy(t => IdentifierCoversColumn(t, line!.Value, column.Value) ? 0 : 1)
                .ThenBy(t => t.Span.Length)
                .FirstOrDefault();
        }

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

    private static bool TypeCoversColumn(MemberDeclarationSyntax type, int line, int column) =>
        IdentifierCoversColumn(type, line, column) ||
        SpanCoversColumn(type.GetLocation().GetLineSpan(), line, column);

    private static bool IdentifierCoversColumn(MemberDeclarationSyntax type, int line, int column)
    {
        var identifier = GetTypeIdentifier(type);
        return identifier != default
            && SpanCoversColumn(identifier.GetLocation().GetLineSpan(), line, column);
    }

    private static SyntaxToken GetTypeIdentifier(MemberDeclarationSyntax type) => type switch
    {
        BaseTypeDeclarationSyntax named => named.Identifier,
        DelegateDeclarationSyntax del => del.Identifier,
        _ => default
    };

    /// <summary>
    /// 1-based line/column coverage. <see cref="FileLinePositionSpan.EndLinePosition"/>
    /// is exclusive, so <paramref name="column"/> must be strictly before the
    /// exclusive end (reject <c>column &gt;= endCol</c>). Treating the end as
    /// inclusive would let the first character of an adjacent type also
    /// match the previous declaration. Same helper as
    /// <c>GenerateConstructorOperation.SpanCoversColumn</c> /
    /// <c>ImplementAbstractOperation.SpanCoversColumn</c> /
    /// <c>GenerateOverridesOperation.SpanCoversColumn</c>.
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

    /// <summary>
    /// 1-based line coverage. <see cref="FileLinePositionSpan.EndLinePosition"/>
    /// is exclusive, so a span that ends at the start of a line does not
    /// cover that line. Treating the end as inclusive would let the first
    /// line of an adjacent type also match the previous declaration. Same
    /// exclusive-end idea as <c>GeneratePropertyOperation.SpanCoversLine</c>.
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

    private static MethodDeclarationSyntax GenerateToString(
        string typeName,
        List<ISymbol> members,
        string? format,
        bool callSuper)
    {
        return IsStringBuilderFormat(format)
            ? GenerateStringBuilderToString(typeName, members, callSuper)
            : GenerateInterpolatedToString(typeName, members, callSuper);
    }

    private static MethodDeclarationSyntax GenerateInterpolatedToString(
        string typeName,
        List<ISymbol> members,
        bool callSuper)
    {
        // $"TypeName {{ Field1 = {Field1}, Field2 = {Field2} }}"
        // With callSuper: $"TypeName {{ {base.ToString()}, Field1 = {Field1}, ... }}"
        // ParseExpression keeps "{{ {" intact — building the interpolation by hand
        // then NormalizeWhitespace collapses the opening "{{" into a single "{".
        var pieces = new List<string>();
        if (callSuper)
            pieces.Add("{base.ToString()}");
        foreach (var member in members)
            pieces.Add($"{member.Name} = {{{member.Name}}}");

        var inner = string.Join(", ", pieces);
        var interpolatedString = SyntaxFactory.ParseExpression("$\"" + typeName + " {{ " + inner + " }}\"");

        return CreateToStringMethod(SyntaxFactory.Block(SyntaxFactory.ReturnStatement(interpolatedString)));
    }

    private static MethodDeclarationSyntax GenerateStringBuilderToString(
        string typeName,
        List<ISymbol> members,
        bool callSuper)
    {
        // Same display shape as interpolated: TypeName { Field1 = {Field1}, Field2 = {Field2} }
        var statements = new List<StatementSyntax>
        {
            SyntaxFactory.LocalDeclarationStatement(
                SyntaxFactory.VariableDeclaration(SyntaxFactory.IdentifierName("var"))
                    .WithVariables(SyntaxFactory.SingletonSeparatedList(
                        SyntaxFactory.VariableDeclarator(SyntaxFactory.Identifier("sb"))
                            .WithInitializer(SyntaxFactory.EqualsValueClause(
                                SyntaxFactory.ObjectCreationExpression(
                                        SyntaxFactory.ParseTypeName("global::System.Text.StringBuilder"))
                                    .WithArgumentList(SyntaxFactory.ArgumentList()))))))
        };

        statements.Add(AppendLiteral($"{typeName} {{ "));

        if (callSuper)
            statements.Add(AppendExpression(BaseToStringCall()));

        for (int i = 0; i < members.Count; i++)
        {
            var member = members[i];
            var prefix = i == 0 && !callSuper ? "" : ", ";
            statements.Add(AppendLiteral($"{prefix}{member.Name} = "));
            statements.Add(AppendExpression(MemberAccess(member.Name)));
        }

        statements.Add(AppendLiteral(" }"));
        statements.Add(SyntaxFactory.ReturnStatement(
            SyntaxFactory.InvocationExpression(
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.IdentifierName("sb"),
                        SyntaxFactory.IdentifierName("ToString")))
                .WithArgumentList(SyntaxFactory.ArgumentList())));

        return CreateToStringMethod(SyntaxFactory.Block(statements));
    }

    private static bool IsObjectOrValueTypeBase(INamedTypeSymbol typeSymbol)
    {
        var baseType = typeSymbol.BaseType;
        return baseType == null
            || baseType.SpecialType is SpecialType.System_Object or SpecialType.System_ValueType;
    }

    private static bool HasAbstractBaseToString(INamedTypeSymbol typeSymbol)
    {
        var baseType = typeSymbol.BaseType;
        if (baseType == null)
            return false;

        var toString = FindParameterlessInstanceToString(baseType);
        return toString?.IsAbstract == true;
    }

    /// <summary>
    /// True when the effective inherited parameterless instance
    /// <c>ToString</c> is sealed (same walk as
    /// <see cref="HasAbstractBaseToString"/> via
    /// <see cref="FindParameterlessInstanceToString"/>). Emitting
    /// <c>public override string ToString()</c> would be CS0239.
    /// </summary>
    internal static bool HasSealedInheritedToString(INamedTypeSymbol typeSymbol)
    {
        var baseType = typeSymbol.BaseType;
        if (baseType == null)
            return false;

        var toString = FindParameterlessInstanceToString(baseType);
        return toString?.IsSealed == true;
    }

    private static IMethodSymbol? FindParameterlessInstanceToString(INamedTypeSymbol type)
    {
        for (var current = type; current != null; current = current.BaseType)
        {
            foreach (var method in current.GetMembers("ToString").OfType<IMethodSymbol>())
            {
                if (method.IsStatic || method.Arity != 0 || method.Parameters.Length != 0)
                    continue;

                return method;
            }
        }

        return null;
    }

    private static InvocationExpressionSyntax BaseToStringCall() =>
        SyntaxFactory.InvocationExpression(
                SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    SyntaxFactory.BaseExpression(),
                    SyntaxFactory.IdentifierName("ToString")))
            .WithArgumentList(SyntaxFactory.ArgumentList());

    private static MethodDeclarationSyntax CreateToStringMethod(BlockSyntax body) =>
        SyntaxFactory.MethodDeclaration(
                SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.StringKeyword)),
                "ToString")
            .WithModifiers(SyntaxFactory.TokenList(
                SyntaxFactory.Token(SyntaxKind.PublicKeyword),
                SyntaxFactory.Token(SyntaxKind.OverrideKeyword)))
            .WithBody(body)
            .NormalizeWhitespace();

    private static MemberAccessExpressionSyntax MemberAccess(string memberName) =>
        SyntaxFactory.MemberAccessExpression(
            SyntaxKind.SimpleMemberAccessExpression,
            SyntaxFactory.ThisExpression(),
            SyntaxFactory.IdentifierName(memberName));

    private static ExpressionStatementSyntax AppendLiteral(string text) =>
        AppendExpression(SyntaxFactory.LiteralExpression(
            SyntaxKind.StringLiteralExpression,
            SyntaxFactory.Literal(text)));

    private static ExpressionStatementSyntax AppendExpression(ExpressionSyntax argument) =>
        SyntaxFactory.ExpressionStatement(
            SyntaxFactory.InvocationExpression(
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.IdentifierName("sb"),
                        SyntaxFactory.IdentifierName("Append")))
                .WithArgumentList(SyntaxFactory.ArgumentList(
                    SyntaxFactory.SingletonSeparatedList(SyntaxFactory.Argument(argument)))));
}

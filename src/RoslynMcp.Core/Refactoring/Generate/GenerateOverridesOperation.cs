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
using RoslynMcp.Core.Resolution;
using RoslynMcp.Core.Workspace;

namespace RoslynMcp.Core.Refactoring.Generate;

/// <summary>
/// Generates override methods, properties/indexers, and events for base
/// class virtual/abstract members. Honors <c>callBase</c> (default true)
/// for ordinary methods (<c>base.Method(...)</c>) and for non-abstract
/// properties / indexers (<c>return base.Prop;</c> / <c>base.Prop = value;</c>,
/// <c>return base[i];</c> / <c>base[i] = value;</c>). Abstract members
/// still throw. Events always use empty add/remove regardless of
/// <c>callBase</c>. Honors <c>replaceExisting</c> to include already-overridden
/// members of this type, remove those override declarations (including
/// across partials) by signature, and insert a standard generated override.
/// <c>new</c> hiders, explicit interface implementations, non-override
/// methods, and primary constructors are never replaced. Extra modifiers
/// on the old override are not copied.
/// Honors optional <c>allFiles</c> to walk every C# document (or the
/// optional single <c>sourceFile</c>) and generate missing overrides on
/// every eligible <see cref="TypeDeclarationSyntax"/>.
/// </summary>
public sealed class GenerateOverridesOperation : RefactoringOperationBase<GenerateOverridesParams>
{
    /// <summary>
    /// Creates a new generate overrides operation.
    /// </summary>
    public GenerateOverridesOperation(WorkspaceContext context) : base(context)
    {
    }

    /// <inheritdoc />
    protected override void ValidateParams(GenerateOverridesParams @params) => Validate(@params);

    /// <summary>
    /// Validates generate-overrides parameters. Internal so tests can exercise
    /// input rules without loading a workspace.
    /// </summary>
    internal static void Validate(GenerateOverridesParams @params)
    {
        if (@params.AllFiles)
        {
            if (!string.IsNullOrWhiteSpace(@params.TypeName) ||
                @params.Members != null ||
                @params.Line.HasValue ||
                @params.Column.HasValue)
            {
                throw new RefactoringException(
                    ErrorCodes.MissingRequiredParam,
                    "allFiles cannot be combined with typeName, members, line, or column.");
            }

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
        GenerateOverridesParams @params,
        CancellationToken cancellationToken)
    {
        if (@params.AllFiles)
            return await ExecuteAllFilesAsync(operationId, @params, cancellationToken);

        var document = GetDocumentOrThrow(@params.SourceFile!);
        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);

        if (root == null || semanticModel == null)
        {
            throw new RefactoringException(ErrorCodes.RoslynError, "Could not parse file.");
        }

        // Find the type declaration (optional line/column disambiguates same-named types)
        var typeDeclaration = FindTypeDeclaration(root, @params.TypeName!, @params.Line, @params.Column);

        if (typeDeclaration == null)
        {
            throw new RefactoringException(
                ErrorCodes.TypeNotFound,
                $"Type '{@params.TypeName}' not found in file.");
        }

        // Get the type symbol
        var typeSymbol = semanticModel.GetDeclaredSymbol(typeDeclaration, cancellationToken) as INamedTypeSymbol;
        if (typeSymbol == null)
        {
            throw new RefactoringException(ErrorCodes.RoslynError, "Could not resolve type symbol.");
        }

        // Check for sealed class
        if (typeSymbol.IsSealed && typeSymbol.TypeKind != TypeKind.Struct)
        {
            // Sealed classes can still override, just can't be inherited from
        }

        var overridableMembers = CollectMembersToOverride(typeSymbol, @params.ReplaceExisting);

        // Filter to requested members if specified
        List<ISymbol> membersToOverride;
        if (@params.Members != null && @params.Members.Count > 0)
        {
            var requestedSet = new HashSet<string>(@params.Members, StringComparer.OrdinalIgnoreCase);
            membersToOverride = overridableMembers.Where(m => requestedSet.Contains(m.Name)).ToList();

            // Check for not found
            var foundNames = membersToOverride.Select(m => m.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var notFound = @params.Members.Where(n => !foundNames.Contains(n)).ToList();
            if (notFound.Count > 0)
            {
                throw new RefactoringException(
                    ErrorCodes.OverrideTargetNotFound,
                    $"Members not found or not overridable: {string.Join(", ", notFound)}. " +
                    $"Available: {string.Join(", ", overridableMembers.Select(m => m.Name))}");
            }
        }
        else
        {
            membersToOverride = overridableMembers;
        }

        if (membersToOverride.Count == 0)
        {
            throw new RefactoringException(
                ErrorCodes.NoOverridableMembers,
                "No overridable members found in base classes.");
        }

        var replacements = ResolveReplacements(typeSymbol, membersToOverride, @params.ReplaceExisting);
        var membersToReplace = membersToOverride.Where(m => replacements.ContainsKey(m)).ToList();
        var membersToGenerate = membersToOverride.Where(m => !replacements.ContainsKey(m)).ToList();

        // Generate overrides
        var overrides = GenerateOverrideMembers(membersToOverride, @params.CallBase, typeSymbol);

        // If preview mode, return without applying
        if (@params.Preview)
        {
            return CreatePreviewResult(operationId, @params, membersToGenerate, membersToReplace, overrides, membersToOverride);
        }

        var newSolution = await ApplyOverridesToSolutionAsync(
            document.Project.Solution,
            document,
            typeDeclaration,
            typeSymbol,
            overrides,
            replacements,
            @params.TypeName!,
            cancellationToken);

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
                Name = @params.TypeName!,
                FullyQualifiedName = typeSymbol.ToDisplayString(),
                Kind = Contracts.Enums.SymbolKind.Class
            },
            0,
            0);
    }

    /// <summary>
    /// Walks every C# document (<c>FilePath</c> ends with <c>.cs</c>; same
    /// document filter as <c>FormatDocumentOperation.ExecuteAllFilesAsync</c>
    /// / <c>ImplementInterfaceOperation.ExecuteAllFilesAsync</c>
    /// / <c>ImplementAbstractOperation.ExecuteAllFilesAsync</c>
    /// / <c>GenerateToStringOperation.ExecuteAllFilesAsync</c>
    /// / <c>GenerateEqualsHashCodeOperation.ExecuteAllFilesAsync</c>
    /// / <c>GenerateConstructorOperation.ExecuteAllFilesAsync</c>) and
    /// generates missing overrides on every eligible
    /// <see cref="TypeDeclarationSyntax"/> (class / struct / record /
    /// record struct / interface, including nested — same node kind as
    /// today's <see cref="FindTypeDeclaration"/>). Optional
    /// <c>sourceFile</c> limits the walk to that one file. Empty collect
    /// / <c>NoOverridableMembers</c>, <c>OverrideExists</c>, uneditable
    /// documents, parse/symbol failures, and otherwise ineligible types
    /// are skipped rather than failing the walk. When a later rewrite
    /// conflicts with an earlier one, the later claim is skipped. When
    /// every type is a no-op, succeeds with empty changes.
    /// </summary>
    private async Task<RefactoringResult> ExecuteAllFilesAsync(
        Guid operationId,
        GenerateOverridesParams @params,
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
                        // Skip empty collect / NoOverridableMembers /
                        // OverrideExists / uneditable / parse-symbol
                        // failures rather than failing the walk.
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
                        : "Update overrides generated in other files",
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
    /// Preview description for a file that generated overrides on
    /// <paramref name="generatedCount"/> types.
    /// </summary>
    internal static string BuildAllFilesDescription(int generatedCount) =>
        generatedCount == 1
            ? "Generate overrides"
            : $"Generate overrides on {generatedCount} types";

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
        TypeDeclarationSyntax typeDeclaration,
        INamedTypeSymbol typeSymbol,
        GenerateOverridesParams @params,
        CancellationToken cancellationToken)
    {
        if (!IsDocumentEditable(document, Context.Workspace))
            return null;

        // Static types cannot host instance overrides (Object ToString /
        // Equals / GetHashCode would otherwise be collected and emitted).
        if (typeSymbol.IsStatic)
            return null;

        List<ISymbol> membersToOverride;
        try
        {
            membersToOverride = CollectMembersToOverride(typeSymbol, @params.ReplaceExisting);
        }
        catch (RefactoringException)
        {
            return null;
        }

        // Bulk skip-not-throw: today's collect only drops `new` / other
        // non-override hiders when replaceExisting is true. Emitting an
        // override beside `public new void M()` / `public new string
        // ToString()` is CS0111. Filter those collisions here without
        // changing single-site CollectMembersToOverride.
        membersToOverride = membersToOverride
            .Where(member => !IsHiddenByNonOverride(typeSymbol, member))
            .ToList();

        if (membersToOverride.Count == 0)
            return null;

        Dictionary<ISymbol, ISymbol> replacements;
        try
        {
            replacements = ResolveReplacements(typeSymbol, membersToOverride, @params.ReplaceExisting);
        }
        catch (RefactoringException)
        {
            return null;
        }

        var overrides = GenerateOverrideMembers(membersToOverride, @params.CallBase, typeSymbol);
        if (overrides.Count == 0)
            return null;

        try
        {
            return await ApplyOverridesToSolutionAsync(
                document.Project.Solution,
                document,
                typeDeclaration,
                typeSymbol,
                overrides,
                replacements,
                typeSymbol.Name,
                cancellationToken);
        }
        catch (RefactoringException)
        {
            return null;
        }
    }

    private static async Task<Solution> ApplyOverridesToSolutionAsync(
        Solution solution,
        Document document,
        TypeDeclarationSyntax typeDeclaration,
        INamedTypeSymbol typeSymbol,
        List<MemberDeclarationSyntax> overrides,
        Dictionary<ISymbol, ISymbol> replacements,
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
        if (replacements.Count > 0)
        {
            // Annotate before the rewrite. Removing an override from an
            // earlier same-file partial shifts both SpanStart and the
            // physical line of a later selected partial — do not re-find
            // with those stale values.
            targetTypeAnnotation = new SyntaxAnnotation("generate-overrides-target-type");
            root = root.ReplaceNode(
                typeDeclaration,
                typeDeclaration.WithAdditionalAnnotations(targetTypeAnnotation));
            document = document.WithSyntaxRoot(root);
            solution = document.Project.Solution;

            solution = await RemoveExistingOverridesAcrossPartialsAsync(
                solution, typeSymbol, replacements.Values, cancellationToken);
            document = solution.GetDocument(document.Id)
                ?? throw new RefactoringException(
                    ErrorCodes.DocumentNotEditable,
                    $"Could not locate the document for type '{typeName}'.");
            root = await document.GetSyntaxRootAsync(cancellationToken)
                ?? throw new RefactoringException(ErrorCodes.RoslynError, "Could not parse file.");
            typeDeclaration = root.GetAnnotatedNodes(targetTypeAnnotation)
                .OfType<TypeDeclarationSyntax>()
                .FirstOrDefault()
                ?? throw new RefactoringException(
                    ErrorCodes.TypeNotFound,
                    $"Type '{typeName}' not found in file.");
        }

        // Add overrides to type. Strip the per-execution annotation
        // so it does not linger in the workspace after commit.
        var newTypeDeclaration = AddMembers(typeDeclaration, overrides);
        if (targetTypeAnnotation != null)
            newTypeDeclaration = (TypeDeclarationSyntax)newTypeDeclaration.WithoutAnnotations(targetTypeAnnotation);
        var newRoot = root.ReplaceNode(typeDeclaration, newTypeDeclaration);
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

    /// <summary>
    /// Missing overridable members (today's <see cref="MemberAnalyzer.GetOverridableMembers"/>
    /// plus Object ToString / Equals(object) / GetHashCode). When
    /// <paramref name="replaceExisting"/> is true, already-overridden members
    /// that generate_overrides would have emitted are added, and base members
    /// hidden by a <c>new</c> or other non-override same-signature member on
    /// this type are dropped.
    /// </summary>
    internal static List<ISymbol> CollectMembersToOverride(INamedTypeSymbol typeSymbol, bool replaceExisting)
    {
        var result = new List<ISymbol>();

        foreach (var member in MemberAnalyzer.GetOverridableMembers(typeSymbol))
            AddUnique(result, member);

        foreach (var member in GetObjectMethodsToOverride(typeSymbol))
            AddUnique(result, member);

        if (!replaceExisting)
            return result;

        foreach (var member in GetExistingOverrideTargets(typeSymbol))
            AddUnique(result, member);

        result.RemoveAll(m => IsHiddenByNonOverride(typeSymbol, m));
        return result;
    }

    private static void AddUnique(List<ISymbol> members, ISymbol member)
    {
        if (members.Any(existing => SignaturesMatch(existing, member)))
            return;

        members.Add(member);
    }

    private static List<ISymbol> GetObjectMethodsToOverride(INamedTypeSymbol typeSymbol)
    {
        var result = new List<ISymbol>();
        var existingOverrides = typeSymbol.GetMembers()
            .OfType<IMethodSymbol>()
            .Where(m => m.IsOverride)
            .Select(m => m.Name)
            .ToHashSet();

        // Find Object type
        var objectType = typeSymbol.BaseType;
        while (objectType != null && objectType.SpecialType != SpecialType.System_Object)
        {
            objectType = objectType.BaseType;
        }

        if (objectType == null) return result;

        // Get ToString, Equals, GetHashCode from Object
        foreach (var member in objectType.GetMembers())
        {
            if (member is IMethodSymbol method &&
                (method.Name == "ToString" || method.Name == "Equals" || method.Name == "GetHashCode") &&
                method.IsVirtual &&
                !existingOverrides.Contains(method.Name))
            {
                // Skip Equals(object, object) static method
                if (method.Name == "Equals" && method.Parameters.Length != 1)
                    continue;

                result.Add(method);
            }
        }

        return result;
    }

    private static IEnumerable<ISymbol> GetExistingOverrideTargets(INamedTypeSymbol typeSymbol)
    {
        foreach (var member in typeSymbol.GetMembers())
        {
            if (member.IsImplicitlyDeclared)
                continue;

            switch (member)
            {
                case IMethodSymbol method when IsEligibleExistingMethodOverride(method):
                    if (method.OverriddenMethod != null && IsGeneratedOverrideTarget(method.OverriddenMethod))
                        yield return method.OverriddenMethod;
                    break;
                case IPropertySymbol property when IsEligibleExistingPropertyOverride(property):
                    if (property.OverriddenProperty != null && IsGeneratedOverrideTarget(property.OverriddenProperty))
                        yield return property.OverriddenProperty;
                    break;
                case IEventSymbol evt when IsEligibleExistingEventOverride(evt):
                    if (evt.OverriddenEvent != null && IsGeneratedOverrideTarget(evt.OverriddenEvent))
                        yield return evt.OverriddenEvent;
                    break;
            }
        }
    }

    private static bool IsEligibleExistingMethodOverride(IMethodSymbol method) =>
        method.IsOverride
        && method.MethodKind == MethodKind.Ordinary
        && method.ExplicitInterfaceImplementations.Length == 0;

    private static bool IsEligibleExistingPropertyOverride(IPropertySymbol property) =>
        property.IsOverride
        && property.ExplicitInterfaceImplementations.Length == 0;

    private static bool IsEligibleExistingEventOverride(IEventSymbol evt) =>
        evt.IsOverride
        && evt.ExplicitInterfaceImplementations.Length == 0;

    private static bool IsEligibleExistingOverride(ISymbol member) =>
        member switch
        {
            IMethodSymbol method => IsEligibleExistingMethodOverride(method),
            IPropertySymbol property => IsEligibleExistingPropertyOverride(property),
            IEventSymbol evt => IsEligibleExistingEventOverride(evt),
            _ => false
        };

    /// <summary>
    /// True when <paramref name="member"/> is a base member
    /// <c>generate_overrides</c> would emit: an overridable (unsealed
    /// virtual/abstract/override) ordinary method, property, or event from a
    /// non-Object base, or Object ToString / Equals(object) / GetHashCode.
    /// </summary>
    private static bool IsGeneratedOverrideTarget(ISymbol member)
    {
        if (member is IMethodSymbol method)
        {
            var original = method;
            while (original.OverriddenMethod != null)
                original = original.OverriddenMethod;

            if (original.ContainingType?.SpecialType == SpecialType.System_Object)
            {
                return original.Name is "ToString" or "GetHashCode"
                    || (original.Name == "Equals" && original.Parameters.Length == 1 && !original.IsStatic);
            }

            return (original.IsVirtual || original.IsAbstract || original.IsOverride)
                && !original.IsSealed
                && original.MethodKind == MethodKind.Ordinary;
        }

        if (member is IPropertySymbol property)
        {
            var original = property;
            while (original.OverriddenProperty != null)
                original = original.OverriddenProperty;

            if (original.ContainingType?.SpecialType == SpecialType.System_Object)
                return false;

            return (original.IsVirtual || original.IsAbstract || original.IsOverride) && !original.IsSealed;
        }

        if (member is IEventSymbol evt)
        {
            var original = evt;
            while (original.OverriddenEvent != null)
                original = original.OverriddenEvent;

            if (original.ContainingType?.SpecialType == SpecialType.System_Object)
                return false;

            return (original.IsVirtual || original.IsAbstract || original.IsOverride) && !original.IsSealed;
        }

        return false;
    }

    private static bool IsHiddenByNonOverride(INamedTypeSymbol typeSymbol, ISymbol baseMember)
    {
        foreach (var member in typeSymbol.GetMembers(baseMember.Name))
        {
            if (member.IsImplicitlyDeclared || member.IsOverride)
                continue;
            if (IsExplicitInterface(member))
                continue;
            if (SignaturesMatch(member, baseMember))
                return true;
        }

        return false;
    }

    private static bool IsExplicitInterface(ISymbol member) =>
        member switch
        {
            IMethodSymbol method => method.ExplicitInterfaceImplementations.Length > 0
                || method.MethodKind == MethodKind.ExplicitInterfaceImplementation,
            IPropertySymbol property => property.ExplicitInterfaceImplementations.Length > 0,
            IEventSymbol evt => evt.ExplicitInterfaceImplementations.Length > 0,
            _ => false
        };

    /// <summary>
    /// Maps each selected member to the existing override on this type that
    /// will be removed. Exact signature match wins. Two same-name existing
    /// overrides with no exact match is <see cref="ErrorCodes.OverrideExists"/>.
    /// </summary>
    internal static Dictionary<ISymbol, ISymbol> ResolveReplacements(
        INamedTypeSymbol typeSymbol,
        IReadOnlyList<ISymbol> selectedMembers,
        bool replaceExisting)
    {
        var replacements = new Dictionary<ISymbol, ISymbol>(SymbolEqualityComparer.Default);
        if (!replaceExisting)
            return replacements;

        foreach (var selected in selectedMembers)
        {
            var existing = FindExistingOverride(typeSymbol, selected, out var ambiguous);
            if (ambiguous)
            {
                throw new RefactoringException(
                    ErrorCodes.OverrideExists,
                    $"Multiple existing overrides named '{selected.Name}' and none matches the selected signature.");
            }

            if (existing != null)
                replacements[selected] = existing;
        }

        return replacements;
    }

    private static ISymbol? FindExistingOverride(
        INamedTypeSymbol typeSymbol,
        ISymbol selected,
        out bool ambiguous)
    {
        ambiguous = false;
        var sameName = new List<ISymbol>();
        ISymbol? exact = null;

        foreach (var member in typeSymbol.GetMembers())
        {
            if (!IsEligibleExistingOverride(member))
                continue;
            if (!string.Equals(member.Name, selected.Name, StringComparison.Ordinal))
                continue;

            sameName.Add(member);
            if (SignaturesMatch(member, selected))
                exact = member;
        }

        if (exact != null)
            return exact;

        if (sameName.Count >= 2)
        {
            ambiguous = true;
            return null;
        }

        return null;
    }

    internal static bool SignaturesMatch(ISymbol left, ISymbol right)
    {
        if (left is IMethodSymbol leftMethod && right is IMethodSymbol rightMethod)
            return MethodSignaturesMatch(leftMethod, rightMethod);

        if (left is IPropertySymbol leftProp && right is IPropertySymbol rightProp)
            return PropertySignaturesMatch(leftProp, rightProp);

        if (left is IEventSymbol leftEvent && right is IEventSymbol rightEvent)
            return string.Equals(leftEvent.Name, rightEvent.Name, StringComparison.Ordinal);

        return false;
    }

    private static bool MethodSignaturesMatch(IMethodSymbol left, IMethodSymbol right)
    {
        if (!string.Equals(left.Name, right.Name, StringComparison.Ordinal))
            return false;
        if (left.Arity != right.Arity)
            return false;
        if (left.Parameters.Length != right.Parameters.Length)
            return false;

        for (var i = 0; i < left.Parameters.Length; i++)
        {
            if (left.Parameters[i].RefKind != right.Parameters[i].RefKind)
                return false;
            if (!ParameterTypesMatch(left.Parameters[i].Type, right.Parameters[i].Type))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Method type parameters are distinct symbols on the base vs override
    /// (<c>Base.M&lt;T&gt;(T)</c> vs <c>Derived.M&lt;T&gt;(T)</c>), so
    /// <see cref="SymbolEqualityComparer.Default"/> misses an exact match.
    /// Compare those by ordinal; keep concrete / named types as today.
    /// </summary>
    private static bool ParameterTypesMatch(ITypeSymbol left, ITypeSymbol right)
    {
        if (left is ITypeParameterSymbol leftTp
            && leftTp.TypeParameterKind == TypeParameterKind.Method
            && right is ITypeParameterSymbol rightTp
            && rightTp.TypeParameterKind == TypeParameterKind.Method)
        {
            return leftTp.Ordinal == rightTp.Ordinal;
        }

        return SymbolEqualityComparer.Default.Equals(left, right);
    }

    private static bool PropertySignaturesMatch(IPropertySymbol left, IPropertySymbol right)
    {
        if (!string.Equals(left.Name, right.Name, StringComparison.Ordinal))
            return false;
        if (left.IsIndexer != right.IsIndexer)
            return false;
        if (!left.IsIndexer)
            return true;
        if (left.Parameters.Length != right.Parameters.Length)
            return false;

        for (var i = 0; i < left.Parameters.Length; i++)
        {
            if (left.Parameters[i].RefKind != right.Parameters[i].RefKind)
                return false;
            if (!SymbolEqualityComparer.Default.Equals(left.Parameters[i].Type, right.Parameters[i].Type))
                return false;
        }

        return true;
    }

    private static List<MemberDeclarationSyntax> GenerateOverrideMembers(
        List<ISymbol> members,
        bool callBase,
        INamedTypeSymbol emittingType)
    {
        var overrides = new List<MemberDeclarationSyntax>();

        foreach (var member in members)
        {
            MemberDeclarationSyntax? impl = member switch
            {
                IMethodSymbol method => SyntaxGenerationHelper.CreateMethodStub(
                    method,
                    explicitInterface: false,
                    callBase: callBase && !method.IsAbstract,
                    throwNotImplemented: method.IsAbstract,
                    emittingType: emittingType),
                IPropertySymbol { IsIndexer: true } indexer => SyntaxGenerationHelper.CreateIndexerStub(
                    indexer,
                    explicitInterface: false,
                    throwNotImplemented: indexer.IsAbstract,
                    callBase: callBase && !indexer.IsAbstract,
                    emittingType: emittingType),
                IPropertySymbol property => SyntaxGenerationHelper.CreatePropertyStub(
                    property,
                    explicitInterface: false,
                    throwNotImplemented: property.IsAbstract,
                    callBase: callBase && !property.IsAbstract,
                    emittingType: emittingType),
                IEventSymbol evt => SyntaxGenerationHelper.CreateEventStub(
                    evt,
                    emittingType: emittingType),
                _ => null
            };

            if (impl != null)
            {
                overrides.Add(impl);
            }
        }

        return overrides;
    }

    private static TypeDeclarationSyntax AddMembers(
        TypeDeclarationSyntax typeDeclaration,
        List<MemberDeclarationSyntax> newMembers)
    {
        var members = typeDeclaration.Members.ToList();

        foreach (var member in newMembers)
        {
            members.Add(member
                .WithLeadingTrivia(SyntaxFactory.CarriageReturnLineFeed, SyntaxFactory.CarriageReturnLineFeed)
                .WithTrailingTrivia(SyntaxFactory.CarriageReturnLineFeed));
        }

        return typeDeclaration.WithMembers(SyntaxFactory.List(members));
    }

    /// <summary>
    /// Removes matched override declarations from every partial that holds
    /// them. Match by span/kind, not SyntaxNode reference — same seam as
    /// constructor / Equals / ToString replaceExisting. Field-like events
    /// (<c>override event EventHandler Changed;</c>) declare as a
    /// <see cref="VariableDeclaratorSyntax"/>; those are removed via the
    /// same event-field path as implement_abstract / implement_interface
    /// (drop only the selected declarator from a multi-variable field).
    /// </summary>
    private static async Task<Solution> RemoveExistingOverridesAcrossPartialsAsync(
        Solution solution,
        INamedTypeSymbol typeSymbol,
        IEnumerable<ISymbol> existingOverrides,
        CancellationToken cancellationToken)
    {
        var membersByTreeAndPart = new Dictionary<SyntaxTree, Dictionary<int, HashSet<(int Start, int End, SyntaxKind Kind)>>>();
        var eventDeclaratorsByTreeAndPart = new Dictionary<SyntaxTree, Dictionary<int, HashSet<(int FieldStart, int DeclaratorStart)>>>();

        foreach (var existing in existingOverrides)
        {
            foreach (var reference in existing.DeclaringSyntaxReferences)
            {
                var syntax = await reference.GetSyntaxAsync(cancellationToken);
                if (TryGetEventFieldDeclarator(syntax, out var eventField, out var declarator)
                    && eventField.Parent is TypeDeclarationSyntax eventPart)
                {
                    if (eventField.Declaration.Variables.Count > 1)
                    {
                        AddKeyed(eventDeclaratorsByTreeAndPart, syntax.SyntaxTree, eventPart.SpanStart,
                            (eventField.SpanStart, declarator.SpanStart));
                    }
                    else
                    {
                        AddKeyed(membersByTreeAndPart, syntax.SyntaxTree, eventPart.SpanStart,
                            (eventField.SpanStart, eventField.Span.End, eventField.Kind()));
                    }

                    continue;
                }

                if (syntax.Parent is not TypeDeclarationSyntax part)
                    continue;

                AddKeyed(membersByTreeAndPart, syntax.SyntaxTree, part.SpanStart,
                    (syntax.SpanStart, syntax.Span.End, syntax.Kind()));
            }
        }

        var trees = membersByTreeAndPart.Keys
            .Concat(eventDeclaratorsByTreeAndPart.Keys)
            .Distinct()
            .ToList();

        foreach (var tree in trees)
        {
            var document = GetDocumentForTree(solution, tree, typeSymbol.Name);
            var root = await document.GetSyntaxRootAsync(cancellationToken)
                ?? throw new RefactoringException(ErrorCodes.RoslynError, "Could not parse file.");

            membersByTreeAndPart.TryGetValue(tree, out var membersByPart);
            eventDeclaratorsByTreeAndPart.TryGetValue(tree, out var eventDeclaratorsByPart);

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

                HashSet<(int Start, int End, SyntaxKind Kind)>? keys = null;
                HashSet<(int FieldStart, int DeclaratorStart)>? eventKeys = null;
                if (membersByPart != null)
                    membersByPart.TryGetValue(part.SpanStart, out keys);
                if (eventDeclaratorsByPart != null)
                    eventDeclaratorsByPart.TryGetValue(part.SpanStart, out eventKeys);

                if ((keys == null || keys.Count == 0) && (eventKeys == null || eventKeys.Count == 0))
                    continue;

                var remainingMembers = new List<MemberDeclarationSyntax>();
                foreach (var member in part.Members)
                {
                    if (member is EventFieldDeclarationSyntax eventField
                        && eventKeys != null
                        && eventKeys.Count > 0)
                    {
                        var remainingVars = eventField.Declaration.Variables
                            .Where(v => !eventKeys.Contains((eventField.SpanStart, v.SpanStart)))
                            .ToList();
                        if (remainingVars.Count != eventField.Declaration.Variables.Count)
                        {
                            if (remainingVars.Count == 0)
                                continue;

                            remainingMembers.Add(eventField.WithDeclaration(
                                eventField.Declaration.WithVariables(SyntaxFactory.SeparatedList(remainingVars))));
                            continue;
                        }
                    }

                    if (keys != null && keys.Contains((member.SpanStart, member.Span.End, member.Kind())))
                        continue;

                    remainingMembers.Add(member);
                }

                replacements[part] = part.WithMembers(SyntaxFactory.List(remainingMembers));
            }

            if (replacements.Count == 0)
                continue;

            var newRoot = root.ReplaceNodes(replacements.Keys, (original, _) => replacements[original]);
            solution = solution.WithDocumentSyntaxRoot(document.Id, newRoot);
        }

        return solution;
    }

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

    private static void AddKeyed<T>(
        Dictionary<SyntaxTree, Dictionary<int, HashSet<T>>> map,
        SyntaxTree tree,
        int partSpanStart,
        T key)
    {
        if (!map.TryGetValue(tree, out var byPart))
        {
            byPart = new Dictionary<int, HashSet<T>>();
            map[tree] = byPart;
        }

        if (!byPart.TryGetValue(partSpanStart, out var keys))
        {
            keys = new HashSet<T>();
            byPart[partSpanStart] = keys;
        }

        keys.Add(key);
    }

    private static bool TryGetEventFieldDeclarator(
        SyntaxNode syntax,
        out EventFieldDeclarationSyntax eventField,
        out VariableDeclaratorSyntax declarator)
    {
        if (syntax is VariableDeclaratorSyntax variable
            && variable.Parent?.Parent is EventFieldDeclarationSyntax field)
        {
            eventField = field;
            declarator = variable;
            return true;
        }

        eventField = null!;
        declarator = null!;
        return false;
    }

    /// <summary>
    /// Finds a type by <paramref name="typeName"/>. Omitted
    /// <paramref name="column"/> keeps today's typeName + optional
    /// <paramref name="line"/> pick, including omitted-line
    /// <c>FirstOrDefault</c> and line-only exclusive-end coverage
    /// (<see cref="SpanCoversLine"/>). Do not force column 1 when omitted.
    /// Column without line keeps today's first-match after the typeName
    /// filter rather than substituting each candidate's own start line.
    /// When column is set with line, picks the type whose identifier or
    /// declaration span covers that 1-based column (same exclusive-end
    /// coverage as <c>AddNullChecksOperation.SpanCoversColumn</c>). Prefer
    /// the identifier hit, then the smallest containing type. Nested types
    /// participate (<c>DescendantNodes</c>). Do not require the declaration
    /// to start on <paramref name="line"/> when column is set — a split
    /// declaration may put the identifier on a continuation line. If column
    /// is set with line and nothing covers that position, return null
    /// (TypeNotFound) rather than falling back to first-match. After
    /// <see cref="RemoveExistingOverridesAcrossPartialsAsync"/>, recover the
    /// selected type from a per-execution annotation — do not reuse a
    /// pre-rewrite SpanStart or line.
    /// </summary>
    internal static TypeDeclarationSyntax? FindTypeDeclaration(
        SyntaxNode root,
        string typeName,
        int? line,
        int? column = null)
    {
        var candidates = root.DescendantNodes()
            .OfType<TypeDeclarationSyntax>()
            .Where(t => t.Identifier.Text == typeName)
            .ToList();

        if (candidates.Count == 0)
            return null;

        // Column without line is not a source position: substituting each
        // candidate's own start line would match every equally-aligned
        // same-name type and could silently pick the shortest. Keep
        // today's FirstOrDefault after the typeName filter.
        if (column.HasValue && !line.HasValue)
            return candidates.FirstOrDefault();

        if (column.HasValue)
        {
            // Do not require the declaration to start on `line` — a split
            // type's identifier may live on a continuation line whose
            // declaration span still covers that column. Prefer the
            // identifier hit, then the smallest containing type (nested
            // over outer). Do not silently pick the first when a covering
            // node exists elsewhere — scan every candidate. If nothing
            // covers this position, keep today's not-found (null) rather
            // than inventing a first-match.
            return candidates
                .Where(t => TypeCoversColumn(t, line!.Value, column.Value))
                .OrderBy(t => IdentifierCoversColumn(t, line!.Value, column.Value) ? 0 : 1)
                .ThenBy(t => t.Span.Length)
                .FirstOrDefault();
        }

        if (!line.HasValue)
            return candidates.FirstOrDefault();

        // Do not require the declaration to start on `line` — a split
        // type's identifier may live on a continuation line whose
        // declaration span still covers that line. Prefer the identifier
        // hit, then the smallest containing type (nested over outer).
        // Do not silently pick the first when a covering node exists
        // elsewhere — scan every candidate. If nothing covers this line,
        // keep today's first-match rather than inventing a not-found.
        return candidates
            .Where(t => TypeCoversLine(t, line.Value))
            .OrderBy(t => IdentifierCoversLine(t, line.Value) ? 0 : 1)
            .ThenBy(t => t.Span.Length)
            .FirstOrDefault()
            ?? candidates.FirstOrDefault();
    }

    private static bool TypeCoversLine(TypeDeclarationSyntax type, int line) =>
        IdentifierCoversLine(type, line) ||
        SpanCoversLine(type.GetLocation().GetLineSpan(), line);

    private static bool IdentifierCoversLine(TypeDeclarationSyntax type, int line) =>
        SpanCoversLine(type.Identifier.GetLocation().GetLineSpan(), line);

    private static bool TypeCoversColumn(TypeDeclarationSyntax type, int line, int column) =>
        IdentifierCoversColumn(type, line, column) ||
        SpanCoversColumn(type.GetLocation().GetLineSpan(), line, column);

    private static bool IdentifierCoversColumn(TypeDeclarationSyntax type, int line, int column) =>
        SpanCoversColumn(type.Identifier.GetLocation().GetLineSpan(), line, column);

    /// <summary>
    /// 1-based line/column coverage. <see cref="FileLinePositionSpan.EndLinePosition"/>
    /// is exclusive, so <paramref name="column"/> must be strictly before the
    /// exclusive end (reject <c>column &gt;= endCol</c>). Treating the end as
    /// inclusive would let the first character of an adjacent type also
    /// match the previous declaration. Same helper as
    /// <c>AddNullChecksOperation.SpanCoversColumn</c>.
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
    /// exclusive-end idea as <c>AddNullChecksOperation.SpanCoversColumn</c>.
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

    private static RefactoringResult CreatePreviewResult(
        Guid operationId,
        GenerateOverridesParams @params,
        List<ISymbol> membersToGenerate,
        List<ISymbol> membersToReplace,
        List<MemberDeclarationSyntax> overrides,
        List<ISymbol> selectedMembers)
    {
        var description = BuildPreviewDescription(membersToGenerate, membersToReplace, @params.CallBase, selectedMembers);
        var overrideCode = string.Join("\n\n",
            overrides.Select(o => o.NormalizeWhitespace().ToFullString()));

        var pendingChanges = new List<PendingChange>
        {
            new()
            {
                File = @params.SourceFile!,
                ChangeType = ChangeKind.Modify,
                Description = description,
                BeforeSnippet = membersToReplace.Count > 0
                    ? $"// Type '{@params.TypeName}' (replacing existing overrides)"
                    : $"// End of type '{@params.TypeName}'",
                AfterSnippet = overrideCode
            }
        };

        return RefactoringResult.PreviewResult(operationId, pendingChanges);
    }

    internal static string BuildPreviewDescription(
        IReadOnlyList<ISymbol> membersToGenerate,
        IReadOnlyList<ISymbol> membersToReplace,
        bool callBase = true,
        IReadOnlyList<ISymbol>? selectedMembers = null)
    {
        var generated = string.Join(", ", membersToGenerate.Select(m => m.Name));
        var replaced = string.Join(", ", membersToReplace.Select(m => m.Name));

        string description;
        if (membersToReplace.Count == 0)
            description = $"Generate overrides for: {generated}";
        else if (membersToGenerate.Count == 0)
            description = $"Replace existing overrides: {replaced}";
        else
            description = $"Generate overrides for: {generated}; replace existing overrides: {replaced}";

        var properties = (selectedMembers ?? membersToGenerate.Concat(membersToReplace))
            .OfType<IPropertySymbol>()
            .ToList();
        if (properties.Count > 0)
        {
            var anyNonAbstract = properties.Any(p => !p.IsAbstract);
            var anyAbstract = properties.Any(p => p.IsAbstract);
            if (!callBase || !anyNonAbstract)
                description += "; property accessors will not call base";
            else if (anyAbstract)
                description += "; non-abstract property accessors will call base";
            else
                description += "; property accessors will call base";
        }

        return description;
    }
}

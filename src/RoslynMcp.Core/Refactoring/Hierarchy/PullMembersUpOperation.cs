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
using RoslynMcp.Core.Workspace;

namespace RoslynMcp.Core.Refactoring.Hierarchy;

/// <summary>
/// Moves selected members from a derived type onto an existing base class or interface.
/// Honors optional <c>line</c> and <c>column</c> to disambiguate
/// same-named types in one file (identifier preferred, then smallest
/// containing type). Omitted column keeps today's typeName + optional
/// line pick. Omitted line keeps today's <c>TypeDeclarationSyntax</c>
/// <c>FirstOrDefault</c> pick (enum and
/// <c>DelegateDeclarationSyntax</c> do not participate).
/// When line is set, a covering enum or delegate is included so it
/// reaches <c>InvalidSymbolKind</c> rather than retargeting a later class.
/// After the pull (derived rewrite + members added to the target), the
/// selected declaration is recovered by a per-execution syntax annotation
/// (stripped before commit).
/// </summary>
public sealed class PullMembersUpOperation : RefactoringOperationBase<PullMembersUpParams>
{
    /// <summary>
    /// Creates a new pull-members-up operation.
    /// </summary>
    public PullMembersUpOperation(WorkspaceContext context) : base(context)
    {
    }

    /// <inheritdoc />
    protected override void ValidateParams(PullMembersUpParams @params) => Validate(@params);

    /// <summary>
    /// Validates pull-members-up parameters. Internal so tests can exercise
    /// input rules without loading a workspace.
    /// </summary>
    internal static void Validate(PullMembersUpParams @params)
    {
        if (string.IsNullOrWhiteSpace(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "sourceFile is required.");

        if (string.IsNullOrWhiteSpace(@params.TypeName))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "typeName is required.");

        if (@params.Members == null || @params.Members.Count == 0 || @params.Members.All(string.IsNullOrWhiteSpace))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "members is required.");

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
        PullMembersUpParams @params,
        CancellationToken cancellationToken)
    {
        var document = GetDocumentOrThrow(@params.SourceFile);
        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);

        if (root == null || semanticModel == null)
            throw new RefactoringException(ErrorCodes.RoslynError, "Could not parse file.");

        // Optional line/column disambiguates same-named types. Omitted
        // column keeps today's TypeDeclarationSyntax FirstOrDefault
        // pick (enum and DelegateDeclarationSyntax do not participate).
        // Line set also includes a covering enum or delegate so it
        // reaches InvalidSymbolKind instead of retargeting a later class.
        var found = FindTypeDeclaration(root, @params.TypeName, @params.Line, @params.Column);
        if (found == null)
        {
            throw new RefactoringException(
                ErrorCodes.TypeNotFound,
                $"Type '{@params.TypeName}' not found in file.");
        }

        var derivedSymbol = semanticModel.GetDeclaredSymbol(found, cancellationToken) as INamedTypeSymbol;
        if (derivedSymbol == null)
            throw new RefactoringException(ErrorCodes.RoslynError, "Could not resolve type symbol.");

        if (found is not TypeDeclarationSyntax derivedDecl)
        {
            throw new RefactoringException(
                ErrorCodes.InvalidSymbolKind,
                $"Type '{derivedSymbol.Name}' is not a supported target for pull_members_up.");
        }

        var target = GetTargetBaseType(derivedSymbol, @params.TargetBaseType);
        ValidateTarget(target);

        var members = FindMembersToPull(derivedDecl, @params.Members, semanticModel, target, cancellationToken);
        ValidateMembersForPull(members, derivedSymbol, target, semanticModel, @params.MakeAbstract);

        var pulledNames = members.Select(m => m.Name).ToList();
        var targetMembers = members
            .Select(m => ConvertForTarget(m.Syntax, m.Name, target, @params.MakeAbstract))
            .ToList();
        var derivedReplacement = BuildDerivedReplacement(
            derivedDecl,
            members,
            target,
            @params.MakeAbstract);

        if (@params.Preview)
        {
            return CreatePreviewResult(
                operationId,
                @params,
                target,
                pulledNames,
                targetMembers,
                derivedDecl,
                derivedReplacement);
        }

        // Fresh instance per execution. A static annotation is shared
        // across operations; after CommitChanges the in-memory solution
        // can still carry it, so a later pull on another type would
        // recover the stale node via FirstOrDefault.
        // Annotate before the rewrite. Same-file pull rewrites the
        // derived type and adds members to the target — both shift
        // later same-named types. Do not re-find with stale SpanStart
        // or line. Today's FindTypeInRoot preferred-span rematch is
        // not enough by itself.
        var targetTypeAnnotation = new SyntaxAnnotation("pull-members-up-target-type");
        var previousTree = root.SyntaxTree;
        root = root.ReplaceNode(
            derivedDecl,
            derivedDecl.WithAdditionalAnnotations(targetTypeAnnotation));
        document = document.WithSyntaxRoot(root);
        var annotatedSolution = document.Project.Solution;
        // If GetDocument(oldTree) misses after the annotation rewrite,
        // look up by file path and rematch by span (same as
        // extract_base_class / implement_abstract).
        document = annotatedSolution.GetDocument(previousTree)
            ?? GetDocumentForTree(annotatedSolution, previousTree, @params.TypeName);
        root = await document.GetSyntaxRootAsync(cancellationToken)
            ?? throw new RefactoringException(ErrorCodes.RoslynError, "Could not parse file.");
        derivedDecl = RecoverAnnotatedType(
            root,
            targetTypeAnnotation,
            derivedDecl,
            @params.TypeName);

        // Strip the per-execution annotation so it does not linger in the
        // workspace after commit.
        derivedReplacement = (TypeDeclarationSyntax)derivedReplacement.WithoutAnnotations(targetTypeAnnotation);

        var solution = await ApplyChangesAsync(
            document,
            derivedDecl,
            derivedReplacement,
            target,
            targetMembers,
            @params.MakeAbstract,
            cancellationToken);

        var commitResult = await CommitChangesAsync(solution, cancellationToken);

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
                Name = target.Name,
                FullyQualifiedName = target.ToDisplayString(),
                Kind = target.TypeKind == TypeKind.Interface
                    ? Contracts.Enums.SymbolKind.Interface
                    : Contracts.Enums.SymbolKind.Class
            },
            0,
            0);
    }

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
    /// <c>ExtractInterfaceOperation.SpanCoversColumn</c> /
    /// <c>ExtractBaseClassOperation.SpanCoversColumn</c> /
    /// <c>GenerateToStringOperation.SpanCoversColumn</c>). Prefer the
    /// identifier hit, then the smallest containing type. Nested types,
    /// enums, and <c>DelegateDeclarationSyntax</c> participate when line
    /// is set so a covering enum or delegate still reaches
    /// <c>InvalidSymbolKind</c> rather than retargeting a later class. Do
    /// not require the declaration to start on <paramref name="line"/>
    /// when column is set — a split declaration may put the identifier on
    /// a continuation line. If column is set with line and nothing covers
    /// that position, return null (TypeNotFound) rather than falling back
    /// to first-match. After the pull (derived rewrite + members added to
    /// the target), recover the selected type from the per-execution
    /// syntax annotation — do not reuse a pre-rewrite SpanStart or line.
    /// </summary>
    internal static MemberDeclarationSyntax? FindTypeDeclaration(
        SyntaxNode root,
        string typeName,
        int? line,
        int? column = null)
    {
        var simpleName = typeName.Contains('.')
            ? typeName[(typeName.LastIndexOf('.') + 1)..]
            : typeName;

        var typeCandidates = root.DescendantNodes()
            .OfType<TypeDeclarationSyntax>()
            .Where(t => t.Identifier.Text == simpleName)
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
                .Where(t => t.Identifier.Text == simpleName)
                .Cast<MemberDeclarationSyntax>()
                .Concat(root.DescendantNodes()
                    .OfType<DelegateDeclarationSyntax>()
                    .Where(d => d.Identifier.Text == simpleName))
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
    /// <c>ExtractInterfaceOperation.SpanCoversColumn</c> /
    /// <c>ExtractBaseClassOperation.SpanCoversColumn</c> /
    /// <c>GenerateToStringOperation.SpanCoversColumn</c>.
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
    /// exclusive-end idea as <c>ExtractInterfaceOperation.SpanCoversLine</c>.
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

    private static TypeDeclarationSyntax RecoverAnnotatedType(
        SyntaxNode root,
        SyntaxAnnotation targetTypeAnnotation,
        TypeDeclarationSyntax original,
        string typeName)
    {
        var annotated = root.GetAnnotatedNodes(targetTypeAnnotation)
            .OfType<TypeDeclarationSyntax>()
            .FirstOrDefault();
        if (annotated != null)
            return annotated;

        return RematchTypeDeclaration(root, original)
            ?? throw new RefactoringException(
                ErrorCodes.TypeNotFound,
                $"Type '{typeName}' not found in file.");
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

    private static TypeDeclarationSyntax? RematchTypeDeclaration(
        SyntaxNode root,
        TypeDeclarationSyntax original) =>
        root.DescendantNodes()
            .OfType<TypeDeclarationSyntax>()
            .FirstOrDefault(t => t.SpanStart == original.SpanStart && t.Identifier.Text == original.Identifier.Text);

    internal static INamedTypeSymbol GetTargetBaseType(INamedTypeSymbol derived, string? targetTypeName)
    {
        var classBases = new List<INamedTypeSymbol>();
        for (var baseType = derived.BaseType; baseType != null; baseType = baseType.BaseType)
        {
            if (baseType.SpecialType == SpecialType.System_Object)
                break;
            classBases.Add(baseType);
        }

        var interfaces = derived.AllInterfaces.ToList();

        if (string.IsNullOrWhiteSpace(targetTypeName))
        {
            if (classBases.Count > 0)
                return classBases[0];

            if (derived.Interfaces.Length == 1)
                return derived.Interfaces[0];

            if (derived.Interfaces.Length > 1)
            {
                throw new RefactoringException(
                    ErrorCodes.NoCommonBase,
                    $"Type '{derived.Name}' implements multiple interfaces; specify targetBaseType.");
            }

            throw new RefactoringException(
                ErrorCodes.NoCommonBase,
                $"Type '{derived.Name}' has no base class or interface to pull members to.");
        }

        var candidates = classBases.Concat(interfaces);
        var match = candidates.FirstOrDefault(candidate =>
            candidate.Name.Equals(targetTypeName, StringComparison.Ordinal) ||
            candidate.ToDisplayString().Equals(targetTypeName, StringComparison.Ordinal));

        if (match == null)
        {
            throw new RefactoringException(
                ErrorCodes.BaseClassNotFound,
                $"Type '{targetTypeName}' is not a base class or interface of '{derived.Name}'.");
        }

        return match;
    }

    internal static void ValidateTarget(INamedTypeSymbol target)
    {
        if (target.TypeKind == TypeKind.Class && target.IsSealed)
        {
            throw new RefactoringException(
                ErrorCodes.BaseClassIsSealed,
                $"Base class '{target.Name}' is sealed.");
        }

        if (!target.Locations.Any(location => location.IsInSource))
        {
            throw new RefactoringException(
                ErrorCodes.BaseClassNotEditable,
                $"Base type '{target.Name}' is not editable (defined in an external assembly).");
        }
    }

    private static List<PullableMember> FindMembersToPull(
        TypeDeclarationSyntax typeDeclaration,
        IReadOnlyList<string> memberNames,
        SemanticModel semanticModel,
        INamedTypeSymbol target,
        CancellationToken cancellationToken)
    {
        var requested = new HashSet<string>(memberNames.Where(n => !string.IsNullOrWhiteSpace(n)));
        var unmatched = new HashSet<string>(requested);
        var found = new List<PullableMember>();

        foreach (var (name, symbol, syntax) in EnumerateDeclaredMembers(typeDeclaration, semanticModel, cancellationToken))
        {
            if (symbol is IPropertySymbol { IsIndexer: true } indexer)
            {
                // Indexers match metadata name (Item), Roslyn name (this[]),
                // and conventional display (this[int i]) — same identity
                // forms as implement_interface / extract_interface /
                // extract_base_class. Explicit interface implementations
                // are skipped when the target does not implement that
                // interface: copying IFoo.this[...] onto a base that does
                // not implement IFoo is CS0540.
                if (IsExplicitInterfaceIndexer(indexer, syntax)
                    && !TargetSupportsExplicitInterface(target, indexer))
                {
                    continue;
                }

                if (!ImplementInterfaceOperation.MatchesRequestedMember(indexer, requested))
                    continue;

                if (!IsSupportedMember(indexer))
                {
                    throw new RefactoringException(
                        ErrorCodes.MemberNotMoveable,
                        $"Member '{indexer.Name}' cannot be pulled up.");
                }

                found.Add(new PullableMember(indexer.Name, indexer, syntax));
                foreach (var request in unmatched.ToList())
                {
                    if (ImplementInterfaceOperation.MatchesRequestedMember(
                            indexer, new HashSet<string> { request }))
                    {
                        unmatched.Remove(request);
                    }
                }

                continue;
            }

            if (!requested.Contains(name))
                continue;

            if (symbol == null)
            {
                throw new RefactoringException(
                    ErrorCodes.RoslynError,
                    $"Could not resolve symbol for member '{name}'.");
            }

            if (!IsSupportedMember(symbol))
            {
                throw new RefactoringException(
                    ErrorCodes.MemberNotMoveable,
                    $"Member '{name}' cannot be pulled up.");
            }

            found.Add(new PullableMember(name, symbol, syntax));
            unmatched.Remove(name);
        }

        if (unmatched.Count > 0)
        {
            throw new RefactoringException(
                ErrorCodes.MemberNotFound,
                $"Members not found: {string.Join(", ", unmatched)}");
        }

        return found;
    }

    private static IEnumerable<(string Name, ISymbol? Symbol, MemberDeclarationSyntax Syntax)> EnumerateDeclaredMembers(
        TypeDeclarationSyntax typeDeclaration,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        foreach (var member in typeDeclaration.Members)
        {
            switch (member)
            {
                case MethodDeclarationSyntax method:
                    yield return (method.Identifier.Text, semanticModel.GetDeclaredSymbol(method, cancellationToken), method);
                    break;
                case PropertyDeclarationSyntax property:
                    yield return (property.Identifier.Text, semanticModel.GetDeclaredSymbol(property, cancellationToken), property);
                    break;
                case IndexerDeclarationSyntax indexer:
                    yield return ("this[]", semanticModel.GetDeclaredSymbol(indexer, cancellationToken), indexer);
                    break;
                case FieldDeclarationSyntax field:
                    foreach (var variable in field.Declaration.Variables)
                    {
                        yield return (variable.Identifier.Text, semanticModel.GetDeclaredSymbol(variable, cancellationToken), field);
                    }
                    break;
                case EventFieldDeclarationSyntax eventField:
                    foreach (var variable in eventField.Declaration.Variables)
                    {
                        yield return (variable.Identifier.Text, semanticModel.GetDeclaredSymbol(variable, cancellationToken), eventField);
                    }
                    break;
                case EventDeclarationSyntax eventDecl:
                    yield return (eventDecl.Identifier.Text, semanticModel.GetDeclaredSymbol(eventDecl, cancellationToken), eventDecl);
                    break;
            }
        }
    }

    private static bool IsSupportedMember(ISymbol symbol) => symbol switch
    {
        IMethodSymbol method => method.MethodKind == MethodKind.Ordinary,
        IPropertySymbol => true,
        IFieldSymbol => true,
        IEventSymbol => true,
        _ => false
    };

    private static void ValidateMembersForPull(
        IReadOnlyList<PullableMember> members,
        INamedTypeSymbol derived,
        INamedTypeSymbol target,
        SemanticModel semanticModel,
        bool makeAbstract)
    {
        var pulledNames = members.Select(m => m.Name).ToHashSet();

        foreach (var member in members)
        {
            if (makeAbstract &&
                target.TypeKind != TypeKind.Interface &&
                !CanPullAsAbstract(member.Symbol))
            {
                throw new RefactoringException(
                    ErrorCodes.MemberNotMoveable,
                    member.Symbol is IEventSymbol
                        ? $"Event '{member.Name}' cannot be pulled up as an abstract member."
                        : member.Symbol is IPropertySymbol { IsIndexer: true }
                            ? $"Indexer '{member.Name}' cannot be pulled up as an abstract member."
                            : "Only methods, properties, indexers, and events can be pulled up as abstract members.");
            }

            if (HasConflict(target, member.Symbol))
            {
                throw new RefactoringException(
                    ErrorCodes.ConflictsWithExistingMember,
                    $"Member '{member.Name}' already exists in '{target.Name}'.");
            }

            if (target.TypeKind == TypeKind.Interface && !IsInterfaceCompatible(member.Symbol))
            {
                throw new RefactoringException(
                    ErrorCodes.MemberNotInterfaceCompatible,
                    $"Member '{member.Name}' cannot be pulled to interface '{target.Name}'.");
            }

            var dependency = FindDerivedOnlyDependency(member, derived, pulledNames, members, semanticModel);
            if (dependency != null)
            {
                throw new RefactoringException(
                    ErrorCodes.MemberDependsOnDerived,
                    $"Member '{member.Name}' depends on '{dependency}' which is not accessible from '{target.Name}'.");
            }
        }
    }

    private static bool HasConflict(INamedTypeSymbol target, ISymbol member)
    {
        foreach (var existing in target.GetMembers(member.Name))
        {
            if (existing.IsImplicitlyDeclared)
                continue;

            if (member is IMethodSymbol method && existing is IMethodSymbol existingMethod)
            {
                if (SignaturesMatch(method, existingMethod))
                    return true;
                continue;
            }

            if (member is IPropertySymbol { IsIndexer: true } indexer
                && existing is IPropertySymbol { IsIndexer: true } existingIndexer)
            {
                if (IndexerSignaturesMatch(indexer, existingIndexer))
                    return true;
                continue;
            }

            return true;
        }

        return false;
    }

    private static bool SignaturesMatch(IMethodSymbol left, IMethodSymbol right)
    {
        if (left.Parameters.Length != right.Parameters.Length)
            return false;

        if (left.TypeParameters.Length != right.TypeParameters.Length)
            return false;

        for (var i = 0; i < left.Parameters.Length; i++)
        {
            if (!SymbolEqualityComparer.Default.Equals(left.Parameters[i].Type, right.Parameters[i].Type))
                return false;
            if (left.Parameters[i].RefKind != right.Parameters[i].RefKind)
                return false;
        }

        return true;
    }

    private static bool IndexerSignaturesMatch(IPropertySymbol left, IPropertySymbol right)
    {
        if (left.Parameters.Length != right.Parameters.Length)
            return false;

        for (var i = 0; i < left.Parameters.Length; i++)
        {
            if (!SymbolEqualityComparer.Default.Equals(left.Parameters[i].Type, right.Parameters[i].Type))
                return false;
            if (left.Parameters[i].RefKind != right.Parameters[i].RefKind)
                return false;
        }

        return true;
    }

    private static bool IsExplicitInterfaceIndexer(IPropertySymbol indexer, MemberDeclarationSyntax syntax) =>
        indexer.ExplicitInterfaceImplementations.Length > 0
        || (syntax is IndexerDeclarationSyntax declaration && declaration.ExplicitInterfaceSpecifier != null);

    private static bool TargetSupportsExplicitInterface(INamedTypeSymbol target, IPropertySymbol indexer)
    {
        foreach (var implemented in indexer.ExplicitInterfaceImplementations)
        {
            var iface = implemented.ContainingType;
            if (SymbolEqualityComparer.Default.Equals(target, iface))
                return true;

            if (target.AllInterfaces.Any(candidate =>
                    SymbolEqualityComparer.Default.Equals(candidate, iface)))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsInterfaceCompatible(ISymbol member)
    {
        if (member.IsStatic)
            return false;

        if (member.DeclaredAccessibility != Accessibility.Public)
            return false;

        return member switch
        {
            IMethodSymbol method => method.MethodKind == MethodKind.Ordinary,
            IPropertySymbol => true,
            IEventSymbol => true,
            _ => false
        };
    }

    private static bool CanPullAsAbstract(ISymbol symbol) => symbol switch
    {
        IMethodSymbol method => method.MethodKind == MethodKind.Ordinary,
        IPropertySymbol property => !property.IsIndexer || CanPullIndexerAsAbstract(property),
        IEventSymbol evt => !evt.IsStatic && evt.ExplicitInterfaceImplementations.Length == 0,
        _ => false
    };

    private static bool CanPullIndexerAsAbstract(IPropertySymbol indexer)
    {
        if (indexer.IsStatic || indexer.ExplicitInterfaceImplementations.Length > 0)
            return false;

        // A wholly private indexer is lifted to protected; implicit
        // accessors follow. An explicit private accessor on a more
        // visible indexer cannot become abstract (CS0621) and cannot
        // stay on the override if the base drops it (CS0546).
        if (indexer.DeclaredAccessibility == Accessibility.Private)
            return true;

        return indexer.GetMethod?.DeclaredAccessibility != Accessibility.Private
            && indexer.SetMethod?.DeclaredAccessibility != Accessibility.Private;
    }

    private static string? FindDerivedOnlyDependency(
        PullableMember member,
        INamedTypeSymbol derived,
        HashSet<string> pulledNames,
        IReadOnlyList<PullableMember> pulledMembers,
        SemanticModel semanticModel)
    {
        foreach (var node in member.Syntax.DescendantNodes())
        {
            ISymbol? referenced = node switch
            {
                IdentifierNameSyntax identifier when !IsUnselectedEventDeclaratorIdentifier(member, identifier) =>
                    semanticModel.GetSymbolInfo(identifier).Symbol,
                ElementAccessExpressionSyntax access => semanticModel.GetSymbolInfo(access).Symbol,
                _ => null
            };

            referenced = AssociatedMemberSymbol(referenced);
            if (referenced == null)
                continue;

            if (referenced is IParameterSymbol or ILocalSymbol or ITypeParameterSymbol)
                continue;

            if (referenced.ContainingType == null)
                continue;

            if (!SymbolEqualityComparer.Default.Equals(referenced.ContainingType, derived))
                continue;

            if (referenced.Kind is not (Microsoft.CodeAnalysis.SymbolKind.Field
                or Microsoft.CodeAnalysis.SymbolKind.Method
                or Microsoft.CodeAnalysis.SymbolKind.Property
                or Microsoft.CodeAnalysis.SymbolKind.Event))
                continue;

            if (SymbolEqualityComparer.Default.Equals(referenced, member.Symbol))
                continue;

            if (referenced is IMethodSymbol referencedMethod &&
                member.Symbol is IMethodSymbol memberMethod &&
                SignaturesMatch(referencedMethod, memberMethod) &&
                referenced.Name == member.Name)
            {
                continue;
            }

            // Indexers share the Roslyn name this[]. Name matching would
            // treat an unselected overload as already pulled. Match the
            // selected indexer by symbol instead.
            if (referenced is IPropertySymbol { IsIndexer: true } referencedIndexer)
            {
                if (pulledMembers.Any(pulled =>
                        SymbolEqualityComparer.Default.Equals(pulled.Symbol, referencedIndexer)))
                {
                    continue;
                }

                return referencedIndexer.Name;
            }

            if (pulledNames.Contains(referenced.Name))
                continue;

            return referenced.Name;
        }

        return null;
    }

    private static ISymbol? AssociatedMemberSymbol(ISymbol? symbol)
    {
        if (symbol is IMethodSymbol accessor
            && accessor.AssociatedSymbol is IPropertySymbol or IEventSymbol
            && accessor.MethodKind is MethodKind.PropertyGet
                or MethodKind.PropertySet
                or MethodKind.EventAdd
                or MethodKind.EventRemove)
        {
            return accessor.AssociatedSymbol;
        }

        return symbol;
    }

    private static bool IsUnselectedEventDeclaratorIdentifier(
        PullableMember member,
        IdentifierNameSyntax identifier)
    {
        if (member.Syntax is not EventFieldDeclarationSyntax eventField)
            return false;

        if (eventField.Declaration.Variables.Count <= 1)
            return false;

        var declarator = identifier.FirstAncestorOrSelf<VariableDeclaratorSyntax>();
        return declarator != null && declarator.Identifier.Text != member.Name;
    }

    private static MemberDeclarationSyntax ConvertForTarget(
        MemberDeclarationSyntax member,
        string name,
        INamedTypeSymbol target,
        bool makeAbstract)
    {
        var isolated = IsolateMemberSyntax(member, name);

        if (target.TypeKind == TypeKind.Interface)
            return ConvertToInterfaceMember(isolated);

        return makeAbstract
            ? HierarchyAbstractMemberRewriter.ConvertToAbstract(
                isolated,
                "Only methods, properties, indexers, and events can be pulled up as abstract members.")
            : ConvertToVirtualOnBase(isolated);
    }

    /// <summary>
    /// Keeps only the requested declarator when an event field declares
    /// multiple variables.
    /// </summary>
    private static MemberDeclarationSyntax IsolateMemberSyntax(MemberDeclarationSyntax syntax, string name) =>
        HierarchyAbstractMemberRewriter.IsolateMemberSyntax(syntax, name);

    private static MemberDeclarationSyntax ConvertToInterfaceMember(MemberDeclarationSyntax member)
    {
        return member switch
        {
            MethodDeclarationSyntax method => method
                .WithModifiers(SyntaxFactory.TokenList())
                .WithBody(null)
                .WithExpressionBody(null)
                .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken))
                .NormalizeWhitespace(),
            PropertyDeclarationSyntax property => ToInterfaceProperty(property),
            IndexerDeclarationSyntax indexer => ToInterfaceIndexer(indexer),
            EventDeclarationSyntax eventDecl => eventDecl
                .WithModifiers(SyntaxFactory.TokenList())
                .WithAccessorList(null)
                .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken))
                .NormalizeWhitespace(),
            EventFieldDeclarationSyntax eventField => SyntaxFactory.EventFieldDeclaration(eventField.Declaration)
                .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken))
                .NormalizeWhitespace(),
            _ => throw new RefactoringException(
                ErrorCodes.MemberNotInterfaceCompatible,
                "Member cannot be declared on an interface.")
        };
    }

    private static PropertyDeclarationSyntax ToInterfaceProperty(PropertyDeclarationSyntax property)
    {
        var accessors = new List<AccessorDeclarationSyntax>();
        if (property.AccessorList != null)
        {
            foreach (var accessor in property.AccessorList.Accessors)
            {
                accessors.Add(accessor
                    .WithModifiers(SyntaxFactory.TokenList())
                    .WithBody(null)
                    .WithExpressionBody(null)
                    .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)));
            }
        }
        else
        {
            accessors.Add(SyntaxFactory.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration)
                .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)));
        }

        return property
            .WithModifiers(SyntaxFactory.TokenList())
            .WithExpressionBody(null)
            .WithSemicolonToken(default)
            .WithAccessorList(SyntaxFactory.AccessorList(SyntaxFactory.List(accessors)))
            .NormalizeWhitespace();
    }

    private static IndexerDeclarationSyntax ToInterfaceIndexer(IndexerDeclarationSyntax indexer)
    {
        var accessors = new List<AccessorDeclarationSyntax>();
        if (indexer.AccessorList != null)
        {
            foreach (var accessor in indexer.AccessorList.Accessors)
            {
                // Same public-accessor gate as extract_interface
                // CreateInterfaceIndexer: a private/protected/internal
                // setter must not become a public interface set;.
                if (HasNonPublicAccessibility(accessor))
                    continue;

                accessors.Add(accessor
                    .WithModifiers(SyntaxFactory.TokenList())
                    .WithBody(null)
                    .WithExpressionBody(null)
                    .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)));
            }
        }
        else
        {
            accessors.Add(SyntaxFactory.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration)
                .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)));
        }

        if (accessors.Count == 0)
        {
            throw new RefactoringException(
                ErrorCodes.MemberNotInterfaceCompatible,
                "Indexer cannot be pulled to an interface because it has no public accessors.");
        }

        return indexer
            .WithModifiers(SyntaxFactory.TokenList())
            .WithExplicitInterfaceSpecifier(null)
            .WithExpressionBody(null)
            .WithSemicolonToken(default)
            .WithAccessorList(SyntaxFactory.AccessorList(SyntaxFactory.List(accessors)))
            .NormalizeWhitespace();
    }

    private static bool HasNonPublicAccessibility(AccessorDeclarationSyntax accessor) =>
        accessor.Modifiers.Any(token =>
            token.IsKind(SyntaxKind.PrivateKeyword)
            || token.IsKind(SyntaxKind.ProtectedKeyword)
            || token.IsKind(SyntaxKind.InternalKeyword));

    private static MemberDeclarationSyntax ConvertToVirtualOnBase(MemberDeclarationSyntax member)
    {
        return member switch
        {
            MethodDeclarationSyntax method => method
                .WithModifiers(AdjustBaseClassModifiers(method.Modifiers, addVirtual: !method.Modifiers.Any(SyntaxKind.StaticKeyword)))
                .NormalizeWhitespace(),
            PropertyDeclarationSyntax property => property
                .WithModifiers(AdjustBaseClassModifiers(property.Modifiers, addVirtual: !property.Modifiers.Any(SyntaxKind.StaticKeyword)))
                .NormalizeWhitespace(),
            IndexerDeclarationSyntax indexer => indexer
                .WithModifiers(AdjustBaseClassModifiers(
                    indexer.Modifiers,
                    addVirtual: !indexer.Modifiers.Any(SyntaxKind.StaticKeyword)
                        && indexer.ExplicitInterfaceSpecifier == null))
                .NormalizeWhitespace(),
            FieldDeclarationSyntax field => field
                .WithModifiers(AdjustBaseClassModifiers(field.Modifiers, addVirtual: false))
                .NormalizeWhitespace(),
            EventFieldDeclarationSyntax eventField => eventField
                .WithModifiers(AdjustBaseClassModifiers(eventField.Modifiers, addVirtual: false))
                .NormalizeWhitespace(),
            EventDeclarationSyntax eventDecl => eventDecl
                .WithModifiers(AdjustBaseClassModifiers(eventDecl.Modifiers, addVirtual: !eventDecl.Modifiers.Any(SyntaxKind.StaticKeyword)))
                .NormalizeWhitespace(),
            _ => member.NormalizeWhitespace()
        };
    }

    private static SyntaxTokenList AdjustBaseClassModifiers(SyntaxTokenList modifiers, bool addVirtual)
    {
        var tokens = StripModifiers(
                modifiers,
                SyntaxKind.PrivateKeyword,
                SyntaxKind.NewKeyword)
            .ToList();

        if (modifiers.Any(SyntaxKind.PrivateKeyword) || !HasAccessibility(tokens))
            tokens.Insert(0, SyntaxFactory.Token(SyntaxKind.ProtectedKeyword));

        var alreadyOverridable = tokens.Any(t =>
            t.IsKind(SyntaxKind.VirtualKeyword) ||
            t.IsKind(SyntaxKind.OverrideKeyword) ||
            t.IsKind(SyntaxKind.AbstractKeyword));

        if (addVirtual && !alreadyOverridable)
            tokens.Add(SyntaxFactory.Token(SyntaxKind.VirtualKeyword));

        return SyntaxFactory.TokenList(tokens);
    }

    private static IEnumerable<SyntaxToken> StripModifiers(SyntaxTokenList modifiers, params SyntaxKind[] kinds)
    {
        var kindSet = kinds.ToHashSet();
        return modifiers.Where(token => !kindSet.Contains(token.Kind()));
    }

    private static bool HasAccessibility(IEnumerable<SyntaxToken> modifiers) =>
        modifiers.Any(token =>
            token.IsKind(SyntaxKind.PublicKeyword) ||
            token.IsKind(SyntaxKind.ProtectedKeyword) ||
            token.IsKind(SyntaxKind.InternalKeyword) ||
            token.IsKind(SyntaxKind.PrivateKeyword));

    private static TypeDeclarationSyntax BuildDerivedReplacement(
        TypeDeclarationSyntax derivedDecl,
        IReadOnlyList<PullableMember> members,
        INamedTypeSymbol target,
        bool makeAbstract)
    {
        var pulledBySyntax = members
            .GroupBy(member => member.Syntax)
            .ToDictionary(group => group.Key, group => group.ToList());
        var keepAsOverride = makeAbstract && target.TypeKind != TypeKind.Interface;
        var newMembers = new List<MemberDeclarationSyntax>();

        foreach (var member in derivedDecl.Members)
        {
            if (!pulledBySyntax.TryGetValue(member, out var pulled))
            {
                newMembers.Add(member);
                continue;
            }

            if (target.TypeKind == TypeKind.Interface)
            {
                newMembers.Add(member);
                continue;
            }

            if (TryKeepRemainingEventDeclarators(member, pulled, out var remaining))
            {
                newMembers.Add(remaining);
                if (keepAsOverride)
                {
                    foreach (var pulledMember in pulled)
                    {
                        newMembers.Add(AddOverrideModifier(
                            IsolateMemberSyntax(member, pulledMember.Name),
                            pulledMember.Symbol,
                            target));
                    }
                }

                continue;
            }

            if (keepAsOverride)
            {
                newMembers.Add(AddOverrideModifier(member, pulled[0].Symbol, target));
            }
        }

        return derivedDecl.WithMembers(SyntaxFactory.List(newMembers));
    }

    private static bool TryKeepRemainingEventDeclarators(
        MemberDeclarationSyntax member,
        IReadOnlyList<PullableMember> pulled,
        out MemberDeclarationSyntax remaining)
    {
        remaining = member;
        if (member is not EventFieldDeclarationSyntax eventField)
            return false;

        var pulledNames = pulled.Select(p => p.Name).ToHashSet();
        var keep = eventField.Declaration.Variables
            .Where(variable => !pulledNames.Contains(variable.Identifier.Text))
            .ToList();
        if (keep.Count == 0)
            return false;

        remaining = eventField.WithDeclaration(
            eventField.Declaration.WithVariables(SyntaxFactory.SeparatedList(keep)));
        return true;
    }

    /// <summary>
    /// Keeps an override on the derived type after <c>makeAbstract</c>.
    /// <paramref name="target"/> is the destination base: the member
    /// symbol still lives on the derived type, so comparing that
    /// assembly against the base is what triggers CS0507 reduction
    /// (same helpers as <see cref="PushMembersDownOperation"/>).
    /// </summary>
    private static MemberDeclarationSyntax AddOverrideModifier(
        MemberDeclarationSyntax member,
        ISymbol symbol,
        INamedTypeSymbol target)
        => HierarchyAbstractMemberRewriter.AddOverrideModifier(member, symbol, target);

    private async Task<Solution> ApplyChangesAsync(
        Document derivedDocument,
        TypeDeclarationSyntax derivedDecl,
        TypeDeclarationSyntax newDerived,
        INamedTypeSymbol target,
        IReadOnlyList<MemberDeclarationSyntax> targetMembers,
        bool makeAbstract,
        CancellationToken cancellationToken)
    {
        var syntaxRef = target.DeclaringSyntaxReferences.FirstOrDefault();
        if (syntaxRef == null)
        {
            throw new RefactoringException(
                ErrorCodes.BaseClassNotEditable,
                $"Base type '{target.Name}' is not editable (defined in an external assembly).");
        }

        var targetSyntax = await syntaxRef.GetSyntaxAsync(cancellationToken) as TypeDeclarationSyntax;
        if (targetSyntax == null)
        {
            throw new RefactoringException(
                ErrorCodes.RoslynError,
                $"Could not locate declaration for '{target.Name}'.");
        }

        var newTarget = AddMembersToTarget(targetSyntax, targetMembers, makeAbstract && target.TypeKind != TypeKind.Interface);

        var solution = derivedDocument.Project.Solution;
        // After WithSyntaxRoot (annotation), GetDocument(oldTree) can miss
        // when the target lives in the same file. Look up by file path
        // rather than treating the miss as a non-editable base.
        var targetDocument = solution.GetDocument(syntaxRef.SyntaxTree)
            ?? GetDocumentByFilePath(solution, syntaxRef.SyntaxTree);
        if (targetDocument == null)
        {
            throw new RefactoringException(
                ErrorCodes.BaseClassNotEditable,
                $"Base type '{target.Name}' is not part of the workspace.");
        }

        if (targetDocument.Id == derivedDocument.Id)
        {
            var root = await derivedDocument.GetSyntaxRootAsync(cancellationToken);
            if (root == null)
                throw new RefactoringException(ErrorCodes.RoslynError, "Could not parse file.");

            var targetInRoot = FindTypeInRoot(root, target.Name, targetSyntax.Span);
            var updatedTarget = AddMembersToTarget(targetInRoot, targetMembers, makeAbstract && target.TypeKind != TypeKind.Interface);
            var newRoot = root.ReplaceNodes(
                new SyntaxNode[] { derivedDecl, targetInRoot },
                (original, _) => original == derivedDecl ? newDerived : updatedTarget);

            return derivedDocument.WithSyntaxRoot(newRoot).Project.Solution;
        }

        var derivedRoot = await derivedDocument.GetSyntaxRootAsync(cancellationToken);
        var targetRoot = await targetDocument.GetSyntaxRootAsync(cancellationToken);
        if (derivedRoot == null || targetRoot == null)
            throw new RefactoringException(ErrorCodes.RoslynError, "Could not parse file.");

        solution = derivedDocument.WithSyntaxRoot(derivedRoot.ReplaceNode(derivedDecl, newDerived)).Project.Solution;
        targetDocument = solution.GetDocument(targetDocument.Id)!;
        targetRoot = await targetDocument.GetSyntaxRootAsync(cancellationToken);
        if (targetRoot == null)
            throw new RefactoringException(ErrorCodes.RoslynError, "Could not parse target file.");

        var currentTarget = FindTypeInRoot(targetRoot, target.Name, targetSyntax.Span);
        return targetDocument.WithSyntaxRoot(targetRoot.ReplaceNode(currentTarget, newTarget)).Project.Solution;
    }

    private static Document? GetDocumentByFilePath(Solution solution, SyntaxTree tree)
    {
        if (string.IsNullOrEmpty(tree.FilePath))
            return null;

        foreach (var id in solution.GetDocumentIdsWithFilePath(tree.FilePath))
        {
            var document = solution.GetDocument(id);
            if (document != null)
                return document;
        }

        return null;
    }

    private static TypeDeclarationSyntax FindTypeInRoot(SyntaxNode root, string typeName, Microsoft.CodeAnalysis.Text.TextSpan preferredSpan)
    {
        var matches = root.DescendantNodes()
            .OfType<TypeDeclarationSyntax>()
            .Where(t => t.Identifier.Text == typeName)
            .ToList();

        return matches.FirstOrDefault(t => t.Span == preferredSpan) ?? matches[0];
    }

    private static TypeDeclarationSyntax AddMembersToTarget(
        TypeDeclarationSyntax target,
        IReadOnlyList<MemberDeclarationSyntax> members,
        bool makeClassAbstract)
    {
        var formatted = members.Select(member => member
            .WithLeadingTrivia(SyntaxFactory.ElasticCarriageReturnLineFeed)
            .WithTrailingTrivia(SyntaxFactory.ElasticCarriageReturnLineFeed));

        var updated = target.WithMembers(target.Members.AddRange(formatted));

        if (makeClassAbstract &&
            target is ClassDeclarationSyntax &&
            !target.Modifiers.Any(SyntaxKind.AbstractKeyword))
        {
            updated = updated.AddModifiers(
                SyntaxFactory.Token(SyntaxKind.AbstractKeyword)
                    .WithTrailingTrivia(SyntaxFactory.Space));
        }

        return updated;
    }

    private static RefactoringResult CreatePreviewResult(
        Guid operationId,
        PullMembersUpParams @params,
        INamedTypeSymbol target,
        IReadOnlyList<string> pulledNames,
        IReadOnlyList<MemberDeclarationSyntax> targetMembers,
        TypeDeclarationSyntax originalDerived,
        TypeDeclarationSyntax updatedDerived)
    {
        var memberList = string.Join(", ", pulledNames);
        var afterSnippet = string.Join(
            Environment.NewLine + Environment.NewLine,
            targetMembers.Select(m => m.NormalizeWhitespace().ToFullString()));

        var targetLocation = target.Locations.FirstOrDefault(l => l.IsInSource);
        var targetFile = targetLocation?.SourceTree?.FilePath ?? @params.SourceFile;

        var pendingChanges = new List<PendingChange>
        {
            new()
            {
                File = targetFile,
                ChangeType = ChangeKind.Modify,
                Description = $"Add {memberList} to {@params.TargetBaseType ?? target.Name}",
                BeforeSnippet = $"{target.TypeKind.ToString().ToLowerInvariant()} {target.Name}",
                AfterSnippet = afterSnippet
            },
            new()
            {
                File = @params.SourceFile,
                ChangeType = ChangeKind.Modify,
                Description = target.TypeKind == TypeKind.Interface
                    ? $"Keep {memberList} on {@params.TypeName} to implement {target.Name}"
                    : @params.MakeAbstract
                        ? $"Keep {memberList} on {@params.TypeName} as override"
                        : $"Remove {memberList} from {@params.TypeName}",
                BeforeSnippet = originalDerived.Identifier.Text,
                AfterSnippet = updatedDerived.NormalizeWhitespace().ToFullString()
            }
        };

        return RefactoringResult.PreviewResult(operationId, pendingChanges);
    }

    private sealed record PullableMember(string Name, ISymbol Symbol, MemberDeclarationSyntax Syntax);
}

using System.Xml;
using System.Xml.Linq;
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
using RoslynMcp.Core.Refactoring.Hierarchy;
using RoslynMcp.Core.Refactoring.Rename;
using RoslynMcp.Core.Workspace;

namespace RoslynMcp.Core.Refactoring.Extract;

/// <summary>
/// Extracts members to a new base class.
/// Honors optional <c>line</c> and <c>column</c> to disambiguate
/// same-named types in one file (identifier preferred, then smallest
/// containing type). Omitted column keeps today's typeName + optional
/// line pick. Omitted line keeps today's <c>ClassDeclarationSyntax</c>
/// <c>FirstOrDefault</c> pick (enum, struct, interface, and
/// <c>DelegateDeclarationSyntax</c> do not participate).
/// When line is set, a covering enum, struct, interface, or delegate is
/// included so it reaches <c>InvalidSymbolKind</c> rather than retargeting
/// a later class.
/// After the extract (and when adding <c>: BaseClassName</c> to the
/// derived type), the selected declaration is recovered by a
/// per-execution syntax annotation (stripped before commit).
/// </summary>
public sealed class ExtractBaseClassOperation : RefactoringOperationBase<ExtractBaseClassParams>
{
    /// <summary>
    /// Creates a new extract base class operation.
    /// </summary>
    public ExtractBaseClassOperation(WorkspaceContext context) : base(context)
    {
    }

    /// <inheritdoc />
    protected override void ValidateParams(ExtractBaseClassParams @params) => Validate(@params);

    /// <summary>
    /// Validates extract-base-class parameters. Internal so tests can exercise
    /// input rules without loading a workspace.
    /// </summary>
    internal static void Validate(ExtractBaseClassParams @params)
    {
        if (string.IsNullOrWhiteSpace(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "sourceFile is required.");

        if (string.IsNullOrWhiteSpace(@params.TypeName))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "typeName is required.");

        if (string.IsNullOrWhiteSpace(@params.BaseClassName))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "baseClassName is required.");

        if (@params.Members == null || @params.Members.Count == 0)
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

        if (!IsValidIdentifier(@params.BaseClassName))
            throw new RefactoringException(ErrorCodes.InvalidSymbolName, $"Invalid base class name: {@params.BaseClassName}");

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
        ExtractBaseClassParams @params,
        CancellationToken cancellationToken)
    {
        var document = GetDocumentOrThrow(@params.SourceFile);
        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);

        if (root == null || semanticModel == null)
        {
            throw new RefactoringException(ErrorCodes.RoslynError, "Could not parse file.");
        }

        // Optional line/column disambiguates same-named types. Omitted
        // column keeps today's ClassDeclarationSyntax FirstOrDefault
        // pick (enum, struct, interface, and DelegateDeclarationSyntax
        // do not participate). Line set also includes a covering enum,
        // struct, interface, or delegate so it reaches InvalidSymbolKind
        // instead of retargeting a later class.
        var found = FindTypeDeclaration(root, @params.TypeName, @params.Line, @params.Column);

        if (found == null)
        {
            throw new RefactoringException(
                ErrorCodes.TypeNotFound,
                $"Class '{@params.TypeName}' not found in file.");
        }

        // Get the type symbol
        var typeSymbol = semanticModel.GetDeclaredSymbol(found, cancellationToken) as INamedTypeSymbol;
        if (typeSymbol == null)
        {
            throw new RefactoringException(ErrorCodes.RoslynError, "Could not resolve type symbol.");
        }

        if (found is not ClassDeclarationSyntax typeDeclaration)
        {
            throw new RefactoringException(
                ErrorCodes.InvalidSymbolKind,
                $"Type '{typeSymbol.Name}' is not a supported target for extract_base_class.");
        }

        // Check if type already has a base class other than Object
        if (typeSymbol.BaseType != null &&
            typeSymbol.BaseType.SpecialType != SpecialType.System_Object)
        {
            throw new RefactoringException(
                ErrorCodes.TypeAlreadyHasBase,
                $"Type '{@params.TypeName}' already has base class '{typeSymbol.BaseType.Name}'.");
        }

        // Check if base class name already exists
        var existingType = await TypeResolver.FindTypeByNameAsync(
            $"{typeSymbol.ContainingNamespace}.{@params.BaseClassName}",
            cancellationToken);

        if (existingType != null)
        {
            throw new RefactoringException(
                ErrorCodes.NameCollision,
                $"Type '{@params.BaseClassName}' already exists.");
        }

        // Find members to extract
        var (membersToExtract, extractedSymbols) = FindMembersToExtract(
            typeDeclaration,
            @params.Members,
            semanticModel,
            @params.MakeAbstract);

        if (@params.MakeAbstract)
            ValidateAbstractMembers(extractedSymbols);

        // Generate base class
        var baseClass = GenerateBaseClass(
            @params.BaseClassName,
            membersToExtract,
            @params.MakeAbstract);

        // Get namespace
        var namespaceName = typeSymbol.ContainingNamespace?.ToDisplayString();

        // Explicit targetFile always wins. separateFile=true with no targetFile
        // writes {BaseClassName}.cs next to the source.
        var targetFile = ResolveTargetFile(@params);
        var isNewFile = targetFile != @params.SourceFile;

        if (isNewFile && typeSymbol.ContainingType != null)
        {
            throw new RefactoringException(
                ErrorCodes.CannotExtractNestedToSeparateFile,
                $"Cannot extract a base class for nested type '{@params.TypeName}' to a separate file. Extract in the source file instead.");
        }

        ThrowIfSiblingTargetExists(@params, targetFile);

        string? projectPath = null;
        string? updatedProjectText = null;
        if (isNewFile)
        {
            (projectPath, updatedProjectText) = PrepareExplicitCompileItemUpdate(document.Project, targetFile);
        }

        // If preview mode, return without applying
        if (@params.Preview)
        {
            return CreatePreviewResult(
                operationId,
                @params,
                membersToExtract,
                baseClass,
                namespaceName,
                targetFile,
                updatedProjectText != null ? projectPath : null);
        }

        // Fresh instance per execution. A static annotation is shared
        // across operations; after CommitChanges the in-memory solution
        // can still carry it, so a later extract on another type would
        // recover the stale node via FirstOrDefault.
        // Annotate before the rewrite. Same-file extract inserts the
        // base class before the selected type and then rematches the
        // derived type to add : BaseClassName — both shift later
        // same-named types. Do not re-find with stale SpanStart or
        // line. Today's rematch First by typeName is not enough.
        var targetTypeAnnotation = new SyntaxAnnotation("extract-base-class-target-type");
        var previousTree = root.SyntaxTree;
        root = root.ReplaceNode(
            typeDeclaration,
            typeDeclaration.WithAdditionalAnnotations(targetTypeAnnotation));
        document = document.WithSyntaxRoot(root);
        var annotatedSolution = document.Project.Solution;
        // If GetDocument(oldTree) misses after the annotation rewrite,
        // look up by file path and rematch by span (same as
        // implement_abstract #226).
        document = annotatedSolution.GetDocument(previousTree)
            ?? GetDocumentForTree(annotatedSolution, previousTree, @params.TypeName);
        root = await document.GetSyntaxRootAsync(cancellationToken)
            ?? throw new RefactoringException(ErrorCodes.RoslynError, "Could not parse file.");
        typeDeclaration = RecoverAnnotatedClass(
            root,
            targetTypeAnnotation,
            typeDeclaration,
            @params.TypeName);

        // Apply changes
        Solution newSolution;
        if (targetFile != @params.SourceFile)
        {
            // Create new file with base class
            newSolution = await CreateBaseClassInNewFileAsync(
                document.Project.Solution,
                document.Project,
                targetFile,
                baseClass,
                namespaceName,
                root,
                cancellationToken);
        }
        else
        {
            // Add base class to same file
            newSolution = AddBaseClassToSameFile(
                document,
                root,
                typeDeclaration,
                baseClass);
        }

        // Update derived class: remove extracted members and add base class.
        // Recover the selected declaration from the per-execution
        // annotation. Do not rematch First by typeName — that attaches
        // to an earlier same-named type after the rewrite. After insert,
        // spans have shifted, so do not rematch by SpanStart.
        var updatedDoc = newSolution.GetDocument(document.Id)
            ?? GetDocumentForTree(newSolution, root.SyntaxTree, @params.TypeName);
        var updatedRoot = await updatedDoc.GetSyntaxRootAsync(cancellationToken)
            ?? throw new RefactoringException(ErrorCodes.RoslynError, "Could not parse file.");
        var updatedTypeDecl = updatedRoot.GetAnnotatedNodes(targetTypeAnnotation)
            .OfType<ClassDeclarationSyntax>()
            .FirstOrDefault()
            ?? throw new RefactoringException(
                ErrorCodes.TypeNotFound,
                $"Class '{@params.TypeName}' not found in file.");

        // Add base class to type
        var baseType = SyntaxFactory.SimpleBaseType(SyntaxFactory.ParseTypeName(@params.BaseClassName));

        ClassDeclarationSyntax newTypeDecl;
        if (updatedTypeDecl.BaseList == null)
        {
            newTypeDecl = updatedTypeDecl.WithBaseList(
                SyntaxFactory.BaseList(SyntaxFactory.SingletonSeparatedList<BaseTypeSyntax>(baseType)));
        }
        else
        {
            // Insert base class before interfaces
            var newBaseList = SyntaxFactory.BaseList(
                SyntaxFactory.SeparatedList(
                    new[] { baseType }.Concat(updatedTypeDecl.BaseList.Types)));
            newTypeDecl = updatedTypeDecl.WithBaseList(newBaseList);
        }

        // Remove extracted members from derived class. Multi-variable
        // event fields drop only the selected declarators. Indexers
        // match by parameter-list signature so Item / this[] /
        // this[int i] all drop the selected indexer only.
        var memberNames = @params.Members.ToHashSet();
        foreach (var extracted in membersToExtract)
        {
            if (extracted is IndexerDeclarationSyntax indexer)
                memberNames.Add(GetIndexerRemovalKey(indexer));
        }

        var newMembers = RebuildDerivedMembers(
            newTypeDecl.Members,
            memberNames,
            @params.MakeAbstract,
            extractedSymbols,
            typeSymbol);

        newTypeDecl = newTypeDecl.WithMembers(SyntaxFactory.List(newMembers));

        // Strip the per-execution annotation so it does not linger in the
        // workspace after commit.
        newTypeDecl = (ClassDeclarationSyntax)newTypeDecl.WithoutAnnotations(targetTypeAnnotation);

        updatedRoot = updatedRoot.ReplaceNode(updatedTypeDecl, newTypeDecl);
        newSolution = updatedDoc.WithSyntaxRoot(updatedRoot).Project.Solution;

        // Commit changes
        var commitResult = await CommitChangesAsync(newSolution, cancellationToken);

        var filesModified = commitResult.FilesModified.ToList();
        if (updatedProjectText != null && !string.IsNullOrWhiteSpace(projectPath))
        {
            WriteProjectFile(projectPath, updatedProjectText);
            if (!filesModified.Contains(projectPath, StringComparer.OrdinalIgnoreCase))
                filesModified.Add(projectPath);
        }

        return RefactoringResult.Succeeded(
            operationId,
            new FileChanges
            {
                FilesModified = filesModified,
                FilesCreated = commitResult.FilesCreated,
                FilesDeleted = commitResult.FilesDeleted
            },
            new Contracts.Models.SymbolInfo
            {
                Name = @params.BaseClassName,
                FullyQualifiedName = string.IsNullOrEmpty(namespaceName)
                    ? @params.BaseClassName
                    : $"{namespaceName}.{@params.BaseClassName}",
                Kind = Contracts.Enums.SymbolKind.Class
            },
            0,
            0);
    }

    private static (List<MemberDeclarationSyntax> Members, Dictionary<string, ISymbol> Symbols) FindMembersToExtract(
        ClassDeclarationSyntax typeDeclaration,
        IReadOnlyList<string> memberNames,
        SemanticModel semanticModel,
        bool makeAbstract)
    {
        var requestedSet = new HashSet<string>(memberNames);
        var unmatched = new HashSet<string>(memberNames);
        var result = new List<MemberDeclarationSyntax>();
        var symbols = new Dictionary<string, ISymbol>(StringComparer.Ordinal);

        foreach (var member in typeDeclaration.Members)
        {
            if (member is EventFieldDeclarationSyntax eventField)
            {
                var selected = eventField.Declaration.Variables
                    .Where(v => requestedSet.Contains(v.Identifier.Text))
                    .ToList();
                if (selected.Count == 0)
                    continue;

                foreach (var variable in selected)
                {
                    unmatched.Remove(variable.Identifier.Text);
                    if (semanticModel.GetDeclaredSymbol(variable) is ISymbol eventSymbol)
                        symbols[variable.Identifier.Text] = eventSymbol;
                }

                result.Add(eventField.WithDeclaration(
                    eventField.Declaration.WithVariables(SyntaxFactory.SeparatedList(selected))));
                continue;
            }

            // Indexers match metadata name (Item), Roslyn name (this[]), and
            // conventional display (this[int i]) — same identity forms as
            // implement_interface / extract_interface. Explicit interface
            // implementations are skipped: copying IFoo.this[...] onto a
            // base that does not implement IFoo is CS0540.
            if (member is IndexerDeclarationSyntax indexerDecl
                && semanticModel.GetDeclaredSymbol(indexerDecl) is IPropertySymbol { IsIndexer: true } indexer)
            {
                if (indexer.ExplicitInterfaceImplementations.Length > 0
                    || indexerDecl.ExplicitInterfaceSpecifier != null)
                {
                    if (makeAbstract
                        && ImplementInterfaceOperation.MatchesRequestedMember(indexer, requestedSet))
                    {
                        throw new RefactoringException(
                            ErrorCodes.MemberNotMoveable,
                            $"Indexer '{indexer.Name}' cannot be extracted as an abstract member.");
                    }

                    continue;
                }

                if (ImplementInterfaceOperation.MatchesRequestedMember(indexer, requestedSet))
                {
                    result.Add(member);
                    symbols[GetIndexerRemovalKey(indexerDecl)] = indexer;
                    foreach (var requested in unmatched.ToList())
                    {
                        if (ImplementInterfaceOperation.MatchesRequestedMember(
                                indexer, new HashSet<string> { requested }))
                        {
                            unmatched.Remove(requested);
                        }
                    }
                }

                continue;
            }

            var name = GetMemberName(member);
            if (name != null && requestedSet.Contains(name))
            {
                result.Add(member);
                unmatched.Remove(name);
                if (semanticModel.GetDeclaredSymbol(member) is ISymbol symbol)
                    symbols[name] = symbol;
            }
        }

        if (unmatched.Count > 0)
        {
            throw new RefactoringException(
                ErrorCodes.MemberNotFound,
                $"Members not found: {string.Join(", ", unmatched)}");
        }

        return (result, symbols);
    }

    private static string? GetMemberName(MemberDeclarationSyntax member)
    {
        return member switch
        {
            MethodDeclarationSyntax m => m.Identifier.Text,
            PropertyDeclarationSyntax p => p.Identifier.Text,
            IndexerDeclarationSyntax => "this[]",
            FieldDeclarationSyntax f => f.Declaration.Variables.FirstOrDefault()?.Identifier.Text,
            EventDeclarationSyntax e => e.Identifier.Text,
            EventFieldDeclarationSyntax ef => ef.Declaration.Variables.FirstOrDefault()?.Identifier.Text,
            _ => null
        };
    }

    /// <summary>
    /// Stable key for dropping a selected indexer from the derived type.
    /// Parameter types, <see cref="RefKind"/>, and the explicit-interface
    /// specifier distinguish overloads so <c>this[]</c> / <c>Item</c> do
    /// not remove an unselected or explicit-interface indexer.
    /// </summary>
    private static string GetIndexerRemovalKey(IndexerDeclarationSyntax indexer)
    {
        var parts = indexer.ParameterList.Parameters.Select(parameter =>
        {
            var type = parameter.Type?.ToString() ?? string.Empty;
            if (parameter.Modifiers.Any(SyntaxKind.RefKeyword)
                && parameter.Modifiers.Any(SyntaxKind.ReadOnlyKeyword))
            {
                return "ref readonly " + type;
            }

            if (parameter.Modifiers.Any(SyntaxKind.RefKeyword))
                return "ref " + type;
            if (parameter.Modifiers.Any(SyntaxKind.OutKeyword))
                return "out " + type;
            if (parameter.Modifiers.Any(SyntaxKind.InKeyword))
                return "in " + type;
            return type;
        });

        var specifier = indexer.ExplicitInterfaceSpecifier?.Name.ToString() ?? string.Empty;
        return "indexer:" + specifier + ":" + string.Join(",", parts);
    }

    private static IEnumerable<string> GetExtractedMemberNames(MemberDeclarationSyntax member)
    {
        if (member is EventFieldDeclarationSyntax eventField)
        {
            foreach (var variable in eventField.Declaration.Variables)
                yield return variable.Identifier.Text;
            yield break;
        }

        var name = GetMemberName(member);
        if (name != null)
            yield return name;
    }

    private static bool ShouldRemoveMember(MemberDeclarationSyntax member, HashSet<string> memberNames)
    {
        if (member is IndexerDeclarationSyntax indexer)
            return memberNames.Contains(GetIndexerRemovalKey(indexer));

        var name = GetMemberName(member);
        return name != null && memberNames.Contains(name);
    }

    /// <summary>
    /// Drops extracted members from the derived type, or keeps them as
    /// <c>override</c> when <paramref name="makeAbstract"/> is true (fields
    /// still move). Field-like events match by declarator name so a
    /// multi-variable event field keeps unrelated declarators on the
    /// derived type.
    /// </summary>
    private static List<MemberDeclarationSyntax> RebuildDerivedMembers(
        SyntaxList<MemberDeclarationSyntax> members,
        HashSet<string> extractedNames,
        bool makeAbstract,
        IReadOnlyDictionary<string, ISymbol> extractedSymbols,
        INamedTypeSymbol derivedType)
    {
        var result = new List<MemberDeclarationSyntax>();
        foreach (var member in members)
        {
            if (member is EventFieldDeclarationSyntax eventField)
            {
                var remaining = eventField.Declaration.Variables
                    .Where(v => !extractedNames.Contains(v.Identifier.Text))
                    .ToList();
                var selected = eventField.Declaration.Variables
                    .Where(v => extractedNames.Contains(v.Identifier.Text))
                    .ToList();
                if (selected.Count == 0)
                {
                    result.Add(member);
                    continue;
                }

                if (remaining.Count > 0)
                {
                    result.Add(eventField.WithDeclaration(
                        eventField.Declaration.WithVariables(SyntaxFactory.SeparatedList(remaining))));
                }

                if (makeAbstract)
                {
                    foreach (var variable in selected)
                    {
                        result.Add(HierarchyAbstractMemberRewriter.AddOverrideModifier(
                            HierarchyAbstractMemberRewriter.IsolateMemberSyntax(
                                member, variable.Identifier.Text),
                            RequireExtractedSymbol(extractedSymbols, variable.Identifier.Text),
                            derivedType));
                    }
                }

                continue;
            }

            if (!ShouldRemoveMember(member, extractedNames))
            {
                result.Add(member);
                continue;
            }

            if (makeAbstract && member is not FieldDeclarationSyntax)
            {
                var key = member is IndexerDeclarationSyntax indexer
                    ? GetIndexerRemovalKey(indexer)
                    : GetMemberName(member);
                result.Add(HierarchyAbstractMemberRewriter.AddOverrideModifier(
                    member,
                    RequireExtractedSymbol(extractedSymbols, key),
                    derivedType));
            }
        }

        return result;
    }

    private static void ValidateAbstractMembers(IReadOnlyDictionary<string, ISymbol> extractedSymbols)
    {
        foreach (var (name, symbol) in extractedSymbols)
        {
            if (symbol is IFieldSymbol)
                continue;

            ThrowIfCannotBeAbstract(symbol, name);
        }
    }

    private static void ThrowIfCannotBeAbstract(ISymbol symbol, string name)
    {
        if (HierarchyAbstractMemberRewriter.CanBeAbstract(symbol))
            return;

        throw new RefactoringException(
            ErrorCodes.MemberNotMoveable,
            symbol switch
            {
                IEventSymbol => $"Event '{name}' cannot be extracted as an abstract member.",
                IPropertySymbol { IsIndexer: true } =>
                    $"Indexer '{name}' cannot be extracted as an abstract member.",
                IPropertySymbol => $"Property '{name}' cannot be extracted as an abstract member.",
                IMethodSymbol => $"Method '{name}' cannot be extracted as an abstract member.",
                _ => $"Member '{name}' cannot be extracted as an abstract member."
            });
    }

    private static ISymbol RequireExtractedSymbol(
        IReadOnlyDictionary<string, ISymbol> extractedSymbols,
        string? key)
    {
        if (key != null && extractedSymbols.TryGetValue(key, out var symbol))
            return symbol;

        throw new RefactoringException(
            ErrorCodes.RoslynError,
            $"Could not resolve symbol for extracted member '{key}'.");
    }

    private static ClassDeclarationSyntax GenerateBaseClass(
        string className,
        List<MemberDeclarationSyntax> members,
        bool makeAbstract)
    {
        var modifiers = new List<SyntaxToken> { SyntaxFactory.Token(SyntaxKind.PublicKeyword) };
        if (makeAbstract)
        {
            modifiers.Add(SyntaxFactory.Token(SyntaxKind.AbstractKeyword));
        }

        // Make members protected if they're private. makeAbstract converts
        // methods / properties / events / indexers to abstract declarations;
        // fields stay concrete.
        var adjustedMembers = new List<MemberDeclarationSyntax>();
        foreach (var member in members)
        {
            if (makeAbstract && member is not FieldDeclarationSyntax)
            {
                foreach (var abstractable in ExpandForAbstract(member))
                {
                    adjustedMembers.Add(HierarchyAbstractMemberRewriter.ConvertToAbstract(
                        AdjustMemberAccessibility(abstractable),
                        "Only methods, properties, indexers, and events can be extracted as abstract members."));
                }
            }
            else
            {
                adjustedMembers.Add(AdjustMemberAccessibility(member));
            }
        }

        return SyntaxFactory.ClassDeclaration(className)
            .WithModifiers(SyntaxFactory.TokenList(modifiers))
            .WithMembers(SyntaxFactory.List(adjustedMembers))
            .NormalizeWhitespace();
    }

    private static IEnumerable<MemberDeclarationSyntax> ExpandForAbstract(MemberDeclarationSyntax member)
    {
        if (member is EventFieldDeclarationSyntax eventField
            && eventField.Declaration.Variables.Count > 1)
        {
            foreach (var variable in eventField.Declaration.Variables)
            {
                yield return HierarchyAbstractMemberRewriter.IsolateMemberSyntax(
                    member, variable.Identifier.Text);
            }

            yield break;
        }

        yield return member;
    }

    private static MemberDeclarationSyntax AdjustMemberAccessibility(MemberDeclarationSyntax member)
    {
        // If private, make protected
        var modifiers = member switch
        {
            MethodDeclarationSyntax m => m.Modifiers,
            PropertyDeclarationSyntax p => p.Modifiers,
            IndexerDeclarationSyntax i => i.Modifiers,
            FieldDeclarationSyntax f => f.Modifiers,
            EventDeclarationSyntax e => e.Modifiers,
            EventFieldDeclarationSyntax ef => ef.Modifiers,
            _ => default
        };

        if (modifiers.Any(SyntaxKind.PrivateKeyword))
        {
            // private → protected. private protected already has
            // ProtectedKeyword; drop only private so we emit a single
            // protected (not protected protected).
            var withoutPrivate = modifiers.Where(m => !m.IsKind(SyntaxKind.PrivateKeyword));
            var newModifiers = SyntaxFactory.TokenList(
                withoutPrivate.Any(t => t.IsKind(SyntaxKind.ProtectedKeyword))
                    ? withoutPrivate
                    : withoutPrivate.Prepend(SyntaxFactory.Token(SyntaxKind.ProtectedKeyword)));

            return member switch
            {
                MethodDeclarationSyntax m => m.WithModifiers(newModifiers),
                PropertyDeclarationSyntax p => p.WithModifiers(newModifiers),
                IndexerDeclarationSyntax i => i.WithModifiers(newModifiers),
                FieldDeclarationSyntax f => f.WithModifiers(newModifiers),
                EventDeclarationSyntax e => e.WithModifiers(newModifiers),
                EventFieldDeclarationSyntax ef => ef.WithModifiers(newModifiers),
                _ => member
            };
        }

        return member;
    }

    private Task<Solution> CreateBaseClassInNewFileAsync(
        Solution solution,
        Project project,
        string targetFile,
        ClassDeclarationSyntax baseClass,
        string? namespaceName,
        SyntaxNode sourceRoot,
        CancellationToken cancellationToken)
    {
        // Build compilation unit with usings from source
        var usings = sourceRoot.DescendantNodes()
            .OfType<UsingDirectiveSyntax>()
            .ToList();

        MemberDeclarationSyntax wrappedClass;
        if (!string.IsNullOrEmpty(namespaceName))
        {
            wrappedClass = SyntaxFactory.FileScopedNamespaceDeclaration(
                    SyntaxFactory.ParseName(namespaceName))
                .WithMembers(SyntaxFactory.SingletonList<MemberDeclarationSyntax>(baseClass));
        }
        else
        {
            wrappedClass = baseClass;
        }

        var compilationUnit = SyntaxFactory.CompilationUnit()
            .WithUsings(SyntaxFactory.List(usings))
            .WithMembers(SyntaxFactory.SingletonList(wrappedClass))
            .NormalizeWhitespace();

        // Create new document
        var newDoc = project.AddDocument(
            Path.GetFileName(targetFile),
            compilationUnit,
            filePath: targetFile);

        return Task.FromResult(newDoc.Project.Solution);
    }

    private static Solution AddBaseClassToSameFile(
        Document document,
        SyntaxNode root,
        ClassDeclarationSyntax derivedClass,
        ClassDeclarationSyntax baseClass)
    {
        // Insert base class before derived class
        var newBaseClass = baseClass
            .WithLeadingTrivia(SyntaxFactory.CarriageReturnLineFeed)
            .WithTrailingTrivia(SyntaxFactory.CarriageReturnLineFeed, SyntaxFactory.CarriageReturnLineFeed);

        var newRoot = root.InsertNodesBefore(derivedClass, new[] { newBaseClass });
        var newDoc = document.WithSyntaxRoot(newRoot);

        return newDoc.Project.Solution;
    }

    /// <summary>
    /// Resolves the destination for the extracted base class.
    /// Explicit <see cref="ExtractBaseClassParams.TargetFile"/> always wins.
    /// </summary>
    private static string ResolveTargetFile(ExtractBaseClassParams @params)
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

        return PathResolver.Combine(directory, @params.BaseClassName + ".cs");
    }

    private static void ThrowIfSiblingTargetExists(ExtractBaseClassParams @params, string targetFile)
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

    /// <summary>
    /// When default compile items are disabled, add an explicit
    /// <c>Compile Include</c> for <paramref name="targetFile"/>.
    /// SDK-style default glob projects are left unchanged.
    /// </summary>
    internal static string AddExplicitCompileItemIfNeeded(
        string projectXml,
        string projectDirectory,
        string targetFile)
    {
        XDocument document;
        try
        {
            document = XDocument.Parse(projectXml, LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo);
        }
        catch (XmlException)
        {
            if (LooksLikeExplicitCompileProject(projectXml))
            {
                throw new RefactoringException(
                    ErrorCodes.DocumentNotEditable,
                    "Could not parse project file to add an explicit Compile item.");
            }

            return projectXml;
        }

        if (!RequiresExplicitCompileItems(document))
            return projectXml;

        var ns = document.Root?.Name.Namespace ?? XNamespace.None;
        if (CompileItemRefersToFile(document, ns, projectDirectory, targetFile))
            return projectXml;

        var include = GetCompileIncludePath(projectDirectory, targetFile);
        var compile = new XElement(ns + "Compile", new XAttribute("Include", include));
        var lastCompile = document.Descendants(ns + "Compile").LastOrDefault();
        if (lastCompile != null)
        {
            lastCompile.AddAfterSelf(new XText(Environment.NewLine + "    "));
            lastCompile.AddAfterSelf(compile);
        }
        else if (document.Root != null)
        {
            var itemGroup = new XElement(
                ns + "ItemGroup",
                new XText(Environment.NewLine + "    "),
                compile,
                new XText(Environment.NewLine + "  "));
            document.Root.Add(new XText(Environment.NewLine + "  "));
            document.Root.Add(itemGroup);
            document.Root.Add(new XText(Environment.NewLine));
        }
        else
        {
            throw new RefactoringException(
                ErrorCodes.DocumentNotEditable,
                "Project file has no root element; cannot add an explicit Compile item.");
        }

        return SerializeProjectXml(document, projectXml);
    }

    internal static string GetCompileIncludePath(string projectDirectory, string filePath)
    {
        var relative = Path.GetRelativePath(
            PathResolver.NormalizePath(projectDirectory),
            PathResolver.NormalizePath(filePath));
        return relative.Replace('\\', '/');
    }

    private static (string? ProjectPath, string? UpdatedText) PrepareExplicitCompileItemUpdate(
        Project project,
        string targetFile)
    {
        var projectPath = project.FilePath;
        if (string.IsNullOrWhiteSpace(projectPath) || !File.Exists(projectPath))
        {
            throw new RefactoringException(
                ErrorCodes.DocumentNotEditable,
                "Project file is not available to add an explicit Compile item for the new base class file.");
        }

        var projectDirectory = Path.GetDirectoryName(projectPath);
        if (string.IsNullOrEmpty(projectDirectory))
        {
            throw new RefactoringException(
                ErrorCodes.DocumentNotEditable,
                $"Project '{project.Name}' is not editable.");
        }

        var original = File.ReadAllText(projectPath);
        var updated = AddExplicitCompileItemIfNeeded(original, projectDirectory, targetFile);
        if (string.Equals(original, updated, StringComparison.Ordinal))
            return (projectPath, null);

        if (new FileInfo(projectPath).IsReadOnly)
        {
            throw new RefactoringException(
                ErrorCodes.DocumentNotEditable,
                $"Project '{project.Name}' is not editable.");
        }

        return (projectPath, updated);
    }

    private static void WriteProjectFile(string projectPath, string projectText)
    {
        try
        {
            File.WriteAllText(projectPath, projectText);
        }
        catch (IOException ex)
        {
            throw new RefactoringException(
                ErrorCodes.FilesystemError,
                $"Failed to update project file: {ex.Message}",
                ex);
        }
    }

    private static bool RequiresExplicitCompileItems(XDocument document)
    {
        var ns = document.Root?.Name.Namespace ?? XNamespace.None;
        var compileDefaults = GetMsBuildProperty(document, ns, "EnableDefaultCompileItems");
        var defaultItems = GetMsBuildProperty(document, ns, "EnableDefaultItems");

        if (string.Equals(compileDefaults, "false", StringComparison.OrdinalIgnoreCase))
            return true;
        if (string.Equals(compileDefaults, "true", StringComparison.OrdinalIgnoreCase))
            return false;
        return string.Equals(defaultItems, "false", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeExplicitCompileProject(string projectXml) =>
        ContainsDisabledProperty(projectXml, "EnableDefaultCompileItems")
        || ContainsDisabledProperty(projectXml, "EnableDefaultItems");

    private static bool ContainsDisabledProperty(string projectXml, string propertyName)
    {
        var start = projectXml.IndexOf(propertyName, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
            return false;

        var close = projectXml.IndexOf('>', start);
        if (close < 0 || close + 1 >= projectXml.Length)
            return false;

        return projectXml.AsSpan(close + 1).TrimStart().StartsWith("false", StringComparison.OrdinalIgnoreCase);
    }

    private static string? GetMsBuildProperty(XDocument document, XNamespace ns, string name) =>
        document.Descendants(ns + name)
            .Select(e => e.Value.Trim())
            .LastOrDefault(v => v.Length > 0);

    private static bool CompileItemRefersToFile(
        XDocument document,
        XNamespace ns,
        string projectDirectory,
        string filePath)
    {
        foreach (var compile in document.Descendants(ns + "Compile"))
        {
            foreach (var attributeName in new[] { "Include", "Update" })
            {
                var value = compile.Attribute(attributeName)?.Value;
                if (!string.IsNullOrWhiteSpace(value)
                    && RenameFileToMatchTypeOperation.ProjectItemRefersToFile(projectDirectory, value, filePath))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static string SerializeProjectXml(XDocument document, string originalXml)
    {
        var writerSettings = new XmlWriterSettings
        {
            OmitXmlDeclaration = !originalXml.Contains("<?xml", StringComparison.OrdinalIgnoreCase),
            NewLineHandling = NewLineHandling.Replace,
            NewLineChars = originalXml.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n",
            Indent = false
        };

        using var writer = new StringWriter();
        using (var xmlWriter = XmlWriter.Create(writer, writerSettings))
        {
            document.Save(xmlWriter);
        }

        var serialized = writer.ToString();
        if (originalXml.EndsWith('\n') && !serialized.EndsWith('\n'))
            serialized += writerSettings.NewLineChars;
        return serialized;
    }

    private static RefactoringResult CreatePreviewResult(
        Guid operationId,
        ExtractBaseClassParams @params,
        List<MemberDeclarationSyntax> members,
        ClassDeclarationSyntax baseClass,
        string? namespaceName,
        string targetFile,
        string? projectPath)
    {
        var memberNames = string.Join(", ", members.SelectMany(GetExtractedMemberNames));
        var abstractNames = string.Join(
            ", ",
            members.Where(m => m is not FieldDeclarationSyntax).SelectMany(GetExtractedMemberNames));
        var baseClassCode = baseClass.NormalizeWhitespace().ToFullString();

        var isNewFile = targetFile != @params.SourceFile;
        var extractDescription = @params.MakeAbstract && abstractNames.Length > 0
            ? $"Extract abstract base class {@params.BaseClassName} with abstract members: {abstractNames}"
            : $"Extract base class {@params.BaseClassName} with members: {memberNames}";
        var derivedDescription = @params.MakeAbstract && abstractNames.Length > 0
            ? $"Keep {abstractNames} on {@params.TypeName} as override"
            : $"Update {@params.TypeName} to inherit from {@params.BaseClassName}";

        var pendingChanges = new List<PendingChange>
        {
            new()
            {
                File = targetFile,
                ChangeType = isNewFile ? ChangeKind.Create : ChangeKind.Modify,
                Description = extractDescription,
                BeforeSnippet = isNewFile ? "// (new file)" : $"// Before class '{@params.TypeName}'",
                AfterSnippet = baseClassCode
            },
            new()
            {
                File = @params.SourceFile,
                ChangeType = ChangeKind.Modify,
                Description = derivedDescription,
                BeforeSnippet = $"class {@params.TypeName}",
                AfterSnippet = $"class {@params.TypeName} : {@params.BaseClassName}"
            }
        };

        if (!string.IsNullOrWhiteSpace(projectPath))
        {
            pendingChanges.Add(new PendingChange
            {
                File = projectPath,
                ChangeType = ChangeKind.Modify,
                Description = "Add explicit Compile item for the new base class file"
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
    /// Finds a type by <paramref name="typeName"/>. Omitted
    /// <paramref name="column"/> keeps today's typeName + optional
    /// <paramref name="line"/> pick, including omitted-line
    /// <c>ClassDeclarationSyntax</c> <c>FirstOrDefault</c> (enum, struct,
    /// interface, and <c>DelegateDeclarationSyntax</c> do not participate)
    /// and line-only exclusive-end coverage (<see cref="SpanCoversLine"/>).
    /// Do not force column 1 when omitted. Do not change
    /// omitted-line/omitted-column to <c>BaseTypeDeclarationSyntax</c> or
    /// <c>TypeDeclarationSyntax</c> FirstOrDefault. Do not add enums,
    /// structs, interfaces, or delegates to the omitted-line set. Column
    /// without line keeps today's first-match after the typeName filter
    /// (<c>ClassDeclarationSyntax</c> only) rather than substituting each
    /// candidate's own start line. When column is set with line, picks the
    /// type whose identifier or declaration span covers that 1-based
    /// column (same exclusive-end coverage as
    /// <c>ExtractInterfaceOperation.SpanCoversColumn</c> /
    /// <c>GenerateToStringOperation.SpanCoversColumn</c>). Prefer the
    /// identifier hit, then the smallest containing type. Nested types,
    /// enums, structs, interfaces, and <c>DelegateDeclarationSyntax</c>
    /// participate when line is set so a covering enum or delegate still
    /// reaches <c>InvalidSymbolKind</c> rather than retargeting a later
    /// class. Do not require the declaration to start on
    /// <paramref name="line"/> when column is set — a split declaration
    /// may put the identifier on a continuation line. If column is set
    /// with line and nothing covers that position, return null
    /// (TypeNotFound) rather than falling back to first-match. After the
    /// extract and when adding <c>: BaseClassName</c>, recover the
    /// selected type from the per-execution syntax annotation — do not
    /// reuse a pre-rewrite SpanStart or line.
    /// </summary>
    internal static MemberDeclarationSyntax? FindTypeDeclaration(
        SyntaxNode root,
        string typeName,
        int? line,
        int? column = null)
    {
        var classCandidates = root.DescendantNodes()
            .OfType<ClassDeclarationSyntax>()
            .Where(t => t.Identifier.Text == typeName)
            .ToList();

        // Line set (including column+line) uses BaseTypeDeclarationSyntax
        // (enum/struct/interface) plus DelegateDeclarationSyntax so a
        // covering enum or delegate reaches InvalidSymbolKind rather than
        // retargeting a later class. Omitted-line / column-without-line
        // stay ClassDeclarationSyntax only — do not switch that set to
        // BaseTypeDeclarationSyntax or TypeDeclarationSyntax.
        var lineCandidates = line.HasValue
            ? root.DescendantNodes()
                .OfType<BaseTypeDeclarationSyntax>()
                .Where(t => t.Identifier.Text == typeName)
                .Cast<MemberDeclarationSyntax>()
                .Concat(root.DescendantNodes()
                    .OfType<DelegateDeclarationSyntax>()
                    .Where(d => d.Identifier.Text == typeName))
                .ToList()
            : classCandidates.Cast<MemberDeclarationSyntax>().ToList();

        // Column without line is not a source position: substituting each
        // candidate's own start line would match every equally-aligned
        // same-name type and could silently pick the shortest. Keep
        // today's FirstOrDefault after the typeName filter
        // (ClassDeclarationSyntax only).
        if (column.HasValue && !line.HasValue)
            return classCandidates.FirstOrDefault();

        if (column.HasValue)
        {
            // Do not require the declaration to start on `line` — a split
            // type's identifier may live on a continuation line whose
            // declaration span still covers that column. Prefer the
            // identifier hit, then the smallest containing type (nested
            // over outer). Include enum, struct, interface, and delegate
            // candidates so a covering non-class still reaches
            // InvalidSymbolKind. Do not silently pick the first when a
            // covering node exists elsewhere — scan every candidate. If
            // nothing covers this position, keep today's not-found (null)
            // rather than inventing a first-match.
            return lineCandidates
                .Where(t => TypeCoversColumn(t, line!.Value, column.Value))
                .OrderBy(t => IdentifierCoversColumn(t, line!.Value, column.Value) ? 0 : 1)
                .ThenBy(t => t.Span.Length)
                .FirstOrDefault();
        }

        if (!line.HasValue)
            return classCandidates.FirstOrDefault();

        // Line set: include BaseTypeDeclarationSyntax (enum/struct/interface)
        // and DelegateDeclarationSyntax in the covering-line set. Do not
        // require the declaration to start on `line` — a split type's
        // identifier may live on a continuation line whose declaration
        // span still covers that line. Prefer the identifier hit, then
        // the smallest containing type (nested over outer). Include enum
        // and delegate candidates. Do not silently pick the first when a
        // covering node exists elsewhere — scan every candidate. If
        // nothing covers this line, keep today's ClassDeclarationSyntax
        // first-match rather than inventing a not-found (enums, structs,
        // interfaces, and delegates stay out of that omitted-line fallback).
        if (lineCandidates.Count == 0)
            return null;

        return lineCandidates
            .Where(t => TypeCoversLine(t, line.Value))
            .OrderBy(t => IdentifierCoversLine(t, line.Value) ? 0 : 1)
            .ThenBy(t => t.Span.Length)
            .FirstOrDefault()
            ?? classCandidates.FirstOrDefault();
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

    private static ClassDeclarationSyntax RecoverAnnotatedClass(
        SyntaxNode root,
        SyntaxAnnotation targetTypeAnnotation,
        ClassDeclarationSyntax original,
        string typeName)
    {
        var annotated = root.GetAnnotatedNodes(targetTypeAnnotation)
            .OfType<ClassDeclarationSyntax>()
            .FirstOrDefault();
        if (annotated != null)
            return annotated;

        return RematchTypeDeclaration(root, original)
            ?? throw new RefactoringException(
                ErrorCodes.TypeNotFound,
                $"Class '{typeName}' not found in file.");
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

    private static ClassDeclarationSyntax? RematchTypeDeclaration(
        SyntaxNode root,
        ClassDeclarationSyntax original) =>
        root.DescendantNodes()
            .OfType<ClassDeclarationSyntax>()
            .FirstOrDefault(t => t.SpanStart == original.SpanStart && t.Identifier.Text == original.Identifier.Text);
}

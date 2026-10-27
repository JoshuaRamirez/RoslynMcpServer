using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Query.Base;
using RoslynMcp.Core.Query.Utilities;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Utilities;
using RoslynMcp.Core.Workspace;

namespace RoslynMcp.Core.Query;

/// <summary>
/// Gets a structured outline of all declarations in a document.
/// Pure syntax traversal; the semantic model is requested only when
/// <see cref="GetDocumentOutlineParams.AccessibilityFilter"/>, <see cref="GetDocumentOutlineParams.NamespaceFilter"/>
/// or <see cref="GetDocumentOutlineParams.StaticFilter"/> is set, to resolve declared accessibility / the declared
/// symbol's namespace / whether it is static.
/// </summary>
public sealed class GetDocumentOutlineOperation : QueryOperationBase<GetDocumentOutlineParams, GetDocumentOutlineResult>
{
    /// <inheritdoc />
    public GetDocumentOutlineOperation(WorkspaceContext context) : base(context)
    {
    }

    /// <inheritdoc />
    protected override void ValidateParams(GetDocumentOutlineParams @params)
    {
        if (string.IsNullOrWhiteSpace(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "sourceFile is required.");

        SourceFilePathHelpers.ValidateSourceFilePath(@params.SourceFile);

        if (!File.Exists(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.SourceFileNotFound, $"Source file not found: {@params.SourceFile}");

        if (@params.MaxResults.HasValue && @params.MaxResults.Value < 1)
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "maxResults must be >= 1.");

        if (@params.MaxDepth.HasValue && @params.MaxDepth.Value < 1)
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "maxDepth must be >= 1.");

        // Same parsing and InvalidSymbolKind error as search_symbols kindFilter.
        SymbolKindFilterParser.Parse(@params.KindFilter);

        // Same parsing and InvalidVisibility error as search_symbols accessibilityFilter.
        AccessibilityFilterParser.Parse(@params.AccessibilityFilter);
    }

    /// <inheritdoc />
    protected override async Task<QueryResult<GetDocumentOutlineResult>> ExecuteCoreAsync(
        Guid operationId,
        GetDocumentOutlineParams @params,
        CancellationToken cancellationToken)
    {
        var document = GetDocumentOrThrow(@params.SourceFile);
        var root = await document.GetSyntaxRootAsync(cancellationToken);

        if (root == null)
            throw new RefactoringException(ErrorCodes.RoslynError, "Could not parse file.");

        var entries = new List<OutlineEntry>();
        var totalCount = 0;

        // When accessibilityFilter or namespaceFilter is set, remember each entry's declaration node so its
        // declared accessibility (implicit defaults and other partial parts count) or namespace can be
        // resolved semantically.
        var accessibilityFilter = AccessibilityFilterParser.Parse(@params.AccessibilityFilter);
        var namespaceFilter = NamespaceFilter.Normalize(@params.NamespaceFilter);
        var staticFilter = @params.StaticFilter;
        var declarations = accessibilityFilter.HasValue || namespaceFilter != null || staticFilter.HasValue
            ? new Dictionary<OutlineEntry, SyntaxNode>(ReferenceEqualityComparer.Instance)
            : null;

        // Process top-level namespace declarations
        foreach (var ns in root.DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>())
        {
            var nsEntry = BuildNamespaceEntry(ns, ref totalCount, declarations);
            entries.Add(nsEntry);
        }

        // Process top-level type declarations (not inside namespaces — for top-level types)
        foreach (var type in root.ChildNodes().OfType<TypeDeclarationSyntax>())
        {
            var typeEntry = BuildTypeEntry(type, ref totalCount, declarations);
            entries.Add(typeEntry);
        }

        // Process top-level enum declarations
        foreach (var enumDecl in root.ChildNodes().OfType<EnumDeclarationSyntax>())
        {
            totalCount++;
            entries.Add(BuildEnumEntry(enumDecl, ref totalCount, declarations));
        }

        // Process top-level delegate declarations
        foreach (var del in root.ChildNodes().OfType<DelegateDeclarationSyntax>())
        {
            totalCount++;
            entries.Add(Track(CreateEntry(del.Identifier.Text, "Delegate",
                del.GetLocation(), GetAccessibility(del.Modifiers), del.ReturnType.ToString()), del, declarations));
        }

        IReadOnlyList<OutlineEntry> returned = entries;
        var kindFilter = SymbolKindFilterParser.Parse(@params.KindFilter);
        var nameFilter = NameFilterMatcher.Normalize(@params.NameFilter);
        Func<OutlineEntry, bool>? accessibilityMatches = null;
        Func<OutlineEntry, bool>? namespaceMatches = null;
        Func<OutlineEntry, bool>? staticMatches = null;
        if (declarations != null)
        {
            var semanticModel = await document.GetSemanticModelAsync(cancellationToken)
                ?? throw new RefactoringException(ErrorCodes.RoslynError, "Could not get semantic model.");
            if (accessibilityFilter.HasValue)
            {
                accessibilityMatches = entry => OutlineAccessibilityMatches(
                    entry, accessibilityFilter.Value, declarations, semanticModel, cancellationToken);
            }

            if (namespaceFilter != null)
            {
                namespaceMatches = entry => OutlineNamespaceMatches(
                    entry, namespaceFilter, declarations, semanticModel, cancellationToken);
            }

            if (staticFilter.HasValue)
            {
                staticMatches = entry => OutlineStaticMatches(
                    entry, staticFilter.Value, declarations, semanticModel, cancellationToken);
            }
        }

        if (kindFilter.HasValue || nameFilter != null || accessibilityMatches != null || namespaceMatches != null ||
            staticMatches != null)
        {
            Func<OutlineEntry, bool> matches = entry =>
                OutlineEntryMatches(entry, kindFilter, nameFilter) &&
                (accessibilityMatches == null || accessibilityMatches(entry)) &&
                (namespaceMatches == null || namespaceMatches(entry)) &&
                (staticMatches == null || staticMatches(entry));
            if (namespaceMatches != null && declarations != null)
                matches = WithEnclosingNamespaceContainers(matches, declarations);

            returned = FilterOutlineForest(entries, matches);
            totalCount = CountNodes(returned);
        }

        var returnedCount = totalCount;
        if (@params.MaxDepth.HasValue)
        {
            returned = PruneOutlineForestByDepth(returned, @params.MaxDepth.Value);
            returnedCount = CountNodes(returned);
        }

        if (@params.MaxResults.HasValue && returnedCount > @params.MaxResults.Value)
        {
            returned = PruneOutlineForest(returned, @params.MaxResults.Value);
        }

        var result = new GetDocumentOutlineResult
        {
            File = @params.SourceFile,
            Entries = returned,
            TotalCount = totalCount,
            Truncated = totalCount > CountNodes(returned)
        };

        return QueryResult<GetDocumentOutlineResult>.Succeeded(operationId, result);
    }

    /// <summary>
    /// Keeps every outline entry that satisfies <paramref name="matches"/> plus the ancestors that
    /// contain a match (kept as containers so the tree shape and line numbers are unchanged).
    /// Subtrees with no matching entry are dropped; a kept entry left with no kept children is
    /// returned with <c>Children = null</c>, matching how leaf entries serialize today.
    /// </summary>
    private static IReadOnlyList<OutlineEntry> FilterOutlineForest(
        IReadOnlyList<OutlineEntry> entries,
        Func<OutlineEntry, bool> matches)
    {
        var filtered = new List<OutlineEntry>();
        foreach (var entry in entries)
        {
            var kept = FilterOutlineEntry(entry, matches);
            if (kept != null)
                filtered.Add(kept);
        }

        return filtered;
    }

    private static OutlineEntry? FilterOutlineEntry(OutlineEntry entry, Func<OutlineEntry, bool> matches)
    {
        IReadOnlyList<OutlineEntry>? children = null;
        if (entry.Children is { Count: > 0 })
        {
            var keptChildren = FilterOutlineForest(entry.Children, matches);
            if (keptChildren.Count > 0)
                children = keptChildren;
        }

        if (children == null && !matches(entry))
            return null;

        return new OutlineEntry
        {
            Name = entry.Name,
            Kind = entry.Kind,
            Line = entry.Line,
            Column = entry.Column,
            Accessibility = entry.Accessibility,
            ReturnType = entry.ReturnType,
            Children = children
        };
    }

    /// <summary>
    /// Extends <paramref name="matches"/> so a namespace entry whose declaration syntactically encloses a
    /// matching declaration is also kept as a container. Block-scoped nested namespaces
    /// (<c>namespace Outer { namespace Inner { ... } }</c>) are emitted as sibling outline roots rather than
    /// as children, so without this <c>namespaceFilter</c> <c>Outer.Inner</c> would drop the containing
    /// <c>Outer</c> entry. Only true syntactic ancestors qualify: a sibling dotted declaration such as
    /// <c>namespace App</c> next to <c>namespace App.Services</c> does not enclose it and is not kept.
    /// Each entry's match is evaluated once up front.
    /// </summary>
    private static Func<OutlineEntry, bool> WithEnclosingNamespaceContainers(
        Func<OutlineEntry, bool> matches,
        IReadOnlyDictionary<OutlineEntry, SyntaxNode> declarations)
    {
        var matched = new HashSet<OutlineEntry>(ReferenceEqualityComparer.Instance);
        var enclosingNamespaces = new HashSet<SyntaxNode>();
        foreach (var (entry, node) in declarations)
        {
            if (!matches(entry))
                continue;

            matched.Add(entry);
            foreach (var ancestor in node.Ancestors().OfType<BaseNamespaceDeclarationSyntax>())
                enclosingNamespaces.Add(ancestor);
        }

        return entry => matched.Contains(entry) ||
            (declarations.TryGetValue(entry, out var node) &&
             node is BaseNamespaceDeclarationSyntax &&
             enclosingNamespaces.Contains(node));
    }

    /// <summary>
    /// Whether an outline entry satisfies every active filter: its kind matches <paramref name="kind"/>
    /// (when set) and its name contains <paramref name="nameFilter"/> as an ordinal, case-insensitive
    /// substring (when set).
    /// </summary>
    private static bool OutlineEntryMatches(OutlineEntry entry, Contracts.Enums.SymbolKind? kind, string? nameFilter)
    {
        if (kind.HasValue && !OutlineKindMatches(entry.Kind, kind.Value))
            return false;

        return NameFilterMatcher.Matches(entry.Name, nameFilter);
    }

    /// <summary>
    /// Whether an outline entry's declared symbol has exactly <paramref name="accessibility"/> as its
    /// <see cref="ISymbol.DeclaredAccessibility"/> (the same exact, semantic match as <c>search_symbols</c>
    /// <c>accessibilityFilter</c>, so implicit defaults count). Namespaces and entries without a tracked
    /// declaration or a declared symbol never match.
    /// </summary>
    private static bool OutlineAccessibilityMatches(
        OutlineEntry entry,
        Accessibility accessibility,
        IReadOnlyDictionary<OutlineEntry, SyntaxNode> declarations,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        if (!declarations.TryGetValue(entry, out var node))
            return false;

        var symbol = semanticModel.GetDeclaredSymbol(node, cancellationToken);
        return symbol is not null and not INamespaceSymbol && symbol.DeclaredAccessibility == accessibility;
    }

    /// <summary>
    /// Whether an outline entry's declared symbol lives in <paramref name="normalizedFilter"/> (a value
    /// returned by <see cref="NamespaceFilter.Normalize"/>) or a namespace nested inside it (the same ordinal
    /// matching as <c>search_symbols</c> <c>namespaceFilter</c>). Type and member entries match on their
    /// containing namespace; namespace entries match on the namespace itself (so the entry <c>App.Services</c>
    /// matches the filter <c>App.Services</c>). Global-namespace symbols and entries without a tracked
    /// declaration or a declared symbol never match.
    /// </summary>
    private static bool OutlineNamespaceMatches(
        OutlineEntry entry,
        string normalizedFilter,
        IReadOnlyDictionary<OutlineEntry, SyntaxNode> declarations,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        if (!declarations.TryGetValue(entry, out var node))
            return false;

        return semanticModel.GetDeclaredSymbol(node, cancellationToken) switch
        {
            INamespaceSymbol ns => NamespaceFilter.IsNamespaceInFilter(ns, normalizedFilter),
            { } symbol => NamespaceFilter.IsInNamespace(symbol, normalizedFilter),
            null => false
        };
    }

    /// <summary>
    /// Whether an outline entry's declared symbol has <see cref="ISymbol.IsStatic"/> equal to
    /// <paramref name="wantStatic"/> (Roslyn reports <c>const</c> fields and enum members as static).
    /// Namespaces and entries without a tracked declaration or a declared symbol never match.
    /// </summary>
    private static bool OutlineStaticMatches(
        OutlineEntry entry,
        bool wantStatic,
        IReadOnlyDictionary<OutlineEntry, SyntaxNode> declarations,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        if (!declarations.TryGetValue(entry, out var node))
            return false;

        var symbol = semanticModel.GetDeclaredSymbol(node, cancellationToken);
        return symbol is not null and not INamespaceSymbol && symbol.IsStatic == wantStatic;
    }

    /// <summary>
    /// Records <paramref name="node"/> as the declaration of <paramref name="entry"/> when
    /// <paramref name="declarations"/> is tracked (accessibilityFilter, namespaceFilter or staticFilter set) and returns the entry.
    /// </summary>
    private static OutlineEntry Track(OutlineEntry entry, SyntaxNode node, Dictionary<OutlineEntry, SyntaxNode>? declarations)
    {
        declarations?.Add(entry, node);
        return entry;
    }

    /// <summary>
    /// Whether an outline entry kind matches a parsed <c>kindFilter</c>. Outline kinds share the
    /// <see cref="Contracts.Enums.SymbolKind"/> names, except that constructors are reported as
    /// <c>Constructor</c> (matched by <c>Method</c>) and enum members as <c>EnumMember</c>
    /// (matched by <c>Constant</c>), the same kinds <c>search_symbols</c> reports for those symbols.
    /// </summary>
    private static bool OutlineKindMatches(string outlineKind, Contracts.Enums.SymbolKind kind)
    {
        return outlineKind switch
        {
            "Constructor" => kind == Contracts.Enums.SymbolKind.Method,
            "EnumMember" => kind == Contracts.Enums.SymbolKind.Constant,
            _ => string.Equals(outlineKind, kind.ToString(), StringComparison.Ordinal)
        };
    }

    /// <summary>
    /// Drops every outline entry deeper than <paramref name="maxDepth"/> (roots are depth 1).
    /// Entries at the last kept depth are returned with no children (<c>Children = null</c>),
    /// matching how leaf entries serialize today.
    /// </summary>
    private static IReadOnlyList<OutlineEntry> PruneOutlineForestByDepth(IReadOnlyList<OutlineEntry> entries, int maxDepth)
    {
        var pruned = new List<OutlineEntry>(entries.Count);
        foreach (var entry in entries)
        {
            pruned.Add(PruneOutlineEntryByDepth(entry, depth: 1, maxDepth));
        }

        return pruned;
    }

    private static OutlineEntry PruneOutlineEntryByDepth(OutlineEntry entry, int depth, int maxDepth)
    {
        IReadOnlyList<OutlineEntry>? children = null;
        if (entry.Children is { Count: > 0 } && depth < maxDepth)
        {
            var prunedChildren = new List<OutlineEntry>(entry.Children.Count);
            foreach (var child in entry.Children)
            {
                prunedChildren.Add(PruneOutlineEntryByDepth(child, depth + 1, maxDepth));
            }

            children = prunedChildren;
        }

        return new OutlineEntry
        {
            Name = entry.Name,
            Kind = entry.Kind,
            Line = entry.Line,
            Column = entry.Column,
            Accessibility = entry.Accessibility,
            ReturnType = entry.ReturnType,
            Children = children
        };
    }

    /// <summary>
    /// Prefix-prunes an outline forest with a DFS pre-order node budget.
    /// Includes a node only while remaining budget > 0 (decrement once per included node),
    /// then recurses into its children with the remaining budget.
    /// </summary>
    private static IReadOnlyList<OutlineEntry> PruneOutlineForest(IReadOnlyList<OutlineEntry> entries, int maxResults)
    {
        var remaining = maxResults;
        var pruned = new List<OutlineEntry>();
        foreach (var entry in entries)
        {
            var node = PruneOutlineEntry(entry, ref remaining);
            if (node == null)
                break;
            pruned.Add(node);
            if (remaining <= 0)
                break;
        }

        return pruned;
    }

    private static OutlineEntry? PruneOutlineEntry(OutlineEntry entry, ref int remaining)
    {
        if (remaining <= 0)
            return null;

        remaining--;

        IReadOnlyList<OutlineEntry>? children = null;
        if (entry.Children is { Count: > 0 } && remaining > 0)
        {
            var prunedChildren = new List<OutlineEntry>();
            foreach (var child in entry.Children)
            {
                var prunedChild = PruneOutlineEntry(child, ref remaining);
                if (prunedChild == null)
                    break;
                prunedChildren.Add(prunedChild);
                if (remaining <= 0)
                    break;
            }

            if (prunedChildren.Count > 0)
                children = prunedChildren;
        }

        return new OutlineEntry
        {
            Name = entry.Name,
            Kind = entry.Kind,
            Line = entry.Line,
            Column = entry.Column,
            Accessibility = entry.Accessibility,
            ReturnType = entry.ReturnType,
            Children = children
        };
    }

    private static int CountNodes(IReadOnlyList<OutlineEntry> entries)
    {
        var count = 0;
        foreach (var entry in entries)
        {
            count++;
            if (entry.Children is { Count: > 0 })
                count += CountNodes(entry.Children);
        }

        return count;
    }

    private static OutlineEntry BuildNamespaceEntry(
        BaseNamespaceDeclarationSyntax ns, ref int totalCount, Dictionary<OutlineEntry, SyntaxNode>? declarations)
    {
        totalCount++;
        var children = new List<OutlineEntry>();

        foreach (var type in ns.Members.OfType<TypeDeclarationSyntax>())
        {
            children.Add(BuildTypeEntry(type, ref totalCount, declarations));
        }

        foreach (var enumDecl in ns.Members.OfType<EnumDeclarationSyntax>())
        {
            totalCount++;
            children.Add(BuildEnumEntry(enumDecl, ref totalCount, declarations));
        }

        foreach (var del in ns.Members.OfType<DelegateDeclarationSyntax>())
        {
            totalCount++;
            children.Add(Track(CreateEntry(del.Identifier.Text, "Delegate",
                del.GetLocation(), GetAccessibility(del.Modifiers), del.ReturnType.ToString()), del, declarations));
        }

        var lineSpan = ns.GetLocation().GetLineSpan();
        return Track(new OutlineEntry
        {
            Name = ns.Name.ToString(),
            Kind = "Namespace",
            Line = lineSpan.StartLinePosition.Line + 1,
            Column = lineSpan.StartLinePosition.Character + 1,
            Children = children.Count > 0 ? children : null
        }, ns, declarations);
    }

    private static OutlineEntry BuildTypeEntry(
        TypeDeclarationSyntax type, ref int totalCount, Dictionary<OutlineEntry, SyntaxNode>? declarations)
    {
        totalCount++;
        var children = new List<OutlineEntry>();
        var kind = type switch
        {
            ClassDeclarationSyntax => "Class",
            InterfaceDeclarationSyntax => "Interface",
            StructDeclarationSyntax => "Struct",
            RecordDeclarationSyntax => "Record",
            _ => "Type"
        };

        // Nested types
        foreach (var nested in type.Members.OfType<TypeDeclarationSyntax>())
        {
            children.Add(BuildTypeEntry(nested, ref totalCount, declarations));
        }

        // Constructors
        foreach (var ctor in type.Members.OfType<ConstructorDeclarationSyntax>())
        {
            totalCount++;
            children.Add(Track(CreateEntry(ctor.Identifier.Text, "Constructor",
                ctor.GetLocation(), GetAccessibility(ctor.Modifiers)), ctor, declarations));
        }

        // Methods
        foreach (var method in type.Members.OfType<MethodDeclarationSyntax>())
        {
            totalCount++;
            children.Add(Track(CreateEntry(method.Identifier.Text, "Method",
                method.GetLocation(), GetAccessibility(method.Modifiers), method.ReturnType.ToString()), method, declarations));
        }

        // Properties
        foreach (var prop in type.Members.OfType<PropertyDeclarationSyntax>())
        {
            totalCount++;
            children.Add(Track(CreateEntry(prop.Identifier.Text, "Property",
                prop.GetLocation(), GetAccessibility(prop.Modifiers), prop.Type.ToString()), prop, declarations));
        }

        // Fields
        foreach (var field in type.Members.OfType<FieldDeclarationSyntax>())
        {
            foreach (var variable in field.Declaration.Variables)
            {
                totalCount++;
                var fieldKind = field.Modifiers.Any(SyntaxKind.ConstKeyword) ? "Constant" : "Field";
                children.Add(Track(CreateEntry(variable.Identifier.Text, fieldKind,
                    variable.GetLocation(), GetAccessibility(field.Modifiers), field.Declaration.Type.ToString()), variable, declarations));
            }
        }

        // Events
        foreach (var evt in type.Members.OfType<EventDeclarationSyntax>())
        {
            totalCount++;
            children.Add(Track(CreateEntry(evt.Identifier.Text, "Event",
                evt.GetLocation(), GetAccessibility(evt.Modifiers), evt.Type.ToString()), evt, declarations));
        }

        foreach (var evtField in type.Members.OfType<EventFieldDeclarationSyntax>())
        {
            foreach (var variable in evtField.Declaration.Variables)
            {
                totalCount++;
                children.Add(Track(CreateEntry(variable.Identifier.Text, "Event",
                    variable.GetLocation(), GetAccessibility(evtField.Modifiers), evtField.Declaration.Type.ToString()), variable, declarations));
            }
        }

        // Enums nested in type
        foreach (var enumDecl in type.Members.OfType<EnumDeclarationSyntax>())
        {
            totalCount++;
            children.Add(BuildEnumEntry(enumDecl, ref totalCount, declarations));
        }

        var lineSpan = type.GetLocation().GetLineSpan();
        return Track(new OutlineEntry
        {
            Name = type.Identifier.Text,
            Kind = kind,
            Line = lineSpan.StartLinePosition.Line + 1,
            Column = lineSpan.StartLinePosition.Character + 1,
            Accessibility = GetAccessibility(type.Modifiers),
            Children = children.Count > 0 ? children : null
        }, type, declarations);
    }

    private static OutlineEntry BuildEnumEntry(
        EnumDeclarationSyntax enumDecl, ref int totalCount, Dictionary<OutlineEntry, SyntaxNode>? declarations)
    {
        var children = new List<OutlineEntry>();
        foreach (var member in enumDecl.Members)
        {
            totalCount++;
            children.Add(Track(CreateEntry(member.Identifier.Text, "EnumMember", member.GetLocation()), member, declarations));
        }

        var lineSpan = enumDecl.GetLocation().GetLineSpan();
        return Track(new OutlineEntry
        {
            Name = enumDecl.Identifier.Text,
            Kind = "Enum",
            Line = lineSpan.StartLinePosition.Line + 1,
            Column = lineSpan.StartLinePosition.Character + 1,
            Accessibility = GetAccessibility(enumDecl.Modifiers),
            Children = children.Count > 0 ? children : null
        }, enumDecl, declarations);
    }

    private static OutlineEntry CreateEntry(string name, string kind, Location location,
        string? accessibility = null, string? returnType = null)
    {
        var lineSpan = location.GetLineSpan();
        return new OutlineEntry
        {
            Name = name,
            Kind = kind,
            Line = lineSpan.StartLinePosition.Line + 1,
            Column = lineSpan.StartLinePosition.Character + 1,
            Accessibility = accessibility,
            ReturnType = returnType
        };
    }

    private static string? GetAccessibility(SyntaxTokenList modifiers)
    {
        if (modifiers.Any(SyntaxKind.PublicKeyword)) return "public";
        if (modifiers.Any(SyntaxKind.PrivateKeyword) && modifiers.Any(SyntaxKind.ProtectedKeyword)) return "private protected";
        if (modifiers.Any(SyntaxKind.ProtectedKeyword) && modifiers.Any(SyntaxKind.InternalKeyword)) return "protected internal";
        if (modifiers.Any(SyntaxKind.ProtectedKeyword)) return "protected";
        if (modifiers.Any(SyntaxKind.InternalKeyword)) return "internal";
        if (modifiers.Any(SyntaxKind.PrivateKeyword)) return "private";
        return null;
    }
}

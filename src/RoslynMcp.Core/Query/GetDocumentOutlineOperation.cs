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
/// Pure syntax traversal — no semantic model needed.
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

        // Process top-level namespace declarations
        foreach (var ns in root.DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>())
        {
            var nsEntry = BuildNamespaceEntry(ns, ref totalCount);
            entries.Add(nsEntry);
        }

        // Process top-level type declarations (not inside namespaces — for top-level types)
        foreach (var type in root.ChildNodes().OfType<TypeDeclarationSyntax>())
        {
            var typeEntry = BuildTypeEntry(type, ref totalCount);
            entries.Add(typeEntry);
        }

        // Process top-level enum declarations
        foreach (var enumDecl in root.ChildNodes().OfType<EnumDeclarationSyntax>())
        {
            totalCount++;
            entries.Add(BuildEnumEntry(enumDecl, ref totalCount));
        }

        // Process top-level delegate declarations
        foreach (var del in root.ChildNodes().OfType<DelegateDeclarationSyntax>())
        {
            totalCount++;
            entries.Add(CreateEntry(del.Identifier.Text, "Delegate",
                del.GetLocation(), GetAccessibility(del.Modifiers), del.ReturnType.ToString()));
        }

        IReadOnlyList<OutlineEntry> returned = entries;
        var kindFilter = SymbolKindFilterParser.Parse(@params.KindFilter);
        var nameFilter = string.IsNullOrWhiteSpace(@params.NameFilter) ? null : @params.NameFilter.Trim();
        if (kindFilter.HasValue || nameFilter != null)
        {
            returned = FilterOutlineForest(entries, entry => OutlineEntryMatches(entry, kindFilter, nameFilter));
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
    /// Whether an outline entry satisfies every active filter: its kind matches <paramref name="kind"/>
    /// (when set) and its name contains <paramref name="nameFilter"/> as an ordinal, case-insensitive
    /// substring (when set).
    /// </summary>
    private static bool OutlineEntryMatches(OutlineEntry entry, Contracts.Enums.SymbolKind? kind, string? nameFilter)
    {
        if (kind.HasValue && !OutlineKindMatches(entry.Kind, kind.Value))
            return false;

        return nameFilter == null || entry.Name.Contains(nameFilter, StringComparison.OrdinalIgnoreCase);
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

    private static OutlineEntry BuildNamespaceEntry(BaseNamespaceDeclarationSyntax ns, ref int totalCount)
    {
        totalCount++;
        var children = new List<OutlineEntry>();

        foreach (var type in ns.Members.OfType<TypeDeclarationSyntax>())
        {
            children.Add(BuildTypeEntry(type, ref totalCount));
        }

        foreach (var enumDecl in ns.Members.OfType<EnumDeclarationSyntax>())
        {
            totalCount++;
            children.Add(BuildEnumEntry(enumDecl, ref totalCount));
        }

        foreach (var del in ns.Members.OfType<DelegateDeclarationSyntax>())
        {
            totalCount++;
            children.Add(CreateEntry(del.Identifier.Text, "Delegate",
                del.GetLocation(), GetAccessibility(del.Modifiers), del.ReturnType.ToString()));
        }

        var lineSpan = ns.GetLocation().GetLineSpan();
        return new OutlineEntry
        {
            Name = ns.Name.ToString(),
            Kind = "Namespace",
            Line = lineSpan.StartLinePosition.Line + 1,
            Column = lineSpan.StartLinePosition.Character + 1,
            Children = children.Count > 0 ? children : null
        };
    }

    private static OutlineEntry BuildTypeEntry(TypeDeclarationSyntax type, ref int totalCount)
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
            children.Add(BuildTypeEntry(nested, ref totalCount));
        }

        // Constructors
        foreach (var ctor in type.Members.OfType<ConstructorDeclarationSyntax>())
        {
            totalCount++;
            children.Add(CreateEntry(ctor.Identifier.Text, "Constructor",
                ctor.GetLocation(), GetAccessibility(ctor.Modifiers)));
        }

        // Methods
        foreach (var method in type.Members.OfType<MethodDeclarationSyntax>())
        {
            totalCount++;
            children.Add(CreateEntry(method.Identifier.Text, "Method",
                method.GetLocation(), GetAccessibility(method.Modifiers), method.ReturnType.ToString()));
        }

        // Properties
        foreach (var prop in type.Members.OfType<PropertyDeclarationSyntax>())
        {
            totalCount++;
            children.Add(CreateEntry(prop.Identifier.Text, "Property",
                prop.GetLocation(), GetAccessibility(prop.Modifiers), prop.Type.ToString()));
        }

        // Fields
        foreach (var field in type.Members.OfType<FieldDeclarationSyntax>())
        {
            foreach (var variable in field.Declaration.Variables)
            {
                totalCount++;
                var fieldKind = field.Modifiers.Any(SyntaxKind.ConstKeyword) ? "Constant" : "Field";
                children.Add(CreateEntry(variable.Identifier.Text, fieldKind,
                    variable.GetLocation(), GetAccessibility(field.Modifiers), field.Declaration.Type.ToString()));
            }
        }

        // Events
        foreach (var evt in type.Members.OfType<EventDeclarationSyntax>())
        {
            totalCount++;
            children.Add(CreateEntry(evt.Identifier.Text, "Event",
                evt.GetLocation(), GetAccessibility(evt.Modifiers), evt.Type.ToString()));
        }

        foreach (var evtField in type.Members.OfType<EventFieldDeclarationSyntax>())
        {
            foreach (var variable in evtField.Declaration.Variables)
            {
                totalCount++;
                children.Add(CreateEntry(variable.Identifier.Text, "Event",
                    variable.GetLocation(), GetAccessibility(evtField.Modifiers), evtField.Declaration.Type.ToString()));
            }
        }

        // Enums nested in type
        foreach (var enumDecl in type.Members.OfType<EnumDeclarationSyntax>())
        {
            totalCount++;
            children.Add(BuildEnumEntry(enumDecl, ref totalCount));
        }

        var lineSpan = type.GetLocation().GetLineSpan();
        return new OutlineEntry
        {
            Name = type.Identifier.Text,
            Kind = kind,
            Line = lineSpan.StartLinePosition.Line + 1,
            Column = lineSpan.StartLinePosition.Character + 1,
            Accessibility = GetAccessibility(type.Modifiers),
            Children = children.Count > 0 ? children : null
        };
    }

    private static OutlineEntry BuildEnumEntry(EnumDeclarationSyntax enumDecl, ref int totalCount)
    {
        var children = new List<OutlineEntry>();
        foreach (var member in enumDecl.Members)
        {
            totalCount++;
            children.Add(CreateEntry(member.Identifier.Text, "EnumMember", member.GetLocation()));
        }

        var lineSpan = enumDecl.GetLocation().GetLineSpan();
        return new OutlineEntry
        {
            Name = enumDecl.Identifier.Text,
            Kind = "Enum",
            Line = lineSpan.StartLinePosition.Line + 1,
            Column = lineSpan.StartLinePosition.Character + 1,
            Accessibility = GetAccessibility(enumDecl.Modifiers),
            Children = children.Count > 0 ? children : null
        };
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

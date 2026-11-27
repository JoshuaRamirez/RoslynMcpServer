using System.Text;
using System.Xml;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Query.Base;
using RoslynMcp.Core.Query.Utilities;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Utilities;
using RoslynMcp.Core.Resolution;
using RoslynMcp.Core.Workspace;

namespace RoslynMcp.Core.Query;

/// <summary>
/// Returns rich metadata about a symbol including type hierarchy, members, and documentation.
/// </summary>
public sealed class GetSymbolInfoOperation : QueryOperationBase<GetSymbolInfoParams, DetailedSymbolInfo>
{
    /// <inheritdoc />
    public GetSymbolInfoOperation(WorkspaceContext context) : base(context)
    {
    }

    /// <inheritdoc />
    protected override void ValidateParams(GetSymbolInfoParams @params)
    {
        if (string.IsNullOrWhiteSpace(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "sourceFile is required.");

        SourceFilePathHelpers.ValidateSourceFilePath(@params.SourceFile);

        if (!File.Exists(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.SourceFileNotFound, $"Source file not found: {@params.SourceFile}");

        SymbolTargetValidator.Validate(@params.Line, @params.Column, @params.SymbolName);

        if (@params.MaxResults.HasValue && @params.MaxResults.Value < 1)
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "maxResults must be >= 1.");

        // Same parsing and InvalidSymbolKind error as search_symbols / get_document_outline kindFilter.
        SymbolKindFilterParser.Parse(@params.KindFilter);

        // Same parsing and InvalidVisibility error as search_symbols / get_document_outline accessibilityFilter.
        AccessibilityFilterParser.Parse(@params.AccessibilityFilter);
    }

    /// <inheritdoc />
    protected override async Task<QueryResult<DetailedSymbolInfo>> ExecuteCoreAsync(
        Guid operationId,
        GetSymbolInfoParams @params,
        CancellationToken cancellationToken)
    {
        var resolved = await SymbolResolver.ResolveSymbolAsync(
            @params.SourceFile, @params.SymbolName, @params.Line, @params.Column, cancellationToken);

        var symbol = resolved.Symbol;
        var compilation = await resolved.Document.Project.GetCompilationAsync(cancellationToken);
        var info = BuildDetailedInfo(
            symbol, compilation, @params.MaxResults, @params.KindFilter, @params.AccessibilityFilter,
            @params.NameFilter, @params.NamespaceFilter, @params.StaticFilter, @params.AbstractFilter, @params.SealedFilter);

        return QueryResult<DetailedSymbolInfo>.Succeeded(operationId, info);
    }

    private static DetailedSymbolInfo BuildDetailedInfo(
        ISymbol symbol,
        Compilation? compilation,
        int? maxResults,
        string? kindFilter,
        string? accessibilityFilter,
        string? nameFilter,
        string? namespaceFilter,
        bool? staticFilter,
        bool? abstractFilter,
        bool? sealedFilter)
    {
        var modifiers = GetModifiers(symbol);
        var location = GetLocation(symbol);
        // Optional kindFilter / accessibilityFilter / nameFilter / namespaceFilter / staticFilter / abstractFilter / sealedFilter (same
        // values / parsing as search_symbols / find_implementations / get_type_hierarchy), judged on each
        // member of a type target: reported Kind (SymbolKindMapper.Map), exact DeclaredAccessibility
        // (implicit defaults count), simple Name (ordinal case-insensitive contains), containing namespace
        // equal to or nested in the filter (members share their type's namespace, nested types included)
        // ISymbol.IsStatic (Roslyn reports const fields as static) and ISymbol.IsAbstract (interface members
        // without a default body and nested interfaces count as abstract) and ISymbol.IsSealed (sealed override members,
        // nested sealed types, structs, enums and delegates count as sealed; struct members and static classes do not). Omitted / blank values impose no filter; invalid kind / accessibility values are
        // already rejected in ValidateParams.
        var memberFilter = ResultSymbolFilter.Create(
            namespaceFilter,
            accessibilityFilter,
            kindFilter,
            nameFilter,
            staticFilter,
            abstractFilter,
            sealedFilter);

        // Gather type-specific fields
        string? baseType = null;
        IReadOnlyList<string>? interfaces = null;
        IReadOnlyList<string>? members = null;
        int? totalCount = null;
        var truncated = false;
        string? returnType = null;
        IReadOnlyList<Contracts.Models.ParameterInfo>? parameters = null;

        if (symbol is INamedTypeSymbol namedType)
        {
            baseType = namedType.BaseType?.SpecialType == SpecialType.System_Object
                ? null
                : namedType.BaseType?.ToDisplayString();
            interfaces = namedType.Interfaces.Select(i => i.ToDisplayString()).ToList();
            // A C# 14 extension block is a nameless nested type (TypeKind.Extension) that
            // CanBeReferencedByName drops; list its members in its place, so Shout / IsBlank inside
            // extension(string tag) { … } are members of the static class like classic extensions (#3046).
            var memberSymbols = namedType.GetMembers()
                .SelectMany(m => m is INamedTypeSymbol { TypeKind: TypeKind.Extension } extensionBlock
                    ? extensionBlock.GetMembers()
                    : [m])
                .Where(m => !m.IsImplicitlyDeclared && m.CanBeReferencedByName)
                .Where(memberFilter.Matches);

            var allMembers = memberSymbols
                .Select(m => m.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat))
                .ToList();

            // The member filters (kindFilter / accessibilityFilter / nameFilter / namespaceFilter / staticFilter / abstractFilter / sealedFilter) apply first; maxResults
            // caps only Members (first N in today's enumeration order); TotalCount stays the filtered
            // member count.
            totalCount = allMembers.Count;
            if (maxResults.HasValue && allMembers.Count > maxResults.Value)
            {
                members = allMembers.Take(maxResults.Value).ToList();
                truncated = true;
            }
            else
            {
                members = allMembers;
            }
        }

        if (symbol is IMethodSymbol method)
        {
            returnType = method.ReturnType.ToDisplayString();
            parameters = method.Parameters.Select(p => new Contracts.Models.ParameterInfo
            {
                Name = p.Name,
                Type = p.Type.ToDisplayString(),
                HasDefaultValue = p.HasExplicitDefaultValue,
                DefaultValue = p.HasExplicitDefaultValue ? p.ExplicitDefaultValue?.ToString() : null
            }).ToList();
        }

        if (symbol is IPropertySymbol property)
        {
            returnType = property.Type.ToDisplayString();
        }

        if (symbol is IFieldSymbol field)
        {
            returnType = field.Type.ToDisplayString();
        }

        return new DetailedSymbolInfo
        {
            Name = symbol.Name,
            FullyQualifiedName = symbol.ToDisplayString(),
            Kind = SymbolKindMapper.Map(symbol),
            Accessibility = symbol.DeclaredAccessibility.ToString().ToLowerInvariant(),
            Modifiers = modifiers,
            ContainingType = symbol.ContainingType?.ToDisplayString(),
            ContainingNamespace = symbol.ContainingNamespace?.IsGlobalNamespace == true
                ? null
                : symbol.ContainingNamespace?.ToDisplayString(),
            Signature = symbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
            DocumentationSummary = GetInheritedDocumentationSummary(symbol, compilation),
            Location = location,
            BaseType = baseType,
            Interfaces = interfaces,
            Members = members,
            TotalCount = totalCount,
            Truncated = truncated,
            ReturnType = returnType,
            Parameters = parameters
        };
    }

    /// <summary>
    /// The symbol's own summary, or for a <c>/// &lt;inheritdoc/&gt;</c> member the summary of the
    /// <c>cref</c> target, the overridden member, the implemented interface member, or the base
    /// type / interface (#3092). <see cref="ISymbol.GetDocumentationCommentXml"/> never expands
    /// <c>inheritdoc</c>, so these members used to report no summary at all.
    /// </summary>
    private static string? GetInheritedDocumentationSummary(ISymbol symbol, Compilation? compilation)
    {
        var visited = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        var current = symbol;
        while (visited.Add(current))
        {
            var summary = GetDocumentationSummary(current);
            if (summary != null)
                return summary;

            var inheritDoc = GetInheritDocElement(current);
            if (inheritDoc == null)
                return null;

            var next = ResolveInheritDocSource(current, (string?)inheritDoc.Attribute("cref"), compilation);
            if (next == null)
                return null;
            current = next;
        }

        return null;
    }

    private static System.Xml.Linq.XElement? GetInheritDocElement(ISymbol symbol)
    {
        var xml = symbol.GetDocumentationCommentXml();
        if (string.IsNullOrWhiteSpace(xml) || !xml.Contains("<inheritdoc", StringComparison.Ordinal))
            return null;
        try
        {
            return System.Xml.Linq.XDocument.Parse(xml).Descendants("inheritdoc").FirstOrDefault();
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }
    }

    private static bool IsEquatableOfSelf(INamedTypeSymbol @interface, INamedTypeSymbol type) =>
        @interface is { Name: "IEquatable", Arity: 1, ContainingNamespace: { Name: "System", ContainingNamespace.IsGlobalNamespace: true } } &&
        SymbolEqualityComparer.Default.Equals(@interface.TypeArguments[0], type);

    private static ISymbol? ResolveInheritDocSource(ISymbol symbol, string? cref, Compilation? compilation)
    {
        if (!string.IsNullOrEmpty(cref))
        {
            return compilation == null
                ? null
                : DocumentationCommentId.GetFirstSymbolForDeclarationId(cref, compilation);
        }

        ISymbol? overridden = symbol switch
        {
            IMethodSymbol m => m.OverriddenMethod,
            IPropertySymbol p => p.OverriddenProperty,
            IEventSymbol e => e.OverriddenEvent,
            _ => null
        };
        if (overridden != null)
            return overridden;

        ISymbol? explicitImplementation = symbol switch
        {
            IMethodSymbol m => m.ExplicitInterfaceImplementations.FirstOrDefault(),
            IPropertySymbol p => p.ExplicitInterfaceImplementations.FirstOrDefault(),
            IEventSymbol e => e.ExplicitInterfaceImplementations.FirstOrDefault(),
            _ => null
        };
        if (explicitImplementation != null)
            return explicitImplementation;

        if (symbol is INamedTypeSymbol type)
        {
            // Only a class inherits its base type's docs: a struct's or enum's BaseType is
            // System.ValueType / System.Enum, whose summary is not the type's (#3092 review).
            if (type.TypeKind == TypeKind.Class && type.BaseType != null && type.BaseType.SpecialType != SpecialType.System_Object)
                return type.BaseType;
            // A record's compiler-synthesized IEquatable<Self> is not a declared contract, and its
            // BCL summary is not the record's (#3092 review), so skip it.
            return type.Interfaces.FirstOrDefault(i => !(type.IsRecord && IsEquatableOfSelf(i, type)));
        }

        if (symbol.ContainingType is { } containingType)
        {
            foreach (var @interface in containingType.AllInterfaces)
            {
                foreach (var member in @interface.GetMembers(symbol.Name))
                {
                    if (SymbolEqualityComparer.Default.Equals(
                            containingType.FindImplementationForInterfaceMember(member), symbol))
                    {
                        return member;
                    }
                }
            }
        }

        return null;
    }

    private static List<string> GetModifiers(ISymbol symbol)
    {
        var modifiers = new List<string>();

        if (symbol.IsStatic) modifiers.Add("static");
        if (symbol.IsAbstract) modifiers.Add("abstract");
        if (symbol.IsSealed) modifiers.Add("sealed");
        if (symbol.IsVirtual) modifiers.Add("virtual");
        if (symbol.IsOverride) modifiers.Add("override");
        if (symbol.IsExtern) modifiers.Add("extern");

        if (symbol is IMethodSymbol method)
        {
            if (method.IsAsync) modifiers.Add("async");
            if (method.IsReadOnly) modifiers.Add("readonly");
        }

        if (symbol is IFieldSymbol field)
        {
            if (field.IsConst) modifiers.Add("const");
            if (field.IsReadOnly) modifiers.Add("readonly");
            if (field.IsVolatile) modifiers.Add("volatile");
        }

        if (symbol is INamedTypeSymbol namedType)
        {
            if (namedType.IsRecord) modifiers.Add("record");
        }

        return modifiers;
    }

    private static SymbolLocation? GetLocation(ISymbol symbol)
    {
        var location = symbol.Locations.FirstOrDefault(l => l.IsInSource);
        if (location == null) return null;

        try
        {
            var lineSpan = location.GetLineSpan();
            return new SymbolLocation
            {
                File = lineSpan.Path,
                Line = lineSpan.StartLinePosition.Line + 1,
                Column = lineSpan.StartLinePosition.Character + 1
            };
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static string? GetDocumentationSummary(ISymbol symbol)
    {
        var xml = symbol.GetDocumentationCommentXml();
        if (string.IsNullOrWhiteSpace(xml)) return null;

        // Render the <summary> element as text instead of slicing raw XML: inner tags
        // (<paramref/>, <see cref/>, <c>) leaked as markup, crefs kept their T:/M: doc-ID
        // prefix, and entities such as &lt; stayed encoded (#3091).
        string summary;
        try
        {
            // PreserveWhitespace keeps the space between adjacent inline elements (<c>x</c> <c>y</c>).
            var element = XDocument.Parse(xml, LoadOptions.PreserveWhitespace).Descendants("summary").FirstOrDefault();
            if (element == null) return null;
            var builder = new StringBuilder();
            RenderDocumentationNodes(element.Nodes(), builder);
            summary = builder.ToString();
        }
        catch (XmlException)
        {
            return null;
        }

        // Clean up whitespace
        summary = string.Join(" ", summary.Split(default(char[]), StringSplitOptions.RemoveEmptyEntries));
        return string.IsNullOrWhiteSpace(summary) ? null : summary;
    }

    private static void RenderDocumentationNodes(IEnumerable<XNode> nodes, StringBuilder builder)
    {
        foreach (var node in nodes)
        {
            switch (node)
            {
                case XText text:
                    builder.Append(text.Value);
                    break;
                case XElement element:
                    RenderDocumentationElement(element, builder);
                    break;
            }
        }
    }

    private static void RenderDocumentationElement(XElement element, StringBuilder builder)
    {
        switch (element.Name.LocalName)
        {
            case "see" or "seealso" when !element.Nodes().Any():
                var reference = (string?)element.Attribute("cref") ?? (string?)element.Attribute("langword") ?? (string?)element.Attribute("href");
                if (reference != null)
                    builder.Append(StripDocumentationIdPrefix(reference));
                break;
            case "paramref" or "typeparamref":
                builder.Append((string?)element.Attribute("name"));
                break;
            case "para" or "br":
                builder.Append(' ');
                RenderDocumentationNodes(element.Nodes(), builder);
                builder.Append(' ');
                break;
            default:
                RenderDocumentationNodes(element.Nodes(), builder);
                break;
        }
    }

    /// <summary>
    /// Drops the compiler's doc-ID kind prefix (<c>T:</c>, <c>M:</c>, …) from a cref.
    /// </summary>
    private static string StripDocumentationIdPrefix(string reference) =>
        reference.Length > 2 && reference[1] == ':' && (char.IsUpper(reference[0]) || reference[0] == '!')
            ? reference[2..]
            : reference;
}

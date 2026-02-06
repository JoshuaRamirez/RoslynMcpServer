using Microsoft.CodeAnalysis;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.FileSystem;
using RoslynMcp.Core.Query.Base;
using RoslynMcp.Core.Refactoring;
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

        if (!PathResolver.IsAbsolutePath(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be an absolute path.");

        if (!PathResolver.IsValidCSharpFilePath(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be a .cs file.");

        if (!File.Exists(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.SourceFileNotFound, $"Source file not found: {@params.SourceFile}");

        if (!@params.Line.HasValue && string.IsNullOrWhiteSpace(@params.SymbolName))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "Either line/column or symbolName must be provided.");

        if (@params.Line.HasValue && @params.Line.Value < 1)
            throw new RefactoringException(ErrorCodes.InvalidLineNumber, "Line number must be >= 1.");

        if (@params.Column.HasValue && @params.Column.Value < 1)
            throw new RefactoringException(ErrorCodes.InvalidColumnNumber, "Column number must be >= 1.");
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
        var info = BuildDetailedInfo(symbol);

        return QueryResult<DetailedSymbolInfo>.Succeeded(operationId, info);
    }

    private static DetailedSymbolInfo BuildDetailedInfo(ISymbol symbol)
    {
        var modifiers = GetModifiers(symbol);
        var location = GetLocation(symbol);

        // Gather type-specific fields
        string? baseType = null;
        IReadOnlyList<string>? interfaces = null;
        IReadOnlyList<string>? members = null;
        string? returnType = null;
        IReadOnlyList<Contracts.Models.ParameterInfo>? parameters = null;

        if (symbol is INamedTypeSymbol namedType)
        {
            baseType = namedType.BaseType?.SpecialType == SpecialType.System_Object
                ? null
                : namedType.BaseType?.ToDisplayString();
            interfaces = namedType.Interfaces.Select(i => i.ToDisplayString()).ToList();
            members = namedType.GetMembers()
                .Where(m => !m.IsImplicitlyDeclared && m.CanBeReferencedByName)
                .Select(m => m.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat))
                .ToList();
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
            DocumentationSummary = GetDocumentationSummary(symbol),
            Location = location,
            BaseType = baseType,
            Interfaces = interfaces,
            Members = members,
            ReturnType = returnType,
            Parameters = parameters
        };
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

        // Extract the <summary> content
        var startTag = "<summary>";
        var endTag = "</summary>";
        var startIndex = xml.IndexOf(startTag, StringComparison.Ordinal);
        var endIndex = xml.IndexOf(endTag, StringComparison.Ordinal);

        if (startIndex < 0 || endIndex < 0) return null;

        startIndex += startTag.Length;
        var summary = xml[startIndex..endIndex].Trim();

        // Clean up whitespace
        summary = string.Join(" ", summary.Split(default(char[]), StringSplitOptions.RemoveEmptyEntries));
        return string.IsNullOrWhiteSpace(summary) ? null : summary;
    }
}

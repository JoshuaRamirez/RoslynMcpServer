using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using RoslynMcp.Core.Refactoring.Utilities;
using RoslynMcp.Core.Resolution;
using Xunit;
using ContractSymbolKind = RoslynMcp.Contracts.Enums.SymbolKind;

namespace RoslynMcp.Core.Tests.Refactoring.Utilities;

/// <summary>
/// Regression coverage for MoveType's <see cref="NamedTypeSymbolKindHelpers.Map"/> —
/// especially <c>record class</c> → <see cref="ContractSymbolKind.Class"/>, which
/// differs from <see cref="SymbolKindMapper"/> (<see cref="ContractSymbolKind.Record"/>).
/// </summary>
public class NamedTypeSymbolKindHelpersTests
{
    [Fact]
    public void Map_RecordClass_ReturnsClass_NotRecord()
    {
        var symbol = GetNamedType("public record class Widget { }", "Widget");

        Assert.Equal(TypeKind.Class, symbol.TypeKind);
        Assert.True(symbol.IsRecord);
        Assert.Equal(ContractSymbolKind.Class, NamedTypeSymbolKindHelpers.Map(symbol));
        // Accidental redirect to SymbolKindMapper would flip this contract to Record.
        Assert.Equal(ContractSymbolKind.Record, SymbolKindMapper.Map(symbol));
    }

    [Fact]
    public void Map_PositionalRecord_ReturnsClass_NotRecord()
    {
        var symbol = GetNamedType("public record Widget(string Name);", "Widget");

        Assert.Equal(TypeKind.Class, symbol.TypeKind);
        Assert.True(symbol.IsRecord);
        Assert.Equal(ContractSymbolKind.Class, NamedTypeSymbolKindHelpers.Map(symbol));
        Assert.Equal(ContractSymbolKind.Record, SymbolKindMapper.Map(symbol));
    }

    [Fact]
    public void Map_OrdinaryClass_ReturnsClass()
    {
        var symbol = GetNamedType("public class Widget { }", "Widget");

        Assert.False(symbol.IsRecord);
        Assert.Equal(ContractSymbolKind.Class, NamedTypeSymbolKindHelpers.Map(symbol));
    }

    [Fact]
    public void Map_RecordStruct_ReturnsStruct()
    {
        var symbol = GetNamedType("public record struct Widget { }", "Widget");

        Assert.Equal(TypeKind.Struct, symbol.TypeKind);
        Assert.True(symbol.IsRecord);
        Assert.Equal(ContractSymbolKind.Struct, NamedTypeSymbolKindHelpers.Map(symbol));
    }

    private static INamedTypeSymbol GetNamedType(string source, string metadataName)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var compilation = CSharpCompilation.Create(
            "NamedTypeSymbolKindHelpersTests",
            [tree],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        return compilation.GetTypeByMetadataName(metadataName)!;
    }
}

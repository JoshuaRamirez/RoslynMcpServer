using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynMcp.Core.Resolution;
using Xunit;
using ContractSymbolKind = RoslynMcp.Contracts.Enums.SymbolKind;

namespace RoslynMcp.Core.Tests.Resolution;

/// <summary>
/// Tests for SymbolKindMapper to ensure all symbol types are correctly mapped.
/// </summary>
public class SymbolKindMapperTests
{
    private static (Compilation Compilation, SemanticModel Model, SyntaxTree Tree) CreateCompilation(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var compilation = CSharpCompilation.Create("TestAssembly")
            .AddReferences(MetadataReference.CreateFromFile(typeof(object).Assembly.Location))
            .AddSyntaxTrees(tree);
        var model = compilation.GetSemanticModel(tree);
        return (compilation, model, tree);
    }

    private static ISymbol GetDeclaredSymbol<TSyntax>(SemanticModel model, SyntaxTree tree)
        where TSyntax : SyntaxNode
    {
        var root = tree.GetCompilationUnitRoot();
        var node = root.DescendantNodes().OfType<TSyntax>().First();
        return model.GetDeclaredSymbol(node)!;
    }

    [Fact]
    public void Map_Class_ReturnsClass()
    {
        var (_, model, tree) = CreateCompilation("public class MyClass { }");
        var symbol = GetDeclaredSymbol<ClassDeclarationSyntax>(model, tree);
        Assert.Equal(ContractSymbolKind.Class, SymbolKindMapper.Map(symbol));
    }

    [Fact]
    public void Map_Interface_ReturnsInterface()
    {
        var (_, model, tree) = CreateCompilation("public interface IMyInterface { }");
        var symbol = GetDeclaredSymbol<InterfaceDeclarationSyntax>(model, tree);
        Assert.Equal(ContractSymbolKind.Interface, SymbolKindMapper.Map(symbol));
    }

    [Fact]
    public void Map_Struct_ReturnsStruct()
    {
        var (_, model, tree) = CreateCompilation("public struct MyStruct { }");
        var symbol = GetDeclaredSymbol<StructDeclarationSyntax>(model, tree);
        Assert.Equal(ContractSymbolKind.Struct, SymbolKindMapper.Map(symbol));
    }

    [Fact]
    public void Map_Enum_ReturnsEnum()
    {
        var (_, model, tree) = CreateCompilation("public enum MyEnum { A, B }");
        var symbol = GetDeclaredSymbol<EnumDeclarationSyntax>(model, tree);
        Assert.Equal(ContractSymbolKind.Enum, SymbolKindMapper.Map(symbol));
    }

    [Fact]
    public void Map_Record_ReturnsRecord()
    {
        var (_, model, tree) = CreateCompilation("public record MyRecord(string Name);");
        var symbol = GetDeclaredSymbol<RecordDeclarationSyntax>(model, tree);
        Assert.Equal(ContractSymbolKind.Record, SymbolKindMapper.Map(symbol));
    }

    [Fact]
    public void Map_Delegate_ReturnsDelegate()
    {
        var (_, model, tree) = CreateCompilation("public delegate void MyDelegate();");
        var symbol = GetDeclaredSymbol<DelegateDeclarationSyntax>(model, tree);
        Assert.Equal(ContractSymbolKind.Delegate, SymbolKindMapper.Map(symbol));
    }

    [Fact]
    public void Map_Method_ReturnsMethod()
    {
        var (_, model, tree) = CreateCompilation("public class C { public void MyMethod() { } }");
        var symbol = GetDeclaredSymbol<MethodDeclarationSyntax>(model, tree);
        Assert.Equal(ContractSymbolKind.Method, SymbolKindMapper.Map(symbol));
    }

    [Fact]
    public void Map_Property_ReturnsProperty()
    {
        var (_, model, tree) = CreateCompilation("public class C { public int MyProp { get; set; } }");
        var symbol = GetDeclaredSymbol<PropertyDeclarationSyntax>(model, tree);
        Assert.Equal(ContractSymbolKind.Property, SymbolKindMapper.Map(symbol));
    }

    [Fact]
    public void Map_Field_ReturnsField()
    {
        var (_, model, tree) = CreateCompilation("public class C { public int _field; }");
        var root = tree.GetCompilationUnitRoot();
        var fieldDecl = root.DescendantNodes().OfType<VariableDeclaratorSyntax>().First();
        var symbol = model.GetDeclaredSymbol(fieldDecl)!;
        Assert.Equal(ContractSymbolKind.Field, SymbolKindMapper.Map(symbol));
    }

    [Fact]
    public void Map_ConstField_ReturnsConstant()
    {
        var (_, model, tree) = CreateCompilation("public class C { public const int MAX = 100; }");
        var root = tree.GetCompilationUnitRoot();
        var fieldDecl = root.DescendantNodes().OfType<VariableDeclaratorSyntax>().First();
        var symbol = model.GetDeclaredSymbol(fieldDecl)!;
        Assert.Equal(ContractSymbolKind.Constant, SymbolKindMapper.Map(symbol));
    }

    [Fact]
    public void Map_Event_ReturnsEvent()
    {
        var source = "using System; public class C { public event EventHandler MyEvent; }";
        var (_, model, tree) = CreateCompilation(source);
        var root = tree.GetCompilationUnitRoot();
        var eventDecl = root.DescendantNodes().OfType<VariableDeclaratorSyntax>().First();
        var symbol = model.GetDeclaredSymbol(eventDecl)!;
        Assert.Equal(ContractSymbolKind.Event, SymbolKindMapper.Map(symbol));
    }

    [Fact]
    public void Map_Parameter_ReturnsParameter()
    {
        var (_, model, tree) = CreateCompilation("public class C { public void M(int x) { } }");
        var symbol = GetDeclaredSymbol<ParameterSyntax>(model, tree);
        Assert.Equal(ContractSymbolKind.Parameter, SymbolKindMapper.Map(symbol));
    }

    [Fact]
    public void Map_Namespace_ReturnsNamespace()
    {
        var (_, model, tree) = CreateCompilation("namespace MyNs { public class C { } }");
        var symbol = GetDeclaredSymbol<NamespaceDeclarationSyntax>(model, tree);
        Assert.Equal(ContractSymbolKind.Namespace, SymbolKindMapper.Map(symbol));
    }
}

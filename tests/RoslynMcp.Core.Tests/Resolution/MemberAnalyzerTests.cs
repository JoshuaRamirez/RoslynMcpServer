using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynMcp.Core.Resolution;
using Xunit;

namespace RoslynMcp.Core.Tests.Resolution;

/// <summary>
/// Tests for <see cref="MemberAnalyzer"/> discovery used by extract operations.
/// </summary>
public class MemberAnalyzerTests
{
    [Fact]
    public void GetMembersForBaseClass_IncludesNonPrivateEvents()
    {
        const string source = """
            public class Employee
            {
                public event System.EventHandler PublicEvent;
                protected event System.EventHandler ProtectedEvent;
                internal event System.EventHandler InternalEvent;
                private event System.EventHandler PrivateEvent;
                public event System.EventHandler AccessorEvent
                {
                    add { }
                    remove { }
                }

                public void Work() { }
                public string Name { get; set; }
                public int Age;
                private int _hidden;
            }
            """;

        var type = GetTypeSymbol(source, "Employee");
        var names = MemberAnalyzer.GetMembersForBaseClass(type)
            .Select(m => m.Name)
            .ToHashSet();

        Assert.Contains("PublicEvent", names);
        Assert.Contains("ProtectedEvent", names);
        Assert.Contains("InternalEvent", names);
        Assert.Contains("AccessorEvent", names);
        Assert.DoesNotContain("PrivateEvent", names);
        Assert.Contains("Work", names);
        Assert.Contains("Name", names);
        Assert.Contains("Age", names);
        Assert.DoesNotContain("_hidden", names);
    }

    [Fact]
    public void GetMembersForBaseClass_EventSymbolsAreIEventSymbol()
    {
        const string source = """
            public class Employee
            {
                public event System.EventHandler Changed;
            }
            """;

        var type = GetTypeSymbol(source, "Employee");
        var changed = MemberAnalyzer.GetMembersForBaseClass(type)
            .Single(m => m.Name == "Changed");

        Assert.IsAssignableFrom<IEventSymbol>(changed);
        Assert.NotEqual(Accessibility.Private, changed.DeclaredAccessibility);
    }

    private static INamedTypeSymbol GetTypeSymbol(string source, string typeName)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var compilation = CSharpCompilation.Create("MemberAnalyzerTest")
            .AddReferences(MetadataReference.CreateFromFile(typeof(object).Assembly.Location))
            .AddSyntaxTrees(tree);
        var model = compilation.GetSemanticModel(tree);
        var declaration = tree.GetCompilationUnitRoot()
            .DescendantNodes()
            .OfType<ClassDeclarationSyntax>()
            .First(c => c.Identifier.Text == typeName);
        return (INamedTypeSymbol)model.GetDeclaredSymbol(declaration)!;
    }
}

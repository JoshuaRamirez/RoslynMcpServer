using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynMcp.Core.Query.Utilities;
using Xunit;

namespace RoslynMcp.Core.Tests.Query;

/// <summary>
/// Regression tests for #3101: get_code_metrics counted dynamic, anonymous types, data pointers and
/// function pointers in classCoupling, although none is a named type the code depends on.
/// </summary>
public class MetricsCalculatorPseudoTypeCouplingTests
{
    private const string Source = """
        namespace S;

        public struct Point { public int X; }

        public class Calc
        {
            public object Dyn(dynamic d)
            {
                return d.Foo(d.Bar);
            }

            public int Anon()
            {
                var a = new { X = 1 };
                return a.X;
            }

            public unsafe int Ptr(int* p)
            {
                return *p;
            }

            public unsafe int FnPtr(delegate*<int, int> f)
            {
                return f(1);
            }

            public unsafe int PointPtr(Point* p)
            {
                return p->X;
            }
        }
        """;

    [Theory]
    [InlineData("Dyn", 0)]
    [InlineData("Anon", 0)]
    [InlineData("Ptr", 0)]
    [InlineData("FnPtr", 0)]
    [InlineData("PointPtr", 1)]
    public void ClassCoupling_IgnoresPseudoTypes(string methodName, int expected)
    {
        var tree = CSharpSyntaxTree.ParseText(Source);
        var compilation = CSharpCompilation.Create(
            "S",
            [tree],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var model = compilation.GetSemanticModel(tree);
        var method = tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Single(m => m.Identifier.ValueText == methodName);

        Assert.Equal(expected, MetricsCalculator.CalculateClassCoupling(model, method));
    }
}

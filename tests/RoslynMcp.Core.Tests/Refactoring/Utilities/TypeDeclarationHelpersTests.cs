using Microsoft.CodeAnalysis.CSharp;
using RoslynMcp.Core.Refactoring.Utilities;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Utilities;

/// <summary>
/// Unit tests for <see cref="TypeDeclarationHelpers.CollectTypeDeclarations"/> —
/// ordering and inclusion previously covered on six Generate private copies.
/// </summary>
public class TypeDeclarationHelpersTests
{
    private const string MixedEligibleAndSkipped = """
        namespace TestApp;

        public abstract class Eligible
        {
            public abstract void Go();
        }

        public static class StaticSkip
        {
        }

        public interface ISkip
        {
            void Go();
        }

        public struct PointSkip
        {
        }

        public class Outer
        {
            public class Nested
            {
            }
        }
        """;

    [Fact]
    public void CollectTypeDeclarations_IncludesNestedAndInterface_OrderedBySpanStart()
    {
        var root = CSharpSyntaxTree.ParseText(MixedEligibleAndSkipped).GetRoot();
        var types = TypeDeclarationHelpers.CollectTypeDeclarations(root);
        var names = types.Select(t => t.Identifier.Text).ToList();

        Assert.Contains("Eligible", names);
        Assert.Contains("StaticSkip", names);
        Assert.Contains("ISkip", names);
        Assert.Contains("PointSkip", names);
        Assert.Contains("Outer", names);
        Assert.Contains("Nested", names);
        Assert.True(names.IndexOf("Outer") < names.IndexOf("Nested"));

        // SpanStart primary order matches source declaration order for top-level types.
        Assert.Equal(
            new[] { "Eligible", "StaticSkip", "ISkip", "PointSkip", "Outer", "Nested" },
            names);
    }

    [Fact]
    public void CollectTypeDeclarations_IncludesClassStructInterfaceRecord_ExcludesEnumAndDelegate()
    {
        const string source = """
            namespace TestApp;
            public class C { }
            public struct S { }
            public interface I { }
            public enum E { A }
            public delegate void D();
            public record R();
            """;

        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var types = TypeDeclarationHelpers.CollectTypeDeclarations(root);
        Assert.Equal(4, types.Count);
        Assert.Contains(types, t => t.Identifier.Text == "C");
        Assert.Contains(types, t => t.Identifier.Text == "S");
        Assert.Contains(types, t => t.Identifier.Text == "I");
        Assert.Contains(types, t => t.Identifier.Text == "R");
        Assert.DoesNotContain(types, t => t.Identifier.Text == "E");
        Assert.DoesNotContain(types, t => t.Identifier.Text == "D");
    }

    [Fact]
    public void CollectTypeDeclarations_OrdersBySpanStart_PreservesSourceOrder()
    {
        // Distinct type decls in one tree always have different SpanStart values, so
        // this asserts the primary OrderBy(SpanStart) key — not the ThenBy(Length)
        // defensive tie-break (same-start ties do not occur for real syntax nodes).
        const string source = """
            class A { }
            class B { }
            """;
        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var types = TypeDeclarationHelpers.CollectTypeDeclarations(root);
        Assert.Equal(new[] { "A", "B" }, types.Select(t => t.Identifier.Text));
        Assert.True(types[0].SpanStart < types[1].SpanStart);
    }
}

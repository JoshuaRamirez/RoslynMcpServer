using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using RoslynMcp.Core.Refactoring.Utilities;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Utilities;

public class MemberHidingHelpersTests
{
    [Fact]
    public void IsHiddenFrom_NoCloserHider_ReturnsFalse()
    {
        var compilation = CreateCompilation("""
            public class Base
            {
                public int Value;
            }
            public class Derived : Base
            {
            }
            """);
        var baseType = compilation.GetTypeByMetadataName("Base")!;
        var derived = compilation.GetTypeByMetadataName("Derived")!;
        var field = baseType.GetMembers("Value").OfType<IFieldSymbol>().Single();

        Assert.False(MemberHidingHelpers.IsHiddenFrom(field, derived));
    }

    [Fact]
    public void IsHiddenFrom_CloserNonImplicitSameName_ReturnsTrue()
    {
        var compilation = CreateCompilation("""
            public class Base
            {
                public int Value;
            }
            public class Derived : Base
            {
                public new int Value;
            }
            """);
        var baseType = compilation.GetTypeByMetadataName("Base")!;
        var derived = compilation.GetTypeByMetadataName("Derived")!;
        var field = baseType.GetMembers("Value").OfType<IFieldSymbol>().Single();

        Assert.True(MemberHidingHelpers.IsHiddenFrom(field, derived));
    }

    [Fact]
    public void IsHiddenFrom_CloserPropertySameName_ReturnsTrue()
    {
        var compilation = CreateCompilation("""
            public class Base
            {
                public int Value;
            }
            public class Derived : Base
            {
                public int Value { get; set; }
            }
            """);
        var baseType = compilation.GetTypeByMetadataName("Base")!;
        var derived = compilation.GetTypeByMetadataName("Derived")!;
        var field = baseType.GetMembers("Value").OfType<IFieldSymbol>().Single();

        Assert.True(MemberHidingHelpers.IsHiddenFrom(field, derived));
    }

    [Fact]
    public void IsHiddenFrom_CloserMethodSameName_ReturnsTrue()
    {
        var compilation = CreateCompilation("""
            public class Base
            {
                public int Value;
            }
            public class Derived : Base
            {
                public void Value() { }
            }
            """);
        var baseType = compilation.GetTypeByMetadataName("Base")!;
        var derived = compilation.GetTypeByMetadataName("Derived")!;
        var field = baseType.GetMembers("Value").OfType<IFieldSymbol>().Single();

        Assert.True(MemberHidingHelpers.IsHiddenFrom(field, derived));
    }

    [Fact]
    public void IsHiddenFrom_MemberDeclaredOnFromType_ReturnsFalse()
    {
        var compilation = CreateCompilation("""
            public class C
            {
                public int Value;
            }
            """);
        var type = compilation.GetTypeByMetadataName("C")!;
        var field = type.GetMembers("Value").OfType<IFieldSymbol>().Single();

        Assert.False(MemberHidingHelpers.IsHiddenFrom(field, type));
    }

    [Fact]
    public void IsHiddenFrom_MultiLevelWalkNoHider_ReturnsFalse()
    {
        // Mid declares no member named Value; walk Derived → Mid → Base finds no hider.
        var compilation = CreateCompilation("""
            public class Base
            {
                public int Value;
            }
            public class Mid : Base
            {
                public int Other { get; set; }
            }
            public class Derived : Mid
            {
            }
            """);
        var baseType = compilation.GetTypeByMetadataName("Base")!;
        var derived = compilation.GetTypeByMetadataName("Derived")!;
        var field = baseType.GetMembers("Value").OfType<IFieldSymbol>().Single();

        Assert.False(MemberHidingHelpers.IsHiddenFrom(field, derived));
    }

    private static CSharpCompilation CreateCompilation(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        return CSharpCompilation.Create(
            "MemberHidingHelpersTests",
            [tree],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }
}

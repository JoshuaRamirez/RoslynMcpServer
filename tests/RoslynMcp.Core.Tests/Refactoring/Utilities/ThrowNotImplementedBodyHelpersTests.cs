using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynMcp.Core.Refactoring.Utilities;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Utilities;

/// <summary>
/// Unit tests for <see cref="ThrowNotImplementedBodyHelpers"/> —
/// global:: throw shape and the CS8156 ref-return throw gate previously
/// duplicated on ImplementAbstract / SyntaxGenerationHelper.
/// </summary>
public class ThrowNotImplementedBodyHelpersTests
{
    [Fact]
    public void CreateThrowNotImplementedBody_IsSingleStatementThrowBlock()
    {
        var body = ThrowNotImplementedBodyHelpers.CreateThrowNotImplementedBody();

        Assert.Single(body.Statements);
        Assert.IsType<ThrowStatementSyntax>(body.Statements[0]);
    }

    [Fact]
    public void CreateThrowNotImplementedBody_ThrowsGlobalSystemNotImplementedException()
    {
        var body = ThrowNotImplementedBodyHelpers.CreateThrowNotImplementedBody();
        var throwStmt = Assert.IsType<ThrowStatementSyntax>(body.Statements[0]);
        var creation = Assert.IsType<ObjectCreationExpressionSyntax>(throwStmt.Expression);

        Assert.Equal("global::System.NotImplementedException", creation.Type.ToString());
        Assert.NotNull(creation.ArgumentList);
        Assert.Empty(creation.ArgumentList.Arguments);
    }

    [Fact]
    public void CreateThrowNotImplementedBody_NormalizedText_MatchesExpectedThrow()
    {
        var body = ThrowNotImplementedBodyHelpers.CreateThrowNotImplementedBody();
        var normalized = body.NormalizeWhitespace().ToFullString();

        Assert.Contains("throw new global::System.NotImplementedException()", normalized);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void RequiresThrowBody_Method_RespectsFlagWhenNotRef(bool throwNotImplemented, bool expected)
    {
        var method = GetMethod("""
            class C
            {
                public virtual int M() => 0;
            }
            """);

        Assert.Equal(expected, ThrowNotImplementedBodyHelpers.RequiresThrowBody(method, throwNotImplemented));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RequiresThrowBody_Method_AlwaysTrueForRefReturn(bool throwNotImplemented)
    {
        var method = GetMethod("""
            class C
            {
                private int _x;
                public virtual ref int M() => ref _x;
            }
            """);

        Assert.True(ThrowNotImplementedBodyHelpers.RequiresThrowBody(method, throwNotImplemented));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RequiresThrowBody_Method_AlwaysTrueForRefReadonlyReturn(bool throwNotImplemented)
    {
        var method = GetMethod("""
            class C
            {
                private int _x;
                public virtual ref readonly int M() => ref _x;
            }
            """);

        Assert.True(ThrowNotImplementedBodyHelpers.RequiresThrowBody(method, throwNotImplemented));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void RequiresThrowBody_Property_RespectsFlagWhenNotRef(bool throwNotImplemented, bool expected)
    {
        var property = GetProperty("""
            class C
            {
                public virtual int P { get; set; }
            }
            """);

        Assert.Equal(expected, ThrowNotImplementedBodyHelpers.RequiresThrowBody(property, throwNotImplemented));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RequiresThrowBody_Property_AlwaysTrueForRefReturn(bool throwNotImplemented)
    {
        var property = GetProperty("""
            class C
            {
                private int _x;
                public virtual ref int P => ref _x;
            }
            """);

        Assert.True(ThrowNotImplementedBodyHelpers.RequiresThrowBody(property, throwNotImplemented));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RequiresThrowBody_Property_AlwaysTrueForRefReadonlyReturn(bool throwNotImplemented)
    {
        var property = GetProperty("""
            class C
            {
                private int _x;
                public virtual ref readonly int P => ref _x;
            }
            """);

        Assert.True(ThrowNotImplementedBodyHelpers.RequiresThrowBody(property, throwNotImplemented));
    }

    private static IMethodSymbol GetMethod(string source)
    {
        var compilation = CreateCompilation(source);
        var type = compilation.GetTypeByMetadataName("C")!;
        return type.GetMembers("M").OfType<IMethodSymbol>().Single();
    }

    private static IPropertySymbol GetProperty(string source)
    {
        var compilation = CreateCompilation(source);
        var type = compilation.GetTypeByMetadataName("C")!;
        return type.GetMembers("P").OfType<IPropertySymbol>().Single();
    }

    private static CSharpCompilation CreateCompilation(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var refs = new[]
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
        };
        return CSharpCompilation.Create(
            "ThrowNotImplementedBodyHelpersTests",
            [tree],
            refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }
}

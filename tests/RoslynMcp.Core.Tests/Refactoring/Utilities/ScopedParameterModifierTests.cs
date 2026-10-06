using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using RoslynMcp.Core.Refactoring.Utilities;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Utilities;

/// <summary>
/// <see cref="SyntaxGenerationHelper.EmitsScopedModifier"/> decides when a
/// regenerated parameter must spell out <c>scoped</c> (#2404).
/// </summary>
public class ScopedParameterModifierTests
{
    private const string Source = """
        using System;
        using System.Diagnostics.CodeAnalysis;

        public ref struct Cursor
        {
            public int Position;
        }

        public abstract class Sample
        {
            public abstract void M(
                scoped ReadOnlySpan<char> scopedValue,
                ReadOnlySpan<char> plainValue,
                scoped Cursor scopedRefStruct,
                scoped ref Span<int> scopedRef,
                ref Span<int> plainRef,
                scoped in Span<int> scopedIn,
                in Span<int> plainIn,
                scoped ref readonly int scopedRefReadOnly,
                ref readonly int plainRefReadOnly,
                out Span<int> outSpan,
                out int outInt,
                [UnscopedRef] out int unscopedOut,
                int plainInt,
                params Span<int> paramsSpan);

            public abstract void P(params int[] paramsArray);
        }
        """;

    [Theory]
    [InlineData("M", "scopedValue", true)]
    [InlineData("M", "plainValue", false)]
    [InlineData("M", "scopedRefStruct", true)]
    [InlineData("M", "scopedRef", true)]
    [InlineData("M", "plainRef", false)]
    [InlineData("M", "scopedIn", true)]
    [InlineData("M", "plainIn", false)]
    [InlineData("M", "scopedRefReadOnly", true)]
    [InlineData("M", "plainRefReadOnly", false)]
    [InlineData("M", "outSpan", false)]
    [InlineData("M", "outInt", false)]
    [InlineData("M", "unscopedOut", false)]
    [InlineData("M", "plainInt", false)]
    [InlineData("M", "paramsSpan", true)]
    [InlineData("P", "paramsArray", false)]
    public void EmitsScopedModifier_MatchesDeclaredScope(string methodName, string parameterName, bool expected)
    {
        var parameter = GetParameter(methodName, parameterName);

        Assert.Equal(expected, SyntaxGenerationHelper.EmitsScopedModifier(parameter));
    }

    [Fact]
    public void EmitsScopedModifier_OutParameter_IsImplicitlyScopedRef()
    {
        // out is always ScopedRef; emitting scoped would be redundant.
        var parameter = GetParameter("M", "outSpan");

        Assert.Equal(ScopedKind.ScopedRef, parameter.ScopedKind);
        Assert.False(SyntaxGenerationHelper.EmitsScopedModifier(parameter));
    }

    private static IParameterSymbol GetParameter(string methodName, string parameterName)
    {
        var compilation = CSharpCompilation.Create(
            "ScopedParameterModifierTests",
            new[] { CSharpSyntaxTree.ParseText(Source) },
            new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) },
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var errors = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(errors.Count == 0, string.Join("\n", errors));

        var method = compilation.GetTypeByMetadataName("Sample")!
            .GetMembers(methodName)
            .OfType<IMethodSymbol>()
            .Single();
        return method.Parameters.Single(p => p.Name == parameterName);
    }
}

using Microsoft.CodeAnalysis;
using RoslynMcp.Core.Refactoring.Utilities;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Utilities;

/// <summary>
/// API-shape tests for <see cref="EqualityMemberCollector"/>.
/// The two-parameter overload must remain for NuGet binary compatibility.
/// </summary>
public class EqualityMemberCollectorTests
{
    [Fact]
    public void CollectMembers_TwoParameterOverload_IsPreserved()
    {
        var twoArg = typeof(EqualityMemberCollector).GetMethod(
            nameof(EqualityMemberCollector.CollectMembers),
            new[] { typeof(INamedTypeSymbol), typeof(IReadOnlyList<string>) });
        var threeArg = typeof(EqualityMemberCollector).GetMethod(
            nameof(EqualityMemberCollector.CollectMembers),
            new[] { typeof(INamedTypeSymbol), typeof(IReadOnlyList<string>), typeof(bool) });

        Assert.NotNull(twoArg);
        Assert.NotNull(threeArg);
        Assert.NotSame(twoArg, threeArg);
        Assert.Equal(typeof(List<ISymbol>), twoArg.ReturnType);
        Assert.Equal(typeof(List<ISymbol>), threeArg.ReturnType);
    }
}

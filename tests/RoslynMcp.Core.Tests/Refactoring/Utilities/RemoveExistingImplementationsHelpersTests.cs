using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using RoslynMcp.Core.Refactoring.Utilities;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Utilities;

public class RemoveExistingImplementationsHelpersTests
{
    [Fact]
    public void AddKeyed_NewTree_CreatesTreeAndPartBuckets()
    {
        var map = new Dictionary<SyntaxTree, Dictionary<int, HashSet<int>>>();
        var tree = CSharpSyntaxTree.ParseText("class A { }");

        RemoveExistingImplementationsHelpers.AddKeyed(map, tree, 0, 42);

        var byPart = Assert.Single(map);
        Assert.Same(tree, byPart.Key);
        var keys = Assert.Single(byPart.Value);
        Assert.Equal(0, keys.Key);
        Assert.Equal(new[] { 42 }, keys.Value);
    }

    [Fact]
    public void AddKeyed_SamePart_AccumulatesKeys()
    {
        var map = new Dictionary<SyntaxTree, Dictionary<int, HashSet<(int Start, int End)>>>();
        var tree = CSharpSyntaxTree.ParseText("class A { }");

        RemoveExistingImplementationsHelpers.AddKeyed(map, tree, 5, (1, 2));
        RemoveExistingImplementationsHelpers.AddKeyed(map, tree, 5, (3, 4));

        var keys = map[tree][5];
        Assert.Equal(2, keys.Count);
        Assert.Contains((1, 2), keys);
        Assert.Contains((3, 4), keys);
    }

    [Fact]
    public void AddKeyed_DuplicateKey_IsStoredOnce()
    {
        var map = new Dictionary<SyntaxTree, Dictionary<int, HashSet<(int Start, int End)>>>();
        var tree = CSharpSyntaxTree.ParseText("class A { }");

        RemoveExistingImplementationsHelpers.AddKeyed(map, tree, 0, (1, 2));
        RemoveExistingImplementationsHelpers.AddKeyed(map, tree, 0, (1, 2));

        Assert.Single(map[tree][0]);
    }

    [Fact]
    public void AddKeyed_DifferentPartSameTree_KeepsPartsSeparate()
    {
        var map = new Dictionary<SyntaxTree, Dictionary<int, HashSet<int>>>();
        var tree = CSharpSyntaxTree.ParseText("partial class A { } partial class A { }");

        RemoveExistingImplementationsHelpers.AddKeyed(map, tree, 0, 1);
        RemoveExistingImplementationsHelpers.AddKeyed(map, tree, 20, 2);

        var byPart = Assert.Single(map).Value;
        Assert.Equal(2, byPart.Count);
        Assert.Equal(new[] { 1 }, byPart[0]);
        Assert.Equal(new[] { 2 }, byPart[20]);
    }

    [Fact]
    public void AddKeyed_DifferentTrees_KeepsTreesSeparate()
    {
        var map = new Dictionary<SyntaxTree, Dictionary<int, HashSet<int>>>();
        var first = CSharpSyntaxTree.ParseText("partial class A { }");
        var second = CSharpSyntaxTree.ParseText("partial class A { }");

        RemoveExistingImplementationsHelpers.AddKeyed(map, first, 0, 1);
        RemoveExistingImplementationsHelpers.AddKeyed(map, second, 0, 2);

        Assert.Equal(2, map.Count);
        Assert.Equal(new[] { 1 }, map[first][0]);
        Assert.Equal(new[] { 2 }, map[second][0]);
    }
}

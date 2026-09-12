using Microsoft.CodeAnalysis;
using RoslynMcp.Core.Refactoring.Utilities;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Utilities;

public class TypeWalkKeyHelpersTests
{
    [Fact]
    public void TypeWalkKey_IncludesProjectIdentity()
    {
        var projectA = ProjectId.CreateNewId();
        var projectB = ProjectId.CreateNewId();
        const string fqn = "global::TestApp.Widget";

        var keyA = TypeWalkKeyHelpers.TypeWalkKey(projectA, fqn);
        var keyB = TypeWalkKeyHelpers.TypeWalkKey(projectB, fqn);

        Assert.NotEqual(keyA, keyB);
        Assert.Equal(keyA, TypeWalkKeyHelpers.TypeWalkKey(projectA, fqn));
        Assert.NotEqual(keyA, TypeWalkKeyHelpers.TypeWalkKey(projectA, "global::TestApp.Other"));
    }

    [Fact]
    public void TypeWalkKey_FileLocalIdentity_DistinguishesSameFqn()
    {
        var project = ProjectId.CreateNewId();
        const string fqn = "global::TestApp.Worker";

        var ordinary = TypeWalkKeyHelpers.TypeWalkKey(project, fqn);
        var fileA = TypeWalkKeyHelpers.TypeWalkKey(project, fqn, "/tmp/FileA.cs");
        var fileB = TypeWalkKeyHelpers.TypeWalkKey(project, fqn, "/tmp/FileB.cs");

        Assert.NotEqual(ordinary, fileA);
        Assert.NotEqual(ordinary, fileB);
        Assert.NotEqual(fileA, fileB);
        Assert.Equal(fileA, TypeWalkKeyHelpers.TypeWalkKey(project, fqn, "/tmp/FileA.cs"));
        Assert.Equal(ordinary, TypeWalkKeyHelpers.TypeWalkKey(project, fqn));
    }
}

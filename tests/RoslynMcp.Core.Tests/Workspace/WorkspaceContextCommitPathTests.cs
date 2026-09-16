using RoslynMcp.Core.Workspace;
using Xunit;

namespace RoslynMcp.Core.Tests.Workspace;

/// <summary>
/// CommitChangesAsync cannot easily drive multi-cased linked FilePaths through
/// a full MSBuild workspace on Linux CI; these tests lock the OS-aware comparer
/// that those path HashSets use (Windows CI proves case-variant dedupe).
/// </summary>
public class WorkspaceContextCommitPathTests
{
    [Fact]
    public void CommitPathComparer_CaseVariantPaths_DedupedOnlyOnWindows()
    {
        var set = new HashSet<string>(WorkspaceContext.CommitPathComparer);
        Assert.True(set.Add("/src/Foo.cs"));
        var addedSecond = set.Add("/src/foo.cs");

        if (OperatingSystem.IsWindows())
            Assert.False(addedSecond);
        else
            Assert.True(addedSecond);
    }

    [Fact]
    public void CommitPathComparer_IdenticalPaths_AlwaysDeduped()
    {
        var set = new HashSet<string>(WorkspaceContext.CommitPathComparer);
        Assert.True(set.Add("/src/Same.cs"));
        Assert.False(set.Add("/src/Same.cs"));
        Assert.Single(set);
    }
}

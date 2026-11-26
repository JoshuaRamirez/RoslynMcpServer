using RoslynMcp.Contracts.Errors;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Workspace;
using Xunit;

namespace RoslynMcp.Core.Tests.Workspace;

/// <summary>
/// Regression tests for #3061: a relative .csproj / .sln path was rejected with
/// "Path must be a .sln, .slnx, or .csproj file." although it has that extension.
/// </summary>
public class WorkspaceRelativePathMessageTests
{
    [Theory]
    [InlineData("Fx.csproj")]
    [InlineData("MySolution.sln")]
    [InlineData("sub/App.slnx")]
    public async Task RelativeProjectOrSolutionPath_ReportsAbsolutePathRequirement(string path)
    {
        var ex = await Assert.ThrowsAsync<RefactoringException>(() => new MSBuildWorkspaceProvider().CreateContextAsync(path));

        Assert.Equal(ErrorCodes.InvalidSourcePath, ex.ErrorCode);
        Assert.Contains("must be an absolute path", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(".sln, .slnx, or .csproj", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AbsolutePathWithWrongExtension_StillReportsExtension()
    {
        var path = Path.Combine(Path.GetTempPath(), "App.txt");

        var ex = await Assert.ThrowsAsync<RefactoringException>(() => new MSBuildWorkspaceProvider().CreateContextAsync(path));

        Assert.Equal(ErrorCodes.InvalidSourcePath, ex.ErrorCode);
        Assert.Equal("Path must be a .sln, .slnx, or .csproj file.", ex.Message);
    }
}

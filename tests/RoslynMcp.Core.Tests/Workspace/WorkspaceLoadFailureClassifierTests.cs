using RoslynMcp.Core.Workspace;
using Xunit;

namespace RoslynMcp.Core.Tests.Workspace;

public class WorkspaceLoadFailureClassifierTests
{
    [Theory]
    [InlineData("Cannot open project 'D:\\repo\\docker-compose.dcproj' because the file extension '.dcproj' is not associated with a language.")]
    [InlineData("Invalid project file path: 'bad-path'")]
    [InlineData("Project file not found: 'D:\\repo\\missing.csproj'")]
    public void Classify_ReturnsUnrecognizedProject_ForRoslynUnrecognizedProjectMessages(string message)
    {
        var category = WorkspaceLoadFailureClassifier.Classify(message);
        Assert.Equal(WorkspaceFailureCategory.UnrecognizedProject, category);
    }

    [Theory]
    [InlineData("warning NU1903: Package 'Foo' has a known vulnerability.")]
    [InlineData("The SDK 'Microsoft.Docker.Sdk' specified could not be found.")]
    [InlineData("Msbuild failed when processing the file 'D:\\repo\\app.csproj' with message: Build failed.")]
    public void Classify_ReturnsOtherFailure_ForNonRoslynUnrecognizedProjectMessages(string message)
    {
        var category = WorkspaceLoadFailureClassifier.Classify(message);
        Assert.Equal(WorkspaceFailureCategory.OtherFailure, category);
    }
}

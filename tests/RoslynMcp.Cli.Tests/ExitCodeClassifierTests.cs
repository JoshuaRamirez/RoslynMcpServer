using RoslynMcp.Cli;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Core.Refactoring;
using Xunit;

namespace RoslynMcp.Cli.Tests;

/// <summary>
/// Regression tests for #3065: exit code 3 (environment error) is decided by the error code,
/// not by "sdk" / "msbuild" substrings in user-supplied names inside the message.
/// </summary>
public class ExitCodeClassifierTests
{
    [Theory]
    [InlineData(ErrorCodes.SourceFileNotFound, "Source file not found: /tmp/h0430/App/SdkHelpers.cs")]
    [InlineData(ErrorCodes.SourceFileNotFound, "Source file not found: /tmp/h0430/App/MsBuildTasks.cs")]
    [InlineData(ErrorCodes.SourceFileNotFound, "Source file not found: /tmp/h0430/MySdkTools/App/Missing.cs")]
    [InlineData(ErrorCodes.InvalidNewName, "'1Sdk' is not a valid C# identifier.")]
    [InlineData(ErrorCodes.InvalidNewName, "'1Bad' is not a valid C# identifier.")]
    public void ToolErrorCode_WithSdkOrMsBuildInMessage_IsNotEnvironmentError(string code, string message)
    {
        Assert.False(ExitCodeClassifier.IsEnvironmentError(new RefactoringException(code, message)));
    }

    [Theory]
    [InlineData(ErrorCodes.MsBuildNotFound)]
    [InlineData(ErrorCodes.RoslynNotAvailable)]
    [InlineData(ErrorCodes.SdkNotFound)]
    [InlineData(ErrorCodes.SolutionLoadFailed)]
    public void EnvironmentErrorCode_IsEnvironmentError(string code)
    {
        Assert.True(ExitCodeClassifier.IsEnvironmentError(new RefactoringException(code, "Workspace could not be loaded.")));
    }

    [Fact]
    public void NonRefactoringException_KeepsMsBuildSdkHeuristicAndFileNotFound()
    {
        Assert.True(ExitCodeClassifier.IsEnvironmentError(new InvalidOperationException("No instances of MSBuild could be detected.")));
        Assert.True(ExitCodeClassifier.IsEnvironmentError(new FileNotFoundException("missing")));
        Assert.False(ExitCodeClassifier.IsEnvironmentError(new InvalidOperationException("Something else failed.")));
    }
}

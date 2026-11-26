using RoslynMcp.Core.Refactoring;

namespace RoslynMcp.Cli;

/// <summary>
/// Decides whether an exception escaping a tool run is an environment error (exit 3)
/// or an ordinary tool error (exit 1).
/// </summary>
public static class ExitCodeClassifier
{
    /// <summary>
    /// True when <paramref name="ex"/> is an environment failure (MSBuild / SDK / Roslyn
    /// unavailable, solution load failure).
    /// </summary>
    /// <remarks>
    /// A <see cref="RefactoringException"/> is classified by its machine-readable error code only:
    /// the 5xxx range (MsBuildNotFound, RoslynNotAvailable, SdkNotFound, SolutionLoadFailed) is an
    /// environment error, everything else is a tool / input error. Its message is never inspected,
    /// because tool messages echo user input verbatim, so a file or identifier spelled with "sdk" /
    /// "msbuild" (SdkHelpers.cs, 1Sdk, MySdkTools/) used to turn an ordinary error into exit 3 (#3065).
    /// The MSBuild / SDK message heuristic remains only for other exception types, such as
    /// MSBuildLocator or assembly-load failures that carry no error code.
    /// </remarks>
    public static bool IsEnvironmentError(Exception ex) => ex switch
    {
        RefactoringException refactoring => IsEnvironmentErrorCode(refactoring.ErrorCode),
        FileNotFoundException or DirectoryNotFoundException => true,
        _ => ex.Message.Contains("MSBuild", StringComparison.OrdinalIgnoreCase) ||
             ex.Message.Contains("SDK", StringComparison.OrdinalIgnoreCase)
    };

    private static bool IsEnvironmentErrorCode(string? errorCode) =>
        errorCode is { Length: 4 } && errorCode[0] == '5' && errorCode.All(char.IsAsciiDigit);
}

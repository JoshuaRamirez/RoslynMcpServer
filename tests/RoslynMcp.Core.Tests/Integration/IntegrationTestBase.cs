using Xunit;

namespace RoslynMcp.Core.Tests.Integration;

/// <summary>
/// Base class for integration tests that require MSBuild.
///
/// NOTE: These tests require special environment setup and may fail in standard
/// test runners due to MSBuild assembly loading conflicts. They are designed to
/// be run manually or in dedicated CI environments.
///
/// To run integration tests manually:
/// 1. Build the test project: dotnet build
/// 2. Run from the output directory with the correct SDK:
///    dotnet test bin/Debug/net9.0/RoslynMcp.Core.Tests.dll --filter "FullyQualifiedName~Integration"
/// </summary>
public abstract class IntegrationTestBase : IDisposable
{
    protected readonly string TestDir;
    protected readonly string SolutionPath;

    protected IntegrationTestBase()
    {
        // Check if MSBuild is available - these tests will skip if not
        if (!ModuleInitializer.MsBuildAvailable)
        {
            Skip.If(true, ModuleInitializer.MsBuildError ??
                "MSBuild not available. Integration tests require MSBuild setup.");
        }

        // Create a unique test directory by copying the test solution
        TestDir = Path.Combine(Path.GetTempPath(), $"RoslynMcpTest_{Guid.NewGuid():N}");
        var sourceDir = GetTestSolutionPath();

        CopyDirectory(sourceDir, TestDir);
        SolutionPath = Path.Combine(TestDir, "TestSolution.sln");
    }

    public void Dispose()
    {
        // Clean up test directory
        if (!string.IsNullOrEmpty(TestDir) && Directory.Exists(TestDir))
        {
            try
            {
                Directory.Delete(TestDir, recursive: true);
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
    }

    protected static string GetTestSolutionPath()
    {
        // Navigate up from test assembly to find testdata
        var dir = AppContext.BaseDirectory;
        while (dir != null && !Directory.Exists(Path.Combine(dir, "testdata")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        if (dir == null)
        {
            throw new InvalidOperationException("Could not find testdata directory");
        }

        return Path.Combine(dir, "testdata", "TestSolution");
    }

    protected static void CopyDirectory(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);

        foreach (var file in Directory.GetFiles(sourceDir))
        {
            var destFile = Path.Combine(destDir, Path.GetFileName(file));
            File.Copy(file, destFile);
        }

        foreach (var subDir in Directory.GetDirectories(sourceDir))
        {
            var destSubDir = Path.Combine(destDir, Path.GetFileName(subDir));
            CopyDirectory(subDir, destSubDir);
        }
    }
}

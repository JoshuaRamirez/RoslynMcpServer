using RoslynMcp.Core.FileSystem;
using Xunit;

namespace RoslynMcp.Core.Tests.FileSystem;

public class PathResolverTests
{
    private static string WinOrUnix(string winPath, string unixPath) =>
        OperatingSystem.IsWindows() ? winPath : unixPath;

    [Theory]
    [MemberData(nameof(IsAbsolutePathData))]
    public void IsAbsolutePath_ReturnsExpected(string? path, bool expected)
    {
        var result = PathResolver.IsAbsolutePath(path!);
        Assert.Equal(expected, result);
    }

    public static TheoryData<string?, bool> IsAbsolutePathData => new()
    {
        { WinOrUnix(@"C:\path\to\file.cs", "/path/to/file.cs"), true },
        { @"/usr/local/file.cs", true },
        { @"relative\path.cs", false },
        { @".\file.cs", false },
        { @"..\file.cs", false },
        { "", false },
        { null, false }
    };

    [Theory]
    [MemberData(nameof(IsValidCSharpFilePathData))]
    public void IsValidCSharpFilePath_ReturnsExpected(string path, bool expected)
    {
        var result = PathResolver.IsValidCSharpFilePath(path);
        Assert.Equal(expected, result);
    }

    public static TheoryData<string, bool> IsValidCSharpFilePathData => new()
    {
        { WinOrUnix(@"C:\project\src\File.cs", "/project/src/File.cs"), true },
        { WinOrUnix(@"C:\project\src\File.CS", "/project/src/File.CS"), true },
        { @"/home/user/File.cs", true },
        { WinOrUnix(@"C:\project\src\File.txt", "/project/src/File.txt"), false },
        { @"relative\File.cs", false },
        { "", false }
    };

    [Theory]
    [MemberData(nameof(IsValidSolutionOrProjectPathData))]
    public void IsValidSolutionOrProjectPath_ReturnsExpected(string path, bool expected)
    {
        var result = PathResolver.IsValidSolutionOrProjectPath(path);
        Assert.Equal(expected, result);
    }

    public static TheoryData<string, bool> IsValidSolutionOrProjectPathData => new()
    {
        { WinOrUnix(@"C:\project\Solution.sln", "/project/Solution.sln"), true },
        { WinOrUnix(@"C:\project\Solution.slnx", "/project/Solution.slnx"), true },
        { WinOrUnix(@"C:\project\Project.csproj", "/project/Project.csproj"), true },
        { WinOrUnix(@"C:\project\File.cs", "/project/File.cs"), false },
        { @"relative\Solution.sln", false }
    };

    [Fact]
    public void GetPathComparisonKey_IdenticalPath_ReturnsSameKey()
    {
        var path = Path.Combine(Path.GetTempPath(), "roslyn-mcp-pr-same-" + Path.GetRandomFileName() + ".cs");

        Assert.Equal(
            PathResolver.GetPathComparisonKey(path),
            PathResolver.GetPathComparisonKey(path));
    }

    [Fact]
    public void GetPathComparisonKey_CaseVariantExistingPath_MatchesFilesystemBehavior()
    {
        var path = Path.Combine(Path.GetTempPath(), "roslyn-mcp-pr-case-" + Path.GetRandomFileName() + ".cs");
        File.WriteAllText(path, "class C {}");

        try
        {
            var query = Path.Combine(Path.GetDirectoryName(path)!, FlipAsciiCase(Path.GetFileName(path)));
            var expectedEqual = File.Exists(query);

            var createdKey = PathResolver.GetPathComparisonKey(path);
            var queryKey = PathResolver.GetPathComparisonKey(query);

            Assert.Equal(
                expectedEqual,
                string.Equals(createdKey, queryKey, StringComparison.Ordinal));

            // On case-insensitive volumes the key must be the directory entry's
            // actual casing, not the wrong-cased alias spelling (Codex P1).
            if (expectedEqual)
            {
                Assert.Equal(createdKey, queryKey);
                Assert.Equal(
                    Path.GetFileName(path),
                    Path.GetFileName(queryKey),
                    StringComparer.Ordinal);
            }
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    private static string FlipAsciiCase(string value)
    {
        var chars = value.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (chars[i] is >= 'a' and <= 'z')
                chars[i] = char.ToUpperInvariant(chars[i]);
            else if (chars[i] is >= 'A' and <= 'Z')
                chars[i] = char.ToLowerInvariant(chars[i]);
        }

        return new string(chars);
    }
}

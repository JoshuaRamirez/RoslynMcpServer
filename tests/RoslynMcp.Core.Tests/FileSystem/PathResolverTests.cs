using Xunit;
using RoslynMcp.Core.FileSystem;

namespace RoslynMcp.Core.Tests.FileSystem;

public class PathResolverTests
{
    [Theory]
    [InlineData(@"C:\path\to\file.cs", true)]
    [InlineData(@"/usr/local/file.cs", true)]
    [InlineData(@"relative\path.cs", false)]
    [InlineData(@".\file.cs", false)]
    [InlineData(@"..\file.cs", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsAbsolutePath_ReturnsExpected(string? path, bool expected)
    {
        var result = PathResolver.IsAbsolutePath(path!);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(@"C:\project\src\File.cs", true)]
    [InlineData(@"C:\project\src\File.CS", true)]
    [InlineData(@"/home/user/File.cs", true)]
    [InlineData(@"C:\project\src\File.txt", false)]
    [InlineData(@"relative\File.cs", false)]
    [InlineData("", false)]
    public void IsValidCSharpFilePath_ReturnsExpected(string path, bool expected)
    {
        var result = PathResolver.IsValidCSharpFilePath(path);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(@"C:\project\Solution.sln", true)]
    [InlineData(@"C:\project\Project.csproj", true)]
    [InlineData(@"C:\project\File.cs", false)]
    [InlineData(@"relative\Solution.sln", false)]
    public void IsValidSolutionOrProjectPath_ReturnsExpected(string path, bool expected)
    {
        var result = PathResolver.IsValidSolutionOrProjectPath(path);
        Assert.Equal(expected, result);
    }
}

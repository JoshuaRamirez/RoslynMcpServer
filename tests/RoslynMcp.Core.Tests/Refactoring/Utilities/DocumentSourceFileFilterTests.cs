using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using RoslynMcp.Core.FileSystem;
using RoslynMcp.Core.Refactoring.Utilities;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Utilities;

public class DocumentSourceFileFilterTests
{
    [Fact]
    public void FilterDocumentsBySourceFile_KeepsMatchingNormalizedPath()
    {
        using var workspace = new AdhocWorkspace();
        var project = workspace.AddProject("P", LanguageNames.CSharp);
        var keepPath = Path.Combine(Path.GetTempPath(), "roslyn-mcp-dsf-keep-" + Path.GetRandomFileName() + ".cs");
        var dropPath = Path.Combine(Path.GetTempPath(), "roslyn-mcp-dsf-drop-" + Path.GetRandomFileName() + ".cs");
        File.WriteAllText(keepPath, "class Keep {}");
        File.WriteAllText(dropPath, "class Drop {}");
        try
        {
            var keep = workspace.AddDocument(project.Id, Path.GetFileName(keepPath), SourceText.From("class Keep {}"))
                .WithFilePath(keepPath);
            workspace.TryApplyChanges(keep.Project.Solution);
            keep = workspace.CurrentSolution.GetDocument(keep.Id)!;

            var drop = workspace.AddDocument(project.Id, Path.GetFileName(dropPath), SourceText.From("class Drop {}"))
                .WithFilePath(dropPath);
            workspace.TryApplyChanges(drop.Project.Solution);
            drop = workspace.CurrentSolution.GetDocument(drop.Id)!;
            keep = workspace.CurrentSolution.GetDocument(keep.Id)!;

            var result = DocumentSourceFileFilter.FilterDocumentsBySourceFile(
                new List<Document> { keep, drop },
                keepPath);

            Assert.Single(result);
            Assert.Equal(keep.Id, result[0].Id);
        }
        finally
        {
            if (File.Exists(keepPath)) File.Delete(keepPath);
            if (File.Exists(dropPath)) File.Delete(dropPath);
        }
    }

    [Fact]
    public void FilterDocumentsBySourceFile_MatchesOrdinalIgnoreCase()
    {
        using var workspace = new AdhocWorkspace();
        var project = workspace.AddProject("P", LanguageNames.CSharp);
        var path = Path.Combine(Path.GetTempPath(), "roslyn-mcp-dsf-case-" + Path.GetRandomFileName() + ".cs");
        File.WriteAllText(path, "class C {}");
        try
        {
            var document = workspace.AddDocument(project.Id, Path.GetFileName(path), SourceText.From("class C {}"))
                .WithFilePath(path);
            workspace.TryApplyChanges(document.Project.Solution);
            document = workspace.CurrentSolution.GetDocument(document.Id)!;

            var flipped = FlipAsciiCase(Path.GetFileName(path));
            var query = Path.Combine(Path.GetDirectoryName(path)!, flipped);
            Assert.False(string.Equals(path, query, StringComparison.Ordinal));

            var result = DocumentSourceFileFilter.FilterDocumentsBySourceFile(
                new List<Document> { document },
                query);

            Assert.Single(result);
            Assert.Equal(document.Id, result[0].Id);
            Assert.Equal(
                PathResolver.NormalizePath(path),
                PathResolver.NormalizePath(query),
                ignoreCase: true);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void FilterDocumentsBySourceFile_ReturnsEmpty_WhenNoMatch()
    {
        using var workspace = new AdhocWorkspace();
        var project = workspace.AddProject("P", LanguageNames.CSharp);
        var path = Path.Combine(Path.GetTempPath(), "roslyn-mcp-dsf-none-" + Path.GetRandomFileName() + ".cs");
        File.WriteAllText(path, "class C {}");
        try
        {
            var document = workspace.AddDocument(project.Id, Path.GetFileName(path), SourceText.From("class C {}"))
                .WithFilePath(path);
            workspace.TryApplyChanges(document.Project.Solution);
            document = workspace.CurrentSolution.GetDocument(document.Id)!;

            var missing = Path.Combine(Path.GetTempPath(), "roslyn-mcp-dsf-missing-" + Path.GetRandomFileName() + ".cs");
            var result = DocumentSourceFileFilter.FilterDocumentsBySourceFile(
                new List<Document> { document },
                missing);

            Assert.Empty(result);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
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

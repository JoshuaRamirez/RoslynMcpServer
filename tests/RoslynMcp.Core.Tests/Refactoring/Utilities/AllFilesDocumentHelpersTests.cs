using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using RoslynMcp.Core.FileSystem;
using RoslynMcp.Core.Refactoring.Utilities;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Utilities;

public class AllFilesDocumentHelpersTests
{
    [Fact]
    public void EnumerateCsharpDocuments_FiltersToCsOrderedByPath()
    {
        using var workspace = new AdhocWorkspace();
        var project = workspace.AddProject("P", LanguageNames.CSharp);

        var csB = Path.Combine(Path.GetTempPath(), "roslyn-mcp-afdh-b-" + Path.GetRandomFileName() + ".cs");
        var csA = Path.Combine(Path.GetTempPath(), "roslyn-mcp-afdh-a-" + Path.GetRandomFileName() + ".cs");
        var txt = Path.Combine(Path.GetTempPath(), "roslyn-mcp-afdh-" + Path.GetRandomFileName() + ".txt");
        File.WriteAllText(csB, "class B {}");
        File.WriteAllText(csA, "class A {}");
        File.WriteAllText(txt, "not csharp");
        try
        {
            AddOnDiskDocument(workspace, project.Id, csB, "class B {}");
            AddOnDiskDocument(workspace, project.Id, csA, "class A {}");
            AddOnDiskDocument(workspace, project.Id, txt, "not csharp");

            var result = AllFilesDocumentHelpers.EnumerateCsharpDocuments(workspace.CurrentSolution);

            Assert.Equal(2, result.Count);
            Assert.Equal(csA, result[0].FilePath);
            Assert.Equal(csB, result[1].FilePath);
            Assert.True(string.CompareOrdinal(result[0].FilePath, result[1].FilePath) < 0);
        }
        finally
        {
            TryDelete(csA);
            TryDelete(csB);
            TryDelete(txt);
        }
    }

    [Fact]
    public void GroupByLinkedPath_GroupsSameComparisonKey()
    {
        using var workspace = new AdhocWorkspace();
        var projectA = workspace.AddProject("A", LanguageNames.CSharp);
        var projectB = workspace.AddProject("B", LanguageNames.CSharp);

        var sharedPath = Path.Combine(Path.GetTempPath(), "roslyn-mcp-afdh-link-" + Path.GetRandomFileName() + ".cs");
        var otherPath = Path.Combine(Path.GetTempPath(), "roslyn-mcp-afdh-other-" + Path.GetRandomFileName() + ".cs");
        File.WriteAllText(sharedPath, "class Shared {}");
        File.WriteAllText(otherPath, "class Other {}");
        try
        {
            var sharedA = AddOnDiskDocument(workspace, projectA.Id, sharedPath, "class Shared {}");
            var sharedB = AddOnDiskDocument(workspace, projectB.Id, sharedPath, "class Shared {}");
            var other = AddOnDiskDocument(workspace, projectA.Id, otherPath, "class Other {}");

            var groups = AllFilesDocumentHelpers.GroupByLinkedPath(
                new List<Document> { sharedB, other, sharedA });

            Assert.Equal(2, groups.Count);
            var sharedGroup = groups.Single(g =>
                PathResolver.GetPathComparisonKey(g[0].FilePath!) ==
                PathResolver.GetPathComparisonKey(sharedPath));
            Assert.Equal(2, sharedGroup.Count);
            Assert.Contains(sharedGroup, d => d.Id == sharedA.Id);
            Assert.Contains(sharedGroup, d => d.Id == sharedB.Id);
            Assert.True(string.CompareOrdinal(sharedGroup[0].Project.Name, sharedGroup[1].Project.Name) <= 0);
        }
        finally
        {
            TryDelete(sharedPath);
            TryDelete(otherPath);
        }
    }

    [Fact]
    public async Task CoalesceLinkedDocumentTextAsync_CopiesTextToEditableSiblings()
    {
        using var workspace = new AdhocWorkspace();
        var projectA = workspace.AddProject("A", LanguageNames.CSharp);
        var projectB = workspace.AddProject("B", LanguageNames.CSharp);

        var sharedPath = Path.Combine(Path.GetTempPath(), "roslyn-mcp-afdh-coal-" + Path.GetRandomFileName() + ".cs");
        File.WriteAllText(sharedPath, "class Shared { }");
        try
        {
            var docA = AddOnDiskDocument(workspace, projectA.Id, sharedPath, "class Shared { }");
            var docB = AddOnDiskDocument(workspace, projectB.Id, sharedPath, "class Shared { }");

            var before = workspace.CurrentSolution;
            var updated = before.WithDocumentText(docA.Id, SourceText.From("class Shared { void M() {} }"));

            var coalesced = await AllFilesDocumentHelpers.CoalesceLinkedDocumentTextAsync(
                before,
                updated,
                workspace,
                CancellationToken.None);

            var textA = await coalesced.GetDocument(docA.Id)!.GetTextAsync();
            var textB = await coalesced.GetDocument(docB.Id)!.GetTextAsync();
            Assert.Equal("class Shared { void M() {} }", textA.ToString());
            Assert.Equal(textA.ToString(), textB.ToString());
        }
        finally
        {
            TryDelete(sharedPath);
        }
    }

    [Fact]
    public async Task CoalesceLinkedDocumentTextAsync_NoOp_WhenNoLinkedSiblings()
    {
        using var workspace = new AdhocWorkspace();
        var project = workspace.AddProject("P", LanguageNames.CSharp);
        var path = Path.Combine(Path.GetTempPath(), "roslyn-mcp-afdh-solo-" + Path.GetRandomFileName() + ".cs");
        File.WriteAllText(path, "class Solo { }");
        try
        {
            var doc = AddOnDiskDocument(workspace, project.Id, path, "class Solo { }");
            var before = workspace.CurrentSolution;
            var updated = before.WithDocumentText(doc.Id, SourceText.From("class Solo { void M() {} }"));

            var coalesced = await AllFilesDocumentHelpers.CoalesceLinkedDocumentTextAsync(
                before,
                updated,
                workspace,
                CancellationToken.None);

            var text = await coalesced.GetDocument(doc.Id)!.GetTextAsync();
            Assert.Equal("class Solo { void M() {} }", text.ToString());
            Assert.Same(updated, coalesced);
        }
        finally
        {
            TryDelete(path);
        }
    }

    private static Document AddOnDiskDocument(
        AdhocWorkspace workspace,
        ProjectId projectId,
        string path,
        string text)
    {
        var document = workspace.AddDocument(projectId, Path.GetFileName(path), SourceText.From(text))
            .WithFilePath(path);
        Assert.True(workspace.TryApplyChanges(document.Project.Solution));
        return workspace.CurrentSolution.GetDocument(document.Id)!;
    }

    private static void TryDelete(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
    }
}

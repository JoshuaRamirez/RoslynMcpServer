using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Core.FileSystem;
using RoslynMcp.Core.Refactoring;
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

    [Fact]
    public void BuildLinkedPathCounts_CountsLinkedViewsByComparisonKey()
    {
        using var workspace = new AdhocWorkspace();
        var projectA = workspace.AddProject("A", LanguageNames.CSharp);
        var projectB = workspace.AddProject("B", LanguageNames.CSharp);

        var sharedPath = Path.Combine(Path.GetTempPath(), "roslyn-mcp-afdh-counts-" + Path.GetRandomFileName() + ".cs");
        var soloPath = Path.Combine(Path.GetTempPath(), "roslyn-mcp-afdh-counts-solo-" + Path.GetRandomFileName() + ".cs");
        File.WriteAllText(sharedPath, "class Shared {}");
        File.WriteAllText(soloPath, "class Solo {}");
        try
        {
            AddOnDiskDocument(workspace, projectA.Id, sharedPath, "class Shared {}");
            AddOnDiskDocument(workspace, projectB.Id, sharedPath, "class Shared {}");
            AddOnDiskDocument(workspace, projectA.Id, soloPath, "class Solo {}");

            var counts = AllFilesDocumentHelpers.BuildLinkedPathCounts(workspace.CurrentSolution);

            var sharedKey = PathResolver.GetPathComparisonKey(sharedPath);
            var soloKey = PathResolver.GetPathComparisonKey(soloPath);
            Assert.Equal(2, counts[sharedKey]);
            Assert.Equal(1, counts[soloKey]);
        }
        finally
        {
            TryDelete(sharedPath);
            TryDelete(soloPath);
        }
    }

    [Fact]
    public void DocumentPathHasLinkedMultiView_TrueOnlyWhenCountExceedsOne()
    {
        using var workspace = new AdhocWorkspace();
        var projectA = workspace.AddProject("A", LanguageNames.CSharp);
        var projectB = workspace.AddProject("B", LanguageNames.CSharp);

        var sharedPath = Path.Combine(Path.GetTempPath(), "roslyn-mcp-afdh-mv-" + Path.GetRandomFileName() + ".cs");
        var soloPath = Path.Combine(Path.GetTempPath(), "roslyn-mcp-afdh-mv-solo-" + Path.GetRandomFileName() + ".cs");
        File.WriteAllText(sharedPath, "class Shared {}");
        File.WriteAllText(soloPath, "class Solo {}");
        try
        {
            var sharedA = AddOnDiskDocument(workspace, projectA.Id, sharedPath, "class Shared {}");
            AddOnDiskDocument(workspace, projectB.Id, sharedPath, "class Shared {}");
            var solo = AddOnDiskDocument(workspace, projectA.Id, soloPath, "class Solo {}");

            var counts = AllFilesDocumentHelpers.BuildLinkedPathCounts(workspace.CurrentSolution);

            Assert.True(AllFilesDocumentHelpers.DocumentPathHasLinkedMultiView(sharedA, counts));
            Assert.False(AllFilesDocumentHelpers.DocumentPathHasLinkedMultiView(solo, counts));
        }
        finally
        {
            TryDelete(sharedPath);
            TryDelete(soloPath);
        }
    }


    [Fact]
    public void FilterAllFilesDocumentsBySourceFile_ExactPath_ReturnsExactAndLinkedSiblings()
    {
        using var workspace = new AdhocWorkspace();
        var projectA = workspace.AddProject("A", LanguageNames.CSharp);
        var projectB = workspace.AddProject("B", LanguageNames.CSharp);

        var sharedPath = Path.Combine(Path.GetTempPath(), "roslyn-mcp-afdh-filter-" + Path.GetRandomFileName() + ".cs");
        var otherPath = Path.Combine(Path.GetTempPath(), "roslyn-mcp-afdh-filter-other-" + Path.GetRandomFileName() + ".cs");
        File.WriteAllText(sharedPath, "class Shared {}");
        File.WriteAllText(otherPath, "class Other {}");
        try
        {
            AddOnDiskDocument(workspace, projectA.Id, sharedPath, "class Shared {}");
            AddOnDiskDocument(workspace, projectB.Id, sharedPath, "class Shared {}");
            AddOnDiskDocument(workspace, projectA.Id, otherPath, "class Other {}");

            var docs = AllFilesDocumentHelpers.EnumerateCsharpDocuments(workspace.CurrentSolution);
            var filtered = AllFilesDocumentHelpers.FilterAllFilesDocumentsBySourceFile(docs, sharedPath);

            Assert.Equal(2, filtered.Count);
            Assert.All(filtered, d => Assert.Equal(
                PathResolver.GetPathComparisonKey(sharedPath),
                PathResolver.GetPathComparisonKey(d.FilePath!)));
        }
        finally
        {
            TryDelete(sharedPath);
            TryDelete(otherPath);
        }
    }

    [Fact]
    public void FilterAllFilesDocumentsBySourceFile_MissingFile_ThrowsSourceFileNotFound()
    {
        using var workspace = new AdhocWorkspace();
        var project = workspace.AddProject("P", LanguageNames.CSharp);
        var existing = Path.Combine(Path.GetTempPath(), "roslyn-mcp-afdh-filter-exist-" + Path.GetRandomFileName() + ".cs");
        var missing = Path.Combine(Path.GetTempPath(), "roslyn-mcp-afdh-filter-missing-" + Path.GetRandomFileName() + ".cs");
        File.WriteAllText(existing, "class E {}");
        try
        {
            AddOnDiskDocument(workspace, project.Id, existing, "class E {}");
            var docs = AllFilesDocumentHelpers.EnumerateCsharpDocuments(workspace.CurrentSolution);

            var ex = Assert.Throws<RefactoringException>(() =>
                AllFilesDocumentHelpers.FilterAllFilesDocumentsBySourceFile(docs, missing));

            Assert.Equal(ErrorCodes.SourceFileNotFound, ex.ErrorCode);
            Assert.Contains(missing, ex.Message);
        }
        finally
        {
            TryDelete(existing);
        }
    }

    [Fact]
    public void FilterAllFilesDocumentsBySourceFile_ExistsButNotInWorkspace_ThrowsSourceNotInWorkspace()
    {
        using var workspace = new AdhocWorkspace();
        var project = workspace.AddProject("P", LanguageNames.CSharp);
        var inWorkspace = Path.Combine(Path.GetTempPath(), "roslyn-mcp-afdh-filter-inws-" + Path.GetRandomFileName() + ".cs");
        var onDiskOnly = Path.Combine(Path.GetTempPath(), "roslyn-mcp-afdh-filter-ondisk-" + Path.GetRandomFileName() + ".cs");
        File.WriteAllText(inWorkspace, "class In {}");
        File.WriteAllText(onDiskOnly, "class Out {}");
        try
        {
            AddOnDiskDocument(workspace, project.Id, inWorkspace, "class In {}");
            var docs = AllFilesDocumentHelpers.EnumerateCsharpDocuments(workspace.CurrentSolution);

            var ex = Assert.Throws<RefactoringException>(() =>
                AllFilesDocumentHelpers.FilterAllFilesDocumentsBySourceFile(docs, onDiskOnly));

            Assert.Equal(ErrorCodes.SourceNotInWorkspace, ex.ErrorCode);
            Assert.Contains(onDiskOnly, ex.Message);
        }
        finally
        {
            TryDelete(inWorkspace);
            TryDelete(onDiskOnly);
        }
    }


    [Fact]
    public void FilterAllFilesDocumentsBySourceFile_IgnoreCaseAlias_ReturnsSingleMatch()
    {
        using var workspace = new AdhocWorkspace();
        var project = workspace.AddProject("P", LanguageNames.CSharp);

        var exactPath = Path.Combine(Path.GetTempPath(), "roslyn-mcp-afdh-filter-Case-" + Path.GetRandomFileName() + "-FileA.cs");
        File.WriteAllText(exactPath, "class FileA {}");
        try
        {
            AddOnDiskDocument(workspace, project.Id, exactPath, "class FileA {}");
            var docs = AllFilesDocumentHelpers.EnumerateCsharpDocuments(workspace.CurrentSolution);
            var aliased = FlipAsciiLetterCasing(exactPath);
            Assert.False(
                string.Equals(exactPath, aliased, StringComparison.Ordinal),
                "FlipAsciiLetterCasing must change the request spelling.");

            var filtered = AllFilesDocumentHelpers.FilterAllFilesDocumentsBySourceFile(docs, aliased);

            Assert.Single(filtered);
            Assert.Equal(
                PathResolver.GetPathComparisonKey(exactPath),
                PathResolver.GetPathComparisonKey(filtered[0].FilePath!));
        }
        finally
        {
            TryDelete(exactPath);
        }
    }

    [SkippableFact]
    public void FilterAllFilesDocumentsBySourceFile_AmbiguousIgnoreCase_ThrowsSourceNotInWorkspace()
    {
        using var workspace = new AdhocWorkspace();
        var project = workspace.AddProject("P", LanguageNames.CSharp);

        var dir = Path.Combine(Path.GetTempPath(), "roslyn-mcp-afdh-filter-amb-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        var upperPath = Path.Combine(dir, "FileA.cs");
        var lowerPath = Path.Combine(dir, "filea.cs");
        File.WriteAllText(upperPath, "class Upper {}");
        File.WriteAllText(lowerPath, "class Lower {}");
        try
        {
            Skip.If(
                string.Equals(
                    PathResolver.GetPathComparisonKey(upperPath),
                    PathResolver.GetPathComparisonKey(lowerPath),
                    StringComparison.Ordinal),
                "Volume does not preserve case-distinct paths.");

            AddOnDiskDocument(workspace, project.Id, upperPath, "class Upper {}");
            AddOnDiskDocument(workspace, project.Id, lowerPath, "class Lower {}");
            var docs = AllFilesDocumentHelpers.EnumerateCsharpDocuments(workspace.CurrentSolution);
            var ambiguous = FlipAsciiLetterCasing(upperPath);

            var ex = Assert.Throws<RefactoringException>(() =>
                AllFilesDocumentHelpers.FilterAllFilesDocumentsBySourceFile(docs, ambiguous));

            Assert.Equal(ErrorCodes.SourceNotInWorkspace, ex.ErrorCode);
            Assert.Contains("exact file path casing", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TryDelete(upperPath);
            TryDelete(lowerPath);
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    [SkippableFact]
    public void FilterAllFilesDocumentsBySourceFile_CasingMismatchMissingPath_RejectFlagThrowsSourceFileNotFound()
    {
        using var workspace = new AdhocWorkspace();
        var project = workspace.AddProject("P", LanguageNames.CSharp);

        var exactPath = Path.Combine(Path.GetTempPath(), "roslyn-mcp-afdh-filter-Casemismatch-" + Path.GetRandomFileName() + "-FileA.cs");
        File.WriteAllText(exactPath, "class FileA {}");
        try
        {
            AddOnDiskDocument(workspace, project.Id, exactPath, "class FileA {}");
            var docs = AllFilesDocumentHelpers.EnumerateCsharpDocuments(workspace.CurrentSolution);
            var aliased = FlipAsciiLetterCasing(exactPath);
            Assert.False(
                string.Equals(exactPath, aliased, StringComparison.Ordinal),
                "FlipAsciiLetterCasing must change the request spelling.");
            Skip.If(
                File.Exists(aliased),
                "Volume resolves flipped casing as an existing path.");

            var ex = Assert.Throws<RefactoringException>(() =>
                AllFilesDocumentHelpers.FilterAllFilesDocumentsBySourceFile(
                    docs, aliased, rejectMissingPathCasingMismatch: true));

            Assert.Equal(ErrorCodes.SourceFileNotFound, ex.ErrorCode);
            Assert.Contains(aliased, ex.Message);
        }
        finally
        {
            TryDelete(exactPath);
        }
    }

    [Fact]
    public void FilterAllFilesDocumentsBySourceFile_CasingMismatchMissingPath_DefaultAllowsIgnoreCaseMatch()
    {
        using var workspace = new AdhocWorkspace();
        var project = workspace.AddProject("P", LanguageNames.CSharp);

        var exactPath = Path.Combine(Path.GetTempPath(), "roslyn-mcp-afdh-filter-CaseDefault-" + Path.GetRandomFileName() + "-FileA.cs");
        File.WriteAllText(exactPath, "class FileA {}");
        try
        {
            AddOnDiskDocument(workspace, project.Id, exactPath, "class FileA {}");
            var docs = AllFilesDocumentHelpers.EnumerateCsharpDocuments(workspace.CurrentSolution);
            var aliased = FlipAsciiLetterCasing(exactPath);
            Assert.False(
                string.Equals(exactPath, aliased, StringComparison.Ordinal),
                "FlipAsciiLetterCasing must change the request spelling.");

            // Default path (flag false) keeps today's ignore-case allow behavior even
            // when the flipped spelling is missing on a case-sensitive volume.
            var filtered = AllFilesDocumentHelpers.FilterAllFilesDocumentsBySourceFile(docs, aliased);

            Assert.Single(filtered);
            Assert.Equal(
                PathResolver.GetPathComparisonKey(exactPath),
                PathResolver.GetPathComparisonKey(filtered[0].FilePath!));
        }
        finally
        {
            TryDelete(exactPath);
        }
    }

    private static string FlipAsciiLetterCasing(string path)
    {
        var chars = path.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (char.IsLetter(chars[i]))
                chars[i] = char.IsUpper(chars[i]) ? char.ToLowerInvariant(chars[i]) : char.ToUpperInvariant(chars[i]);
        }

        return new string(chars);
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

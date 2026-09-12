using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Utilities;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Utilities;

public class DocumentForTreeHelpersTests
{
    [Fact]
    public async Task GetDocumentForTree_ReturnsDocument_WhenGetDocumentFindsTree()
    {
        using var workspace = new AdhocWorkspace();
        var project = workspace.AddProject("P", LanguageNames.CSharp);
        var document = workspace.AddDocument(project.Id, "C.cs", SourceText.From("class C {}"));
        var tree = await document.GetSyntaxTreeAsync();
        Assert.NotNull(tree);

        var result = DocumentForTreeHelpers.GetDocumentForTree(
            workspace.CurrentSolution, tree!, "C");

        Assert.Equal(document.Id, result.Id);
    }

    [Fact]
    public void GetDocumentForTree_FallsBackToFilePath_WhenTreeNotInSolution()
    {
        using var workspace = new AdhocWorkspace();
        var project = workspace.AddProject("P", LanguageNames.CSharp);
        var path = Path.Combine(Path.GetTempPath(), "roslyn-mcp-dft-" + Path.GetRandomFileName() + ".cs");
        File.WriteAllText(path, "class C {}");
        try
        {
            var document = workspace.AddDocument(project.Id, Path.GetFileName(path), SourceText.From("class C {}"))
                .WithFilePath(path);
            Assert.True(workspace.TryApplyChanges(document.Project.Solution));
            document = workspace.CurrentSolution.GetDocument(document.Id)!;
            Assert.Equal(path, document.FilePath);

            // Separate parse tree with same FilePath — not the document's tree instance
            var orphanTree = CSharpSyntaxTree.ParseText("class C {}", path: path);
            Assert.Null(workspace.CurrentSolution.GetDocument(orphanTree));

            var result = DocumentForTreeHelpers.GetDocumentForTree(
                workspace.CurrentSolution, orphanTree, "C");

            Assert.Equal(document.Id, result.Id);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void GetDocumentForTree_ThrowsDocumentNotEditable_WhenTreeHasNoDocument()
    {
        using var workspace = new AdhocWorkspace();
        _ = workspace.AddProject("P", LanguageNames.CSharp);

        var orphanTree = CSharpSyntaxTree.ParseText("class Missing {}");
        Assert.True(string.IsNullOrEmpty(orphanTree.FilePath));
        Assert.Null(workspace.CurrentSolution.GetDocument(orphanTree));

        var ex = Assert.Throws<RefactoringException>(() =>
            DocumentForTreeHelpers.GetDocumentForTree(
                workspace.CurrentSolution, orphanTree, "Missing"));

        Assert.Equal(ErrorCodes.DocumentNotEditable, ex.ErrorCode);
        Assert.Contains("Could not locate a declaring document for type 'Missing'", ex.Message);
    }

    [Fact]
    public void GetDocumentForTree_Throws_WhenFilePathHasNoMatchingDocument()
    {
        using var workspace = new AdhocWorkspace();
        _ = workspace.AddProject("P", LanguageNames.CSharp);

        var missingPath = Path.Combine(Path.GetTempPath(), "roslyn-mcp-dft-missing-" + Path.GetRandomFileName() + ".cs");
        Assert.False(File.Exists(missingPath));
        var orphanTree = CSharpSyntaxTree.ParseText("class Ghost {}", path: missingPath);
        Assert.Null(workspace.CurrentSolution.GetDocument(orphanTree));
        Assert.Empty(workspace.CurrentSolution.GetDocumentIdsWithFilePath(missingPath));

        var ex = Assert.Throws<RefactoringException>(() =>
            DocumentForTreeHelpers.GetDocumentForTree(
                workspace.CurrentSolution, orphanTree, "Ghost"));

        Assert.Equal(ErrorCodes.DocumentNotEditable, ex.ErrorCode);
        Assert.Contains("Ghost", ex.Message);
    }

    [Fact]
    public void GetDocumentByFilePath_ReturnsDocument_WhenFilePathMatches()
    {
        using var workspace = new AdhocWorkspace();
        var project = workspace.AddProject("P", LanguageNames.CSharp);
        var path = Path.Combine(Path.GetTempPath(), "roslyn-mcp-dbfp-" + Path.GetRandomFileName() + ".cs");
        File.WriteAllText(path, "class C {}");
        try
        {
            var document = workspace.AddDocument(project.Id, Path.GetFileName(path), SourceText.From("class C {}"))
                .WithFilePath(path);
            Assert.True(workspace.TryApplyChanges(document.Project.Solution));
            document = workspace.CurrentSolution.GetDocument(document.Id)!;
            Assert.Equal(path, document.FilePath);

            // Separate parse tree with same FilePath — not the document's tree instance
            var orphanTree = CSharpSyntaxTree.ParseText("class C {}", path: path);
            Assert.Null(workspace.CurrentSolution.GetDocument(orphanTree));

            var result = DocumentForTreeHelpers.GetDocumentByFilePath(
                workspace.CurrentSolution, orphanTree);

            Assert.NotNull(result);
            Assert.Equal(document.Id, result!.Id);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void GetDocumentByFilePath_ReturnsNull_WhenFilePathEmpty()
    {
        using var workspace = new AdhocWorkspace();
        _ = workspace.AddProject("P", LanguageNames.CSharp);

        var orphanTree = CSharpSyntaxTree.ParseText("class Missing {}");
        Assert.True(string.IsNullOrEmpty(orphanTree.FilePath));

        var result = DocumentForTreeHelpers.GetDocumentByFilePath(
            workspace.CurrentSolution, orphanTree);

        Assert.Null(result);
    }

    [Fact]
    public void GetDocumentByFilePath_ReturnsNull_WhenFilePathHasNoMatchingDocument()
    {
        using var workspace = new AdhocWorkspace();
        _ = workspace.AddProject("P", LanguageNames.CSharp);

        var missingPath = Path.Combine(Path.GetTempPath(), "roslyn-mcp-dbfp-missing-" + Path.GetRandomFileName() + ".cs");
        Assert.False(File.Exists(missingPath));
        var orphanTree = CSharpSyntaxTree.ParseText("class Ghost {}", path: missingPath);
        Assert.Null(workspace.CurrentSolution.GetDocument(orphanTree));
        Assert.Empty(workspace.CurrentSolution.GetDocumentIdsWithFilePath(missingPath));

        var result = DocumentForTreeHelpers.GetDocumentByFilePath(
            workspace.CurrentSolution, orphanTree);

        Assert.Null(result);
    }

}

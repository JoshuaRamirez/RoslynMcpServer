using System.IO;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using RoslynMcp.Core.Refactoring.Utilities;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Utilities;

public class DocumentEditableHelpersTests
{
    [Fact]
    public void IsDocumentEditable_False_WhenFilePathNullOrWhitespace()
    {
        using var workspace = new AdhocWorkspace();
        var project = workspace.AddProject("P", LanguageNames.CSharp);
        // AdhocWorkspace documents start with no on-disk FilePath
        var document = workspace.AddDocument(project.Id, "C.cs", SourceText.From("class C {}"));

        Assert.True(string.IsNullOrWhiteSpace(document.FilePath));
        Assert.False(DocumentEditableHelpers.IsDocumentEditable(document, workspace));
    }

    [Fact]
    public void IsDocumentEditable_False_WhenFilePathMissingOnDisk()
    {
        using var workspace = new AdhocWorkspace();
        var project = workspace.AddProject("P", LanguageNames.CSharp);
        var missingPath = Path.Combine(Path.GetTempPath(), "roslyn-mcp-missing-" + Path.GetRandomFileName() + ".cs");
        Assert.False(File.Exists(missingPath));

        var document = workspace.AddDocument(project.Id, "Missing.cs", SourceText.From("class C {}"))
            .WithFilePath(missingPath);

        // Apply WithFilePath via solution update so workspace document has the path
        workspace.TryApplyChanges(document.Project.Solution);
        document = workspace.CurrentSolution.GetDocument(document.Id)!;

        Assert.Equal(missingPath, document.FilePath);
        Assert.False(DocumentEditableHelpers.IsDocumentEditable(document, workspace));
    }

    [Fact]
    public void IsDocumentEditable_True_WhenOnDiskAndWorkspaceCanChangeDocument()
    {
        using var workspace = new AdhocWorkspace();
        var project = workspace.AddProject("P", LanguageNames.CSharp);
        var path = Path.Combine(Path.GetTempPath(), "roslyn-mcp-editable-" + Path.GetRandomFileName() + ".cs");
        File.WriteAllText(path, "class C {}");
        try
        {
            var document = workspace.AddDocument(project.Id, Path.GetFileName(path), SourceText.From("class C {}"))
                .WithFilePath(path);
            workspace.TryApplyChanges(document.Project.Solution);
            document = workspace.CurrentSolution.GetDocument(document.Id)!;

            Assert.True(File.Exists(document.FilePath));
            Assert.True(workspace.CanApplyChange(ApplyChangesKind.ChangeDocument));
            Assert.True(DocumentEditableHelpers.IsDocumentEditable(document, workspace));
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void IsDocumentEditable_False_WhenFilePathEmptyString()
    {
        using var workspace = new AdhocWorkspace();
        var project = workspace.AddProject("P", LanguageNames.CSharp);
        var document = workspace.AddDocument(project.Id, "EmptyPath.cs", SourceText.From("class C {}"))
            .WithFilePath("   ");
        workspace.TryApplyChanges(document.Project.Solution);
        document = workspace.CurrentSolution.GetDocument(document.Id)!;

        Assert.True(string.IsNullOrWhiteSpace(document.FilePath));
        Assert.False(DocumentEditableHelpers.IsDocumentEditable(document, workspace));
    }
}

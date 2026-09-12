using System.Collections.Immutable;
using System.IO;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Core.Refactoring;
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
            .WithFilePath(string.Empty);
        workspace.TryApplyChanges(document.Project.Solution);
        document = workspace.CurrentSolution.GetDocument(document.Id)!;

        Assert.Equal(string.Empty, document.FilePath);
        Assert.False(DocumentEditableHelpers.IsDocumentEditable(document, workspace));
    }

    [Fact]
    public void IsDocumentEditable_False_WhenFilePathWhitespace()
    {
        using var workspace = new AdhocWorkspace();
        var project = workspace.AddProject("P", LanguageNames.CSharp);
        var document = workspace.AddDocument(project.Id, "WhitespacePath.cs", SourceText.From("class C {}"))
            .WithFilePath("   ");
        workspace.TryApplyChanges(document.Project.Solution);
        document = workspace.CurrentSolution.GetDocument(document.Id)!;

        Assert.True(string.IsNullOrWhiteSpace(document.FilePath));
        Assert.False(DocumentEditableHelpers.IsDocumentEditable(document, workspace));
    }

    [Fact]
    public async Task IsDocumentEditable_False_WhenSourceGeneratedDocument()
    {
        using var workspace = new AdhocWorkspace();
        var project = workspace.AddProject("P", LanguageNames.CSharp);
        project = project.AddDocument("Host.cs", "namespace Host { public class H { } }").Project;
        project = project.AddAnalyzerReference(
            new TestSourceGeneratorReference(new DummySourceGenerator().AsSourceGenerator()));

        Assert.True(workspace.TryApplyChanges(project.Solution));
        project = workspace.CurrentSolution.GetProject(project.Id)!;

        var generated = (await project.GetSourceGeneratedDocumentsAsync()).ToList();
        Assert.NotEmpty(generated);
        var document = Assert.IsType<SourceGeneratedDocument>(generated[0]);

        Assert.False(DocumentEditableHelpers.IsDocumentEditable(document, workspace));
    }


    [Fact]
    public void ValidateDocumentIsEditable_Throws_WhenFilePathNullOrWhitespace()
    {
        using var workspace = new AdhocWorkspace();
        var project = workspace.AddProject("P", LanguageNames.CSharp);
        var document = workspace.AddDocument(project.Id, "C.cs", SourceText.From("class C {}"));

        Assert.True(string.IsNullOrWhiteSpace(document.FilePath));
        var ex = Assert.Throws<RefactoringException>(() =>
            DocumentEditableHelpers.ValidateDocumentIsEditable(document, workspace));
        Assert.Equal(ErrorCodes.DocumentNotEditable, ex.ErrorCode);
        Assert.Contains("is not editable.", ex.Message);
    }

    [Fact]
    public void ValidateDocumentIsEditable_Throws_WhenFilePathMissingOnDisk()
    {
        using var workspace = new AdhocWorkspace();
        var project = workspace.AddProject("P", LanguageNames.CSharp);
        var missingPath = Path.Combine(Path.GetTempPath(), "roslyn-mcp-missing-" + Path.GetRandomFileName() + ".cs");
        Assert.False(File.Exists(missingPath));

        var document = workspace.AddDocument(project.Id, "Missing.cs", SourceText.From("class C {}"))
            .WithFilePath(missingPath);
        workspace.TryApplyChanges(document.Project.Solution);
        document = workspace.CurrentSolution.GetDocument(document.Id)!;

        Assert.Equal(missingPath, document.FilePath);
        var ex = Assert.Throws<RefactoringException>(() =>
            DocumentEditableHelpers.ValidateDocumentIsEditable(document, workspace));
        Assert.Equal(ErrorCodes.DocumentNotEditable, ex.ErrorCode);
        Assert.Contains("is not editable.", ex.Message);
    }

    [Fact]
    public void ValidateDocumentIsEditable_Throws_WhenFilePathEmptyString()
    {
        using var workspace = new AdhocWorkspace();
        var project = workspace.AddProject("P", LanguageNames.CSharp);
        var document = workspace.AddDocument(project.Id, "EmptyPath.cs", SourceText.From("class C {}"))
            .WithFilePath(string.Empty);
        workspace.TryApplyChanges(document.Project.Solution);
        document = workspace.CurrentSolution.GetDocument(document.Id)!;

        Assert.Equal(string.Empty, document.FilePath);
        var ex = Assert.Throws<RefactoringException>(() =>
            DocumentEditableHelpers.ValidateDocumentIsEditable(document, workspace));
        Assert.Equal(ErrorCodes.DocumentNotEditable, ex.ErrorCode);
    }

    [Fact]
    public void ValidateDocumentIsEditable_Throws_WhenFilePathWhitespace()
    {
        using var workspace = new AdhocWorkspace();
        var project = workspace.AddProject("P", LanguageNames.CSharp);
        var document = workspace.AddDocument(project.Id, "WhitespacePath.cs", SourceText.From("class C {}"))
            .WithFilePath("   ");
        workspace.TryApplyChanges(document.Project.Solution);
        document = workspace.CurrentSolution.GetDocument(document.Id)!;

        Assert.True(string.IsNullOrWhiteSpace(document.FilePath));
        var ex = Assert.Throws<RefactoringException>(() =>
            DocumentEditableHelpers.ValidateDocumentIsEditable(document, workspace));
        Assert.Equal(ErrorCodes.DocumentNotEditable, ex.ErrorCode);
    }

    [Fact]
    public void ValidateDocumentIsEditable_DoesNotThrow_WhenOnDiskAndWorkspaceCanChangeDocument()
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
            DocumentEditableHelpers.ValidateDocumentIsEditable(document, workspace);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public async Task ValidateDocumentIsEditable_Throws_WhenSourceGeneratedDocument()
    {
        using var workspace = new AdhocWorkspace();
        var project = workspace.AddProject("P", LanguageNames.CSharp);
        project = project.AddDocument("Host.cs", "namespace Host { public class H { } }").Project;
        project = project.AddAnalyzerReference(
            new TestSourceGeneratorReference(new DummySourceGenerator().AsSourceGenerator()));

        Assert.True(workspace.TryApplyChanges(project.Solution));
        project = workspace.CurrentSolution.GetProject(project.Id)!;

        var generated = (await project.GetSourceGeneratedDocumentsAsync()).ToList();
        Assert.NotEmpty(generated);
        var document = Assert.IsType<SourceGeneratedDocument>(generated[0]);

        var ex = Assert.Throws<RefactoringException>(() =>
            DocumentEditableHelpers.ValidateDocumentIsEditable(document, workspace));
        Assert.Equal(ErrorCodes.DocumentNotEditable, ex.ErrorCode);
        Assert.Contains("source-generated", ex.Message);
    }

    /// <summary>
    /// Minimal IIncrementalGenerator that emits one source file so AdhocWorkspace
    /// can surface a real <see cref="SourceGeneratedDocument"/>.
    /// </summary>
    private sealed class DummySourceGenerator : IIncrementalGenerator
    {
        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            context.RegisterPostInitializationOutput(ctx =>
                ctx.AddSource(
                    "Dummy.g.cs",
                    "// <auto-generated/>\nnamespace Gen { public class G { } }\n"));
        }
    }

    /// <summary>
    /// In-memory <see cref="AnalyzerReference"/> that exposes source generators
    /// (AnalyzerImageReference only wraps diagnostic analyzers).
    /// </summary>
    private sealed class TestSourceGeneratorReference : AnalyzerReference
    {
        private readonly ImmutableArray<ISourceGenerator> _generators;

        public TestSourceGeneratorReference(ISourceGenerator generator)
        {
            _generators = ImmutableArray.Create(generator);
            Id = Guid.NewGuid();
        }

        public override string? FullPath => null;
        public override string Display => nameof(TestSourceGeneratorReference);
        public override object Id { get; }

        public override ImmutableArray<DiagnosticAnalyzer> GetAnalyzers(string language) =>
            ImmutableArray<DiagnosticAnalyzer>.Empty;

        public override ImmutableArray<DiagnosticAnalyzer> GetAnalyzersForAllLanguages() =>
            ImmutableArray<DiagnosticAnalyzer>.Empty;

        public override ImmutableArray<ISourceGenerator> GetGenerators(string language) =>
            _generators;

        public override ImmutableArray<ISourceGenerator> GetGeneratorsForAllLanguages() =>
            _generators;
    }
}

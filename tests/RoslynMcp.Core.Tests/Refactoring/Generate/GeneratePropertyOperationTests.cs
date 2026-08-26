using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Generate;
using RoslynMcp.Core.Workspace;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Generate;

/// <summary>
/// Operation-level tests for <see cref="GeneratePropertyOperation"/>.
/// </summary>
public class GeneratePropertyOperationTests
{
    #region Input Validation

    [Fact]
    public void Validate_MissingSourceFile_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            GeneratePropertyOperation.Validate(new GeneratePropertyParams
            {
                SourceFile = "",
                TypeName = "Widget",
                PropertyName = "Name",
                PropertyType = "string"
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public void Validate_MissingTypeName_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            GeneratePropertyOperation.Validate(new GeneratePropertyParams
            {
                SourceFile = AbsoluteTestPath(),
                TypeName = "",
                PropertyName = "Name",
                PropertyType = "string"
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public void Validate_MissingPropertyNameAndFieldName_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            GeneratePropertyOperation.Validate(new GeneratePropertyParams
            {
                SourceFile = AbsoluteTestPath(),
                TypeName = "Widget",
                PropertyType = "string"
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public void Validate_MissingPropertyTypeWithoutField_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            GeneratePropertyOperation.Validate(new GeneratePropertyParams
            {
                SourceFile = AbsoluteTestPath(),
                TypeName = "Widget",
                PropertyName = "Name"
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public void Validate_RelativePath_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            GeneratePropertyOperation.Validate(new GeneratePropertyParams
            {
                SourceFile = "Types.cs",
                TypeName = "Widget",
                PropertyName = "Name",
                PropertyType = "string"
            }));

        Assert.Equal(ErrorCodes.InvalidSourcePath, ex.ErrorCode);
    }

    [Fact]
    public void Validate_MissingFile_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            GeneratePropertyOperation.Validate(new GeneratePropertyParams
            {
                SourceFile = AbsoluteTestPath(),
                TypeName = "Widget",
                PropertyName = "Name",
                PropertyType = "string"
            }));

        Assert.Equal(ErrorCodes.SourceFileNotFound, ex.ErrorCode);
    }

    [Fact]
    public void Validate_InvalidVisibility_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            GeneratePropertyOperation.Validate(new GeneratePropertyParams
            {
                SourceFile = AbsoluteTestPath(),
                TypeName = "Widget",
                PropertyName = "Name",
                PropertyType = "string",
                Visibility = "secret"
            }));

        Assert.Equal(ErrorCodes.InvalidVisibility, ex.ErrorCode);
    }

    #endregion

    #region P0 Happy Path

    [SkippableFact]
    public async Task GenerateProperty_AutoProperty_AddsGetSet()
    {
        const string source = """
            namespace TestApp;

            public class Widget
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new GeneratePropertyOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GeneratePropertyParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget",
            PropertyName = "Name",
            PropertyType = "string"
        });

        Assert.True(result.Success);
        Assert.False(result.Preview);
        Assert.NotNull(result.Symbol);
        Assert.Equal("Name", result.Symbol.Name);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("public string Name { get; set; }", updated);
    }

    [SkippableFact]
    public async Task GenerateProperty_InitOnly_AddsGetInit()
    {
        const string source = """
            namespace TestApp;

            public class Widget
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new GeneratePropertyOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GeneratePropertyParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget",
            PropertyName = "Id",
            PropertyType = "int",
            InitOnly = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("public int Id { get; init; }", updated);
        Assert.DoesNotContain("{ get; set; }", updated);
    }

    [SkippableFact]
    public async Task GenerateProperty_BackingField_AddsExpressionAccessors()
    {
        const string source = """
            namespace TestApp;

            public class Widget
            {
                private string _name;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new GeneratePropertyOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GeneratePropertyParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget",
            FieldName = "_name"
        });

        Assert.True(result.Success);
        Assert.Equal("Name", result.Symbol!.Name);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("private string _name;", updated);
        Assert.Contains("get => _name;", updated);
        Assert.Contains("set => _name = value;", updated);
    }

    [SkippableFact]
    public async Task GenerateProperty_Preview_DoesNotWriteFiles()
    {
        const string source = """
            namespace TestApp;

            public class Widget
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new GeneratePropertyOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var result = await operation.ExecuteAsync(new GeneratePropertyParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget",
            PropertyName = "Name",
            PropertyType = "string",
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotEmpty(result.PendingChanges);
        Assert.Contains("get; set;", result.PendingChanges[0].AfterSnippet);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    #endregion

    #region Reject Cases

    [SkippableFact]
    public async Task GenerateProperty_NoSymbol_Throws()
    {
        const string source = """
            namespace TestApp;

            public class Widget
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new GeneratePropertyOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GeneratePropertyParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Missing",
                PropertyName = "Name",
                PropertyType = "string"
            }));

        Assert.Equal(ErrorCodes.SymbolNotFound, ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task GenerateProperty_UnsupportedTarget_Enum_Throws()
    {
        const string source = """
            namespace TestApp;

            public enum Status
            {
                Ready
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new GeneratePropertyOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GeneratePropertyParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Status",
                PropertyName = "Name",
                PropertyType = "string"
            }));

        Assert.Equal(ErrorCodes.InvalidSymbolKind, ex.ErrorCode);
        Assert.Contains("not a supported target", ex.Message);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task GenerateProperty_NameClash_Throws()
    {
        const string source = """
            namespace TestApp;

            public class Widget
            {
                public string Name { get; set; }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new GeneratePropertyOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GeneratePropertyParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Widget",
                PropertyName = "Name",
                PropertyType = "string"
            }));

        Assert.Equal(ErrorCodes.NameCollision, ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [Fact]
    public void GenerateProperty_UneditableDocument_Throws()
    {
        var workspace = new AdhocWorkspace();
        var project = workspace.AddProject("P", LanguageNames.CSharp);
        var document = workspace.AddDocument(project.Id, "Generated.cs", SourceText.From("class C {}"));

        var ex = Assert.Throws<RefactoringException>(() =>
            GeneratePropertyOperation.ValidateDocumentIsEditable(document, workspace));

        Assert.Equal(ErrorCodes.DocumentNotEditable, ex.ErrorCode);
    }

    [SkippableFact]
    public async Task GenerateProperty_MissingField_Throws()
    {
        const string source = """
            namespace TestApp;

            public class Widget
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new GeneratePropertyOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GeneratePropertyParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Widget",
                FieldName = "_missing"
            }));

        Assert.Equal(ErrorCodes.SymbolNotFound, ex.ErrorCode);
    }

    #endregion

    #region Helpers

    private static string AbsoluteTestPath() =>
        Path.Combine(Path.GetTempPath(), "RoslynMcpGeneratePropertyMissing.cs");

    private static string NormalizeNewlines(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal);

    private sealed class TempWorkspace : IAsyncDisposable
    {
        public required string DirectoryPath { get; init; }
        public required string ProjectPath { get; init; }
        public required string SourcePath { get; init; }
        public required WorkspaceContext Context { get; init; }

        public static async Task<TempWorkspace> CreateAsync(string source, string fileName = "Types.cs")
        {
            Skip.IfNot(ModuleInitializer.MsBuildAvailable, ModuleInitializer.MsBuildError ?? "MSBuild not available");

            var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpGenerateProperty_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);

            var projectPath = Path.Combine(directory, "TestApp.csproj");
            var sourcePath = Path.Combine(directory, fileName);

            await File.WriteAllTextAsync(projectPath, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net9.0</TargetFramework>
                    <Nullable>enable</Nullable>
                  </PropertyGroup>
                </Project>
                """);
            await File.WriteAllTextAsync(sourcePath, source);

            try
            {
                var provider = new MSBuildWorkspaceProvider();
                var context = await provider.CreateContextAsync(projectPath);
                if (context.GetDocumentByPath(sourcePath) == null)
                {
                    context.Dispose();
                    throw new InvalidOperationException($"Workspace loaded but did not include {sourcePath}.");
                }

                return new TempWorkspace
                {
                    DirectoryPath = directory,
                    ProjectPath = projectPath,
                    SourcePath = sourcePath,
                    Context = context
                };
            }
            catch (Exception ex) when (ex is not SkipException)
            {
                try
                {
                    Directory.Delete(directory, recursive: true);
                }
                catch
                {
                    // ignore cleanup failures
                }

                Skip.If(true, $"Workspace load failed: {ex.Message}");
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            Context.Dispose();
            await Task.Run(() =>
            {
                try
                {
                    Directory.Delete(DirectoryPath, recursive: true);
                }
                catch
                {
                    // ignore locked temp files
                }
            });
        }
    }

    #endregion
}

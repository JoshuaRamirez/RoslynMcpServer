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
/// Operation-level tests for <see cref="ImplementAbstractOperation"/>.
/// </summary>
public class ImplementAbstractOperationTests
{
    #region Input Validation

    [Fact]
    public void Validate_MissingSourceFile_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ImplementAbstractOperation.Validate(new ImplementAbstractParams
            {
                SourceFile = "",
                TypeName = "Widget"
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public void Validate_MissingTypeName_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ImplementAbstractOperation.Validate(new ImplementAbstractParams
            {
                SourceFile = AbsoluteTestPath(),
                TypeName = ""
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public void Validate_RelativePath_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ImplementAbstractOperation.Validate(new ImplementAbstractParams
            {
                SourceFile = "Types.cs",
                TypeName = "Widget"
            }));

        Assert.Equal(ErrorCodes.InvalidSourcePath, ex.ErrorCode);
    }

    [Fact]
    public void Validate_MissingFile_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ImplementAbstractOperation.Validate(new ImplementAbstractParams
            {
                SourceFile = AbsoluteTestPath(),
                TypeName = "Widget"
            }));

        Assert.Equal(ErrorCodes.SourceFileNotFound, ex.ErrorCode);
    }

    #endregion

    #region P0 Happy Path

    [SkippableFact]
    public async Task ImplementAbstract_Method_AddsOverrideStub()
    {
        const string source = """
            namespace TestApp;

            public abstract class Shape
            {
                public abstract void Draw();
            }

            public class Circle : Shape
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ImplementAbstractOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ImplementAbstractParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Circle"
        });

        Assert.True(result.Success);
        Assert.False(result.Preview);
        Assert.NotNull(result.Symbol);
        Assert.Equal("Circle", result.Symbol.Name);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("public override void Draw()", updated);
        Assert.Contains("throw new global::System.NotImplementedException();", updated);
    }

    [SkippableFact]
    public async Task ImplementAbstract_Property_AddsOverrideAccessors()
    {
        const string source = """
            namespace TestApp;

            public abstract class Shape
            {
                public abstract int Area { get; }
            }

            public class Circle : Shape
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ImplementAbstractOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ImplementAbstractParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Circle"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("public override int Area", updated);
        Assert.Contains("get", updated);
        Assert.Contains("throw new global::System.NotImplementedException();", updated);
        Assert.DoesNotContain("set", updated);
    }

    [SkippableFact]
    public async Task ImplementAbstract_MethodAndProperty_AddsBoth()
    {
        const string source = """
            namespace TestApp;

            public abstract class Shape
            {
                public abstract string Name { get; set; }

                public abstract void Draw();
            }

            public class Circle : Shape
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ImplementAbstractOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ImplementAbstractParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Circle"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("public override string Name", updated);
        Assert.Contains("public override void Draw()", updated);
        Assert.Contains("throw new global::System.NotImplementedException();", updated);
    }

    [SkippableFact]
    public async Task ImplementAbstract_ProtectedMember_PreservesAccessibility()
    {
        const string source = """
            namespace TestApp;

            public abstract class Shape
            {
                protected abstract void Paint();
            }

            public class Circle : Shape
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ImplementAbstractOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ImplementAbstractParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Circle"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("protected override void Paint()", updated);
    }

    [SkippableFact]
    public async Task ImplementAbstract_AlreadyImplementedMember_GeneratesRemaining()
    {
        const string source = """
            namespace TestApp;

            public abstract class Shape
            {
                public abstract void Draw();
                public abstract void Resize();
            }

            public class Circle : Shape
            {
                public override void Draw()
                {
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ImplementAbstractOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ImplementAbstractParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Circle"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("public override void Resize()", updated);
        Assert.Contains("throw new global::System.NotImplementedException();", updated);
        Assert.Equal(1, CountOccurrences(updated, "public override void Draw()"));
    }

    [SkippableFact]
    public async Task ImplementAbstract_Preview_DoesNotWriteFiles()
    {
        const string source = """
            namespace TestApp;

            public abstract class Shape
            {
                public abstract void Draw();
            }

            public class Circle : Shape
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ImplementAbstractOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var result = await operation.ExecuteAsync(new ImplementAbstractParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Circle",
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.NotEmpty(result.PendingChanges);
        Assert.Contains("Draw", result.PendingChanges[0].AfterSnippet);
        Assert.Contains("NotImplementedException", result.PendingChanges[0].AfterSnippet);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task ImplementAbstract_MembersFilter_ImplementsRequestedOnly()
    {
        const string source = """
            namespace TestApp;

            public abstract class Shape
            {
                public abstract void Draw();
                public abstract void Resize();
            }

            public class Circle : Shape
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ImplementAbstractOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ImplementAbstractParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Circle",
            Members = new[] { "Resize" }
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("public override void Resize()", updated);
        Assert.DoesNotContain("public override void Draw()", updated);
    }

    #endregion

    #region Reject Cases

    [SkippableFact]
    public async Task ImplementAbstract_NoSymbol_Throws()
    {
        const string source = """
            namespace TestApp;

            public abstract class Shape
            {
                public abstract void Draw();
            }

            public class Circle : Shape
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ImplementAbstractOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ImplementAbstractParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Missing"
            }));

        Assert.Equal(ErrorCodes.SymbolNotFound, ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task ImplementAbstract_NoUnimplementedAbstractMembers_Throws()
    {
        const string source = """
            namespace TestApp;

            public class Widget
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ImplementAbstractOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ImplementAbstractParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Widget"
            }));

        Assert.Equal(ErrorCodes.NoUnimplementedAbstractMembers, ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task ImplementAbstract_AlreadyFullyImplemented_Throws()
    {
        const string source = """
            namespace TestApp;

            public abstract class Shape
            {
                public abstract void Draw();
            }

            public class Circle : Shape
            {
                public override void Draw()
                {
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ImplementAbstractOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ImplementAbstractParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Circle"
            }));

        Assert.Equal(ErrorCodes.NoUnimplementedAbstractMembers, ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task ImplementAbstract_UnsupportedTarget_Enum_Throws()
    {
        const string source = """
            namespace TestApp;

            public enum Status
            {
                Ready
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ImplementAbstractOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ImplementAbstractParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Status"
            }));

        Assert.Equal(ErrorCodes.InvalidSymbolKind, ex.ErrorCode);
        Assert.Contains("not a supported target", ex.Message);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task ImplementAbstract_UnsupportedTarget_Interface_Throws()
    {
        const string source = """
            namespace TestApp;

            public interface IDrawable
            {
                void Draw();
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ImplementAbstractOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ImplementAbstractParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "IDrawable"
            }));

        Assert.Equal(ErrorCodes.InvalidSymbolKind, ex.ErrorCode);
        Assert.Contains("not a supported target", ex.Message);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [Fact]
    public void ImplementAbstract_UneditableDocument_Throws()
    {
        var workspace = new AdhocWorkspace();
        var project = workspace.AddProject("P", LanguageNames.CSharp);
        var document = workspace.AddDocument(project.Id, "Generated.cs", SourceText.From("class C {}"));

        var ex = Assert.Throws<RefactoringException>(() =>
            ImplementAbstractOperation.ValidateDocumentIsEditable(document, workspace));

        Assert.Equal(ErrorCodes.DocumentNotEditable, ex.ErrorCode);
    }

    #endregion

    #region Helpers

    private static string AbsoluteTestPath() =>
        Path.Combine(Path.GetTempPath(), "RoslynMcpImplementAbstractMissing.cs");

    private static string NormalizeNewlines(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal);

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    private sealed class TempWorkspace : IAsyncDisposable
    {
        public required string DirectoryPath { get; init; }
        public required string ProjectPath { get; init; }
        public required string SourcePath { get; init; }
        public required WorkspaceContext Context { get; init; }

        public static async Task<TempWorkspace> CreateAsync(string source, string fileName = "Types.cs")
        {
            Skip.IfNot(ModuleInitializer.MsBuildAvailable, ModuleInitializer.MsBuildError ?? "MSBuild not available");

            var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpImplementAbstract_" + Guid.NewGuid().ToString("N"));
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

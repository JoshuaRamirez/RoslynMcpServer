using Microsoft.CodeAnalysis;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Generate;
using RoslynMcp.Core.Workspace;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Generate;

/// <summary>
/// Regression tests for #2878: <c>generate_property</c> initOnly emitted C# 9 <c>init</c> accessors on
/// LangVersion 7.3 (CS8370). It is now refused there (single) / skipped (allFiles); C# 9+ still works.
/// </summary>
public class GeneratePropertyInitOnlyLangVersionTests
{
    private const string Source = """
        class Point
        {
            private int _y;

            public int Sum() => _y;
        }
        """;

    [SkippableTheory]
    [InlineData(null)]
    [InlineData("_y")]
    public async Task InitOnly_OnCSharp73_IsRefusedAndWritesNothing(string? fieldName)
    {
        await using var workspace = await TempWorkspace.CreateAsync(Source, "7.3");
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            new GeneratePropertyOperation(workspace.Context).ExecuteAsync(new GeneratePropertyParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Point",
                PropertyName = fieldName == null ? "X" : null,
                PropertyType = fieldName == null ? "int" : null,
                FieldName = fieldName,
                InitOnly = true
            }));

        Assert.Equal(ErrorCodes.CompilationError, ex.ErrorCode);
        Assert.Contains("C# 9", ex.Message, StringComparison.Ordinal);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task InitOnly_AllFilesOnCSharp73_DoesNotEmitInitAccessor()
    {
        await using var workspace = await TempWorkspace.CreateAsync(Source, "7.3");

        // The only type would need an init accessor, so allFiles skips it.
        await new GeneratePropertyOperation(workspace.Context).ExecuteAsync(new GeneratePropertyParams
        {
            AllFiles = true,
            PropertyName = "X",
            PropertyType = "int",
            InitOnly = true
        });

        Assert.DoesNotContain("init", await File.ReadAllTextAsync(workspace.SourcePath), StringComparison.Ordinal);
        var errors = await workspace.GetErrorsAsync();
        Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors));
    }

    [SkippableFact]
    public async Task NotInitOnly_OnCSharp73_StillGenerates()
    {
        await using var workspace = await TempWorkspace.CreateAsync(Source, "7.3");

        var result = await new GeneratePropertyOperation(workspace.Context).ExecuteAsync(new GeneratePropertyParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Point",
            PropertyName = "X",
            PropertyType = "int"
        });

        Assert.True(result.Success, result.Error?.Message);
        Assert.Contains("set;", await File.ReadAllTextAsync(workspace.SourcePath), StringComparison.Ordinal);
        var errors = await workspace.GetErrorsAsync();
        Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors));
    }

    [SkippableFact]
    public async Task InitOnly_OnCSharp9_StillGeneratesInitAccessor()
    {
        await using var workspace = await TempWorkspace.CreateAsync(Source, "9.0");

        var result = await new GeneratePropertyOperation(workspace.Context).ExecuteAsync(new GeneratePropertyParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Point",
            PropertyName = "X",
            PropertyType = "int",
            InitOnly = true
        });

        Assert.True(result.Success, result.Error?.Message);
        Assert.Contains("init;", await File.ReadAllTextAsync(workspace.SourcePath), StringComparison.Ordinal);
        var errors = await workspace.GetErrorsAsync();
        Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors));
    }

    private sealed class TempWorkspace : IAsyncDisposable
    {
        public required string DirectoryPath { get; init; }
        public required string SourcePath { get; init; }
        public required WorkspaceContext Context { get; init; }

        public static async Task<TempWorkspace> CreateAsync(string source, string langVersion)
        {
            Skip.IfNot(ModuleInitializer.MsBuildAvailable, ModuleInitializer.MsBuildError ?? "MSBuild not available");

            var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpLangVersion_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var projectPath = Path.Combine(directory, "Case.csproj");
            var sourcePath = Path.Combine(directory, "Program.cs");
            await File.WriteAllTextAsync(projectPath, $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <OutputType>Library</OutputType>
                    <TargetFramework>net9.0</TargetFramework>
                    <ImplicitUsings>disable</ImplicitUsings>
                    <LangVersion>{langVersion}</LangVersion>
                  </PropertyGroup>
                </Project>
                """);
            await File.WriteAllTextAsync(sourcePath, source);

            try
            {
                var context = await new MSBuildWorkspaceProvider().CreateContextAsync(projectPath);
                if (context.GetDocumentByPath(sourcePath) == null)
                {
                    context.Dispose();
                    throw new InvalidOperationException($"Workspace loaded but did not include {sourcePath}.");
                }

                return new TempWorkspace { DirectoryPath = directory, SourcePath = sourcePath, Context = context };
            }
            catch (Exception ex) when (ex is not SkipException)
            {
                try { Directory.Delete(directory, recursive: true); } catch { /* ignore cleanup failures */ }
                Skip.If(true, $"Workspace load failed: {ex.Message}");
                throw;
            }
        }

        public async Task<List<string>> GetErrorsAsync()
        {
            var document = Context.GetDocumentByPath(SourcePath);
            Assert.NotNull(document);
            var compilation = await document.Project.GetCompilationAsync();
            Assert.NotNull(compilation);
            return compilation.GetDiagnostics()
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(d => d.ToString())
                .ToList();
        }

        public async ValueTask DisposeAsync()
        {
            Context.Dispose();
            await Task.Run(() =>
            {
                try { Directory.Delete(DirectoryPath, recursive: true); } catch { /* ignore locked temp files */ }
            });
        }
    }
}

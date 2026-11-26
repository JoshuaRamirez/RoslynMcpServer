using Microsoft.CodeAnalysis;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Generate;
using RoslynMcp.Core.Workspace;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Generate;

/// <summary>
/// Regression tests for #2881: <c>generate_tostring</c> format "interpolated" emitted <c>$"..."</c>
/// on LangVersion 5 (CS8026). It is now refused there (single) / skipped (allFiles); "stringbuilder"
/// still works, and C# 6+ keeps the interpolated body.
/// </summary>
public class GenerateToStringLangVersionTests
{
    private const string Source = """
        class Point
        {
            public int X;
            public int Y;
        }
        """;

    [SkippableTheory]
    [InlineData(null)]
    [InlineData("interpolated")]
    public async Task InterpolatedFormat_OnCSharp5_IsRefusedAndWritesNothing(string? format)
    {
        await using var workspace = await TempWorkspace.CreateAsync(Source, "5");
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            new GenerateToStringOperation(workspace.Context).ExecuteAsync(new GenerateToStringParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Point",
                Format = format
            }));

        Assert.Equal(ErrorCodes.InvalidToStringFormat, ex.ErrorCode);
        Assert.Contains("C# 6", ex.Message, StringComparison.Ordinal);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task InterpolatedFormat_AllFilesOnCSharp5_DoesNotEmitInterpolatedString()
    {
        await using var workspace = await TempWorkspace.CreateAsync(Source, "5");

        // The only type needs an interpolated body, so allFiles skips it (nothing to generate).
        await new GenerateToStringOperation(workspace.Context).ExecuteAsync(new GenerateToStringParams
        {
            AllFiles = true
        });

        Assert.DoesNotContain("$\"", await File.ReadAllTextAsync(workspace.SourcePath), StringComparison.Ordinal);
        var errors = await workspace.GetErrorsAsync();
        Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors));
    }

    [SkippableFact]
    public async Task StringBuilderFormat_OnCSharp5_StillGeneratesAndCompiles()
    {
        await using var workspace = await TempWorkspace.CreateAsync(Source, "5");

        var result = await new GenerateToStringOperation(workspace.Context).ExecuteAsync(new GenerateToStringParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Point",
            Format = "stringbuilder"
        });

        Assert.True(result.Success, result.Error?.Message);
        Assert.Contains("ToString()", await File.ReadAllTextAsync(workspace.SourcePath), StringComparison.Ordinal);
        var errors = await workspace.GetErrorsAsync();
        Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors));
    }

    [SkippableFact]
    public async Task InterpolatedFormat_OnCSharp6_StillGeneratesInterpolatedString()
    {
        await using var workspace = await TempWorkspace.CreateAsync(Source, "6");

        var result = await new GenerateToStringOperation(workspace.Context).ExecuteAsync(new GenerateToStringParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Point"
        });

        Assert.True(result.Success, result.Error?.Message);
        Assert.Contains("$\"Point {{", await File.ReadAllTextAsync(workspace.SourcePath), StringComparison.Ordinal);
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

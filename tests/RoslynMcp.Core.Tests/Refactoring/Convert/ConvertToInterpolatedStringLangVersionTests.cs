using Microsoft.CodeAnalysis;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Convert;
using RoslynMcp.Core.Workspace;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring;

/// <summary>
/// Regression tests for #2892: <c>convert_to_interpolated_string</c> rewrote <c>string.Format</c> /
/// concatenations to <c>$"..."</c> on LangVersion 5 (CS8026). The single path now refuses and allFiles
/// skips pre-C# 6 documents; C# 6+ still converts.
/// </summary>
public class ConvertToInterpolatedStringLangVersionTests
{
    private const string Source = """
        class C
        {
            static string Format(string name)
            {
                var s = string.Format("Hello, {0}!", name);
                return s;
            }

            static string Concat(string name)
            {
                return "Hi " + name + "!";
            }
        }
        """;

    [SkippableTheory]
    [InlineData(5)]
    [InlineData(11)]
    public async Task SingleSite_OnCSharp5_IsRefusedAndWritesNothing(int line)
    {
        await using var workspace = await TempWorkspace.CreateAsync(Source, "5");
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            new ConvertToInterpolatedStringOperation(workspace.Context).ExecuteAsync(new ConvertToInterpolatedStringParams
            {
                SourceFile = workspace.SourcePath,
                Line = line
            }));

        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
        Assert.Contains("C# 6", ex.Message, StringComparison.Ordinal);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task AllFiles_OnCSharp5_LeavesCodeUnchangedAndCompiling()
    {
        await using var workspace = await TempWorkspace.CreateAsync(Source, "5");
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var result = await new ConvertToInterpolatedStringOperation(workspace.Context).ExecuteAsync(new ConvertToInterpolatedStringParams
        {
            AllFiles = true
        });

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
        var errors = await workspace.GetErrorsAsync();
        Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors));
    }

    [SkippableFact]
    public async Task SingleSite_OnCSharp6_StillConverts()
    {
        await using var workspace = await TempWorkspace.CreateAsync(Source, "6");

        var result = await new ConvertToInterpolatedStringOperation(workspace.Context).ExecuteAsync(new ConvertToInterpolatedStringParams
        {
            SourceFile = workspace.SourcePath,
            Line = 5
        });

        Assert.True(result.Success, result.Error?.Message);
        Assert.Contains("$\"Hello, {name}!\"", await File.ReadAllTextAsync(workspace.SourcePath), StringComparison.Ordinal);
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

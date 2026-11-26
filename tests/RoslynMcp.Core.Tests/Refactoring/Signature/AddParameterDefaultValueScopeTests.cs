using Microsoft.CodeAnalysis;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Signature;
using RoslynMcp.Core.Workspace;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Signature;

/// <summary>
/// Regression tests for #3150: add_parameter with a defaultValue validated the type in a detached probe
/// without the target file's usings or namespace, so CancellationToken or a same-namespace enum was rejected.
/// </summary>
public class AddParameterDefaultValueScopeTests
{
    private const string Source = """
        using System;
        using System.Threading;
        using System.Threading.Tasks;

        namespace App;

        public enum Mode { Fast, Safe }

        public static class Worker
        {
            public static Task<int> RunAsync(int x) => Task.FromResult(x);

            public static int Run(int x) => x;

            public static async Task Main()
            {
                Console.WriteLine(await RunAsync(1));
                Console.WriteLine(Run(2));
            }
        }
        """;

    [SkippableTheory]
    [InlineData("RunAsync", "cancellationToken", "CancellationToken", "default", "CancellationToken cancellationToken = default")]
    [InlineData("Run", "mode", "Mode", "Mode.Safe", "Mode mode = Mode.Safe")]
    [InlineData("Run", "factory", "Func<int>", "null", "Func<int> factory = null")]
    public async Task DefaultValue_BindsWithFileUsingsAndNamespace(
        string methodName, string parameterName, string parameterType, string defaultValue, string expected)
    {
        await using var workspace = await TempWorkspace.CreateAsync(Source, "latest");

        var result = await new AddParameterOperation(workspace.Context).ExecuteAsync(new AddParameterParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = methodName,
            ParameterName = parameterName,
            ParameterType = parameterType,
            DefaultValue = defaultValue
        });

        Assert.True(result.Success, result.Error?.Message);
        Assert.Contains(expected, await File.ReadAllTextAsync(workspace.SourcePath), StringComparison.Ordinal);
        Assert.Empty(await workspace.GetDiskErrorsAsync());
    }

    [SkippableFact]
    public async Task AllFiles_DefaultValue_BindsWithFileUsings()
    {
        const string source = """
            using System.Threading;
            using System.Threading.Tasks;

            namespace App;

            public static class Worker
            {
                public static Task<int> RunAsync(int x) => Task.FromResult(x);
            }
            """;
        await using var workspace = await TempWorkspace.CreateAsync(source, "latest");

        var result = await new AddParameterOperation(workspace.Context).ExecuteAsync(new AddParameterParams
        {
            AllFiles = true,
            ParameterName = "cancellationToken",
            ParameterType = "CancellationToken",
            DefaultValue = "default"
        });

        Assert.True(result.Success, result.Error?.Message);
        Assert.Contains(
            "RunAsync(int x, CancellationToken cancellationToken = default)",
            await File.ReadAllTextAsync(workspace.SourcePath),
            StringComparison.Ordinal);
        Assert.Empty(await workspace.GetDiskErrorsAsync());
    }

    [SkippableFact]
    public async Task UnknownType_IsStillRejected()
    {
        await using var workspace = await TempWorkspace.CreateAsync(Source, "latest");

        var ex = await Assert.ThrowsAsync<RefactoringException>(() => new AddParameterOperation(workspace.Context)
            .ExecuteAsync(new AddParameterParams
            {
                SourceFile = workspace.SourcePath,
                MethodName = "Run",
                ParameterName = "t",
                ParameterType = "NoSuchType",
                DefaultValue = "default"
            }));

        Assert.Equal(ErrorCodes.InvalidParameterType, ex.ErrorCode);
    }

    private sealed class TempWorkspace : IAsyncDisposable
    {
        public required string DirectoryPath { get; init; }
        public required string SourcePath { get; init; }
        public required WorkspaceContext Context { get; init; }

        public static async Task<TempWorkspace> CreateAsync(string source, string langVersion, params (string Name, string Text)[] extraFiles)
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
            foreach (var (name, text) in extraFiles)
                await File.WriteAllTextAsync(Path.Combine(directory, name), text);

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

        /// <summary>Compile errors of the project with every .cs file re-read from disk.</summary>
        public async Task<List<string>> GetDiskErrorsAsync()
        {
            var document = Context.GetDocumentByPath(SourcePath);
            Assert.NotNull(document);
            var project = document.Project;
            foreach (var path in Directory.GetFiles(DirectoryPath, "*.cs"))
            {
                var existing = project.Documents.FirstOrDefault(d => string.Equals(d.FilePath, path, StringComparison.Ordinal));
                var text = Microsoft.CodeAnalysis.Text.SourceText.From(await File.ReadAllTextAsync(path));
                project = existing != null
                    ? existing.WithText(text).Project
                    : project.AddDocument(Path.GetFileName(path), text, filePath: path).Project;
            }

            var compilation = await project.GetCompilationAsync();
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

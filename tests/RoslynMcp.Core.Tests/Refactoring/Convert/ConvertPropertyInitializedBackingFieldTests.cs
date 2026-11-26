using Microsoft.CodeAnalysis;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Convert;
using RoslynMcp.Core.Workspace;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring;

/// <summary>
/// Regression tests for #3281: <c>convert_property</c> ToAutoProperty dropped the backing field's initializer, so the
/// property silently started at <c>default</c>. Such properties are now refused (single) / skipped (allFiles).
/// </summary>
public class ConvertPropertyInitializedBackingFieldTests
{
    private const string Source = """
        public class Settings
        {
            private int _retries = 3;
            private string _name = "primary";
            private int _plain;

            public int Retries { get { return _retries; } set { _retries = value; } }

            public string Name { get => this._name; set => this._name = value; }

            public int Plain { get { return _plain; } set { _plain = value; } }
        }
        """;

    [SkippableTheory]
    [InlineData("Retries", "_retries")]
    [InlineData("Name", "_name")]
    public async Task ToAutoProperty_InitializedBackingField_IsRefusedAndWritesNothing(string propertyName, string fieldName)
    {
        await using var workspace = await TempWorkspace.CreateAsync(Source, "latest");
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            new ConvertPropertyOperation(workspace.Context).ExecuteAsync(new ConvertPropertyParams
            {
                SourceFile = workspace.SourcePath,
                PropertyName = propertyName,
                Direction = "ToAutoProperty"
            }));

        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
        Assert.Contains(fieldName, ex.Message, StringComparison.Ordinal);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task ToAutoProperty_UninitializedBackingField_StillConverts()
    {
        await using var workspace = await TempWorkspace.CreateAsync(Source, "latest");

        var result = await new ConvertPropertyOperation(workspace.Context).ExecuteAsync(new ConvertPropertyParams
        {
            SourceFile = workspace.SourcePath,
            PropertyName = "Plain",
            Direction = "ToAutoProperty"
        });

        Assert.True(result.Success, result.Error?.Message);
        Assert.Contains("public int Plain { get; set; }", await File.ReadAllTextAsync(workspace.SourcePath), StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task ToAutoProperty_AllFiles_SkipsInitializedBackingFields()
    {
        await using var workspace = await TempWorkspace.CreateAsync(Source, "latest");

        await new ConvertPropertyOperation(workspace.Context).ExecuteAsync(new ConvertPropertyParams
        {
            AllFiles = true,
            Direction = "ToAutoProperty"
        });

        var after = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("get { return _retries; }", after, StringComparison.Ordinal);
        Assert.Contains("get => this._name;", after, StringComparison.Ordinal);
        Assert.Contains("public int Plain { get; set; }", after, StringComparison.Ordinal);
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

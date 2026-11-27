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

            private int _p = 4;

            public int P { get { return (_p); } set { _p = value; } }
        }

        public partial class Split
        {
            private int _q = 5;
        }

        public partial class Split
        {
            public int Q { get { return _q; } set { _q = value; } }

            public int R { get { return _r; } set { _r = value; } }
        }
        """;

    private const string StaticAndSuppressedSource = """
        public class S
        {
            private static int _s = 5;
            private string _n = "x";

            public int St { get { return S._s; } set { S._s = value; } }

            public string N { get { return _n!; } set { _n = value; } }
        }

        public class Base
        {
            protected int _b = 7;
        }

        public class Derived : Base
        {
            public int B { get { return _b; } set { _b = value; } }
        }
        """;

    // Another part of Split, in a second file, initializes R's backing field.
    private const string OtherPart = """
        public partial class Split
        {
            private int _r = 6;
        }
        """;

    [SkippableTheory]
    [InlineData("Retries", "_retries")]
    [InlineData("Name", "_name")]
    [InlineData("P", "_p")]
    [InlineData("Q", "_q")]
    [InlineData("R", "_r")]
    [InlineData("St", "_s")]
    [InlineData("N", "_n")]
    [InlineData("B", "_b")]
    public async Task ToAutoProperty_InitializedBackingField_IsRefusedAndWritesNothing(string propertyName, string fieldName)
    {
        await using var workspace = await TempWorkspace.CreateAsync(Source + "\n" + StaticAndSuppressedSource, "latest", OtherPart);
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
        await using var workspace = await TempWorkspace.CreateAsync(Source + "\n" + StaticAndSuppressedSource, "latest", OtherPart);

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
        await using var workspace = await TempWorkspace.CreateAsync(Source + "\n" + StaticAndSuppressedSource, "latest", OtherPart);

        await new ConvertPropertyOperation(workspace.Context).ExecuteAsync(new ConvertPropertyParams
        {
            AllFiles = true,
            Direction = "ToAutoProperty"
        });

        var after = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("get { return _retries; }", after, StringComparison.Ordinal);
        Assert.Contains("get => this._name;", after, StringComparison.Ordinal);
        Assert.Contains("get { return (_p); }", after, StringComparison.Ordinal);
        Assert.Contains("get { return _q; }", after, StringComparison.Ordinal);
        Assert.Contains("get { return _r; }", after, StringComparison.Ordinal);
        Assert.Contains("get { return S._s; }", after, StringComparison.Ordinal);
        Assert.Contains("get { return _n!; }", after, StringComparison.Ordinal);
        Assert.Contains("get { return _b; }", after, StringComparison.Ordinal);
        Assert.Contains("public int Plain { get; set; }", after, StringComparison.Ordinal);
        var errors = await workspace.GetErrorsAsync();
        Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors));
    }

    private sealed class TempWorkspace : IAsyncDisposable
    {
        public required string DirectoryPath { get; init; }
        public required string SourcePath { get; init; }
        public required WorkspaceContext Context { get; init; }

        public static async Task<TempWorkspace> CreateAsync(string source, string langVersion, string? otherPart = null)
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
            if (otherPart != null)
                await File.WriteAllTextAsync(Path.Combine(directory, "OtherPart.cs"), otherPart);

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

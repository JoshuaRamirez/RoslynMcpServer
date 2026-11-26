using Microsoft.CodeAnalysis;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Query;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Workspace;
using Xunit;

namespace RoslynMcp.Core.Tests.Query;

/// <summary>
/// Regression tests for #3195: search_symbols accepted kindFilter Local / Parameter (and listed
/// them as valid) although it never searches locals or parameters, so they always returned an
/// empty success.
/// </summary>
public class SearchSymbolsLocalParameterKindTests
{
    private const string Source = """
        namespace App;
        public class Order(int orderQty)
        {
            public int OrderTotal(int orderPrice)
            {
                var orderSum = orderPrice * orderQty;
                return orderSum;
            }
        }
        """;

    [SkippableTheory]
    [InlineData("Parameter")]
    [InlineData("Local")]
    [InlineData(" local ")]
    public async Task LocalOrParameterKindFilter_IsInvalidSymbolKind(string kindFilter)
    {
        await using var workspace = await TempWorkspace.CreateAsync(Source, "latest");

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            new SearchSymbolsOperation(workspace.Context).ExecuteAsync(new SearchSymbolsParams
            {
                Query = "order",
                KindFilter = kindFilter
            }));

        Assert.Equal(ErrorCodes.InvalidSymbolKind, ex.ErrorCode);
        Assert.Equal(
            $"Invalid kindFilter '{kindFilter}'. Valid values: Class, Struct, Interface, Enum, Record, Delegate, Method, Property, Field, Event, Constant, Namespace",
            ex.Message);
    }

    [SkippableTheory]
    [InlineData("Method", "OrderTotal")]
    [InlineData("class", "Order")]
    public async Task SearchableKindFilter_StillFilters(string kindFilter, string expected)
    {
        await using var workspace = await TempWorkspace.CreateAsync(Source, "latest");

        var result = await new SearchSymbolsOperation(workspace.Context).ExecuteAsync(new SearchSymbolsParams
        {
            Query = "order",
            KindFilter = kindFilter
        });

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(expected, Assert.Single(result.Data!.Symbols).Name);
    }

    [SkippableFact]
    public async Task BogusKindFilter_ListsOnlySearchableKinds()
    {
        await using var workspace = await TempWorkspace.CreateAsync(Source, "latest");

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            new SearchSymbolsOperation(workspace.Context).ExecuteAsync(new SearchSymbolsParams
            {
                Query = "order",
                KindFilter = "Bogus"
            }));

        Assert.Equal(ErrorCodes.InvalidSymbolKind, ex.ErrorCode);
        Assert.DoesNotContain("Local", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Parameter", ex.Message, StringComparison.Ordinal);
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

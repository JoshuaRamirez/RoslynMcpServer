using Microsoft.CodeAnalysis;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Query;
using RoslynMcp.Core.Workspace;
using Xunit;

namespace RoslynMcp.Core.Tests.Query;

/// <summary>
/// Regression tests for #3091: get_symbol_info DocumentationSummary sliced the raw doc-comment
/// XML, so inner tags, T:/M: cref doc-IDs and entities such as &amp;lt; leaked into the text.
/// </summary>
public class GetSymbolInfoDocumentationSummaryTests
{
    private const string Source = """
        namespace Demo;

        public class Widget { }

        public class Ops
        {
            /// <summary>Returns true when <paramref name="a"/> &lt; <paramref name="b"/>.</summary>
            public static bool Less(int a, int b) => a < b;

            /// <summary>Creates a <see cref="Widget"/> using <c>Make&lt;T&gt;()</c> &amp; friends.</summary>
            public static Widget Build() => new Widget();

            /// <summary>
            /// Plain summary text.
            /// </summary>
            public static void Plain() { }

            /// <summary>Returns <see langword="null"/> or <see cref="Widget">a widget</see> of <typeparamref name="T"/>.<para>Second.</para></summary>
            public static T? Pick<T>() where T : class => null;

            /// <summary>Accepts <c>x</c> <c>y</c> as input.</summary>
            public static void Pair() { }

            /// <summary>See <see cref="Widget"/> <see cref="Ops"/> for details.</summary>
            public static void Refs() { }
        }
        """;

    [SkippableTheory]
    [InlineData("Less", "Returns true when a < b.")]
    [InlineData("Build", "Creates a Demo.Widget using Make<T>() & friends.")]
    [InlineData("Plain", "Plain summary text.")]
    [InlineData("Pick", "Returns null or a widget of T. Second.")]
    // Whitespace-only text between adjacent inline elements was dropped by XDocument.Parse (#3091 review).
    [InlineData("Pair", "Accepts x y as input.")]
    [InlineData("Refs", "See Demo.Widget Demo.Ops for details.")]
    public async Task DocumentationSummary_IsRenderedText(string member, string expected)
    {
        await using var workspace = await TempWorkspace.CreateAsync(Source, "latest");

        var result = await new GetSymbolInfoOperation(workspace.Context).ExecuteAsync(
            new GetSymbolInfoParams { SourceFile = workspace.SourcePath, SymbolName = member });

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(expected, result.Data?.DocumentationSummary);
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

using Microsoft.CodeAnalysis;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Query;
using RoslynMcp.Core.Workspace;
using Xunit;

namespace RoslynMcp.Core.Tests.Query;

/// <summary>
/// Regression tests for #3092: get_symbol_info DocumentationSummary was null for every
/// <c>/// &lt;inheritdoc/&gt;</c> member, because the compiler's doc XML never expands it.
/// </summary>
public class GetSymbolInfoInheritDocTests
{
    private const string Source = """
        namespace Demo;

        public abstract class BaseShape
        {
            /// <summary>Computes the area of the shape.</summary>
            public abstract double Area();

            /// <summary>The shape's display name.</summary>
            public virtual string Name => "shape";
        }

        public interface IRunner
        {
            /// <summary>Runs the job once.</summary>
            void Run();

            /// <summary>Stops the job.</summary>
            void Stop();
        }

        /// <summary>A round shape.</summary>
        public class Circle : BaseShape, IRunner
        {
            /// <inheritdoc/>
            public override double Area() => 3.14;

            /// <inheritdoc/>
            public override string Name => "circle";

            /// <inheritdoc/>
            public void Run() { }

            /// <inheritdoc/>
            void IRunner.Stop() { }

            /// <inheritdoc cref="BaseShape.Area"/>
            public double Size() => 1;

            /// <inheritdoc/>
            public void Orphan() { }
        }

        /// <inheritdoc/>
        public class Disc : Circle
        {
            /// <inheritdoc/>
            public override double Area() => 1.0;
        }
        """;

    [SkippableTheory]
    [InlineData("Circle.Area", "Computes the area of the shape.")]
    [InlineData("Circle.Name", "The shape's display name.")]
    [InlineData("Circle.Run", "Runs the job once.")]
    [InlineData("Circle.Size", "Computes the area of the shape.")]
    [InlineData("Disc.Area", "Computes the area of the shape.")]
    [InlineData("Disc", "A round shape.")]
    [InlineData("BaseShape.Area", "Computes the area of the shape.")]
    public async Task InheritDoc_ResolvesInheritedSummary(string member, string expected)
    {
        await using var workspace = await TempWorkspace.CreateAsync(Source, "latest");

        Assert.Equal(expected, await GetSummary(workspace, member));
    }

    [SkippableFact]
    public async Task InheritDoc_ExplicitInterfaceImplementation_ResolvesInterfaceSummary()
    {
        await using var workspace = await TempWorkspace.CreateAsync(Source, "latest");
        var line = File.ReadAllLines(workspace.SourcePath).ToList().FindIndex(l => l.Contains("void IRunner.Stop()", StringComparison.Ordinal)) + 1;
        var column = File.ReadAllLines(workspace.SourcePath)[line - 1].IndexOf("Stop", StringComparison.Ordinal) + 1;

        var result = await new GetSymbolInfoOperation(workspace.Context).ExecuteAsync(
            new GetSymbolInfoParams { SourceFile = workspace.SourcePath, Line = line, Column = column });

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal("Stops the job.", result.Data?.DocumentationSummary);
    }

    [SkippableFact]
    public async Task InheritDoc_WithNothingToInherit_StaysNull()
    {
        await using var workspace = await TempWorkspace.CreateAsync(Source, "latest");

        Assert.Null(await GetSummary(workspace, "Circle.Orphan"));
    }

    [SkippableTheory]
    [InlineData("Point")]
    [InlineData("Color")]
    public async Task InheritDoc_OnStructOrEnum_DoesNotTakeValueTypeOrEnumSummary(string typeName)
    {
        // A struct's BaseType is System.ValueType and an enum's is System.Enum; their summaries are not the
        // type's, so <inheritdoc/> with nothing else to inherit stays null as on master (#3092 review).
        const string source = """
            namespace Demo;

            /// <inheritdoc/>
            public struct Point { public int X; }

            /// <inheritdoc/>
            public enum Color { Red, Green }
            """;
        await using var workspace = await TempWorkspace.CreateAsync(source, "latest");

        var result = await new GetSymbolInfoOperation(workspace.Context).ExecuteAsync(
            new GetSymbolInfoParams { SourceFile = workspace.SourcePath, SymbolName = typeName });

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(typeName, result.Data?.Name);
        Assert.Null(result.Data?.DocumentationSummary);
    }

    private static async Task<string?> GetSummary(TempWorkspace workspace, string member)
    {
        var lines = File.ReadAllLines(workspace.SourcePath);
        var parts = member.Split('.');
        var typeLine = Array.FindIndex(lines, l => l.Contains($"class {parts[0]}", StringComparison.Ordinal));
        int line;
        string name;
        if (parts.Length == 1)
        {
            line = typeLine + 1;
            name = parts[0];
        }
        else
        {
            name = parts[1];
            var memberLine = Array.FindIndex(lines, typeLine, l =>
                System.Text.RegularExpressions.Regex.IsMatch(l, $@"\b{name}\b(\(| =>)") && !l.TrimStart().StartsWith("///", StringComparison.Ordinal));
            line = memberLine + 1;
        }

        var column = System.Text.RegularExpressions.Regex.Match(lines[line - 1], $@"\b{name}\b").Index + 1;
        var result = await new GetSymbolInfoOperation(workspace.Context).ExecuteAsync(
            new GetSymbolInfoParams { SourceFile = workspace.SourcePath, Line = line, Column = column });

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(name, result.Data?.Name);
        return result.Data?.DocumentationSummary;
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

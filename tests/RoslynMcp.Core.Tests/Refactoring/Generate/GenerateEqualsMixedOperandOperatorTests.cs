using Microsoft.CodeAnalysis;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Generate;
using RoslynMcp.Core.Workspace;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Generate;

/// <summary>
/// Regression tests for #3392: generate_equals_hashcode replaceExisting + generateOperators deleted
/// mixed-operand operator ==/!= overloads, and their presence tripped the existing-operators gate.
/// </summary>
public class GenerateEqualsMixedOperandOperatorTests
{
    private const string MixedIntSource = """
        #nullable enable
        public class Point
        {
            public int X;
            public int Y;
            public static bool operator ==(Point p, int magnitude) => p.X + p.Y == magnitude;
            public static bool operator !=(Point p, int magnitude) => !(p == magnitude);
            public override bool Equals(object? obj) => ReferenceEquals(this, obj);
            public override int GetHashCode() => 0;
        }

        public static class Use
        {
            public static bool A(Point a) => a == 3;
            public static bool B(Point a) => a != 4;
        }
        """;

    private const string MixedObjectSource = """
        #nullable enable
        public class Point
        {
            public int X;
            public int Y;
            public static bool operator ==(Point p, object? other) => other is System.ValueTuple<int, int> t && t.Item1 == p.X && t.Item2 == p.Y;
            public static bool operator !=(Point p, object? other) => !(p == other);
            public override bool Equals(object? obj) => obj is Point q && q.X == X && q.Y == Y;
            public override int GetHashCode() => System.HashCode.Combine(X, Y);
        }
        """;

    [SkippableTheory]
    [InlineData("int magnitude")]
    [InlineData("object? other")]
    public async Task ReplaceExisting_KeepsMixedOperandOperators(string operand)
    {
        var source = operand.StartsWith("int", StringComparison.Ordinal) ? MixedIntSource : MixedObjectSource;
        await using var workspace = await TempWorkspace.CreateAsync(source, "latest");

        var result = await Run(workspace, replaceExisting: true);

        Assert.True(result.Success, result.Error?.Message);
        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains($"operator ==(Point p, {operand})", text, StringComparison.Ordinal);
        Assert.Contains($"operator !=(Point p, {operand})", text, StringComparison.Ordinal);
        Assert.Contains("operator ==(Point? left, Point? right)", text, StringComparison.Ordinal);
        Assert.Empty(await workspace.GetDiskErrorsAsync());
    }

    [SkippableFact]
    public async Task MixedOperandOperators_DoNotTripExistingOperatorGate()
    {
        const string source = """
            #nullable enable
            public class Point
            {
                public int X;
                public static bool operator ==(Point p, int x) => p.X == x;
                public static bool operator !=(Point p, int x) => p.X != x;
            }
            """;
        await using var workspace = await TempWorkspace.CreateAsync(source, "latest");

        var result = await Run(workspace, replaceExisting: false);

        Assert.True(result.Success, result.Error?.Message);
        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("operator ==(Point p, int x)", text, StringComparison.Ordinal);
        Assert.Contains("operator ==(Point? left, Point? right)", text, StringComparison.Ordinal);
        Assert.Empty(await workspace.GetDiskErrorsAsync());
    }

    [SkippableFact]
    public async Task SelfOperators_StillTripExistingOperatorGate()
    {
        const string source = """
            #nullable enable
            public class Point
            {
                public int X;
                public static bool operator ==(Point? a, Point? b) => ReferenceEquals(a, b);
                public static bool operator !=(Point? a, Point? b) => !ReferenceEquals(a, b);
            }
            """;
        await using var workspace = await TempWorkspace.CreateAsync(source, "latest");

        var ex = await Assert.ThrowsAsync<RefactoringException>(() => Run(workspace, replaceExisting: false));

        Assert.Equal(ErrorCodes.AlreadyHasEqualityOperators, ex.ErrorCode);
    }

    private static Task<RefactoringResult> Run(TempWorkspace workspace, bool replaceExisting) =>
        new GenerateEqualsHashCodeOperation(workspace.Context).ExecuteAsync(new GenerateEqualsHashCodeParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Point",
            GenerateOperators = true,
            ReplaceExisting = replaceExisting
        });

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

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring.Convert;
using RoslynMcp.Core.Workspace;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring;

/// <summary>
/// Regression tests for #3321: convert_to_block_body / convert_expression_body with memberName
/// on a partial method or property took the first match, the bodiless defining declaration,
/// and refused with 3020 although the implementing declaration in the same file converts.
/// </summary>
public class ConvertBodyPartialMemberNameTests
{
    private const string Source = """
        using System;
        namespace N;
        public partial class C
        {
            public partial int Compute(int x);
            public partial int Prop { get; }
            public partial int Twice(int x);
        }
        public partial class C
        {
            public partial int Compute(int x) => x * 2;
            public partial int Prop => 7;
            public partial int Twice(int x)
            {
                return x + x;
            }
            public void Use() { Console.WriteLine(Compute(3) + Prop + Twice(1)); }
        }
        """;

    [SkippableTheory]
    [InlineData("Compute", "return x * 2;")]
    [InlineData("Prop", "return 7;")]
    public async Task ConvertToBlockBody_MemberNameOnPartial_ConvertsImplementation(string member, string expected)
    {
        await using var workspace = await TempWorkspace.CreateAsync(Source, "latest");

        var result = await new ConvertToBlockBodyOperation(workspace.Context).ExecuteAsync(new ConvertToBlockBodyParams
        {
            SourceFile = workspace.SourcePath,
            MemberName = member
        });

        Assert.True(result.Success, result.Error?.Message);
        var text = Normalize(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains(expected, text, StringComparison.Ordinal);
        Assert.DoesNotContain(member == "Compute" ? "Compute(int x) => x * 2;" : "Prop => 7;", text, StringComparison.Ordinal);
        Assert.Contains("public partial int Compute(int x);", text, StringComparison.Ordinal);
        Assert.Contains("public partial int Prop { get; }", text, StringComparison.Ordinal);
        Assert.Empty(await workspace.GetDiskErrorsAsync());
    }

    [SkippableFact]
    public async Task ConvertToBlockBody_PreviewOnPartial_Succeeds()
    {
        await using var workspace = await TempWorkspace.CreateAsync(Source, "latest");

        var result = await new ConvertToBlockBodyOperation(workspace.Context).ExecuteAsync(new ConvertToBlockBodyParams
        {
            SourceFile = workspace.SourcePath,
            MemberName = "Compute",
            Preview = true
        });

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(Source, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableTheory]
    [InlineData("Compute", "ToBlockBody", "return x * 2;")]
    [InlineData("Prop", "ToBlockBody", "return 7;")]
    [InlineData("Twice", "ToExpressionBody", "public partial int Twice(int x) => x + x;")]
    public async Task ConvertExpressionBody_MemberNameOnPartial_ConvertsImplementation(string member, string direction, string expected)
    {
        await using var workspace = await TempWorkspace.CreateAsync(Source, "latest");

        var result = await new ConvertExpressionBodyOperation(workspace.Context).ExecuteAsync(new ConvertExpressionBodyParams
        {
            SourceFile = workspace.SourcePath,
            MemberName = member,
            Direction = direction
        });

        Assert.True(result.Success, result.Error?.Message);
        var text = Normalize(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains(expected, text, StringComparison.Ordinal);
        Assert.Contains("public partial int Compute(int x);", text, StringComparison.Ordinal);
        Assert.Contains("public partial int Twice(int x);", text, StringComparison.Ordinal);
        Assert.Empty(await workspace.GetDiskErrorsAsync());
    }

    [Fact]
    public void FindMember_DefinitionWithoutImplementationInFile_IsStillPicked()
    {
        // The implementation lives elsewhere; a same-name overload must not be picked instead.
        var root = CSharpSyntaxTree.ParseText("""
            namespace N;
            public partial class C
            {
                public partial int Compute(int x);
                public int Compute(string s) => s.Length;
            }
            """).GetRoot();

        var block = ConvertToBlockBodyOperation.FindMember(root, "Compute", line: null, column: null);
        var expression = ConvertExpressionBodyOperation.FindMember(root, "Compute", line: null, column: null);

        Assert.IsType<MethodDeclarationSyntax>(block);
        Assert.Null(((MethodDeclarationSyntax)block!).ExpressionBody);
        Assert.NotNull(expression);
        Assert.Null(((MethodDeclarationSyntax)expression!).ExpressionBody);
    }

    [Fact]
    public void FindMember_LineOnDefinition_StillPicksDefinition()
    {
        var root = CSharpSyntaxTree.ParseText(Source).GetRoot();

        // Line 5 is the defining `public partial int Compute(int x);`.
        var block = ConvertToBlockBodyOperation.FindMember(root, "Compute", line: 5, column: null);
        var expression = ConvertExpressionBodyOperation.FindMember(root, "Compute", line: 5, column: null);

        Assert.Null(((MethodDeclarationSyntax)block!).ExpressionBody);
        Assert.Null(((MethodDeclarationSyntax)expression!).ExpressionBody);
    }

    [Fact]
    public void FindMember_OverloadedPartials_PickImplementationWithMatchingParameters()
    {
        var root = CSharpSyntaxTree.ParseText("""
            namespace N;
            public partial class C
            {
                public partial int F(int x);
                public partial int F(string s);
            }
            public partial class C
            {
                public partial int F(string text) => text.Length;
            }
            """).GetRoot();

        // F(int) has no implementation in this file, so it stays the first match.
        var picked = (MethodDeclarationSyntax)ConvertToBlockBodyOperation.FindMember(root, "F", line: null, column: null)!;
        Assert.Equal("int", picked.ParameterList.Parameters[0].Type!.ToString());
        Assert.Null(picked.ExpressionBody);
    }

    private static string Normalize(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);

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

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Convert;
using RoslynMcp.Core.Workspace;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring;

/// <summary>
/// Operation-level tests for <see cref="SimplifyNameOperation"/> (UC-A8).
/// </summary>
public class SimplifyNameOperationTests
{
    #region Input Validation

    [Fact]
    public void Validate_MissingSourceFile_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            SimplifyNameOperation.Validate(new SimplifyNameParams
            {
                SourceFile = ""
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public void Validate_RelativePath_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            SimplifyNameOperation.Validate(new SimplifyNameParams
            {
                SourceFile = "Types.cs"
            }));

        Assert.Equal(ErrorCodes.InvalidSourcePath, ex.ErrorCode);
    }

    [Fact]
    public void Validate_LocationScope_MissingLine_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            SimplifyNameOperation.Validate(new SimplifyNameParams
            {
                SourceFile = AbsoluteTestPath(),
                Scope = "location"
            }));

        Assert.Equal(ErrorCodes.InvalidLineNumber, ex.ErrorCode);
    }

    [Fact]
    public void Validate_InvalidLine_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            SimplifyNameOperation.Validate(new SimplifyNameParams
            {
                SourceFile = AbsoluteTestPath(),
                Line = 0
            }));

        Assert.Equal(ErrorCodes.InvalidLineNumber, ex.ErrorCode);
    }

    [Fact]
    public void Validate_InvalidColumn_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            SimplifyNameOperation.Validate(new SimplifyNameParams
            {
                SourceFile = AbsoluteTestPath(),
                Line = 1,
                Column = 0
            }));

        Assert.Equal(ErrorCodes.InvalidColumnNumber, ex.ErrorCode);
    }

    [Fact]
    public void Validate_InvalidScope_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            SimplifyNameOperation.Validate(new SimplifyNameParams
            {
                SourceFile = AbsoluteTestPath(),
                Scope = "type"
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public void Validate_MissingFile_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            SimplifyNameOperation.Validate(new SimplifyNameParams
            {
                SourceFile = AbsoluteTestPath()
            }));

        Assert.Equal(ErrorCodes.SourceFileNotFound, ex.ErrorCode);
    }

    [Fact]
    public void NormalizeScope_Default_IsFile()
    {
        Assert.Equal(SimplifyNameOperation.ScopeFile, SimplifyNameOperation.NormalizeScope(null));
        Assert.Equal(SimplifyNameOperation.ScopeFile, SimplifyNameOperation.NormalizeScope(""));
        Assert.Equal(SimplifyNameOperation.ScopeFile, SimplifyNameOperation.NormalizeScope("FILE"));
        Assert.Equal(SimplifyNameOperation.ScopeLocation, SimplifyNameOperation.NormalizeScope("Location"));
    }

    [Fact]
    public void CollectQualifiedNameNodes_SkipsUsingsAndNamespace()
    {
        var root = CSharpSyntaxTree.ParseText("""
            using System.Collections.Generic;

            namespace TestApp.Services
            {
                class C
                {
                    System.Collections.Generic.List<int> items;
                }
            }
            """).GetRoot();

        var names = SimplifyNameOperation.CollectQualifiedNameNodes(root);

        Assert.Single(names);
        Assert.Contains("List", names[0].ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(names, node => node.ToString() == "System.Collections.Generic" && node.Parent is UsingDirectiveSyntax);
    }

    [Fact]
    public void FindNameAtLocation_PicksLeftmostWithoutColumn()
    {
        var root = CSharpSyntaxTree.ParseText("""
            class C
            {
                System.Text.StringBuilder a; System.Collections.Generic.List<int> b;
            }
            """).GetRoot();

        var names = SimplifyNameOperation.CollectQualifiedNameNodes(root);
        var line = 3;
        var found = SimplifyNameOperation.FindNameAtLocation(names, line, column: null);

        Assert.NotNull(found);
        Assert.Contains("StringBuilder", found.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void FindNameAtLocation_ColumnNarrowsName()
    {
        const string source = """
            class C
            {
                System.Text.StringBuilder a; System.Collections.Generic.List<int> b;
            }
            """;
        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var names = SimplifyNameOperation.CollectQualifiedNameNodes(root);
        var listIndex = source.IndexOf("System.Collections.Generic.List", StringComparison.Ordinal);
        var column = ColumnOnLine(source, listIndex);
        var line = 3;

        var found = SimplifyNameOperation.FindNameAtLocation(names, line, column);

        Assert.NotNull(found);
        Assert.Contains("List", found.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void GetRightmostIdentifier_QualifiedGeneric()
    {
        var name = SyntaxFactory.ParseName("System.Collections.Generic.List<int>");
        Assert.Equal("List", SimplifyNameOperation.GetRightmostIdentifier(name));
    }

    [Fact]
    public void GetShorterForms_DropsLeftQualifiers()
    {
        var name = SyntaxFactory.ParseName("System.Collections.Generic.List<int>");
        var forms = SimplifyNameOperation.GetShorterForms(name).Select(form => form.ToString()).ToList();

        Assert.Contains("List<int>", forms);
        Assert.Contains("Generic.List<int>", forms);
        Assert.DoesNotContain("System.Collections.Generic.List<int>", forms);
    }

    [Fact]
    public void ClassifySkipReason_LocalTypeConflict()
    {
        var tree = CSharpSyntaxTree.ParseText("""
            using System.Collections.Generic;
            class List {}
            class C { System.Collections.Generic.List<int> items; }
            """);
        var compilation = CSharpCompilation.Create(
            "skip",
            [tree],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
             MetadataReference.CreateFromFile(typeof(List<>).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var model = compilation.GetSemanticModel(tree);
        var name = tree.GetRoot().DescendantNodes().OfType<QualifiedNameSyntax>()
            .First(node => node.ToString().Contains("List", StringComparison.Ordinal)
                && node.Parent is not QualifiedNameSyntax);

        var reason = SimplifyNameOperation.ClassifySkipReason(name, model);

        Assert.Contains("conflict", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SimplifyName_UneditableDocument_Throws()
    {
        var workspace = new AdhocWorkspace();
        var project = workspace.AddProject("P", LanguageNames.CSharp);
        var document = workspace.AddDocument(project.Id, "Generated.cs", SourceText.From("class C {}"));

        var ex = Assert.Throws<RefactoringException>(() =>
            SimplifyNameOperation.ValidateDocumentIsEditable(document, workspace));

        Assert.Equal(ErrorCodes.DocumentNotEditable, ex.ErrorCode);
    }

    #endregion

    #region P0 Happy Path

    [SkippableFact]
    public async Task SimplifyName_FullyQualifiedName_Shortens()
    {
        const string source = """
            using System.Collections.Generic;

            namespace TestApp;

            public class Processor
            {
                public System.Collections.Generic.List<int> Items()
                {
                    return new System.Collections.Generic.List<int>();
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new SimplifyNameOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new SimplifyNameParams
        {
            SourceFile = workspace.SourcePath
        });

        Assert.True(result.Success);
        Assert.False(result.Preview);
        Assert.True(result.SimplificationsApplied >= 1);
        Assert.Equal("file", result.Scope);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.DoesNotContain("System.Collections.Generic.List", updated, StringComparison.Ordinal);
        Assert.Contains("List<int>", updated, StringComparison.Ordinal);
        await AssertCompilesAsync(workspace);
    }

    [SkippableFact]
    public async Task SimplifyName_MultipleNames_ShortensEach()
    {
        const string source = """
            using System.Collections.Generic;
            using System.Text;

            namespace TestApp;

            public class Processor
            {
                public System.Collections.Generic.List<int> Items()
                {
                    return new System.Collections.Generic.List<int>();
                }

                public System.Text.StringBuilder Builder()
                {
                    return new System.Text.StringBuilder();
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new SimplifyNameOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new SimplifyNameParams
        {
            SourceFile = workspace.SourcePath
        });

        Assert.True(result.Success);
        Assert.True(result.SimplificationsApplied >= 2);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.DoesNotContain("System.Collections.Generic.List", updated, StringComparison.Ordinal);
        Assert.DoesNotContain("System.Text.StringBuilder", updated, StringComparison.Ordinal);
        Assert.Contains("List<int>", updated, StringComparison.Ordinal);
        Assert.Contains("StringBuilder", updated, StringComparison.Ordinal);
        await AssertCompilesAsync(workspace);
    }

    [SkippableFact]
    public async Task SimplifyName_SkipAmbiguous_ReportsReasonAndKeepsConflict()
    {
        const string source = """
            using System.Collections.Generic;
            using System.Text;

            namespace TestApp;

            public class StringBuilder
            {
            }

            public class Processor
            {
                public System.Collections.Generic.List<int> Items()
                {
                    return new System.Collections.Generic.List<int>();
                }

                public System.Text.StringBuilder Builder()
                {
                    return new System.Text.StringBuilder();
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new SimplifyNameOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new SimplifyNameParams
        {
            SourceFile = workspace.SourcePath
        });

        Assert.True(result.Success);
        Assert.True(result.SimplificationsApplied >= 1);
        Assert.True(result.SimplificationsSkipped >= 1);
        Assert.NotNull(result.SkippedReasons);
        Assert.Contains(result.SkippedReasons, skip =>
            skip.Name.Contains("StringBuilder", StringComparison.Ordinal) &&
            (skip.Reason.Contains("conflict", StringComparison.OrdinalIgnoreCase)
                || skip.Reason.Contains("ambiguous", StringComparison.OrdinalIgnoreCase)));
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("System.Text.StringBuilder", updated, StringComparison.Ordinal);
        Assert.DoesNotContain("System.Collections.Generic.List", updated, StringComparison.Ordinal);
        await AssertCompilesAsync(workspace);
    }

    [SkippableFact]
    public async Task SimplifyName_AllAmbiguous_ThrowsNoSimplifiableNames()
    {
        const string source = """
            using System.Text;

            namespace TestApp;

            public class StringBuilder
            {
            }

            public class Processor
            {
                public System.Text.StringBuilder Builder()
                {
                    return new System.Text.StringBuilder();
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new SimplifyNameOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new SimplifyNameParams
            {
                SourceFile = workspace.SourcePath
            }));

        Assert.Equal(ErrorCodes.NoSimplifiableNames, ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task SimplifyName_FileScope_NothingToSimplify_Throws()
    {
        const string source = """
            using System.Collections.Generic;

            namespace TestApp;

            public class Processor
            {
                public List<int> Items() => new List<int>();
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new SimplifyNameOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new SimplifyNameParams
            {
                SourceFile = workspace.SourcePath
            }));

        Assert.Equal(ErrorCodes.NoSimplifiableNames, ex.ErrorCode);
    }

    #endregion

    #region P0 Preview

    [SkippableFact]
    public async Task SimplifyName_Preview_DoesNotModifyFile()
    {
        const string source = """
            using System.Collections.Generic;

            namespace TestApp;

            public class Processor
            {
                public System.Collections.Generic.List<int> Items()
                {
                    return new System.Collections.Generic.List<int>();
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new SimplifyNameOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new SimplifyNameParams
        {
            SourceFile = workspace.SourcePath,
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.True(result.SimplificationsApplied >= 1);
        Assert.Equal("file", result.Scope);
        Assert.NotNull(result.PendingChanges);
        Assert.Contains(result.PendingChanges, change =>
            change.Description.Contains("Simplify", StringComparison.Ordinal) &&
            change.AfterSnippet != null &&
            change.AfterSnippet.Contains("List", StringComparison.Ordinal));
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    #endregion

    #region P1 Scope and Edge

    [SkippableFact]
    public async Task SimplifyName_Location_SimplifiesOnlyThatName()
    {
        const string source = """
            using System.Collections.Generic;
            using System.Text;

            namespace TestApp;

            public class Processor
            {
                public System.Collections.Generic.List<int> Items()
                {
                    return new System.Collections.Generic.List<int>();
                }

                public System.Text.StringBuilder Builder()
                {
                    return new System.Text.StringBuilder();
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new SimplifyNameOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new SimplifyNameParams
        {
            SourceFile = workspace.SourcePath,
            Scope = "location",
            Line = FindLine(source, "System.Text.StringBuilder Builder")
        });

        Assert.True(result.Success);
        Assert.Equal(1, result.SimplificationsApplied);
        Assert.Equal("location", result.Scope);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("System.Collections.Generic.List", updated, StringComparison.Ordinal);
        Assert.DoesNotContain("System.Text.StringBuilder Builder", updated, StringComparison.Ordinal);
        Assert.Contains("StringBuilder Builder", updated, StringComparison.Ordinal);
        await AssertCompilesAsync(workspace);
    }

    [SkippableFact]
    public async Task SimplifyName_Location_NoName_Throws()
    {
        const string source = """
            using System.Collections.Generic;

            namespace TestApp;

            public class Processor
            {
                public List<int> Items() => new List<int>();
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new SimplifyNameOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new SimplifyNameParams
            {
                SourceFile = workspace.SourcePath,
                Scope = "location",
                Line = FindLine(source, "public class Processor")
            }));

        Assert.Equal(ErrorCodes.NoSimplifiableNames, ex.ErrorCode);
    }

    [SkippableFact]
    public async Task SimplifyName_GenericTypeQualification_Shortens()
    {
        const string source = """
            using System.Collections.Generic;

            namespace TestApp;

            public class Processor
            {
                public System.Collections.Generic.Dictionary<string, int> Map()
                {
                    return new System.Collections.Generic.Dictionary<string, int>();
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new SimplifyNameOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new SimplifyNameParams
        {
            SourceFile = workspace.SourcePath
        });

        Assert.True(result.Success);
        Assert.True(result.SimplificationsApplied >= 1);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.DoesNotContain("System.Collections.Generic.Dictionary", updated, StringComparison.Ordinal);
        Assert.Contains("Dictionary<string, int>", updated, StringComparison.Ordinal);
        await AssertCompilesAsync(workspace);
    }

    [SkippableFact]
    public async Task SimplifyName_NestedTypeQualification_Shortens()
    {
        const string source = """
            namespace TestApp;

            public class Outer
            {
                public class Inner
                {
                    public int Value { get; set; }
                }
            }

            public class Consumer
            {
                public TestApp.Outer.Inner Create()
                {
                    return new TestApp.Outer.Inner();
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new SimplifyNameOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new SimplifyNameParams
        {
            SourceFile = workspace.SourcePath
        });

        Assert.True(result.Success);
        Assert.True(result.SimplificationsApplied >= 1);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.DoesNotContain("TestApp.Outer.Inner", updated, StringComparison.Ordinal);
        Assert.Contains("Outer.Inner", updated, StringComparison.Ordinal);
        await AssertCompilesAsync(workspace);
    }

    [SkippableFact]
    public async Task SimplifyName_GlobalAlias_Required_IsLeftAlone()
    {
        const string source = """
            using System.Text;

            namespace TestApp;

            public class StringBuilder
            {
            }

            public class Processor
            {
                public global::System.Text.StringBuilder Builder()
                {
                    return new global::System.Text.StringBuilder();
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new SimplifyNameOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        try
        {
            var result = await operation.ExecuteAsync(new SimplifyNameParams
            {
                SourceFile = workspace.SourcePath
            });

            var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
            Assert.DoesNotContain("public StringBuilder Builder", updated, StringComparison.Ordinal);
            Assert.Contains("System.Text.StringBuilder", updated, StringComparison.Ordinal);
            if (result.Success && result.SimplificationsApplied > 0)
                await AssertCompilesAsync(workspace);
        }
        catch (RefactoringException ex)
        {
            Assert.Equal(ErrorCodes.NoSimplifiableNames, ex.ErrorCode);
            Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
        }
    }

    [SkippableFact]
    public async Task SimplifyName_Nameof_PreservesMeaning()
    {
        const string source = """
            using System;

            namespace TestApp;

            public class Processor
            {
                public string Name() => nameof(System.String);
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new SimplifyNameOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new SimplifyNameParams
        {
            SourceFile = workspace.SourcePath
        });

        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        if (result.Success && result.SimplificationsApplied > 0)
        {
            Assert.DoesNotContain("nameof(System.String)", updated, StringComparison.Ordinal);
            Assert.Contains("nameof(", updated, StringComparison.Ordinal);
        }

        await AssertCompilesAsync(workspace);
    }

    [SkippableFact]
    public async Task SimplifyName_CurrentNamespace_Shortens()
    {
        const string source = """
            namespace TestApp.Services;

            public class Worker
            {
            }

            public class Consumer
            {
                public TestApp.Services.Worker Create() => new TestApp.Services.Worker();
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new SimplifyNameOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new SimplifyNameParams
        {
            SourceFile = workspace.SourcePath
        });

        Assert.True(result.Success);
        Assert.True(result.SimplificationsApplied >= 1);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.DoesNotContain("TestApp.Services.Worker", updated, StringComparison.Ordinal);
        Assert.Contains("Worker", updated, StringComparison.Ordinal);
        await AssertCompilesAsync(workspace);
    }

    #endregion

    #region Helpers

    private static async Task AssertCompilesAsync(TempWorkspace workspace)
    {
        var document = workspace.Context.GetDocumentByPath(workspace.SourcePath);
        Assert.NotNull(document);
        var compilation = await document.Project.GetCompilationAsync();
        Assert.NotNull(compilation);
        var errors = compilation.GetDiagnostics()
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Select(diagnostic => diagnostic.ToString())
            .ToList();
        Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors));
    }

    private static string AbsoluteTestPath() =>
        Path.Combine(Path.GetTempPath(), "RoslynMcpSimplifyNameMissing.cs");

    private static string NormalizeNewlines(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal);

    private static int FindLine(string source, string snippet)
    {
        var index = source.IndexOf(snippet, StringComparison.Ordinal);
        if (index < 0)
            throw new InvalidOperationException($"Snippet not found: {snippet}");

        var line = 1;
        for (var i = 0; i < index; i++)
        {
            if (source[i] == '\n')
                line++;
        }

        return line;
    }

    private static int ColumnOnLine(string source, int index)
    {
        var lineStart = source.LastIndexOf('\n', index) + 1;
        return index - lineStart + 1;
    }

    private sealed class TempWorkspace : IAsyncDisposable
    {
        public required string DirectoryPath { get; init; }
        public required string ProjectPath { get; init; }
        public required string SourcePath { get; init; }
        public required WorkspaceContext Context { get; init; }

        public static async Task<TempWorkspace> CreateAsync(string source, string fileName = "Types.cs")
        {
            Skip.IfNot(ModuleInitializer.MsBuildAvailable, ModuleInitializer.MsBuildError ?? "MSBuild not available");

            var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpSimplifyName_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);

            var projectPath = Path.Combine(directory, "TestApp.csproj");
            var sourcePath = Path.Combine(directory, fileName);

            await File.WriteAllTextAsync(projectPath, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net9.0</TargetFramework>
                    <Nullable>enable</Nullable>
                  </PropertyGroup>
                </Project>
                """);
            await File.WriteAllTextAsync(sourcePath, source);

            try
            {
                var provider = new MSBuildWorkspaceProvider();
                var context = await provider.CreateContextAsync(projectPath);
                if (context.GetDocumentByPath(sourcePath) == null)
                {
                    context.Dispose();
                    throw new InvalidOperationException($"Workspace loaded but did not include {sourcePath}.");
                }

                return new TempWorkspace
                {
                    DirectoryPath = directory,
                    ProjectPath = projectPath,
                    SourcePath = sourcePath,
                    Context = context
                };
            }
            catch (Exception ex) when (ex is not SkipException)
            {
                try
                {
                    Directory.Delete(directory, recursive: true);
                }
                catch
                {
                    // ignore cleanup failures
                }

                Skip.If(true, $"Workspace load failed: {ex.Message}");
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            Context.Dispose();
            await Task.Run(() =>
            {
                try
                {
                    Directory.Delete(DirectoryPath, recursive: true);
                }
                catch
                {
                    // ignore locked temp files
                }
            });
        }
    }

    #endregion
}

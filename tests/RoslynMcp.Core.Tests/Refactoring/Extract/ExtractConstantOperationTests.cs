using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Extract;
using RoslynMcp.Core.Workspace;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Extract;

/// <summary>
/// Operation-level tests for <see cref="ExtractConstantOperation"/> allFiles.
/// </summary>
public class ExtractConstantOperationTests
{
    private const string EligibleFileA = """
        namespace TestApp;

        public class FileA
        {
            public int Run()
            {
                return 42 + 42;
            }

            public string Greet()
            {
                return "hi";
            }
        }
        """;

    private const string EligibleFileB = """
        namespace TestApp;

        public class FileB
        {
            public int Capacity()
            {
                return 10;
            }
        }
        """;

    private const string IneligibleFileC = """
        namespace TestApp;

        public class FileC
        {
            private const int Already = 99;

            public int Prop => Already;

            public void NoLiterals()
            {
            }
        }
        """;

    private const string CollisionFile = """
        namespace TestApp;

        public class CollisionHost
        {
            private const int _42 = 1;

            public int Run()
            {
                return 42;
            }
        }
        """;

    private const string TypeAttributeLiteralFile = """
        using System;

        [Obsolete("hi")]
        public class AttributeHost
        {
            public int Run()
            {
                return 42;
            }
        }
        """;

    [SkippableFact]
    public async Task ExtractConstant_OmittedAllFiles_KeepsSingleSiteExtract()
    {
        const string source = """
            namespace TestApp;

            public class Calculator
            {
                public int Run()
                {
                    return 42;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ExtractConstantOperation(workspace.Context);
        var span = FindSpan(source, "42");

        var result = await operation.ExecuteAsync(new ExtractConstantParams
        {
            SourceFile = workspace.SourcePath,
            StartLine = span.StartLine,
            StartColumn = span.StartColumn,
            EndLine = span.EndLine,
            EndColumn = span.EndColumn,
            ConstantName = "MaxRetries"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("const int MaxRetries", updated, StringComparison.Ordinal);
        Assert.Contains("return MaxRetries;", updated, StringComparison.Ordinal);
        Assert.DoesNotContain("return 42;", updated, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task ExtractConstant_AllFilesTrue_ExtractsEligibleLiteralsAcrossFiles()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("FileA.cs", EligibleFileA),
            ("FileB.cs", EligibleFileB),
            ("FileC.cs", IneligibleFileC));
        var operation = new ExtractConstantOperation(workspace.Context);
        var beforeC = await File.ReadAllTextAsync(workspace.SourcePaths["FileC.cs"]);

        var result = await operation.ExecuteAsync(new ExtractConstantParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        Assert.False(result.Preview);
        var updatedA = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePaths["FileA.cs"]));
        var updatedB = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePaths["FileB.cs"]));
        Assert.Contains("const int _42", updatedA, StringComparison.Ordinal);
        Assert.Contains("const string Hi", updatedA, StringComparison.Ordinal);
        // Without replaceAll, only the first matching literal is rewritten; the
        // second 42 skips on name collision with the newly introduced const.
        Assert.Contains("return _42 + 42;", updatedA, StringComparison.Ordinal);
        Assert.Contains("return Hi;", updatedA, StringComparison.Ordinal);
        Assert.Contains("const int _10", updatedB, StringComparison.Ordinal);
        Assert.Contains("return _10;", updatedB, StringComparison.Ordinal);
        Assert.Equal(beforeC, await File.ReadAllTextAsync(workspace.SourcePaths["FileC.cs"]));
        Assert.True(result.Changes!.FilesModified.Count >= 2);
        Assert.Contains(result.Changes.FilesModified, p => PathEquals(p, workspace.SourcePaths["FileA.cs"]));
        Assert.Contains(result.Changes.FilesModified, p => PathEquals(p, workspace.SourcePaths["FileB.cs"]));
        Assert.DoesNotContain(result.Changes.FilesModified, p => PathEquals(p, workspace.SourcePaths["FileC.cs"]));
    }

    [SkippableFact]
    public async Task ExtractConstant_AllFilesTrue_WithoutSourceFileOrConstantName_Succeeds()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("FileA.cs", EligibleFileA),
            ("FileB.cs", EligibleFileB));
        var operation = new ExtractConstantOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ExtractConstantParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        Assert.True(result.Changes!.FilesModified.Count >= 2);
    }

    [SkippableFact]
    public async Task ExtractConstant_AllFilesFalse_WithoutSourceFile_MissingRequiredParam()
    {
        await using var workspace = await TempWorkspace.CreateAsync(EligibleFileA);
        var operation = new ExtractConstantOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ExtractConstantParams
            {
                AllFiles = false,
                ConstantName = "MaxRetries",
                StartLine = 8,
                StartColumn = 1,
                EndLine = 8,
                EndColumn = 5
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("sourceFile", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task ExtractConstant_AllFilesTrue_WithConstantName_Rejects()
    {
        await using var workspace = await TempWorkspace.CreateAsync(EligibleFileA);
        var operation = new ExtractConstantOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ExtractConstantParams
            {
                AllFiles = true,
                ConstantName = "MaxRetries"
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("constantName", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task ExtractConstant_AllFilesTrue_WithStartLine_Rejects()
    {
        await using var workspace = await TempWorkspace.CreateAsync(EligibleFileA);
        var operation = new ExtractConstantOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ExtractConstantParams
            {
                AllFiles = true,
                StartLine = 8
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("startLine", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task ExtractConstant_PreviewAllFiles_AggregatesChangedFilesAndWritesNothing()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("FileA.cs", EligibleFileA),
            ("FileB.cs", EligibleFileB),
            ("FileC.cs", IneligibleFileC));
        var operation = new ExtractConstantOperation(workspace.Context);
        var beforeA = await File.ReadAllTextAsync(workspace.SourcePaths["FileA.cs"]);
        var beforeB = await File.ReadAllTextAsync(workspace.SourcePaths["FileB.cs"]);
        var beforeC = await File.ReadAllTextAsync(workspace.SourcePaths["FileC.cs"]);

        var result = await operation.ExecuteAsync(new ExtractConstantParams
        {
            AllFiles = true,
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.True(result.PendingChanges.Count >= 2);
        Assert.Contains(result.PendingChanges, c => PathEquals(c.File, workspace.SourcePaths["FileA.cs"]));
        Assert.Contains(result.PendingChanges, c => PathEquals(c.File, workspace.SourcePaths["FileB.cs"]));
        Assert.DoesNotContain(result.PendingChanges, c => PathEquals(c.File, workspace.SourcePaths["FileC.cs"]));
        Assert.Contains(result.PendingChanges, c =>
            c.Description.Contains("Extract", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(beforeA, await File.ReadAllTextAsync(workspace.SourcePaths["FileA.cs"]));
        Assert.Equal(beforeB, await File.ReadAllTextAsync(workspace.SourcePaths["FileB.cs"]));
        Assert.Equal(beforeC, await File.ReadAllTextAsync(workspace.SourcePaths["FileC.cs"]));
    }

    [SkippableFact]
    public async Task ExtractConstant_AllFilesTrue_EveryFileIneligible_SucceedsWithEmptyChanges()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("FileC.cs", IneligibleFileC),
            ("FileC2.cs", IneligibleFileC));
        var operation = new ExtractConstantOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ExtractConstantParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        Assert.Empty(result.Changes!.FilesModified);
        Assert.Empty(result.Changes.FilesCreated);
        Assert.Empty(result.Changes.FilesDeleted);
    }

    [SkippableFact]
    public async Task ExtractConstant_AllFilesTrue_OptionalSourceFile_LimitsWalk()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("FileA.cs", EligibleFileA),
            ("FileB.cs", EligibleFileB),
            ("FileC.cs", IneligibleFileC));
        var operation = new ExtractConstantOperation(workspace.Context);
        var beforeB = await File.ReadAllTextAsync(workspace.SourcePaths["FileB.cs"]);
        var beforeC = await File.ReadAllTextAsync(workspace.SourcePaths["FileC.cs"]);

        var result = await operation.ExecuteAsync(new ExtractConstantParams
        {
            AllFiles = true,
            SourceFile = workspace.SourcePaths["FileA.cs"]
        });

        Assert.True(result.Success);
        var updatedA = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePaths["FileA.cs"]));
        Assert.Contains("const int _42", updatedA, StringComparison.Ordinal);
        Assert.Equal(beforeB, await File.ReadAllTextAsync(workspace.SourcePaths["FileB.cs"]));
        Assert.Equal(beforeC, await File.ReadAllTextAsync(workspace.SourcePaths["FileC.cs"]));
        Assert.Single(result.Changes!.FilesModified);
        Assert.Contains(result.Changes.FilesModified, p => PathEquals(p, workspace.SourcePaths["FileA.cs"]));
    }

    [SkippableFact]
    public async Task ExtractConstant_AllFilesTrue_SkipsNameCollision()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("Collision.cs", CollisionFile));
        var operation = new ExtractConstantOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePaths["Collision.cs"]);

        var result = await operation.ExecuteAsync(new ExtractConstantParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePaths["Collision.cs"]));
        Assert.Empty(result.Changes!.FilesModified);
    }


    [SkippableFact]
    public async Task ExtractConstant_AllFilesTrue_SkipsPropertyNameCollision()
    {
        const string source = """
            namespace TestApp;

            public class Host
            {
                public string Name { get; set; }

                public string Run()
                {
                    return "name";
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ExtractConstantOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var result = await operation.ExecuteAsync(new ExtractConstantParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Empty(result.Changes!.FilesModified);
    }

    [SkippableFact]
    public async Task ExtractConstant_AllFilesTrue_SkipsParameterShadowing()
    {
        const string source = """
            namespace TestApp;

            public class Host
            {
                public int Run(int _42)
                {
                    return 42;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ExtractConstantOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var result = await operation.ExecuteAsync(new ExtractConstantParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Empty(result.Changes!.FilesModified);
    }

    [SkippableFact]
    public async Task ExtractConstant_AllFilesTrue_UsesEnumConvertedTypeForZeroLiteral()
    {
        const string source = """
            namespace TestApp;

            public enum State { Off = 0, On = 1 }

            public class Host
            {
                public State Run()
                {
                    State value = 0;
                    return value;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ExtractConstantOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ExtractConstantParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("const", updated, StringComparison.Ordinal);
        Assert.Contains("State _0", updated, StringComparison.Ordinal);
        Assert.Contains("State value = _0;", updated, StringComparison.Ordinal);
        Assert.DoesNotContain("const int _0", updated, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task ExtractConstant_AllFilesTrue_ReplaceAll_DoesNotRewriteNestedTypeLiterals()
    {
        const string source = """
            namespace TestApp;

            public class Outer
            {
                public int Run()
                {
                    return 42;
                }

                public class Nested
                {
                    private const int _42 = 7;

                    public int Run()
                    {
                        return 42;
                    }
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ExtractConstantOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ExtractConstantParams
        {
            AllFiles = true,
            ReplaceAll = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("const int _42 = 42", updated, StringComparison.Ordinal);
        // Nested keeps its own const and its literal 42 (collision skip or untouched).
        Assert.Contains("private const int _42 = 7;", updated, StringComparison.Ordinal);
        Assert.Contains("return _42;", updated, StringComparison.Ordinal);
        Assert.True(CountOccurrences(updated, "return 42;") >= 1);
    }

    [Fact]
    public void IsVisibilityIncompatible_ProtectedOnStaticClass_True()
    {
        var tree = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText("""
            static class Host { public static int Run() => 1; }
            """);
        var type = tree.GetRoot().DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax>().Single();
        Assert.True(ExtractConstantOperation.IsVisibilityIncompatibleWithContainingType("protected", type));
        Assert.False(ExtractConstantOperation.IsVisibilityIncompatibleWithContainingType("private", type));
    }

    [Fact]
    public void IsVisibilityIncompatible_ProtectedOnStruct_True()
    {
        var tree = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText("""
            struct Point { public int Run() => 1; }
            """);
        var type = tree.GetRoot().DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.StructDeclarationSyntax>().Single();
        Assert.True(ExtractConstantOperation.IsVisibilityIncompatibleWithContainingType("protected", type));
        Assert.True(ExtractConstantOperation.IsVisibilityIncompatibleWithContainingType("protected internal", type));
        Assert.False(ExtractConstantOperation.IsVisibilityIncompatibleWithContainingType("public", type));
    }

    [SkippableFact]
    public async Task ExtractConstant_AllFilesTrue_Protected_SkipsStaticClassLiterals()
    {
        const string source = """
            namespace TestApp;

            public static class Host
            {
                public static int Run()
                {
                    return 42;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ExtractConstantOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var result = await operation.ExecuteAsync(new ExtractConstantParams
        {
            AllFiles = true,
            Visibility = "protected"
        });

        Assert.True(result.Success);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Empty(result.Changes!.FilesModified);
    }

    [SkippableFact]
    public async Task ExtractConstant_AllFilesTrue_ReplaceAll_ReplacesMatchingLiteralsInType()
    {
        const string source = """
            namespace TestApp;

            public class Host
            {
                public int Run()
                {
                    return 7 + 7;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ExtractConstantOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ExtractConstantParams
        {
            AllFiles = true,
            ReplaceAll = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("const int _7", updated, StringComparison.Ordinal);
        Assert.Contains("return _7 + _7;", updated, StringComparison.Ordinal);
        Assert.DoesNotContain("return 7 + 7;", updated, StringComparison.Ordinal);
        // Only one const field for the value when replaceAll collapses matches.
        Assert.Equal(1, CountOccurrences(updated, "const int _7"));
    }

    [SkippableFact]
    public async Task ExtractConstant_AllFilesTrue_SkipsAttributeLiterals()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("AttributeHost.cs", TypeAttributeLiteralFile));
        var operation = new ExtractConstantOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ExtractConstantParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePaths["AttributeHost.cs"]));
        Assert.Contains("[Obsolete(\"hi\")]", updated, StringComparison.Ordinal);
        Assert.Contains("const int _42", updated, StringComparison.Ordinal);
        Assert.Contains("return _42;", updated, StringComparison.Ordinal);
        Assert.DoesNotContain("[Obsolete(Hi)]", updated, StringComparison.Ordinal);
    }

    #region Helpers

    private static bool PathEquals(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static string NormalizeNewlines(string text) =>
        text.Replace("\r\n", "\n");

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    private static (int StartLine, int StartColumn, int EndLine, int EndColumn) FindSpan(string source, string snippet)
    {
        var index = source.IndexOf(snippet, StringComparison.Ordinal);
        if (index < 0)
            throw new InvalidOperationException($"Snippet not found: {snippet}");

        return (GetLineColumn(source, index).Line, GetLineColumn(source, index).Column,
            GetLineColumn(source, index + snippet.Length).Line, GetLineColumn(source, index + snippet.Length).Column);
    }

    private static (int Line, int Column) GetLineColumn(string source, int index)
    {
        var line = 1;
        var column = 1;
        for (var i = 0; i < index; i++)
        {
            if (source[i] == '\n')
            {
                line++;
                column = 1;
            }
            else if (source[i] != '\r')
            {
                column++;
            }
        }

        return (line, column);
    }

    private sealed class TempWorkspace : IAsyncDisposable
    {
        public required string DirectoryPath { get; init; }
        public required string ProjectPath { get; init; }
        public required string SourcePath { get; init; }
        public required IReadOnlyDictionary<string, string> SourcePaths { get; init; }
        public required WorkspaceContext Context { get; init; }

        public static Task<TempWorkspace> CreateAsync(string source, string fileName = "Types.cs") =>
            CreateMultiFileAsync((fileName, source));

        public static Task<TempWorkspace> CreateWithFilesAsync(params (string FileName, string Source)[] files) =>
            CreateMultiFileAsync(files);

        public static async Task<TempWorkspace> CreateMultiFileAsync(params (string FileName, string Source)[] files)
        {
            Skip.IfNot(ModuleInitializer.MsBuildAvailable, ModuleInitializer.MsBuildError ?? "MSBuild not available");

            var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpExtractConstant_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);

            var projectPath = Path.Combine(directory, "TestApp.csproj");
            var sourcePaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            await File.WriteAllTextAsync(projectPath, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net9.0</TargetFramework>
                    <Nullable>enable</Nullable>
                    <GenerateAssemblyInfo>false</GenerateAssemblyInfo>
                    <GenerateTargetFrameworkAttribute>false</GenerateTargetFrameworkAttribute>
                  </PropertyGroup>
                </Project>
                """);

            string? firstSource = null;
            foreach (var (fileName, source) in files)
            {
                var sourcePath = Path.Combine(directory, fileName);
                Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
                await File.WriteAllTextAsync(sourcePath, source);
                sourcePaths[fileName] = sourcePath;
                firstSource ??= sourcePath;
            }

            try
            {
                var provider = new MSBuildWorkspaceProvider();
                var context = await provider.CreateContextAsync(projectPath);
                foreach (var sourcePath in sourcePaths.Values)
                {
                    if (context.GetDocumentByPath(sourcePath) == null)
                    {
                        context.Dispose();
                        throw new InvalidOperationException($"Workspace loaded but did not include {sourcePath}.");
                    }
                }

                return new TempWorkspace
                {
                    DirectoryPath = directory,
                    ProjectPath = projectPath,
                    SourcePath = firstSource!,
                    SourcePaths = sourcePaths,
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

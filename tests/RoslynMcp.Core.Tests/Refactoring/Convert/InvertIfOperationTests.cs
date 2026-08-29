using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Convert;
using RoslynMcp.Core.Workspace;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring;

/// <summary>
/// Operation-level tests for <see cref="InvertIfOperation"/> (UC-A4).
/// </summary>
public class InvertIfOperationTests
{
    #region Input Validation

    [Fact]
    public void Validate_MissingSourceFile_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            InvertIfOperation.Validate(new InvertIfParams
            {
                SourceFile = "",
                Line = 1
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public void Validate_RelativePath_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            InvertIfOperation.Validate(new InvertIfParams
            {
                SourceFile = "Types.cs",
                Line = 1
            }));

        Assert.Equal(ErrorCodes.InvalidSourcePath, ex.ErrorCode);
    }

    [Fact]
    public void Validate_InvalidLine_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            InvertIfOperation.Validate(new InvertIfParams
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
            InvertIfOperation.Validate(new InvertIfParams
            {
                SourceFile = AbsoluteTestPath(),
                Line = 1,
                Column = 0
            }));

        Assert.Equal(ErrorCodes.InvalidColumnNumber, ex.ErrorCode);
    }

    [Fact]
    public void Validate_MissingFile_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            InvertIfOperation.Validate(new InvertIfParams
            {
                SourceFile = AbsoluteTestPath(),
                Line = 1
            }));

        Assert.Equal(ErrorCodes.SourceFileNotFound, ex.ErrorCode);
    }

    #endregion

    #region P0 Happy Path

    [SkippableFact]
    public async Task InvertIf_SimpleComparison_FlipsOperator()
    {
        const string source = """
            namespace TestApp;

            public class Calculator
            {
                public string Classify(int x)
                {
                    if (x > 0)
                        return "positive";
                    else
                        return "non-positive";
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new InvertIfOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new InvertIfParams
        {
            SourceFile = workspace.SourcePath,
            Line = FindLine(source, "if (x > 0)")
        });

        Assert.True(result.Success);
        Assert.False(result.Preview);
        Assert.Equal("x > 0", result.OriginalCondition);
        Assert.Equal("x <= 0", result.InvertedCondition);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("if (x <= 0)", updated);
        Assert.DoesNotContain("if (x > 0)", updated);
        Assert.Contains("return \"non-positive\";", updated);
        Assert.Contains("return \"positive\";", updated);
        AssertPositiveAfterNonPositive(updated);
    }

    [SkippableFact]
    public async Task InvertIf_WithElse_SwapsBranches()
    {
        const string source = """
            namespace TestApp;

            public class Router
            {
                public void Route(bool flag)
                {
                    if (flag)
                    {
                        ThenBranch();
                    }
                    else
                    {
                        ElseBranch();
                    }
                }

                private static void ThenBranch() { }
                private static void ElseBranch() { }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new InvertIfOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new InvertIfParams
        {
            SourceFile = workspace.SourcePath,
            Line = FindLine(source, "if (flag)")
        });

        Assert.True(result.Success);
        Assert.Equal("flag", result.OriginalCondition);
        Assert.Equal("!flag", result.InvertedCondition);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("if (!flag)", updated);
        var thenIndex = updated.IndexOf("ThenBranch();", StringComparison.Ordinal);
        var elseIndex = updated.IndexOf("ElseBranch();", StringComparison.Ordinal);
        Assert.True(elseIndex >= 0 && thenIndex >= 0);
        Assert.True(elseIndex < thenIndex, "Else branch should become the if body.");
    }

    #endregion

    #region P0 Rejects

    [SkippableFact]
    public async Task InvertIf_NoIfAtLocation_Throws()
    {
        const string source = """
            namespace TestApp;

            public class Empty
            {
                public void Work()
                {
                    var x = 1;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new InvertIfOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new InvertIfParams
            {
                SourceFile = workspace.SourcePath,
                Line = FindLine(source, "var x = 1;")
            }));

        Assert.Equal(ErrorCodes.NoIfStatementAtLocation, ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task InvertIf_PatternVariable_Throws()
    {
        const string source = """
            namespace TestApp;

            public class Parser
            {
                public int Length(object value)
                {
                    if (value is string s)
                        return s.Length;
                    else
                        return 0;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new InvertIfOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new InvertIfParams
            {
                SourceFile = workspace.SourcePath,
                Line = FindLine(source, "if (value is string s)")
            }));

        Assert.Equal(ErrorCodes.ConditionNotInvertible, ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [Fact]
    public void InvertIf_UneditableDocument_Throws()
    {
        var workspace = new AdhocWorkspace();
        var project = workspace.AddProject("P", LanguageNames.CSharp);
        var document = workspace.AddDocument(project.Id, "Generated.cs", SourceText.From("class C {}"));

        var ex = Assert.Throws<RefactoringException>(() =>
            InvertIfOperation.ValidateDocumentIsEditable(document, workspace));

        Assert.Equal(ErrorCodes.DocumentNotEditable, ex.ErrorCode);
    }

    #endregion

    #region P0 Preview

    [SkippableFact]
    public async Task InvertIf_Preview_DoesNotModifyFile()
    {
        const string source = """
            namespace TestApp;

            public class Calculator
            {
                public string Classify(int x)
                {
                    if (x > 0)
                        return "positive";
                    else
                        return "non-positive";
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new InvertIfOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new InvertIfParams
        {
            SourceFile = workspace.SourcePath,
            Line = FindLine(source, "if (x > 0)"),
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.Equal("x > 0", result.OriginalCondition);
        Assert.Equal("x <= 0", result.InvertedCondition);
        Assert.NotNull(result.PendingChanges);
        Assert.Contains(result.PendingChanges, change =>
            change.Description.Contains("x > 0", StringComparison.Ordinal) &&
            change.Description.Contains("x <= 0", StringComparison.Ordinal) &&
            change.Description.Contains("swap if/else branches", StringComparison.Ordinal));
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    #endregion

    #region P1

    [SkippableFact]
    public async Task InvertIf_LogicalAnd_AppliesDeMorgan()
    {
        const string source = """
            namespace TestApp;

            public class Gate
            {
                public void Run(bool a, bool b)
                {
                    if (a && b)
                    {
                        Both();
                    }
                    else
                    {
                        Other();
                    }
                }

                private static void Both() { }
                private static void Other() { }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new InvertIfOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new InvertIfParams
        {
            SourceFile = workspace.SourcePath,
            Line = FindLine(source, "if (a && b)")
        });

        Assert.True(result.Success);
        Assert.Equal("a && b", result.OriginalCondition);
        Assert.Equal("!a || !b", result.InvertedCondition);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("if (!a || !b)", updated);
        var bothIndex = updated.IndexOf("Both();", StringComparison.Ordinal);
        var otherIndex = updated.IndexOf("Other();", StringComparison.Ordinal);
        Assert.True(otherIndex < bothIndex);
    }

    [SkippableFact]
    public async Task InvertIf_LogicalOr_AppliesDeMorgan()
    {
        const string source = """
            namespace TestApp;

            public class Gate
            {
                public void Run(bool a, bool b)
                {
                    if (a || b)
                    {
                        Either();
                    }
                    else
                    {
                        Neither();
                    }
                }

                private static void Either() { }
                private static void Neither() { }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new InvertIfOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new InvertIfParams
        {
            SourceFile = workspace.SourcePath,
            Line = FindLine(source, "if (a || b)")
        });

        Assert.True(result.Success);
        Assert.Equal("!a && !b", result.InvertedCondition);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("if (!a && !b)", updated);
    }

    [SkippableFact]
    public async Task InvertIf_WithoutElse_CreatesEmptyIfAndMovesBody()
    {
        const string source = """
            namespace TestApp;

            public class Logger
            {
                public void Log(bool enabled)
                {
                    if (enabled)
                    {
                        Write();
                    }
                }

                private static void Write() { }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new InvertIfOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new InvertIfParams
        {
            SourceFile = workspace.SourcePath,
            Line = FindLine(source, "if (enabled)")
        });

        Assert.True(result.Success);
        Assert.Equal("!enabled", result.InvertedCondition);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("if (!enabled)", updated);
        Assert.Contains("else", updated);
        Assert.Contains("Write();", updated);
        var ifIndex = updated.IndexOf("if (!enabled)", StringComparison.Ordinal);
        var elseIndex = updated.IndexOf("else", ifIndex, StringComparison.Ordinal);
        var writeIndex = updated.IndexOf("Write();", StringComparison.Ordinal);
        Assert.True(ifIndex < elseIndex);
        Assert.True(elseIndex < writeIndex);
    }

    [SkippableFact]
    public async Task InvertIf_NestedIf_InvertsOnlyTargetedIf()
    {
        const string source = """
            namespace TestApp;

            public class Nest
            {
                public void Run(bool outer, bool inner)
                {
                    if (outer)
                    {
                        if (inner)
                        {
                            InnerThen();
                        }
                        else
                        {
                            InnerElse();
                        }

                        AfterInner();
                    }
                    else
                    {
                        OuterElse();
                    }
                }

                private static void InnerThen() { }
                private static void InnerElse() { }
                private static void AfterInner() { }
                private static void OuterElse() { }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new InvertIfOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new InvertIfParams
        {
            SourceFile = workspace.SourcePath,
            Line = FindLine(source, "if (inner)")
        });

        Assert.True(result.Success);
        Assert.Equal("inner", result.OriginalCondition);
        Assert.Equal("!inner", result.InvertedCondition);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("if (outer)", updated);
        Assert.Contains("if (!inner)", updated);
        Assert.DoesNotContain("if (inner)", updated);
        Assert.Contains("OuterElse();", updated);
        Assert.Contains("AfterInner();", updated);
        var innerElseIndex = updated.IndexOf("InnerElse();", StringComparison.Ordinal);
        var innerThenIndex = updated.IndexOf("InnerThen();", StringComparison.Ordinal);
        Assert.True(innerElseIndex < innerThenIndex);
        var afterInner = updated.IndexOf("AfterInner();", StringComparison.Ordinal);
        Assert.True(innerThenIndex < afterInner);
    }

    [SkippableFact]
    public async Task InvertIf_ColumnSelectsInnerIfOnSameLine()
    {
        const string source = """
            namespace TestApp;

            public class SameLine
            {
                public string Pick(bool a, bool b)
                {
                    if (a) if (b) return "both"; else return "a-only"; else return "none";
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new InvertIfOperation(workspace.Context);
        var line = FindLine(source, "if (a) if (b)");
        var innerIfColumn = source.IndexOf("if (b)", StringComparison.Ordinal)
            - source.LastIndexOf('\n', source.IndexOf("if (b)", StringComparison.Ordinal));

        var result = await operation.ExecuteAsync(new InvertIfParams
        {
            SourceFile = workspace.SourcePath,
            Line = line,
            Column = innerIfColumn
        });

        Assert.True(result.Success);
        Assert.Equal("b", result.OriginalCondition);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("if (a)", updated);
        Assert.Contains("if (!b)", updated);
    }

    #endregion

    #region Helpers

    private static void AssertPositiveAfterNonPositive(string updated)
    {
        var nonPositive = updated.IndexOf("return \"non-positive\";", StringComparison.Ordinal);
        var positive = updated.IndexOf("return \"positive\";", StringComparison.Ordinal);
        Assert.True(nonPositive >= 0 && positive >= 0);
        Assert.True(nonPositive < positive);
    }

    private static string AbsoluteTestPath() =>
        Path.Combine(Path.GetTempPath(), "RoslynMcpInvertIfMissing.cs");

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

    private sealed class TempWorkspace : IAsyncDisposable
    {
        public required string DirectoryPath { get; init; }
        public required string ProjectPath { get; init; }
        public required string SourcePath { get; init; }
        public required WorkspaceContext Context { get; init; }

        public static async Task<TempWorkspace> CreateAsync(string source, string fileName = "Types.cs")
        {
            Skip.IfNot(ModuleInitializer.MsBuildAvailable, ModuleInitializer.MsBuildError ?? "MSBuild not available");

            var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpInvertIf_" + Guid.NewGuid().ToString("N"));
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

using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Extract;
using RoslynMcp.Core.Workspace;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Extract;

/// <summary>
/// Operation-level tests for <see cref="ExtractVariableOperation"/>, including <c>replaceAll</c>.
/// </summary>
public class ExtractVariableOperationTests
{
    private const string DuplicateExpressionSource = """
        namespace TestApp;

        public class Calculator
        {
            public int Compute(int a, int b)
            {
                var first = a + b;
                var second = a + b;
                return first + second;
            }
        }
        """;

    [SkippableFact]
    public async Task ExtractVariable_Default_ReplacesOnlySelectedOccurrence()
    {
        await using var workspace = await TempWorkspace.CreateAsync(DuplicateExpressionSource);
        var operation = new ExtractVariableOperation(workspace.Context);
        var span = FindSpan(DuplicateExpressionSource, "a + b");

        var result = await operation.ExecuteAsync(new ExtractVariableParams
        {
            SourceFile = workspace.SourcePath,
            StartLine = span.StartLine,
            StartColumn = span.StartColumn,
            EndLine = span.EndLine,
            EndColumn = span.EndColumn,
            VariableName = "extracted"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("var extracted = a + b;", updated);
        Assert.Contains("var first = extracted;", updated);
        Assert.Contains("var second = a + b;", updated);
        Assert.DoesNotContain("var second = extracted;", updated);
        Assert.Equal(2, CountOccurrences(updated, "a + b"));
    }

    [SkippableFact]
    public async Task ExtractVariable_ReplaceAllFalse_ReplacesOnlySelectedOccurrence()
    {
        await using var workspace = await TempWorkspace.CreateAsync(DuplicateExpressionSource);
        var operation = new ExtractVariableOperation(workspace.Context);
        var span = FindSpan(DuplicateExpressionSource, "a + b");

        var result = await operation.ExecuteAsync(new ExtractVariableParams
        {
            SourceFile = workspace.SourcePath,
            StartLine = span.StartLine,
            StartColumn = span.StartColumn,
            EndLine = span.EndLine,
            EndColumn = span.EndColumn,
            VariableName = "extracted",
            ReplaceAll = false
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("var first = extracted;", updated);
        Assert.Contains("var second = a + b;", updated);
        Assert.DoesNotContain("var second = extracted;", updated);
    }

    [SkippableFact]
    public async Task ExtractVariable_ReplaceAllTrue_ReplacesAllEquivalentsInSameScope()
    {
        await using var workspace = await TempWorkspace.CreateAsync(DuplicateExpressionSource);
        var operation = new ExtractVariableOperation(workspace.Context);
        var span = FindSpan(DuplicateExpressionSource, "a + b");

        var result = await operation.ExecuteAsync(new ExtractVariableParams
        {
            SourceFile = workspace.SourcePath,
            StartLine = span.StartLine,
            StartColumn = span.StartColumn,
            EndLine = span.EndLine,
            EndColumn = span.EndColumn,
            VariableName = "extracted",
            ReplaceAll = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("var extracted = a + b;", updated);
        Assert.Contains("var first = extracted;", updated);
        Assert.Contains("var second = extracted;", updated);
        Assert.DoesNotContain("var second = a + b;", updated);
        Assert.Equal(1, CountOccurrences(updated, "a + b"));
    }

    [SkippableFact]
    public async Task ExtractVariable_ReplaceAllTrue_SelectedLaterOccurrence_InsertsBeforeFirstUse()
    {
        await using var workspace = await TempWorkspace.CreateAsync(DuplicateExpressionSource);
        var operation = new ExtractVariableOperation(workspace.Context);
        var span = FindSpan(DuplicateExpressionSource, "a + b", occurrence: 2);

        var result = await operation.ExecuteAsync(new ExtractVariableParams
        {
            SourceFile = workspace.SourcePath,
            StartLine = span.StartLine,
            StartColumn = span.StartColumn,
            EndLine = span.EndLine,
            EndColumn = span.EndColumn,
            VariableName = "extracted",
            ReplaceAll = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var declarationIndex = updated.IndexOf("var extracted = a + b;", StringComparison.Ordinal);
        var firstUseIndex = updated.IndexOf("var first = extracted;", StringComparison.Ordinal);
        var secondUseIndex = updated.IndexOf("var second = extracted;", StringComparison.Ordinal);
        Assert.True(declarationIndex >= 0 && firstUseIndex > declarationIndex && secondUseIndex > firstUseIndex);
        Assert.DoesNotContain("var first = a + b;", updated);
        Assert.DoesNotContain("var second = a + b;", updated);
    }

    [SkippableFact]
    public async Task ExtractVariable_ReplaceAllTrue_SkipsOccurrenceAfterInterveningWrite()
    {
        const string source = """
            namespace TestApp;

            public class Calculator
            {
                public int Compute()
                {
                    int x = 0;
                    var first = x + 1;
                    x = 10;
                    var second = x + 1;
                    return first + second;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ExtractVariableOperation(workspace.Context);
        var span = FindSpan(source, "x + 1");

        var result = await operation.ExecuteAsync(new ExtractVariableParams
        {
            SourceFile = workspace.SourcePath,
            StartLine = span.StartLine,
            StartColumn = span.StartColumn,
            EndLine = span.EndLine,
            EndColumn = span.EndColumn,
            VariableName = "extracted",
            ReplaceAll = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("var extracted = x + 1;", updated);
        Assert.Contains("var first = extracted;", updated);
        Assert.Contains("x = 10;", updated);
        Assert.Contains("var second = x + 1;", updated);
        Assert.DoesNotContain("var second = extracted;", updated);
    }

    [SkippableFact]
    public async Task ExtractVariable_ReplaceAllTrue_DoesNotHoistOutOfGuardedBlocks()
    {
        const string source = """
            namespace TestApp;

            public class Calculator
            {
                public int Compute(bool ok, int divisor)
                {
                    int result = 0;
                    if (ok)
                    {
                        result += 10 / divisor;
                    }
                    if (ok)
                    {
                        result += 10 / divisor;
                    }
                    return result;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ExtractVariableOperation(workspace.Context);
        var span = FindSpan(source, "10 / divisor");

        var result = await operation.ExecuteAsync(new ExtractVariableParams
        {
            SourceFile = workspace.SourcePath,
            StartLine = span.StartLine,
            StartColumn = span.StartColumn,
            EndLine = span.EndLine,
            EndColumn = span.EndColumn,
            VariableName = "extracted",
            ReplaceAll = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("var extracted = 10 / divisor;", updated);
        Assert.Contains("result += extracted;", updated);
        Assert.Equal(1, CountOccurrences(updated, "result += extracted;"));
        Assert.Equal(1, CountOccurrences(updated, "result += 10 / divisor;"));

        var firstIf = updated.IndexOf("if (ok)", StringComparison.Ordinal);
        var secondIf = updated.IndexOf("if (ok)", firstIf + 1, StringComparison.Ordinal);
        var declaration = updated.IndexOf("var extracted = 10 / divisor;", StringComparison.Ordinal);
        Assert.True(firstIf >= 0 && secondIf > firstIf);
        Assert.True(declaration > firstIf && declaration < secondIf);
    }

    [SkippableFact]
    public async Task ExtractVariable_ReplaceAllTrue_PropertyGetter_Fails3038()
    {
        const string source = """
            namespace TestApp;

            public class Calculator
            {
                private int _n;

                public int Value => _n++;

                public int Compute()
                {
                    var first = Value;
                    var second = Value;
                    return first + second;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ExtractVariableOperation(workspace.Context);
        var span = FindSpan(source, "Value", occurrence: 2);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ExtractVariableParams
            {
                SourceFile = workspace.SourcePath,
                StartLine = span.StartLine,
                StartColumn = span.StartColumn,
                EndLine = span.EndLine,
                EndColumn = span.EndColumn,
                VariableName = "extracted",
                ReplaceAll = true
            }));

        Assert.Equal(ErrorCodes.ExpressionHasSideEffects, ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task ExtractVariable_ReplaceAllTrue_DoesNotReplaceDifferentBindings()
    {
        const string source = """
            namespace TestApp;

            public class Calculator
            {
                public int Compute(int a, int b)
                {
                    var first = a + b;
                    {
                        int a = 3;
                        int b = 4;
                        var second = a + b;
                    }
                    return first;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ExtractVariableOperation(workspace.Context);
        var span = FindSpan(source, "a + b");

        var result = await operation.ExecuteAsync(new ExtractVariableParams
        {
            SourceFile = workspace.SourcePath,
            StartLine = span.StartLine,
            StartColumn = span.StartColumn,
            EndLine = span.EndLine,
            EndColumn = span.EndColumn,
            VariableName = "extracted",
            ReplaceAll = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("var first = extracted;", updated);
        Assert.Contains("var second = a + b;", updated);
        Assert.DoesNotContain("var second = extracted;", updated);
    }

    [SkippableFact]
    public async Task ExtractVariable_ReplaceAllTrue_DoesNotReplaceOtherMethod()
    {
        const string source = """
            namespace TestApp;

            public class Calculator
            {
                public int First(int a, int b)
                {
                    return a + b;
                }

                public int Second(int a, int b)
                {
                    return a + b;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ExtractVariableOperation(workspace.Context);
        var span = FindSpan(source, "a + b");

        var result = await operation.ExecuteAsync(new ExtractVariableParams
        {
            SourceFile = workspace.SourcePath,
            StartLine = span.StartLine,
            StartColumn = span.StartColumn,
            EndLine = span.EndLine,
            EndColumn = span.EndColumn,
            VariableName = "extracted",
            ReplaceAll = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("return extracted;", updated);
        Assert.Equal(1, CountOccurrences(updated, "return a + b;"));
        Assert.Contains("public int Second(int a, int b)", updated);
    }

    [SkippableFact]
    public async Task ExtractVariable_ReplaceAllTrue_NoAdditionalEquivalents_SucceedsAsSingle()
    {
        const string source = """
            namespace TestApp;

            public class Calculator
            {
                public int Compute(int a, int b)
                {
                    return a + b;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ExtractVariableOperation(workspace.Context);
        var span = FindSpan(source, "a + b");

        var result = await operation.ExecuteAsync(new ExtractVariableParams
        {
            SourceFile = workspace.SourcePath,
            StartLine = span.StartLine,
            StartColumn = span.StartColumn,
            EndLine = span.EndLine,
            EndColumn = span.EndColumn,
            VariableName = "extracted",
            ReplaceAll = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("var extracted = a + b;", updated);
        Assert.Contains("return extracted;", updated);
    }

    [SkippableFact]
    public async Task ExtractVariable_ReplaceAllTrue_UseVarFalse_KeepsExplicitType()
    {
        await using var workspace = await TempWorkspace.CreateAsync(DuplicateExpressionSource);
        var operation = new ExtractVariableOperation(workspace.Context);
        var span = FindSpan(DuplicateExpressionSource, "a + b");

        var result = await operation.ExecuteAsync(new ExtractVariableParams
        {
            SourceFile = workspace.SourcePath,
            StartLine = span.StartLine,
            StartColumn = span.StartColumn,
            EndLine = span.EndLine,
            EndColumn = span.EndColumn,
            VariableName = "extracted",
            UseVar = false,
            ReplaceAll = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("int extracted = a + b;", updated);
        Assert.DoesNotContain("var extracted = a + b;", updated);
        Assert.Contains("var first = extracted;", updated);
        Assert.Contains("var second = extracted;", updated);
    }

    [SkippableTheory]
    [InlineData("GetValue()", "int GetValue() => 1;")]
    [InlineData("counter++", "int counter = 0;")]
    [InlineData("++counter", "int counter = 0;")]
    [InlineData("counter += 1", "int counter = 0;")]
    [InlineData("new System.Uri(\"https://example.com\")", "")]
    [InlineData("new int[3]", "")]
    public async Task ExtractVariable_ReplaceAllTrue_SideEffectingExpression_Fails3038(
        string expression,
        string extraMember)
    {
        var source = $$"""
            namespace TestApp;

            public class Calculator
            {
                public int Compute()
                {
                    var first = {{expression}};
                    var second = {{expression}};
                    return 0;
                }

                {{extraMember}}
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ExtractVariableOperation(workspace.Context);
        var span = FindSpan(source, expression);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ExtractVariableParams
            {
                SourceFile = workspace.SourcePath,
                StartLine = span.StartLine,
                StartColumn = span.StartColumn,
                EndLine = span.EndLine,
                EndColumn = span.EndColumn,
                VariableName = "extracted",
                ReplaceAll = true
            }));

        Assert.Equal(ErrorCodes.ExpressionHasSideEffects, ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task ExtractVariable_ReplaceAllTrue_AwaitExpression_Fails3038()
    {
        const string source = """
            namespace TestApp;

            public class Calculator
            {
                public async System.Threading.Tasks.Task<int> Compute()
                {
                    var first = await GetAsync();
                    var second = await GetAsync();
                    return first + second;
                }

                private static System.Threading.Tasks.Task<int> GetAsync() => System.Threading.Tasks.Task.FromResult(1);
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ExtractVariableOperation(workspace.Context);
        var span = FindSpan(source, "await GetAsync()");
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ExtractVariableParams
            {
                SourceFile = workspace.SourcePath,
                StartLine = span.StartLine,
                StartColumn = span.StartColumn,
                EndLine = span.EndLine,
                EndColumn = span.EndColumn,
                VariableName = "extracted",
                ReplaceAll = true
            }));

        Assert.Equal(ErrorCodes.ExpressionHasSideEffects, ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task ExtractVariable_ReplaceAllFalse_SideEffectingExpression_StillSucceeds()
    {
        const string source = """
            namespace TestApp;

            public class Calculator
            {
                public int Compute()
                {
                    var first = GetValue();
                    var second = GetValue();
                    return first + second;
                }

                private int GetValue() => 1;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ExtractVariableOperation(workspace.Context);
        var span = FindSpan(source, "GetValue()");

        var result = await operation.ExecuteAsync(new ExtractVariableParams
        {
            SourceFile = workspace.SourcePath,
            StartLine = span.StartLine,
            StartColumn = span.StartColumn,
            EndLine = span.EndLine,
            EndColumn = span.EndColumn,
            VariableName = "extracted",
            ReplaceAll = false
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("var extracted = GetValue();", updated);
        Assert.Contains("var first = extracted;", updated);
        Assert.Contains("var second = GetValue();", updated);
    }

    [SkippableFact]
    public async Task ExtractVariable_Preview_DoesNotWriteFiles()
    {
        await using var workspace = await TempWorkspace.CreateAsync(DuplicateExpressionSource);
        var operation = new ExtractVariableOperation(workspace.Context);
        var span = FindSpan(DuplicateExpressionSource, "a + b");
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var result = await operation.ExecuteAsync(new ExtractVariableParams
        {
            SourceFile = workspace.SourcePath,
            StartLine = span.StartLine,
            StartColumn = span.StartColumn,
            EndLine = span.EndLine,
            EndColumn = span.EndColumn,
            VariableName = "extracted",
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.NotEmpty(result.PendingChanges);
        Assert.Contains("extracted", result.PendingChanges[0].AfterSnippet);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task ExtractVariable_ReplaceAllTrue_Preview_DoesNotWriteFiles()
    {
        await using var workspace = await TempWorkspace.CreateAsync(DuplicateExpressionSource);
        var operation = new ExtractVariableOperation(workspace.Context);
        var span = FindSpan(DuplicateExpressionSource, "a + b");
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var result = await operation.ExecuteAsync(new ExtractVariableParams
        {
            SourceFile = workspace.SourcePath,
            StartLine = span.StartLine,
            StartColumn = span.StartColumn,
            EndLine = span.EndLine,
            EndColumn = span.EndColumn,
            VariableName = "extracted",
            ReplaceAll = true,
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.NotEmpty(result.PendingChanges);
        Assert.Contains("extracted", result.PendingChanges[0].AfterSnippet);
        Assert.Contains("2 replacements", result.PendingChanges[0].Description);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

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

    private static (int StartLine, int StartColumn, int EndLine, int EndColumn) FindSpan(
        string source,
        string snippet,
        int occurrence = 1)
    {
        var index = -1;
        for (var i = 0; i < occurrence; i++)
        {
            index = source.IndexOf(snippet, index + 1, StringComparison.Ordinal);
            if (index < 0)
                throw new InvalidOperationException($"Snippet not found (occurrence {occurrence}): {snippet}");
        }

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
            else
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
        public required WorkspaceContext Context { get; init; }

        public static async Task<TempWorkspace> CreateAsync(string source, string fileName = "Calculator.cs")
        {
            Skip.IfNot(ModuleInitializer.MsBuildAvailable, ModuleInitializer.MsBuildError ?? "MSBuild not available");

            var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpExtractVariable_" + Guid.NewGuid().ToString("N"));
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
}

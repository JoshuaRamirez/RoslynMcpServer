using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Query;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Workspace;
using Xunit;

namespace RoslynMcp.Core.Tests.Query;

/// <summary>
/// Operation-level tests for <see cref="GetCodeMetricsOperation"/>, including optional
/// <c>column</c> disambiguation via shipped <c>SymbolResolver.ResolveSymbolAsync</c>.
/// </summary>
public class GetCodeMetricsOperationTests
{
    // Methods start at column 1 so today's Line-only path (SymbolResolver column ?? 1)
    // lands on `void` and walks up to the declaration instead of indent trivia.
    // Closing """ is at column 0 so raw-string dedent does not re-indent these lines.
    private const string SameLineDualProcessSource =
        """
class Container
{
void Unique() { }
void Process(int x) { } /* a-process */ void Process(string s) { if (true) { } } /* b-process */
}
""";

    private const string SeparateLineDualProcessSource =
        """
class Container
{
void Process(int x) { } // a-process
void Process(string s) { if (true) { } } // b-process
}
""";

    #region Input Validation

    [Fact]
    public void Column_DefaultsToNull()
    {
        var @params = new GetCodeMetricsParams { SourceFile = AbsoluteTestPath() };
        Assert.Null(@params.Column);
    }

    #endregion

    #region Existing file-level / symbolName / line cases

    [SkippableFact]
    public async Task GetCodeMetrics_FileLevel_UsesFileName()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SameLineDualProcessSource);
        var operation = new GetCodeMetricsOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GetCodeMetricsParams
        {
            SourceFile = workspace.SourcePath
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Equal(Path.GetFileName(workspace.SourcePath), result.Data.SymbolName);
        Assert.Equal(workspace.SourcePath, result.Data.FullyQualifiedName);
    }

    [SkippableFact]
    public async Task GetCodeMetrics_SymbolName_PicksUniqueType()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SameLineDualProcessSource);
        var operation = new GetCodeMetricsOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GetCodeMetricsParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Container"
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Equal("Container", result.Data.SymbolName);
        Assert.Contains("Container", result.Data.FullyQualifiedName, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task GetCodeMetrics_LineOnly_PicksTypeOnClassLine()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SeparateLineDualProcessSource);
        var operation = new GetCodeMetricsOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GetCodeMetricsParams
        {
            SourceFile = workspace.SourcePath,
            Line = FindLine(SeparateLineDualProcessSource, "class Container")
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Equal("Container", result.Data.SymbolName);
        Assert.Contains("Container", result.Data.FullyQualifiedName, StringComparison.Ordinal);
    }

    #endregion

    #region P0 optional column disambiguation

    [SkippableFact]
    public async Task GetCodeMetrics_ColumnOnAIdentifier_PicksA()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SameLineDualProcessSource);
        var operation = new GetCodeMetricsOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GetCodeMetricsParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Process",
            Line = FindLine(SameLineDualProcessSource, "a-process"),
            Column = ColumnOf(SameLineDualProcessSource, "Process(int")
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Equal("Process", result.Data.SymbolName);
        Assert.Contains("int", result.Data.FullyQualifiedName, StringComparison.Ordinal);
        Assert.DoesNotContain("string", result.Data.FullyQualifiedName, StringComparison.Ordinal);
        Assert.Equal(1, result.Data.CyclomaticComplexity);
    }

    [SkippableFact]
    public async Task GetCodeMetrics_ColumnOnBIdentifier_PicksB()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SameLineDualProcessSource);
        var operation = new GetCodeMetricsOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GetCodeMetricsParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Process",
            Line = FindLine(SameLineDualProcessSource, "b-process"),
            Column = ColumnOf(SameLineDualProcessSource, "Process(string")
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Equal("Process", result.Data.SymbolName);
        Assert.Contains("string", result.Data.FullyQualifiedName, StringComparison.Ordinal);
        Assert.Equal(2, result.Data.CyclomaticComplexity);
    }

    [SkippableFact]
    public async Task GetCodeMetrics_OmittedColumn_PreservesLineOnlyPath()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SameLineDualProcessSource);
        var operation = new GetCodeMetricsOperation(workspace.Context);
        var classLine = FindLine(SameLineDualProcessSource, "class Container");
        var processLine = FindLine(SameLineDualProcessSource, "a-process");

        var omittedClass = await operation.ExecuteAsync(new GetCodeMetricsParams
        {
            SourceFile = workspace.SourcePath,
            Line = classLine
        });
        var nullColumnClass = await operation.ExecuteAsync(new GetCodeMetricsParams
        {
            SourceFile = workspace.SourcePath,
            Line = classLine,
            Column = null
        });

        Assert.True(omittedClass.Success);
        Assert.True(nullColumnClass.Success);
        Assert.NotNull(omittedClass.Data);
        Assert.NotNull(nullColumnClass.Data);
        Assert.Equal(nullColumnClass.Data.SymbolName, omittedClass.Data.SymbolName);
        Assert.Equal(nullColumnClass.Data.FullyQualifiedName, omittedClass.Data.FullyQualifiedName);
        Assert.Equal(nullColumnClass.Data.CyclomaticComplexity, omittedClass.Data.CyclomaticComplexity);
        Assert.Equal("Container", omittedClass.Data.SymbolName);

        // Same-line dual Process: today's Line-only path is column ?? 1 on `void`,
        // which resolves to System.Void (no declaring syntax). Do not invent a
        // covering-span / FirstOrDefault pick of Process(string).
        var omittedProcess = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GetCodeMetricsParams
            {
                SourceFile = workspace.SourcePath,
                Line = processLine
            }));
        var nullColumnProcess = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GetCodeMetricsParams
            {
                SourceFile = workspace.SourcePath,
                Line = processLine,
                Column = null
            }));

        Assert.Equal(nullColumnProcess.ErrorCode, omittedProcess.ErrorCode);
        Assert.Equal(nullColumnProcess.Message, omittedProcess.Message);
        Assert.Equal(ErrorCodes.SymbolNotFound, omittedProcess.ErrorCode);
        Assert.Contains("no syntax declaration", omittedProcess.Message, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task GetCodeMetrics_OmittedColumn_DoesNotInventLineFirstOrDefault()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SeparateLineDualProcessSource);
        var operation = new GetCodeMetricsOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GetCodeMetricsParams
            {
                SourceFile = workspace.SourcePath,
                Line = FindLine(SeparateLineDualProcessSource, "b-process")
            }));

        Assert.Equal(ErrorCodes.SymbolNotFound, ex.ErrorCode);
        Assert.Contains("no syntax declaration", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task GetCodeMetrics_ColumnWithoutLine_UniqueName_KeepsNameBasedPath()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SameLineDualProcessSource);
        var operation = new GetCodeMetricsOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GetCodeMetricsParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Unique",
            Column = ColumnOf(SameLineDualProcessSource, "Process(string")
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Equal("Unique", result.Data.SymbolName);
        Assert.Equal(1, result.Data.CyclomaticComplexity);
    }

    [SkippableFact]
    public async Task GetCodeMetrics_ColumnWithoutLine_AmbiguousName_ThrowsSymbolAmbiguous()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SameLineDualProcessSource);
        var operation = new GetCodeMetricsOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GetCodeMetricsParams
            {
                SourceFile = workspace.SourcePath,
                SymbolName = "Process",
                Column = ColumnOf(SameLineDualProcessSource, "Process(string")
            }));

        Assert.Equal(ErrorCodes.SymbolAmbiguous, ex.ErrorCode);
        Assert.Equal("2004", ex.ErrorCode);
    }

    [SkippableFact]
    public async Task GetCodeMetrics_ColumnWithoutLine_NoSymbolName_KeepsFileLevelPath()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SameLineDualProcessSource);
        var operation = new GetCodeMetricsOperation(workspace.Context);

        var strayColumn = await operation.ExecuteAsync(new GetCodeMetricsParams
        {
            SourceFile = workspace.SourcePath,
            Column = ColumnOf(SameLineDualProcessSource, "Process(string")
        });
        var fileLevel = await operation.ExecuteAsync(new GetCodeMetricsParams
        {
            SourceFile = workspace.SourcePath
        });

        Assert.True(strayColumn.Success);
        Assert.True(fileLevel.Success);
        Assert.NotNull(strayColumn.Data);
        Assert.NotNull(fileLevel.Data);
        Assert.Equal(fileLevel.Data.SymbolName, strayColumn.Data.SymbolName);
        Assert.Equal(fileLevel.Data.FullyQualifiedName, strayColumn.Data.FullyQualifiedName);
        Assert.Equal(fileLevel.Data.CyclomaticComplexity, strayColumn.Data.CyclomaticComplexity);
        Assert.Equal(Path.GetFileName(workspace.SourcePath), strayColumn.Data.SymbolName);
    }

    [SkippableFact]
    public async Task GetCodeMetrics_ColumnOnCrlfSource_PicksB()
    {
        var source = SameLineDualProcessSource.Replace("\n", "\r\n", StringComparison.Ordinal);
        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new GetCodeMetricsOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GetCodeMetricsParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Process",
            Line = FindLine(source, "b-process"),
            Column = ColumnOf(source, "Process(string")
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Equal("Process", result.Data.SymbolName);
        Assert.Contains("string", result.Data.FullyQualifiedName, StringComparison.Ordinal);
        Assert.Equal(2, result.Data.CyclomaticComplexity);
    }

    [SkippableFact]
    public async Task GetCodeMetrics_InvalidColumn_Throws()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SameLineDualProcessSource);
        var operation = new GetCodeMetricsOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GetCodeMetricsParams
            {
                SourceFile = workspace.SourcePath,
                Line = 1,
                Column = 0
            }));

        Assert.Equal(ErrorCodes.InvalidColumnNumber, ex.ErrorCode);
        Assert.Equal("1007", ex.ErrorCode);
        Assert.Equal("Column number must be >= 1.", ex.Message);
    }

    [SkippableFact]
    public async Task GetCodeMetrics_NegativeColumn_Throws()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SameLineDualProcessSource);
        var operation = new GetCodeMetricsOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GetCodeMetricsParams
            {
                SourceFile = workspace.SourcePath,
                Line = 1,
                Column = -1
            }));

        Assert.Equal(ErrorCodes.InvalidColumnNumber, ex.ErrorCode);
        Assert.Equal("1007", ex.ErrorCode);
        Assert.Equal("Column number must be >= 1.", ex.Message);
    }

    [SkippableTheory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task GetCodeMetrics_EmptySourceFile_WithColumnAndLine_ThrowsMissingRequiredParam(string sourceFile)
    {
        await using var workspace = await TempWorkspace.CreateAsync(SameLineDualProcessSource);
        var operation = new GetCodeMetricsOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GetCodeMetricsParams
            {
                SourceFile = sourceFile,
                Line = 1,
                Column = 1
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    #endregion

    #region Helpers

    private static string AbsoluteTestPath(string name = "Missing.cs") =>
        Path.Combine(Path.GetTempPath(), "RoslynMcpGetCodeMetrics_" + name);

    private static string NormalizeNewlines(string source) =>
        source.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    private static int FindLine(string source, string snippet)
    {
        source = NormalizeNewlines(source);
        snippet = NormalizeNewlines(snippet);
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

    private static int ColumnOf(string source, string snippet)
    {
        source = NormalizeNewlines(source);
        snippet = NormalizeNewlines(snippet);
        var index = source.IndexOf(snippet, StringComparison.Ordinal);
        if (index < 0)
            throw new InvalidOperationException($"Snippet not found: {snippet}");

        var lineStart = source.LastIndexOf('\n', index);
        return index - lineStart;
    }

    private sealed class TempWorkspace : IAsyncDisposable
    {
        public required string DirectoryPath { get; init; }
        public required string SourcePath { get; init; }
        public required WorkspaceContext Context { get; init; }

        public static Task<TempWorkspace> CreateAsync(string source, string fileName = "Foo.cs") =>
            CreateAsync((fileName, source));

        public static async Task<TempWorkspace> CreateAsync(params (string FileName, string Source)[] files)
        {
            Skip.IfNot(ModuleInitializer.MsBuildAvailable, ModuleInitializer.MsBuildError ?? "MSBuild not available");

            var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpGetCodeMetrics_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);

            var projectPath = Path.Combine(directory, "TestApp.csproj");
            await File.WriteAllTextAsync(projectPath, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net9.0</TargetFramework>
                    <Nullable>enable</Nullable>
                  </PropertyGroup>
                </Project>
                """);

            string? sourcePath = null;
            foreach (var (fileName, source) in files)
            {
                var relative = fileName.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
                var path = Path.Combine(directory, relative);
                var parent = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(parent))
                    Directory.CreateDirectory(parent);
                await File.WriteAllTextAsync(path, source);
                sourcePath ??= path;
            }

            sourcePath ??= Path.Combine(directory, "Foo.cs");

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

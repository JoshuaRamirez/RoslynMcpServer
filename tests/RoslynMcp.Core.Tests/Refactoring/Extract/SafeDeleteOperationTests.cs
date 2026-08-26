using System.Collections;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Extract;
using RoslynMcp.Core.Workspace;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Extract;

/// <summary>
/// Operation-level tests for <see cref="SafeDeleteOperation"/>.
/// </summary>
public class SafeDeleteOperationTests
{
    #region Input Validation

    [Fact]
    public void Validate_MissingSourceFile_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            SafeDeleteOperation.Validate(new SafeDeleteParams
            {
                SourceFile = "",
                StartLine = 1,
                StartColumn = 1,
                EndLine = 1,
                EndColumn = 2
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public void Validate_RelativePath_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            SafeDeleteOperation.Validate(new SafeDeleteParams
            {
                SourceFile = "Types.cs",
                StartLine = 1,
                StartColumn = 1,
                EndLine = 1,
                EndColumn = 2
            }));

        Assert.Equal(ErrorCodes.InvalidSourcePath, ex.ErrorCode);
    }

    [Fact]
    public void Validate_InvalidLine_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            SafeDeleteOperation.Validate(new SafeDeleteParams
            {
                SourceFile = AbsoluteTestPath(),
                StartLine = 0,
                StartColumn = 1,
                EndLine = 1,
                EndColumn = 2
            }));

        Assert.Equal(ErrorCodes.InvalidLineNumber, ex.ErrorCode);
    }

    [Fact]
    public void Validate_InvalidSelectionRange_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            SafeDeleteOperation.Validate(new SafeDeleteParams
            {
                SourceFile = AbsoluteTestPath(),
                StartLine = 2,
                StartColumn = 1,
                EndLine = 1,
                EndColumn = 2
            }));

        Assert.Equal(ErrorCodes.InvalidSelectionRange, ex.ErrorCode);
    }

    [Fact]
    public void Validate_MissingFile_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            SafeDeleteOperation.Validate(new SafeDeleteParams
            {
                SourceFile = AbsoluteTestPath(),
                StartLine = 1,
                StartColumn = 1,
                EndLine = 1,
                EndColumn = 2
            }));

        Assert.Equal(ErrorCodes.SourceFileNotFound, ex.ErrorCode);
    }

    #endregion

    #region P0 Happy Path

    [SkippableFact]
    public async Task SafeDelete_UnusedField_RemovesDeclaration()
    {
        const string source = """
            namespace TestApp;

            public class Calculator
            {
                private int _unused;

                public int Get()
                {
                    return 42;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new SafeDeleteOperation(workspace.Context);
        var span = FindSpan(source, "_unused");

        var result = await operation.ExecuteAsync(new SafeDeleteParams
        {
            SourceFile = workspace.SourcePath,
            StartLine = span.StartLine,
            StartColumn = span.StartColumn,
            EndLine = span.EndLine,
            EndColumn = span.EndColumn,
            SymbolName = "_unused"
        });

        Assert.True(result.Success);
        Assert.False(result.Preview);
        Assert.NotNull(result.Symbol);
        Assert.Equal("_unused", result.Symbol.Name);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.DoesNotContain("_unused", updated);
        Assert.Contains("return 42;", updated);
    }

    [SkippableFact]
    public async Task SafeDelete_UnusedMethod_RemovesDeclaration()
    {
        const string source = """
            namespace TestApp;

            public class Calculator
            {
                public int Get()
                {
                    return 42;
                }

                private void UnusedHelper()
                {
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new SafeDeleteOperation(workspace.Context);
        var span = FindSpan(source, "UnusedHelper");

        var result = await operation.ExecuteAsync(new SafeDeleteParams
        {
            SourceFile = workspace.SourcePath,
            StartLine = span.StartLine,
            StartColumn = span.StartColumn,
            EndLine = span.EndLine,
            EndColumn = span.EndColumn
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.DoesNotContain("UnusedHelper", updated);
        Assert.Contains("public int Get()", updated);
    }

    #endregion

    #region P0 Preview

    [SkippableFact]
    public async Task SafeDelete_Preview_DoesNotModifyFile()
    {
        const string source = """
            namespace TestApp;

            public class Calculator
            {
                private int _unused;

                public int Get()
                {
                    return 42;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new SafeDeleteOperation(workspace.Context);
        var span = FindSpan(source, "_unused");

        var result = await operation.ExecuteAsync(new SafeDeleteParams
        {
            SourceFile = workspace.SourcePath,
            StartLine = span.StartLine,
            StartColumn = span.StartColumn,
            EndLine = span.EndLine,
            EndColumn = span.EndColumn,
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.Contains(result.PendingChanges, change => change.Description.Contains("_unused"));
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    #endregion

    #region P0 Rejects

    [SkippableFact]
    public async Task SafeDelete_InUseField_ThrowsWithLocations()
    {
        const string source = """
            namespace TestApp;

            public class Calculator
            {
                private int _used;

                public int Get()
                {
                    return _used;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new SafeDeleteOperation(workspace.Context);
        var span = FindSpan(source, "_used");
        var usage = FindSpan(source, "return _used;");
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new SafeDeleteParams
            {
                SourceFile = workspace.SourcePath,
                StartLine = span.StartLine,
                StartColumn = span.StartColumn,
                EndLine = span.EndLine,
                EndColumn = span.EndColumn,
                SymbolName = "_used"
            }));

        Assert.Equal(ErrorCodes.MemberHasUsages, ex.ErrorCode);
        Assert.NotNull(ex.Details);
        Assert.True(ex.Details.ContainsKey("locations"));
        Assert.Equal(1, Convert.ToInt32(ex.Details["usageCount"]));

        var locations = Assert.IsAssignableFrom<IEnumerable>(ex.Details["locations"]);
        var locationMaps = locations.Cast<Dictionary<string, object>>().ToList();
        Assert.NotEmpty(locationMaps);
        Assert.Contains(locationMaps, location =>
            Convert.ToInt32(location["line"]) == usage.StartLine &&
            location["snippet"] is string snippet &&
            snippet.Contains("_used"));
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task SafeDelete_NoSymbol_Throws()
    {
        const string source = """
            namespace TestApp;

            public class Calculator
            {
                public int Get()
                {
                    return 42;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new SafeDeleteOperation(workspace.Context);
        var span = FindSpan(source, "return");

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new SafeDeleteParams
            {
                SourceFile = workspace.SourcePath,
                StartLine = span.StartLine,
                StartColumn = span.StartColumn,
                EndLine = span.EndLine,
                EndColumn = span.EndColumn
            }));

        Assert.Equal(ErrorCodes.SymbolNotFound, ex.ErrorCode);
    }

    [Fact]
    public void SafeDelete_UneditableDocument_Throws()
    {
        var workspace = new AdhocWorkspace();
        var project = workspace.AddProject("P", LanguageNames.CSharp);
        var document = workspace.AddDocument(project.Id, "Generated.cs", SourceText.From("class C {}"));

        var ex = Assert.Throws<RefactoringException>(() =>
            SafeDeleteOperation.ValidateDocumentIsEditable(document, workspace));

        Assert.Equal(ErrorCodes.DocumentNotEditable, ex.ErrorCode);
    }

    #endregion

    #region Helpers

    private static string AbsoluteTestPath() =>
        Path.Combine(Path.GetTempPath(), "RoslynMcpSafeDeleteMissing.cs");

    private static string NormalizeNewlines(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal);

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

        public static async Task<TempWorkspace> CreateAsync(string source, string fileName = "Types.cs")
        {
            Skip.IfNot(ModuleInitializer.MsBuildAvailable, ModuleInitializer.MsBuildError ?? "MSBuild not available");

            var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpSafeDelete_" + Guid.NewGuid().ToString("N"));
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

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
/// Operation-level tests for <see cref="MakeStaticOperation"/>.
/// </summary>
public class MakeStaticOperationTests
{
    #region Input Validation

    [Fact]
    public void Validate_MissingSourceFile_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            MakeStaticOperation.Validate(new MakeStaticParams
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
            MakeStaticOperation.Validate(new MakeStaticParams
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
            MakeStaticOperation.Validate(new MakeStaticParams
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
            MakeStaticOperation.Validate(new MakeStaticParams
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
            MakeStaticOperation.Validate(new MakeStaticParams
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
    public async Task MakeStatic_PureMethod_AddsStaticAndUpdatesCallSites()
    {
        const string source = """
            namespace TestApp;

            public class Calculator
            {
                public int Add(int a, int b)
                {
                    return a + b;
                }

                public int Use()
                {
                    var other = new Calculator();
                    return other.Add(1, 2) + this.Add(3, 4) + Add(5, 6);
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new MakeStaticOperation(workspace.Context);
        var span = FindSpan(source, "Add");

        var result = await operation.ExecuteAsync(new MakeStaticParams
        {
            SourceFile = workspace.SourcePath,
            StartLine = span.StartLine,
            StartColumn = span.StartColumn,
            EndLine = span.EndLine,
            EndColumn = span.EndColumn,
            SymbolName = "Add"
        });

        Assert.True(result.Success);
        Assert.False(result.Preview);
        Assert.NotNull(result.Symbol);
        Assert.Equal("Add", result.Symbol.Name);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("public static int Add(int a, int b)", updated);
        Assert.DoesNotContain("other.Add(1, 2)", updated);
        Assert.DoesNotContain("this.Add(3, 4)", updated);
        Assert.Contains("Calculator.Add(1, 2)", updated);
        Assert.Contains("Calculator.Add(3, 4)", updated);
        Assert.Contains("Add(5, 6)", updated);
    }

    [SkippableFact]
    public async Task MakeStatic_MethodGroup_RewritesToTypeName()
    {
        const string source = """
            namespace TestApp;

            public class Calculator
            {
                public int Double(int x)
                {
                    return x * 2;
                }

                public void Use()
                {
                    var other = new Calculator();
                    System.Func<int, int> fromInstance = other.Double;
                    System.Func<int, int> fromThis = this.Double;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new MakeStaticOperation(workspace.Context);
        var span = FindSpan(source, "Double");

        var result = await operation.ExecuteAsync(new MakeStaticParams
        {
            SourceFile = workspace.SourcePath,
            StartLine = span.StartLine,
            StartColumn = span.StartColumn,
            EndLine = span.EndLine,
            EndColumn = span.EndColumn,
            SymbolName = "Double"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("public static int Double(int x)", updated);
        Assert.Contains("fromInstance = Calculator.Double", updated);
        Assert.Contains("fromThis = Calculator.Double", updated);
        Assert.DoesNotContain("other.Double", updated);
        Assert.DoesNotContain("this.Double", updated);
    }

    #endregion

    #region P0 Preview

    [SkippableFact]
    public async Task MakeStatic_Preview_DoesNotModifyFile()
    {
        const string source = """
            namespace TestApp;

            public class Calculator
            {
                public int Add(int a, int b)
                {
                    return a + b;
                }

                public int Use()
                {
                    return this.Add(1, 2);
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new MakeStaticOperation(workspace.Context);
        var span = FindSpan(source, "Add");

        var result = await operation.ExecuteAsync(new MakeStaticParams
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
        Assert.Contains(result.PendingChanges, change => change.Description.Contains("Add"));
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    #endregion

    #region P0 Rejects

    [SkippableFact]
    public async Task MakeStatic_UsesInstanceField_Throws()
    {
        const string source = """
            namespace TestApp;

            public class Calculator
            {
                private int _value;

                public int Get()
                {
                    return _value;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new MakeStaticOperation(workspace.Context);
        var span = FindSpan(source, "Get");
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new MakeStaticParams
            {
                SourceFile = workspace.SourcePath,
                StartLine = span.StartLine,
                StartColumn = span.StartColumn,
                EndLine = span.EndLine,
                EndColumn = span.EndColumn,
                SymbolName = "Get"
            }));

        Assert.Equal(ErrorCodes.UsesInstanceMembers, ex.ErrorCode);
        Assert.NotNull(ex.Details);
        Assert.True(ex.Details.ContainsKey("members"));
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("public int Get()", before);
        Assert.DoesNotContain("static", before);
    }

    [SkippableFact]
    public async Task MakeStatic_UsesImplicitThisMethod_Throws()
    {
        const string source = """
            namespace TestApp;

            public class Calculator
            {
                public int Value()
                {
                    return 1;
                }

                public int Get()
                {
                    return Value();
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new MakeStaticOperation(workspace.Context);
        var span = FindIdentifierSpan(source, "Get");
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new MakeStaticParams
            {
                SourceFile = workspace.SourcePath,
                StartLine = span.StartLine,
                StartColumn = span.StartColumn,
                EndLine = span.EndLine,
                EndColumn = span.EndColumn,
                SymbolName = "Get"
            }));

        Assert.Equal(ErrorCodes.UsesInstanceMembers, ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task MakeStatic_AlreadyStatic_Throws()
    {
        const string source = """
            namespace TestApp;

            public class Calculator
            {
                public static int Add(int a, int b)
                {
                    return a + b;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new MakeStaticOperation(workspace.Context);
        var span = FindSpan(source, "Add");
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new MakeStaticParams
            {
                SourceFile = workspace.SourcePath,
                StartLine = span.StartLine,
                StartColumn = span.StartColumn,
                EndLine = span.EndLine,
                EndColumn = span.EndColumn,
                SymbolName = "Add"
            }));

        Assert.Equal(ErrorCodes.AlreadyStatic, ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task MakeStatic_NoSymbol_Throws()
    {
        const string source = """
            namespace TestApp;

            public class Calculator
            {
                public int Add(int a, int b)
                {
                    return a + b;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new MakeStaticOperation(workspace.Context);
        var span = FindSpan(source, "return");

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new MakeStaticParams
            {
                SourceFile = workspace.SourcePath,
                StartLine = span.StartLine,
                StartColumn = span.StartColumn,
                EndLine = span.EndLine,
                EndColumn = span.EndColumn
            }));

        Assert.Equal(ErrorCodes.SymbolNotFound, ex.ErrorCode);
    }

    [SkippableFact]
    public async Task MakeStatic_NonMethod_Throws()
    {
        const string source = """
            namespace TestApp;

            public class Calculator
            {
                private int _value;

                public int Get()
                {
                    return 42;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new MakeStaticOperation(workspace.Context);
        var span = FindSpan(source, "_value");
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new MakeStaticParams
            {
                SourceFile = workspace.SourcePath,
                StartLine = span.StartLine,
                StartColumn = span.StartColumn,
                EndLine = span.EndLine,
                EndColumn = span.EndColumn,
                SymbolName = "_value"
            }));

        Assert.Equal(ErrorCodes.InvalidSymbolKind, ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [Fact]
    public void MakeStatic_UneditableDocument_Throws()
    {
        var workspace = new AdhocWorkspace();
        var project = workspace.AddProject("P", LanguageNames.CSharp);
        var document = workspace.AddDocument(project.Id, "Generated.cs", SourceText.From("class C {}"));

        var ex = Assert.Throws<RefactoringException>(() =>
            MakeStaticOperation.ValidateDocumentIsEditable(document, workspace));

        Assert.Equal(ErrorCodes.DocumentNotEditable, ex.ErrorCode);
    }

    #endregion

    #region Helpers

    private static string AbsoluteTestPath() =>
        Path.Combine(Path.GetTempPath(), "RoslynMcpMakeStaticMissing.cs");

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

    /// <summary>
    /// Finds the first identifier occurrence that is a standalone token, not a
    /// prefix of a longer identifier (for example <c>Get</c> in <c>Get()</c>
    /// rather than the <c>Get</c> inside <c>GetHashCode</c>).
    /// </summary>
    private static (int StartLine, int StartColumn, int EndLine, int EndColumn) FindIdentifierSpan(
        string source,
        string identifier)
    {
        var start = 0;
        while (start < source.Length)
        {
            var index = source.IndexOf(identifier, start, StringComparison.Ordinal);
            if (index < 0)
                throw new InvalidOperationException($"Identifier not found: {identifier}");

            var beforeOk = index == 0 || !IsIdentifierPart(source[index - 1]);
            var afterIndex = index + identifier.Length;
            var afterOk = afterIndex >= source.Length || !IsIdentifierPart(source[afterIndex]);
            if (beforeOk && afterOk)
            {
                return (GetLineColumn(source, index).Line, GetLineColumn(source, index).Column,
                    GetLineColumn(source, afterIndex).Line, GetLineColumn(source, afterIndex).Column);
            }

            start = index + 1;
        }

        throw new InvalidOperationException($"Identifier not found: {identifier}");
    }

    private static bool IsIdentifierPart(char c) => char.IsLetterOrDigit(c) || c == '_';

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

            var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpMakeStatic_" + Guid.NewGuid().ToString("N"));
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

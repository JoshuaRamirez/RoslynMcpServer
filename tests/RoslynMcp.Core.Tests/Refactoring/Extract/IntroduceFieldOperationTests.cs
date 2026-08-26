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
/// Operation-level tests for <see cref="IntroduceFieldOperation"/>.
/// </summary>
public class IntroduceFieldOperationTests
{
    #region Input Validation

    [Fact]
    public void Validate_MissingSourceFile_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            IntroduceFieldOperation.Validate(new IntroduceFieldParams
            {
                SourceFile = "",
                StartLine = 1,
                StartColumn = 1,
                EndLine = 1,
                EndColumn = 2,
                FieldName = "_value"
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public void Validate_MissingFieldName_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            IntroduceFieldOperation.Validate(new IntroduceFieldParams
            {
                SourceFile = AbsoluteTestPath(),
                StartLine = 1,
                StartColumn = 1,
                EndLine = 1,
                EndColumn = 2,
                FieldName = ""
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public void Validate_RelativePath_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            IntroduceFieldOperation.Validate(new IntroduceFieldParams
            {
                SourceFile = "Types.cs",
                StartLine = 1,
                StartColumn = 1,
                EndLine = 1,
                EndColumn = 2,
                FieldName = "_value"
            }));

        Assert.Equal(ErrorCodes.InvalidSourcePath, ex.ErrorCode);
    }

    [Fact]
    public void Validate_InvalidLine_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            IntroduceFieldOperation.Validate(new IntroduceFieldParams
            {
                SourceFile = AbsoluteTestPath(),
                StartLine = 0,
                StartColumn = 1,
                EndLine = 1,
                EndColumn = 2,
                FieldName = "_value"
            }));

        Assert.Equal(ErrorCodes.InvalidLineNumber, ex.ErrorCode);
    }

    [Fact]
    public void Validate_InvalidSelectionRange_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            IntroduceFieldOperation.Validate(new IntroduceFieldParams
            {
                SourceFile = AbsoluteTestPath(),
                StartLine = 2,
                StartColumn = 1,
                EndLine = 1,
                EndColumn = 2,
                FieldName = "_value"
            }));

        Assert.Equal(ErrorCodes.InvalidSelectionRange, ex.ErrorCode);
    }

    [Fact]
    public void Validate_InvalidFieldName_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            IntroduceFieldOperation.Validate(new IntroduceFieldParams
            {
                SourceFile = AbsoluteTestPath(),
                StartLine = 1,
                StartColumn = 1,
                EndLine = 1,
                EndColumn = 2,
                FieldName = "123bad"
            }));

        Assert.Equal(ErrorCodes.InvalidSymbolName, ex.ErrorCode);
    }

    [Fact]
    public void Validate_MissingFile_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            IntroduceFieldOperation.Validate(new IntroduceFieldParams
            {
                SourceFile = AbsoluteTestPath(),
                StartLine = 1,
                StartColumn = 1,
                EndLine = 1,
                EndColumn = 2,
                FieldName = "_value"
            }));

        Assert.Equal(ErrorCodes.SourceFileNotFound, ex.ErrorCode);
    }

    #endregion

    #region P0 Happy Path

    [SkippableFact]
    public async Task IntroduceField_Literal_CreatesFieldAndReplacesExpression()
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
        var operation = new IntroduceFieldOperation(workspace.Context);
        var span = FindSpan(source, "42");

        var result = await operation.ExecuteAsync(new IntroduceFieldParams
        {
            SourceFile = workspace.SourcePath,
            StartLine = span.StartLine,
            StartColumn = span.StartColumn,
            EndLine = span.EndLine,
            EndColumn = span.EndColumn,
            FieldName = "_answer"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("private int _answer = 42;", updated);
        Assert.Contains("return _answer;", updated);
        Assert.DoesNotContain("return 42;", updated);
    }

    [SkippableFact]
    public async Task IntroduceField_Expression_CreatesFieldAndReplacesExpression()
    {
        const string source = """
            namespace TestApp;

            public class Calculator
            {
                public int Get()
                {
                    return 1 + 2;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new IntroduceFieldOperation(workspace.Context);
        var span = FindSpan(source, "1 + 2");

        var result = await operation.ExecuteAsync(new IntroduceFieldParams
        {
            SourceFile = workspace.SourcePath,
            StartLine = span.StartLine,
            StartColumn = span.StartColumn,
            EndLine = span.EndLine,
            EndColumn = span.EndColumn,
            FieldName = "_sum"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("private int _sum = 1 + 2;", updated);
        Assert.Contains("return _sum;", updated);
    }

    [SkippableFact]
    public async Task IntroduceField_LocalVariable_PromotesToField()
    {
        const string source = """
            namespace TestApp;

            public class Calculator
            {
                public int Get()
                {
                    int value = 7;
                    return value;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new IntroduceFieldOperation(workspace.Context);
        var span = FindSpan(source, "value = 7");

        var result = await operation.ExecuteAsync(new IntroduceFieldParams
        {
            SourceFile = workspace.SourcePath,
            StartLine = span.StartLine,
            StartColumn = span.StartColumn,
            EndLine = span.EndLine,
            EndColumn = span.EndColumn,
            FieldName = "_value"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("private int _value = 7;", updated);
        Assert.Contains("return _value;", updated);
        Assert.DoesNotContain("int value = 7;", updated);
    }

    [SkippableFact]
    public async Task IntroduceField_InitializeInConstructor_AddsAssignment()
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
        var operation = new IntroduceFieldOperation(workspace.Context);
        var span = FindSpan(source, "42");

        var result = await operation.ExecuteAsync(new IntroduceFieldParams
        {
            SourceFile = workspace.SourcePath,
            StartLine = span.StartLine,
            StartColumn = span.StartColumn,
            EndLine = span.EndLine,
            EndColumn = span.EndColumn,
            FieldName = "_answer",
            InitializeInConstructor = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("private int _answer;", updated);
        Assert.DoesNotContain("private int _answer = 42;", updated);
        Assert.Contains("public Calculator()", updated);
        Assert.Contains("_answer = 42;", updated);
        Assert.Contains("return _answer;", updated);
    }

    [SkippableFact]
    public async Task IntroduceField_InitializeInExistingConstructor_PrependsAssignment()
    {
        const string source = """
            namespace TestApp;

            public class Calculator
            {
                public Calculator()
                {
                    Warmup();
                }

                public int Get()
                {
                    return 42;
                }

                private static void Warmup()
                {
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new IntroduceFieldOperation(workspace.Context);
        var span = FindSpan(source, "42");

        var result = await operation.ExecuteAsync(new IntroduceFieldParams
        {
            SourceFile = workspace.SourcePath,
            StartLine = span.StartLine,
            StartColumn = span.StartColumn,
            EndLine = span.EndLine,
            EndColumn = span.EndColumn,
            FieldName = "_answer",
            InitializeInConstructor = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("_answer = 42;", updated);
        Assert.Contains("Warmup();", updated);
        Assert.Equal(1, CountOccurrences(updated, "public Calculator()"));
    }

    [SkippableFact]
    public async Task IntroduceField_Preview_DoesNotModifyFile()
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
        var before = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new IntroduceFieldOperation(workspace.Context);
        var span = FindSpan(source, "42");

        var result = await operation.ExecuteAsync(new IntroduceFieldParams
        {
            SourceFile = workspace.SourcePath,
            StartLine = span.StartLine,
            StartColumn = span.StartColumn,
            EndLine = span.EndLine,
            EndColumn = span.EndColumn,
            FieldName = "_answer",
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.Contains(result.PendingChanges, c => c.Description.Contains("_answer"));
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task IntroduceField_Readonly_AddsModifier()
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
        var operation = new IntroduceFieldOperation(workspace.Context);
        var span = FindSpan(source, "42");

        var result = await operation.ExecuteAsync(new IntroduceFieldParams
        {
            SourceFile = workspace.SourcePath,
            StartLine = span.StartLine,
            StartColumn = span.StartColumn,
            EndLine = span.EndLine,
            EndColumn = span.EndColumn,
            FieldName = "_answer",
            IsReadonly = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("private readonly int _answer = 42;", updated);
    }

    #endregion

    #region P0 Rejects

    [SkippableFact]
    public async Task IntroduceField_NoExpression_Throws()
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
        var operation = new IntroduceFieldOperation(workspace.Context);
        var span = FindSpan(source, "return");

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new IntroduceFieldParams
            {
                SourceFile = workspace.SourcePath,
                StartLine = span.StartLine,
                StartColumn = span.StartColumn,
                EndLine = span.EndLine,
                EndColumn = span.EndColumn,
                FieldName = "_answer"
            }));

        Assert.Equal(ErrorCodes.ExpressionNotFound, ex.ErrorCode);
    }

    [Fact]
    public void IntroduceField_UneditableDocument_Throws()
    {
        var workspace = new AdhocWorkspace();
        var project = workspace.AddProject("P", LanguageNames.CSharp);
        var document = workspace.AddDocument(project.Id, "Generated.cs", SourceText.From("class C {}"));

        var ex = Assert.Throws<RefactoringException>(() =>
            IntroduceFieldOperation.ValidateDocumentIsEditable(document, workspace));

        Assert.Equal(ErrorCodes.DocumentNotEditable, ex.ErrorCode);
    }

    [SkippableFact]
    public async Task IntroduceField_InterfaceTarget_Throws()
    {
        const string source = """
            namespace TestApp;

            public interface ICalculator
            {
                int Get()
                {
                    return 42;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new IntroduceFieldOperation(workspace.Context);
        var span = FindSpan(source, "42");

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new IntroduceFieldParams
            {
                SourceFile = workspace.SourcePath,
                StartLine = span.StartLine,
                StartColumn = span.StartColumn,
                EndLine = span.EndLine,
                EndColumn = span.EndColumn,
                FieldName = "_answer"
            }));

        Assert.Equal(ErrorCodes.InvalidTargetType, ex.ErrorCode);
    }

    [SkippableFact]
    public async Task IntroduceField_NameExists_Throws()
    {
        const string source = """
            namespace TestApp;

            public class Calculator
            {
                private int _answer = 1;

                public int Get()
                {
                    return 42;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new IntroduceFieldOperation(workspace.Context);
        var span = FindSpan(source, "42");

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new IntroduceFieldParams
            {
                SourceFile = workspace.SourcePath,
                StartLine = span.StartLine,
                StartColumn = span.StartColumn,
                EndLine = span.EndLine,
                EndColumn = span.EndColumn,
                FieldName = "_answer"
            }));

        Assert.Equal(ErrorCodes.NameCollision, ex.ErrorCode);
    }

    [SkippableFact]
    public async Task IntroduceField_ExpressionUsesLocal_Throws()
    {
        const string source = """
            namespace TestApp;

            public class Calculator
            {
                public int Get()
                {
                    int offset = 3;
                    return offset + 2;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new IntroduceFieldOperation(workspace.Context);
        var span = FindSpan(source, "offset + 2");

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new IntroduceFieldParams
            {
                SourceFile = workspace.SourcePath,
                StartLine = span.StartLine,
                StartColumn = span.StartColumn,
                EndLine = span.EndLine,
                EndColumn = span.EndColumn,
                FieldName = "_sum"
            }));

        Assert.Equal(ErrorCodes.ExpressionCapturesLocal, ex.ErrorCode);
    }

    [SkippableFact]
    public async Task IntroduceField_InstanceFieldFromStaticMember_Throws()
    {
        const string source = """
            namespace TestApp;

            public class Calculator
            {
                public static int Get()
                {
                    return 42;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new IntroduceFieldOperation(workspace.Context);
        var span = FindSpan(source, "42");

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new IntroduceFieldParams
            {
                SourceFile = workspace.SourcePath,
                StartLine = span.StartLine,
                StartColumn = span.StartColumn,
                EndLine = span.EndLine,
                EndColumn = span.EndColumn,
                FieldName = "_answer"
            }));

        Assert.Equal(ErrorCodes.InvalidTargetType, ex.ErrorCode);
    }

    #endregion

    #region Helpers

    private static string AbsoluteTestPath() =>
        OperatingSystem.IsWindows() ? @"C:\test\file.cs" : "/test/file.cs";

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

            var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpIntroduceField_" + Guid.NewGuid().ToString("N"));
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

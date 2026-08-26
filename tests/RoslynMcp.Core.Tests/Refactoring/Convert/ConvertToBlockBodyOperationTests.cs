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
/// Operation-level tests for <see cref="ConvertToBlockBodyOperation"/>.
/// </summary>
public class ConvertToBlockBodyOperationTests
{
    #region Input Validation

    [Fact]
    public void Validate_MissingSourceFile_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ConvertToBlockBodyOperation.Validate(new ConvertToBlockBodyParams
            {
                SourceFile = "",
                MemberName = "Get"
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public void Validate_RelativePath_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ConvertToBlockBodyOperation.Validate(new ConvertToBlockBodyParams
            {
                SourceFile = "Types.cs",
                MemberName = "Get"
            }));

        Assert.Equal(ErrorCodes.InvalidSourcePath, ex.ErrorCode);
    }

    [Fact]
    public void Validate_NoMemberOrLine_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ConvertToBlockBodyOperation.Validate(new ConvertToBlockBodyParams
            {
                SourceFile = AbsoluteTestPath()
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public void Validate_InvalidLine_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ConvertToBlockBodyOperation.Validate(new ConvertToBlockBodyParams
            {
                SourceFile = AbsoluteTestPath(),
                Line = 0
            }));

        Assert.Equal(ErrorCodes.InvalidLineNumber, ex.ErrorCode);
    }

    [Fact]
    public void Validate_MissingFile_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ConvertToBlockBodyOperation.Validate(new ConvertToBlockBodyParams
            {
                SourceFile = AbsoluteTestPath(),
                MemberName = "Get"
            }));

        Assert.Equal(ErrorCodes.SourceFileNotFound, ex.ErrorCode);
    }

    #endregion

    #region P0 Happy Path

    [SkippableFact]
    public async Task Convert_ReturningMethod_InsertsReturnBlock()
    {
        const string source = """
            namespace TestApp;

            public class Calculator
            {
                public int Add(int a, int b) => a + b;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ConvertToBlockBodyOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ConvertToBlockBodyParams
        {
            SourceFile = workspace.SourcePath,
            MemberName = "Add"
        });

        Assert.True(result.Success);
        Assert.False(result.Preview);
        Assert.NotNull(result.Symbol);
        Assert.Equal("Add", result.Symbol.Name);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.DoesNotContain("=>", updated);
        Assert.Contains("return a + b;", updated);
    }

    [SkippableFact]
    public async Task Convert_VoidMethod_InsertsExpressionStatement()
    {
        const string source = """
            namespace TestApp;

            public class Logger
            {
                public void Log(string message) => System.Console.WriteLine(message);
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ConvertToBlockBodyOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ConvertToBlockBodyParams
        {
            SourceFile = workspace.SourcePath,
            MemberName = "Log"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.DoesNotContain("=>", updated);
        Assert.Contains("System.Console.WriteLine(message);", updated);
        Assert.DoesNotContain("return System.Console.WriteLine", updated);
    }

    [SkippableFact]
    public async Task Convert_ExpressionBodiedProperty_CreatesGetterBlock()
    {
        const string source = """
            namespace TestApp;

            public class Person
            {
                public string First { get; set; } = "Ada";
                public string Last { get; set; } = "Lovelace";
                public string FullName => First + " " + Last;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ConvertToBlockBodyOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ConvertToBlockBodyParams
        {
            SourceFile = workspace.SourcePath,
            MemberName = "FullName"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.DoesNotContain("FullName =>", updated);
        Assert.Contains("get", updated);
        Assert.Contains("return First + \" \" + Last;", updated);
    }

    [SkippableFact]
    public async Task Convert_ExpressionBodiedAccessor_ConvertsGetter()
    {
        const string source = """
            namespace TestApp;

            public class Person
            {
                private string _name = "Ada";
                public string Name
                {
                    get => _name;
                    set => _name = value;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ConvertToBlockBodyOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ConvertToBlockBodyParams
        {
            SourceFile = workspace.SourcePath,
            MemberName = "Name"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.DoesNotContain("get =>", updated);
        Assert.DoesNotContain("set =>", updated);
        Assert.Contains("return _name;", updated);
        Assert.Contains("_name = value;", updated);
    }

    [SkippableFact]
    public async Task Convert_ByLine_FindsMember()
    {
        const string source = """
            namespace TestApp;

            public class Calculator
            {
                public int Value => 42;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ConvertToBlockBodyOperation(workspace.Context);
        var line = FindLine(source, "Value =>");

        var result = await operation.ExecuteAsync(new ConvertToBlockBodyParams
        {
            SourceFile = workspace.SourcePath,
            Line = line
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("return 42;", updated);
    }

    #endregion

    #region P0 Preview

    [SkippableFact]
    public async Task Convert_Preview_DoesNotModifyFile()
    {
        const string source = """
            namespace TestApp;

            public class Calculator
            {
                public int Add(int a, int b) => a + b;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new ConvertToBlockBodyOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ConvertToBlockBodyParams
        {
            SourceFile = workspace.SourcePath,
            MemberName = "Add",
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
    public async Task Convert_NoSymbol_Throws()
    {
        const string source = """
            namespace TestApp;

            public class Calculator
            {
                public int Add(int a, int b) => a + b;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ConvertToBlockBodyOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ConvertToBlockBodyParams
            {
                SourceFile = workspace.SourcePath,
                MemberName = "Missing"
            }));

        Assert.Equal(ErrorCodes.SymbolNotFound, ex.ErrorCode);
    }

    [SkippableFact]
    public async Task Convert_AlreadyBlockBody_Throws()
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
        var operation = new ConvertToBlockBodyOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ConvertToBlockBodyParams
            {
                SourceFile = workspace.SourcePath,
                MemberName = "Add"
            }));

        Assert.Equal(ErrorCodes.AlreadyBlockBody, ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task Convert_UnsupportedMember_Throws()
    {
        const string source = """
            namespace TestApp;

            public class Calculator
            {
                public int Value;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ConvertToBlockBodyOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ConvertToBlockBodyParams
            {
                SourceFile = workspace.SourcePath,
                MemberName = "Value"
            }));

        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [Fact]
    public void Convert_UneditableDocument_Throws()
    {
        var workspace = new AdhocWorkspace();
        var project = workspace.AddProject("P", LanguageNames.CSharp);
        var document = workspace.AddDocument(project.Id, "Generated.cs", SourceText.From("class C {}"));

        var ex = Assert.Throws<RefactoringException>(() =>
            ConvertToBlockBodyOperation.ValidateDocumentIsEditable(document, workspace));

        Assert.Equal(ErrorCodes.DocumentNotEditable, ex.ErrorCode);
    }

    #endregion

    #region Helpers

    private static string AbsoluteTestPath() =>
        Path.Combine(Path.GetTempPath(), "RoslynMcpConvertToBlockBodyMissing.cs");

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

            var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpConvertToBlockBody_" + Guid.NewGuid().ToString("N"));
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

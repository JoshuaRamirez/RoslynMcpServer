using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Convert;
using RoslynMcp.Core.Workspace;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring;

/// <summary>
/// Operation-level tests for <see cref="ConvertExpressionBodyOperation"/> (UC-CV3 column).
/// </summary>
public class ConvertExpressionBodyOperationTests
{
    private const string SameLineBlockSource = """
        namespace TestApp;

        public class Pair
        {
            public int Foo() { return 1; } public int Bar() { return 2; }
        }
        """;

    private const string SameLineExpressionSource = """
        namespace TestApp;

        public class Pair
        {
            public int Foo() => 1; public int Bar() => 2;
        }
        """;

    private const string SingleBlockMethodSource = """
        namespace TestApp;

        public class Calculator
        {
            public int Add(int a, int b)
            {
                return a + b;
            }
        }
        """;

    private const string SingleExpressionMethodSource = """
        namespace TestApp;

        public class Calculator
        {
            public int Add(int a, int b) => a + b;
        }
        """;

    #region Input Validation

    [Fact]
    public void Validate_MissingSourceFile_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ConvertExpressionBodyOperation.Validate(new ConvertExpressionBodyParams
            {
                SourceFile = "",
                Direction = "ToExpressionBody"
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public void Validate_MissingDirection_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ConvertExpressionBodyOperation.Validate(new ConvertExpressionBodyParams
            {
                SourceFile = AbsoluteTestPath(),
                Direction = ""
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public void Validate_NoMemberOrLine_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ConvertExpressionBodyOperation.Validate(new ConvertExpressionBodyParams
            {
                SourceFile = AbsoluteTestPath(),
                Direction = "ToBlockBody"
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public void Validate_InvalidLine_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ConvertExpressionBodyOperation.Validate(new ConvertExpressionBodyParams
            {
                SourceFile = AbsoluteTestPath(),
                Line = 0,
                Direction = "ToBlockBody"
            }));

        Assert.Equal(ErrorCodes.InvalidLineNumber, ex.ErrorCode);
    }

    [Fact]
    public void Validate_InvalidColumn_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ConvertExpressionBodyOperation.Validate(new ConvertExpressionBodyParams
            {
                SourceFile = AbsoluteTestPath(),
                MemberName = "Foo",
                Column = 0,
                Direction = "ToExpressionBody"
            }));

        Assert.Equal(ErrorCodes.InvalidColumnNumber, ex.ErrorCode);
    }

    [Fact]
    public void Validate_InvalidDirection_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ConvertExpressionBodyOperation.Validate(new ConvertExpressionBodyParams
            {
                SourceFile = AbsoluteTestPath(),
                MemberName = "Foo",
                Direction = "Invalid"
            }));

        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
    }

    [Fact]
    public void Validate_MissingFile_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ConvertExpressionBodyOperation.Validate(new ConvertExpressionBodyParams
            {
                SourceFile = AbsoluteTestPath(),
                MemberName = "Foo",
                Direction = "ToExpressionBody"
            }));

        Assert.Equal(ErrorCodes.SourceFileNotFound, ex.ErrorCode);
    }

    #endregion

    #region Existing direction / memberName / line

    [SkippableFact]
    public async Task Convert_MemberName_ToExpressionBody()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SingleBlockMethodSource);
        var operation = new ConvertExpressionBodyOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ConvertExpressionBodyParams
        {
            SourceFile = workspace.SourcePath,
            MemberName = "Add",
            Direction = "ToExpressionBody"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("=>", updated, StringComparison.Ordinal);
        Assert.DoesNotContain("return a + b;", updated, StringComparison.Ordinal);
        await AssertCompilesAsync(workspace);
    }

    [SkippableFact]
    public async Task Convert_Line_ToExpressionBody()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SingleBlockMethodSource);
        var operation = new ConvertExpressionBodyOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ConvertExpressionBodyParams
        {
            SourceFile = workspace.SourcePath,
            Line = FindLine(SingleBlockMethodSource, "public int Add"),
            Direction = "ToExpressionBody"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("=>", updated, StringComparison.Ordinal);
        await AssertCompilesAsync(workspace);
    }

    [SkippableFact]
    public async Task Convert_MemberName_ToBlockBody()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SingleExpressionMethodSource);
        var operation = new ConvertExpressionBodyOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ConvertExpressionBodyParams
        {
            SourceFile = workspace.SourcePath,
            MemberName = "Add",
            Direction = "ToBlockBody"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("return a + b;", updated, StringComparison.Ordinal);
        Assert.DoesNotContain("=>", updated, StringComparison.Ordinal);
        await AssertCompilesAsync(workspace);
    }

    #endregion

    #region P0 omitted column converts the first matching member

    [SkippableFact]
    public async Task ConvertExpressionBody_OmittedColumn_ConvertsFirstMemberOnTheLine()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SameLineBlockSource);
        var operation = new ConvertExpressionBodyOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ConvertExpressionBodyParams
        {
            SourceFile = workspace.SourcePath,
            Line = FindLine(SameLineBlockSource, "public int Foo()"),
            Direction = "ToExpressionBody"
        });

        Assert.True(result.Success);

        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("Foo()", updated, StringComparison.Ordinal);
        Assert.Contains("=>", updated, StringComparison.Ordinal);
        Assert.Contains("public int Bar() { return 2; }", updated, StringComparison.Ordinal);
        Assert.DoesNotContain("public int Foo() { return 1; }", updated, StringComparison.Ordinal);
        await AssertCompilesAsync(workspace);
    }

    #endregion

    #region P0 column picks the intended member when two share a line

    [SkippableFact]
    public async Task ConvertExpressionBody_Column_SelectsSecondMemberOnSameLine()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SameLineBlockSource);
        var operation = new ConvertExpressionBodyOperation(workspace.Context);
        var line = FindLine(SameLineBlockSource, "public int Foo()");
        var secondColumn = ColumnOf(SameLineBlockSource, "Bar()");

        var result = await operation.ExecuteAsync(new ConvertExpressionBodyParams
        {
            SourceFile = workspace.SourcePath,
            Line = line,
            Column = secondColumn,
            Direction = "ToExpressionBody"
        });

        Assert.True(result.Success);

        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("public int Foo() { return 1; }", updated, StringComparison.Ordinal);
        Assert.DoesNotContain("public int Bar() { return 2; }", updated, StringComparison.Ordinal);
        Assert.Contains("Bar()", updated, StringComparison.Ordinal);
        Assert.Contains("=>", updated, StringComparison.Ordinal);
        await AssertCompilesAsync(workspace);
    }

    [SkippableFact]
    public async Task ConvertExpressionBody_Column_SelectsFirstMemberOnSameLine()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SameLineBlockSource);
        var operation = new ConvertExpressionBodyOperation(workspace.Context);
        var line = FindLine(SameLineBlockSource, "public int Foo()");
        var firstColumn = ColumnOf(SameLineBlockSource, "Foo()");

        var result = await operation.ExecuteAsync(new ConvertExpressionBodyParams
        {
            SourceFile = workspace.SourcePath,
            Line = line,
            Column = firstColumn,
            Direction = "ToExpressionBody"
        });

        Assert.True(result.Success);

        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.DoesNotContain("public int Foo() { return 1; }", updated, StringComparison.Ordinal);
        Assert.Contains("public int Bar() { return 2; }", updated, StringComparison.Ordinal);
        Assert.Contains("Foo()", updated, StringComparison.Ordinal);
        Assert.Contains("=>", updated, StringComparison.Ordinal);
        await AssertCompilesAsync(workspace);
    }

    [SkippableFact]
    public async Task ConvertExpressionBody_ColumnOnContinuationLine_ConvertsThatMember()
    {
        const string source = """
            namespace TestApp;

            public class Split
            {
                public int
                Foo() { return 1; }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ConvertExpressionBodyOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ConvertExpressionBodyParams
        {
            SourceFile = workspace.SourcePath,
            Line = FindLine(source, "Foo()"),
            Column = ColumnOf(source, "Foo()"),
            Direction = "ToExpressionBody"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("=>", updated, StringComparison.Ordinal);
        Assert.DoesNotContain("return 1;", updated, StringComparison.Ordinal);
        await AssertCompilesAsync(workspace);
    }

    [SkippableFact]
    public async Task ConvertExpressionBody_AdjacentMembers_ColumnOnSecondDoesNotRewriteFirst()
    {
        const string source = """
            namespace TestApp;

            public class Adjacent
            {
                public int A()=>1;public int Longer()=>2;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ConvertExpressionBodyOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ConvertExpressionBodyParams
        {
            SourceFile = workspace.SourcePath,
            Line = FindLine(source, "public int A()"),
            Column = ColumnOf(source, "public int Longer()"),
            Direction = "ToBlockBody"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("public int A()=>1;", updated, StringComparison.Ordinal);
        Assert.DoesNotContain("public int Longer()=>2;", updated, StringComparison.Ordinal);
        Assert.Contains("return 2;", updated, StringComparison.Ordinal);
        await AssertCompilesAsync(workspace);
    }

    [SkippableFact]
    public async Task ConvertExpressionBody_Column_ToBlockBody_SelectsSecondMemberOnSameLine()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SameLineExpressionSource);
        var operation = new ConvertExpressionBodyOperation(workspace.Context);
        var line = FindLine(SameLineExpressionSource, "public int Foo()");
        var secondColumn = ColumnOf(SameLineExpressionSource, "Bar()");

        var result = await operation.ExecuteAsync(new ConvertExpressionBodyParams
        {
            SourceFile = workspace.SourcePath,
            Line = line,
            Column = secondColumn,
            Direction = "ToBlockBody"
        });

        Assert.True(result.Success);

        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("public int Foo() => 1;", updated, StringComparison.Ordinal);
        Assert.DoesNotContain("public int Bar() => 2;", updated, StringComparison.Ordinal);
        Assert.Contains("return 2;", updated, StringComparison.Ordinal);
        await AssertCompilesAsync(workspace);
    }

    [Fact]
    public void FindMember_ColumnPicksIdentifierCoverage()
    {
        var tree = CSharpSyntaxTree.ParseText(SameLineBlockSource);
        var root = tree.GetRoot();
        var line = FindLine(SameLineBlockSource, "public int Foo()");
        var first = ConvertExpressionBodyOperation.FindMember(root, null, line, ColumnOf(SameLineBlockSource, "Foo()"));
        var second = ConvertExpressionBodyOperation.FindMember(root, null, line, ColumnOf(SameLineBlockSource, "Bar()"));
        var omitted = ConvertExpressionBodyOperation.FindMember(root, null, line, column: null);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotNull(omitted);
        Assert.Equal("Foo", ((MethodDeclarationSyntax)first).Identifier.Text);
        Assert.Equal("Bar", ((MethodDeclarationSyntax)second).Identifier.Text);
        Assert.Equal("Foo", ((MethodDeclarationSyntax)omitted).Identifier.Text);
    }

    [Fact]
    public void FindMember_ColumnOnContinuationLine_PicksMember()
    {
        const string source = """
            class C
            {
                public int
                Foo() { return 1; }
            }
            """;

        var tree = CSharpSyntaxTree.ParseText(source);
        var root = tree.GetRoot();
        var startLine = FindLine(source, "public int");
        var identifierLine = FindLine(source, "Foo()");
        Assert.NotEqual(startLine, identifierLine);

        var byStartLineOnly = ConvertExpressionBodyOperation.FindMember(root, null, identifierLine, column: null);
        var byColumn = ConvertExpressionBodyOperation.FindMember(
            root, null, identifierLine, ColumnOf(source, "Foo()"));

        Assert.Null(byStartLineOnly);
        Assert.NotNull(byColumn);
        Assert.Equal("Foo", ((MethodDeclarationSyntax)byColumn).Identifier.Text);
    }

    [Fact]
    public void FindMember_AdjacentMembers_ExclusiveEndDoesNotStealNextMember()
    {
        const string source = """
            class C
            {
                public int A()=>1;public int Longer()=>2;
            }
            """;

        var tree = CSharpSyntaxTree.ParseText(source);
        var root = tree.GetRoot();
        var line = FindLine(source, "public int A()");
        var secondStart = ColumnOf(source, "public int Longer()");
        var secondId = ColumnOf(source, "Longer()");

        var atSecondStart = ConvertExpressionBodyOperation.FindMember(root, null, line, secondStart);
        var atSecondId = ConvertExpressionBodyOperation.FindMember(root, null, line, secondId);

        Assert.NotNull(atSecondStart);
        Assert.NotNull(atSecondId);
        Assert.Equal("Longer", ((MethodDeclarationSyntax)atSecondStart).Identifier.Text);
        Assert.Equal("Longer", ((MethodDeclarationSyntax)atSecondId).Identifier.Text);
    }

    #endregion

    #region P0 preview describes the rewrite and writes nothing

    [SkippableFact]
    public async Task ConvertExpressionBody_Preview_DescribesRewriteAndWritesNothing()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SingleBlockMethodSource);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new ConvertExpressionBodyOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ConvertExpressionBodyParams
        {
            SourceFile = workspace.SourcePath,
            MemberName = "Add",
            Direction = "ToExpressionBody",
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.Contains(result.PendingChanges, change =>
            change.Description.Contains("ToExpressionBody", StringComparison.Ordinal) &&
            change.AfterSnippet != null &&
            change.AfterSnippet.Contains("=>", StringComparison.Ordinal));
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
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
        Path.Combine(Path.GetTempPath(), "RoslynMcpConvertExpressionBodyMissing.cs");

    private static string NormalizeNewlines(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal);

    private static int FindLine(string source, string snippet)
    {
        var index = source.IndexOf(snippet, StringComparison.Ordinal);
        Assert.True(index >= 0, $"Snippet not found: {snippet}");

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
        var index = source.IndexOf(snippet, StringComparison.Ordinal);
        Assert.True(index >= 0, $"Snippet not found: {snippet}");
        var lineStart = source.LastIndexOf('\n', index);
        return index - lineStart;
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

            var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpConvertExpressionBody_" + Guid.NewGuid().ToString("N"));
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

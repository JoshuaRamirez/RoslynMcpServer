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
/// Operation-level tests for <see cref="AddBracesOperation"/> (UC-A5).
/// </summary>
public class AddBracesOperationTests
{
    #region Input Validation

    [Fact]
    public void Validate_MissingSourceFile_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            AddBracesOperation.Validate(new AddBracesParams
            {
                SourceFile = ""
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public void Validate_RelativePath_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            AddBracesOperation.Validate(new AddBracesParams
            {
                SourceFile = "Types.cs",
                Line = 1
            }));

        Assert.Equal(ErrorCodes.InvalidSourcePath, ex.ErrorCode);
    }

    [Fact]
    public void Validate_StatementScope_MissingLine_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            AddBracesOperation.Validate(new AddBracesParams
            {
                SourceFile = AbsoluteTestPath(),
                Scope = "statement"
            }));

        Assert.Equal(ErrorCodes.InvalidLineNumber, ex.ErrorCode);
    }

    [Fact]
    public void Validate_InvalidLine_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            AddBracesOperation.Validate(new AddBracesParams
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
            AddBracesOperation.Validate(new AddBracesParams
            {
                SourceFile = AbsoluteTestPath(),
                Line = 1,
                Column = 0
            }));

        Assert.Equal(ErrorCodes.InvalidColumnNumber, ex.ErrorCode);
    }

    [Fact]
    public void Validate_TypeScope_MissingTypeName_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            AddBracesOperation.Validate(new AddBracesParams
            {
                SourceFile = AbsoluteTestPath(),
                Scope = "type"
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public void Validate_InvalidScope_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            AddBracesOperation.Validate(new AddBracesParams
            {
                SourceFile = AbsoluteTestPath(),
                Scope = "method"
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public void Validate_MissingFile_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            AddBracesOperation.Validate(new AddBracesParams
            {
                SourceFile = AbsoluteTestPath(),
                Line = 1
            }));

        Assert.Equal(ErrorCodes.SourceFileNotFound, ex.ErrorCode);
    }

    #endregion

    #region P0 Happy Path

    [SkippableFact]
    public async Task AddBraces_If_WrapsThenBody()
    {
        const string source = """
            namespace TestApp;

            public class Gate
            {
                public string Classify(int x)
                {
                    if (x > 0)
                        return "positive";
                    return "other";
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new AddBracesOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new AddBracesParams
        {
            SourceFile = workspace.SourcePath,
            Line = FindLine(source, "if (x > 0)")
        });

        Assert.True(result.Success);
        Assert.False(result.Preview);
        Assert.Equal(1, result.StatementsModified);
        Assert.Equal("statement", result.Scope);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        AssertIfBodyIsBlock(updated, "x > 0");
        Assert.Contains("return \"positive\";", updated);
        Assert.DoesNotContain("{            return", updated);
        await AssertCompilesAsync(workspace);
    }

    [SkippableFact]
    public async Task AddBraces_Else_WrapsElseBody()
    {
        const string source = """
            namespace TestApp;

            public class Gate
            {
                public string Classify(bool flag)
                {
                    if (flag)
                    {
                        return "yes";
                    }
                    else
                        return "no";
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new AddBracesOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new AddBracesParams
        {
            SourceFile = workspace.SourcePath,
            Line = FindLine(source, "else")
        });

        Assert.True(result.Success);
        Assert.Equal(1, result.StatementsModified);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var tree = CSharpSyntaxTree.ParseText(updated);
        var ifStatement = tree.GetRoot().DescendantNodes().OfType<IfStatementSyntax>().Single();
        Assert.IsType<BlockSyntax>(ifStatement.Statement);
        Assert.IsType<BlockSyntax>(ifStatement.Else?.Statement);
        Assert.Contains("return \"no\";", ifStatement.Else!.Statement.ToString());
        await AssertCompilesAsync(workspace);
    }

    [SkippableFact]
    public async Task AddBraces_For_WrapsBody()
    {
        const string source = """
            namespace TestApp;

            public class Loop
            {
                public int Sum(int n)
                {
                    var total = 0;
                    for (var i = 0; i < n; i++)
                        total += i;
                    return total;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new AddBracesOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new AddBracesParams
        {
            SourceFile = workspace.SourcePath,
            Line = FindLine(source, "for (var i = 0; i < n; i++)")
        });

        Assert.True(result.Success);
        Assert.Equal(1, result.StatementsModified);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var forStatement = CSharpSyntaxTree.ParseText(updated).GetRoot()
            .DescendantNodes().OfType<ForStatementSyntax>().Single();
        Assert.IsType<BlockSyntax>(forStatement.Statement);
        Assert.Contains("total += i;", forStatement.Statement.ToString());
        await AssertCompilesAsync(workspace);
    }

    [SkippableFact]
    public async Task AddBraces_Foreach_WrapsBody()
    {
        const string source = """
            namespace TestApp;

            public class Loop
            {
                public int Count(int[] items)
                {
                    var n = 0;
                    foreach (var item in items)
                        n++;
                    return n;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new AddBracesOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new AddBracesParams
        {
            SourceFile = workspace.SourcePath,
            Line = FindLine(source, "foreach (var item in items)")
        });

        Assert.True(result.Success);
        Assert.Equal(1, result.StatementsModified);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var forEach = CSharpSyntaxTree.ParseText(updated).GetRoot()
            .DescendantNodes().OfType<ForEachStatementSyntax>().Single();
        Assert.IsType<BlockSyntax>(forEach.Statement);
        Assert.Contains("n++;", forEach.Statement.ToString());
        await AssertCompilesAsync(workspace);
    }

    [SkippableFact]
    public async Task AddBraces_While_WrapsBody()
    {
        const string source = """
            namespace TestApp;

            public class Loop
            {
                public int Drain(int n)
                {
                    while (n > 0)
                        n--;
                    return n;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new AddBracesOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new AddBracesParams
        {
            SourceFile = workspace.SourcePath,
            Line = FindLine(source, "while (n > 0)")
        });

        Assert.True(result.Success);
        Assert.Equal(1, result.StatementsModified);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var whileStatement = CSharpSyntaxTree.ParseText(updated).GetRoot()
            .DescendantNodes().OfType<WhileStatementSyntax>().Single();
        Assert.IsType<BlockSyntax>(whileStatement.Statement);
        Assert.Contains("n--;", whileStatement.Statement.ToString());
        await AssertCompilesAsync(workspace);
    }

    [SkippableFact]
    public async Task AddBraces_Using_WrapsBody()
    {
        const string source = """
            namespace TestApp;

            public class Holder
            {
                public void Use()
                {
                    using (var stream = new System.IO.MemoryStream())
                        stream.WriteByte(1);
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new AddBracesOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new AddBracesParams
        {
            SourceFile = workspace.SourcePath,
            Line = FindLine(source, "using (var stream")
        });

        Assert.True(result.Success);
        Assert.Equal(1, result.StatementsModified);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var usingStatement = CSharpSyntaxTree.ParseText(updated).GetRoot()
            .DescendantNodes().OfType<UsingStatementSyntax>().Single();
        Assert.IsType<BlockSyntax>(usingStatement.Statement);
        Assert.Contains("WriteByte(1);", usingStatement.Statement.ToString());
        await AssertCompilesAsync(workspace);
    }

    #endregion

    #region P0 Rejects

    [SkippableFact]
    public async Task AddBraces_AlreadyHasBraces_Throws()
    {
        const string source = """
            namespace TestApp;

            public class Gate
            {
                public void Run(bool flag)
                {
                    if (flag)
                    {
                        Work();
                    }
                }

                private static void Work() { }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new AddBracesOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new AddBracesParams
            {
                SourceFile = workspace.SourcePath,
                Line = FindLine(source, "if (flag)")
            }));

        Assert.Equal(ErrorCodes.AlreadyHasBraces, ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task AddBraces_NoControlStatement_Throws()
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
        var operation = new AddBracesOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new AddBracesParams
            {
                SourceFile = workspace.SourcePath,
                Line = FindLine(source, "var x = 1;")
            }));

        Assert.Equal(ErrorCodes.NoControlStatement, ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [Fact]
    public void AddBraces_UneditableDocument_Throws()
    {
        var workspace = new AdhocWorkspace();
        var project = workspace.AddProject("P", LanguageNames.CSharp);
        var document = workspace.AddDocument(project.Id, "Generated.cs", SourceText.From("class C {}"));

        var ex = Assert.Throws<RefactoringException>(() =>
            AddBracesOperation.ValidateDocumentIsEditable(document, workspace));

        Assert.Equal(ErrorCodes.DocumentNotEditable, ex.ErrorCode);
    }

    #endregion

    #region P0 Preview

    [SkippableFact]
    public async Task AddBraces_Preview_DoesNotModifyFile()
    {
        const string source = """
            namespace TestApp;

            public class Gate
            {
                public string Classify(int x)
                {
                    if (x > 0)
                        return "positive";
                    return "other";
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new AddBracesOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new AddBracesParams
        {
            SourceFile = workspace.SourcePath,
            Line = FindLine(source, "if (x > 0)"),
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.Equal(1, result.StatementsModified);
        Assert.Equal("statement", result.Scope);
        Assert.NotNull(result.PendingChanges);
        Assert.Contains(result.PendingChanges, change =>
            change.Description.Contains("Add braces", StringComparison.Ordinal) &&
            change.AfterSnippet != null &&
            change.AfterSnippet.Contains("{", StringComparison.Ordinal));
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    #endregion

    #region P1 Scope

    [SkippableFact]
    public async Task AddBraces_FileScope_WrapsEveryBracelessBody()
    {
        const string source = """
            namespace TestApp;

            public class One
            {
                public void Run(int[] items)
                {
                    if (items.Length > 0)
                        Start();
                    else
                        Stop();

                    for (var i = 0; i < items.Length; i++)
                        Touch(items[i]);

                    foreach (var item in items)
                        Touch(item);

                    while (items.Length == 0)
                        return;
                }

                private static void Start() { }
                private static void Stop() { }
                private static void Touch(int value) { }
            }

            public class Two
            {
                public void Use()
                {
                    using (var stream = new System.IO.MemoryStream())
                        stream.WriteByte(1);
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new AddBracesOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new AddBracesParams
        {
            SourceFile = workspace.SourcePath,
            Scope = "file"
        });

        Assert.True(result.Success);
        Assert.Equal(6, result.StatementsModified);
        Assert.Equal("file", result.Scope);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var root = CSharpSyntaxTree.ParseText(updated).GetRoot();
        Assert.All(root.DescendantNodes().OfType<IfStatementSyntax>(), statement =>
        {
            Assert.IsType<BlockSyntax>(statement.Statement);
            Assert.IsType<BlockSyntax>(statement.Else?.Statement);
        });
        Assert.All(root.DescendantNodes().OfType<ForStatementSyntax>(),
            statement => Assert.IsType<BlockSyntax>(statement.Statement));
        Assert.All(root.DescendantNodes().OfType<ForEachStatementSyntax>(),
            statement => Assert.IsType<BlockSyntax>(statement.Statement));
        Assert.All(root.DescendantNodes().OfType<WhileStatementSyntax>(),
            statement => Assert.IsType<BlockSyntax>(statement.Statement));
        Assert.All(root.DescendantNodes().OfType<UsingStatementSyntax>(),
            statement => Assert.IsType<BlockSyntax>(statement.Statement));
        await AssertCompilesAsync(workspace);
    }

    [SkippableFact]
    public async Task AddBraces_TypeScope_WrapsOnlyThatType()
    {
        const string source = """
            namespace TestApp;

            public class Inside
            {
                public void Run(bool flag)
                {
                    if (flag)
                        Work();
                }

                private static void Work() { }
            }

            public class Outside
            {
                public void Run(bool flag)
                {
                    if (flag)
                        Work();
                }

                private static void Work() { }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new AddBracesOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new AddBracesParams
        {
            SourceFile = workspace.SourcePath,
            Scope = "type",
            TypeName = "Inside"
        });

        Assert.True(result.Success);
        Assert.Equal(1, result.StatementsModified);
        Assert.Equal("type", result.Scope);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var root = CSharpSyntaxTree.ParseText(updated).GetRoot();
        var insideIf = root.DescendantNodes().OfType<ClassDeclarationSyntax>()
            .Single(type => type.Identifier.Text == "Inside")
            .DescendantNodes().OfType<IfStatementSyntax>().Single();
        var outsideIf = root.DescendantNodes().OfType<ClassDeclarationSyntax>()
            .Single(type => type.Identifier.Text == "Outside")
            .DescendantNodes().OfType<IfStatementSyntax>().Single();
        Assert.IsType<BlockSyntax>(insideIf.Statement);
        Assert.IsNotType<BlockSyntax>(outsideIf.Statement);
        await AssertCompilesAsync(workspace);
    }

    [SkippableFact]
    public async Task AddBraces_TypeScope_MissingType_Throws()
    {
        const string source = """
            namespace TestApp;

            public class Only
            {
                public void Run() { }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new AddBracesOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new AddBracesParams
            {
                SourceFile = workspace.SourcePath,
                Scope = "type",
                TypeName = "Missing"
            }));

        Assert.Equal(ErrorCodes.TypeNotFound, ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task AddBraces_FileScope_LeavesElseIfAsSingleConstruct()
    {
        const string source = """
            namespace TestApp;

            public class Chain
            {
                public string Pick(bool a, bool b)
                {
                    if (a)
                        return "a";
                    else if (b)
                        return "b";
                    return "none";
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new AddBracesOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new AddBracesParams
        {
            SourceFile = workspace.SourcePath,
            Scope = "file"
        });

        Assert.True(result.Success);
        Assert.Equal(2, result.StatementsModified);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var outerIf = CSharpSyntaxTree.ParseText(updated).GetRoot()
            .DescendantNodes().OfType<IfStatementSyntax>()
            .First(statement => statement.Condition.ToString().Contains("a", StringComparison.Ordinal));
        Assert.IsType<BlockSyntax>(outerIf.Statement);
        Assert.IsType<IfStatementSyntax>(outerIf.Else?.Statement);
        Assert.IsType<BlockSyntax>(((IfStatementSyntax)outerIf.Else!.Statement).Statement);
        await AssertCompilesAsync(workspace);
    }

    [SkippableFact]
    public async Task AddBraces_ColumnSelectsInnerIfOnSameLine()
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
        var operation = new AddBracesOperation(workspace.Context);
        var line = FindLine(source, "if (a) if (b)");
        var innerIfColumn = source.IndexOf("if (b)", StringComparison.Ordinal)
            - source.LastIndexOf('\n', source.IndexOf("if (b)", StringComparison.Ordinal));

        var result = await operation.ExecuteAsync(new AddBracesParams
        {
            SourceFile = workspace.SourcePath,
            Line = line,
            Column = innerIfColumn
        });

        Assert.True(result.Success);
        Assert.Equal(1, result.StatementsModified);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var root = CSharpSyntaxTree.ParseText(updated).GetRoot();
        var innerIf = root.DescendantNodes().OfType<IfStatementSyntax>()
            .Single(statement => statement.Condition.ToString() == "b");
        var outerIf = root.DescendantNodes().OfType<IfStatementSyntax>()
            .Single(statement => statement.Condition.ToString() == "a");
        Assert.IsType<BlockSyntax>(innerIf.Statement);
        Assert.IsNotType<BlockSyntax>(outerIf.Statement);
        await AssertCompilesAsync(workspace);
    }

    #endregion

    #region Helpers

    private static void AssertIfBodyIsBlock(string updated, string condition)
    {
        var ifStatement = CSharpSyntaxTree.ParseText(updated).GetRoot()
            .DescendantNodes()
            .OfType<IfStatementSyntax>()
            .First(statement => statement.Condition.ToString().Contains(condition, StringComparison.Ordinal));
        Assert.IsType<BlockSyntax>(ifStatement.Statement);
    }

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
        Path.Combine(Path.GetTempPath(), "RoslynMcpAddBracesMissing.cs");

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

            var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpAddBraces_" + Guid.NewGuid().ToString("N"));
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

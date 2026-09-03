using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Signature;
using RoslynMcp.Core.Workspace;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Signature;

/// <summary>
/// Operation-level tests for <see cref="ChangeSignatureOperation"/> (leftover column).
/// </summary>
public class ChangeSignatureOperationTests
{
    private const string SingleMethodSource = """
        namespace TestApp;

        public class Worker
        {
            public void Process(int x) { }
        }
        """;

    private const string IndentedMethodSource = """
        namespace TestApp;

        public class Worker
        {
            public void Process(int x) { }
        }
        """;

    private const string SameLineOverloadsSource = """
        namespace TestApp;

        public class Worker
        {
            public void Process(int x) { } public void Process(int x, int y) { }
        }
        """;

    private static IReadOnlyList<ParameterChange> KeepXAddFlag() =>
    [
        new() { OriginalName = "x", Name = "x" },
        new() { Name = "flag", Type = "bool" }
    ];

    private static IReadOnlyList<ParameterChange> KeepXYAddFlag() =>
    [
        new() { OriginalName = "x", Name = "x" },
        new() { OriginalName = "y", Name = "y" },
        new() { Name = "flag", Type = "bool" }
    ];

    #region Input Validation

    [Fact]
    public void Validate_InvalidColumn_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ChangeSignatureOperation.Validate(new ChangeSignatureParams
            {
                SourceFile = AbsoluteTestPath(),
                MethodName = "Process",
                Parameters = KeepXAddFlag(),
                Column = 0
            }));

        Assert.Equal(ErrorCodes.InvalidColumnNumber, ex.ErrorCode);
        Assert.Equal("1007", ex.ErrorCode);
    }

    [Fact]
    public void Validate_NegativeColumn_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ChangeSignatureOperation.Validate(new ChangeSignatureParams
            {
                SourceFile = AbsoluteTestPath(),
                MethodName = "Process",
                Parameters = KeepXAddFlag(),
                Column = -1
            }));

        Assert.Equal(ErrorCodes.InvalidColumnNumber, ex.ErrorCode);
        Assert.Equal("1007", ex.ErrorCode);
    }

    [Fact]
    public void Validate_InvalidLine_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ChangeSignatureOperation.Validate(new ChangeSignatureParams
            {
                SourceFile = AbsoluteTestPath(),
                MethodName = "Process",
                Parameters = KeepXAddFlag(),
                Line = 0
            }));

        Assert.Equal(ErrorCodes.InvalidLineNumber, ex.ErrorCode);
        Assert.Equal("1006", ex.ErrorCode);
    }

    [Fact]
    public void Validate_NegativeLine_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ChangeSignatureOperation.Validate(new ChangeSignatureParams
            {
                SourceFile = AbsoluteTestPath(),
                MethodName = "Process",
                Parameters = KeepXAddFlag(),
                Line = -1
            }));

        Assert.Equal(ErrorCodes.InvalidLineNumber, ex.ErrorCode);
        Assert.Equal("1006", ex.ErrorCode);
    }

    [Fact]
    public void Validate_MissingMethodName_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ChangeSignatureOperation.Validate(new ChangeSignatureParams
            {
                SourceFile = AbsoluteTestPath(),
                MethodName = "",
                Parameters = KeepXAddFlag()
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    #endregion

    #region P0 omitted column keeps today's MethodName + optional Line pick

    [SkippableFact]
    public async Task ChangeSignature_OmittedColumn_ChangesTheNamedMethod()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SingleMethodSource);
        var operation = new ChangeSignatureOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ChangeSignatureParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Process",
            Parameters = KeepXAddFlag()
        });

        Assert.True(result.Success);
        var updated = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.True(HasParameters(updated, "Process", ("int", "x"), ("bool", "flag")));
    }

    [SkippableFact]
    public async Task ChangeSignature_OmittedColumn_IndentedMethodStillChanges()
    {
        await using var workspace = await TempWorkspace.CreateAsync(IndentedMethodSource);
        var operation = new ChangeSignatureOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ChangeSignatureParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Process",
            Parameters = KeepXAddFlag()
        });

        Assert.True(result.Success);
        var updated = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.True(HasParameters(updated, "Process", ("int", "x"), ("bool", "flag")));
    }

    [SkippableFact]
    public async Task ChangeSignature_OmittedColumn_SameLineOverloads_ChangesFirstStartLineMatch()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SameLineOverloadsSource);
        var operation = new ChangeSignatureOperation(workspace.Context);
        var line = FindLine(SameLineOverloadsSource, "public void Process(int x) { }");

        var result = await operation.ExecuteAsync(new ChangeSignatureParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Process",
            Line = line,
            Parameters = KeepXAddFlag()
        });

        Assert.True(result.Success);
        var updated = await File.ReadAllTextAsync(workspace.SourcePath);
        var processMethods = GetMethods(updated, "Process");
        Assert.Equal(2, processMethods.Count);
        Assert.Contains(processMethods, m => ParameterNames(m) is ["x", "flag"]);
        Assert.Contains(processMethods, m => ParameterNames(m) is ["x", "y"]);
        Assert.DoesNotContain(processMethods, m => ParameterNames(m) is ["x", "y", "flag"]);
    }

    [SkippableFact]
    public async Task ChangeSignature_OmittedColumn_MultipleMethodsWithoutLine_ThrowsSymbolAmbiguous()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SameLineOverloadsSource);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new ChangeSignatureOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ChangeSignatureParams
            {
                SourceFile = workspace.SourcePath,
                MethodName = "Process",
                Parameters = KeepXAddFlag()
            }));

        Assert.Equal(ErrorCodes.SymbolAmbiguous, ex.ErrorCode);
        Assert.Equal("2004", ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    #endregion

    #region P0 column picks the intended method when two share a line

    [SkippableFact]
    public async Task ChangeSignature_Column_SelectsSecondOverloadOnSameLine()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SameLineOverloadsSource);
        var operation = new ChangeSignatureOperation(workspace.Context);
        var line = FindLine(SameLineOverloadsSource, "public void Process(int x) { }");
        var secondColumn = ColumnOf(SameLineOverloadsSource, "Process(int x, int y)");

        var result = await operation.ExecuteAsync(new ChangeSignatureParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Process",
            Line = line,
            Column = secondColumn,
            Parameters = KeepXYAddFlag()
        });

        Assert.True(result.Success);
        var updated = await File.ReadAllTextAsync(workspace.SourcePath);
        var processMethods = GetMethods(updated, "Process");
        Assert.Equal(2, processMethods.Count);
        Assert.Contains(processMethods, m => ParameterNames(m) is ["x"]);
        Assert.Contains(processMethods, m => ParameterNames(m) is ["x", "y", "flag"]);
        Assert.DoesNotContain(processMethods, m => ParameterNames(m) is ["x", "flag"]);
    }

    [SkippableFact]
    public async Task ChangeSignature_Column_SelectsFirstOverloadOnSameLine()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SameLineOverloadsSource);
        var operation = new ChangeSignatureOperation(workspace.Context);
        var line = FindLine(SameLineOverloadsSource, "public void Process(int x) { }");
        var firstColumn = ColumnOf(SameLineOverloadsSource, "Process(int x) { }");

        var result = await operation.ExecuteAsync(new ChangeSignatureParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Process",
            Line = line,
            Column = firstColumn,
            Parameters = KeepXAddFlag()
        });

        Assert.True(result.Success);
        var updated = await File.ReadAllTextAsync(workspace.SourcePath);
        var processMethods = GetMethods(updated, "Process");
        Assert.Equal(2, processMethods.Count);
        Assert.Contains(processMethods, m => ParameterNames(m) is ["x", "flag"]);
        Assert.Contains(processMethods, m => ParameterNames(m) is ["x", "y"]);
        Assert.DoesNotContain(processMethods, m => ParameterNames(m) is ["x", "y", "flag"]);
    }

    [SkippableFact]
    public async Task ChangeSignature_ColumnOnContinuationLine_ChangesThatMethod()
    {
        const string source = """
            namespace TestApp;

            public class Split
            {
                public void
                Process(int x) { }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ChangeSignatureOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ChangeSignatureParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Process",
            Line = FindLine(source, "Process(int x)"),
            Column = ColumnOf(source, "Process(int x)"),
            Parameters = KeepXAddFlag()
        });

        Assert.True(result.Success);
        var updated = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.True(HasParameters(updated, "Process", ("int", "x"), ("bool", "flag")));
    }

    [SkippableFact]
    public async Task ChangeSignature_AdjacentMethods_ColumnOnSecondDoesNotRewriteFirst()
    {
        const string source = """
            namespace TestApp;

            public class Adjacent
            {
                public void Other(int x){}public void Process(int x){}
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ChangeSignatureOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ChangeSignatureParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Process",
            Line = FindLine(source, "public void Other"),
            Column = ColumnOf(source, "Process(int x)"),
            Parameters = KeepXAddFlag()
        });

        Assert.True(result.Success);
        var updated = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.True(HasParameters(updated, "Other", ("int", "x")));
        Assert.True(HasParameters(updated, "Process", ("int", "x"), ("bool", "flag")));
    }

    [Fact]
    public void FindMethod_ColumnPicksIdentifierCoverage()
    {
        var tree = CSharpSyntaxTree.ParseText(SameLineOverloadsSource);
        var root = tree.GetRoot();
        var line = FindLine(SameLineOverloadsSource, "public void Process(int x) { }");
        var first = ChangeSignatureOperation.FindMethod(
            root, "Process", line, ColumnOf(SameLineOverloadsSource, "Process(int x) { }"));
        var second = ChangeSignatureOperation.FindMethod(
            root, "Process", line, ColumnOf(SameLineOverloadsSource, "Process(int x, int y)"));
        var omitted = ChangeSignatureOperation.FindMethod(root, "Process", line, column: null);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotNull(omitted);
        Assert.Single(first.ParameterList.Parameters);
        Assert.Equal(2, second.ParameterList.Parameters.Count);
        Assert.Single(omitted.ParameterList.Parameters);
    }

    [Fact]
    public void FindMethod_ColumnOnContinuationLine_PicksMethod()
    {
        const string source = """
            class C
            {
                public void
                Process(int x) { }

                public void Process(int x, int y) { }
            }
            """;

        var tree = CSharpSyntaxTree.ParseText(source);
        var root = tree.GetRoot();
        var startLine = FindLine(source, "public void");
        var identifierLine = FindLine(source, "Process(int x) { }");
        Assert.NotEqual(startLine, identifierLine);

        // Omitted column keeps today's start-line filter when more than one
        // match exists — the split signature does not start on the identifier
        // line. Column still selects it.
        var byStartLineOnly = ChangeSignatureOperation.FindMethod(root, "Process", identifierLine, column: null);
        var byColumn = ChangeSignatureOperation.FindMethod(
            root, "Process", identifierLine, ColumnOf(source, "Process(int x) { }"));

        Assert.Null(byStartLineOnly);
        Assert.NotNull(byColumn);
        Assert.Single(byColumn.ParameterList.Parameters);
    }

    [Fact]
    public void FindMethod_AdjacentMethods_ExclusiveEndDoesNotStealNextMethod()
    {
        const string source = """
            class C
            {
                public void Other(int x){}public void Process(int x){}
            }
            """;

        var tree = CSharpSyntaxTree.ParseText(source);
        var root = tree.GetRoot();
        var line = FindLine(source, "public void Other");
        var secondStart = ColumnOf(source, "public void Process");
        var secondId = ColumnOf(source, "Process(int x)");

        var atSecondStart = ChangeSignatureOperation.FindMethod(root, "Process", line, secondStart);
        var atSecondId = ChangeSignatureOperation.FindMethod(root, "Process", line, secondId);
        var atFirstId = ChangeSignatureOperation.FindMethod(root, "Other", line, ColumnOf(source, "Other(int x)"));
        var firstAtSecondStart = ChangeSignatureOperation.FindMethod(root, "Other", line, secondStart);

        Assert.NotNull(atSecondStart);
        Assert.NotNull(atSecondId);
        Assert.NotNull(atFirstId);
        Assert.Equal("Process", atSecondStart.Identifier.Text);
        Assert.Equal("Process", atSecondId.Identifier.Text);
        Assert.Equal("Other", atFirstId.Identifier.Text);
        Assert.Null(firstAtSecondStart);
    }

    [Fact]
    public void SpanCoversColumn_TreatsEndAsExclusive()
    {
        const string source = "class C { public void A(int x){}public void B(int x){} }";
        var tree = CSharpSyntaxTree.ParseText(source);
        var method = tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>()
            .First(m => m.Identifier.Text == "A");
        var span = method.GetLocation().GetLineSpan();
        var line = span.StartLinePosition.Line + 1;
        var startCol = span.StartLinePosition.Character + 1;
        var endCol = span.EndLinePosition.Character + 1;

        Assert.True(ChangeSignatureOperation.SpanCoversColumn(span, line, startCol));
        Assert.True(ChangeSignatureOperation.SpanCoversColumn(span, line, endCol - 1));
        Assert.False(ChangeSignatureOperation.SpanCoversColumn(span, line, endCol));
        Assert.False(ChangeSignatureOperation.SpanCoversColumn(span, line, startCol - 1));
    }

    #endregion

    #region P0 preview describes the rewrite and writes nothing

    [SkippableFact]
    public async Task ChangeSignature_Preview_Column_DescribesRewriteAndWritesNothing()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SameLineOverloadsSource);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new ChangeSignatureOperation(workspace.Context);
        var line = FindLine(SameLineOverloadsSource, "public void Process(int x) { }");
        var secondColumn = ColumnOf(SameLineOverloadsSource, "Process(int x, int y)");

        var result = await operation.ExecuteAsync(new ChangeSignatureParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Process",
            Line = line,
            Column = secondColumn,
            Parameters = KeepXYAddFlag(),
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.Contains(result.PendingChanges, change =>
            change.Description.Contains("Change signature of 'Process'", StringComparison.Ordinal) &&
            change.AfterSnippet != null &&
            change.AfterSnippet.Contains("flag", StringComparison.Ordinal));
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    #endregion

    #region Existing change_signature / reject cases

    [SkippableFact]
    public async Task ChangeSignature_MethodNotFound_Throws()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SingleMethodSource);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new ChangeSignatureOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ChangeSignatureParams
            {
                SourceFile = workspace.SourcePath,
                MethodName = "Missing",
                Parameters = KeepXAddFlag()
            }));

        Assert.Equal(ErrorCodes.MethodNotFound, ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task ChangeSignature_LineDoesNotMatch_ThrowsMethodNotFound()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SameLineOverloadsSource);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new ChangeSignatureOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ChangeSignatureParams
            {
                SourceFile = workspace.SourcePath,
                MethodName = "Process",
                Line = 1,
                Parameters = KeepXAddFlag()
            }));

        Assert.Equal(ErrorCodes.MethodNotFound, ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task ChangeSignature_ColumnWithoutLine_SameIndentOverloads_ThrowsSymbolAmbiguous()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                public void Foo(int x)
                {
                }

                public void Foo(int x, int y)
                {
                }
            }
            """;

        var column = ColumnOf(source, "Foo(int x)");
        Assert.Equal(column, ColumnOf(source, "Foo(int x, int y)"));

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new ChangeSignatureOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ChangeSignatureParams
            {
                SourceFile = workspace.SourcePath,
                MethodName = "Foo",
                Column = column,
                Parameters =
                [
                    new() { OriginalName = "x", Name = "x" },
                    new() { Name = "flag", Type = "bool" }
                ]
            }));

        Assert.Equal(ErrorCodes.SymbolAmbiguous, ex.ErrorCode);
        Assert.Equal("2004", ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.DoesNotContain("bool flag", NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath)));
    }

    [SkippableFact]
    public async Task ChangeSignature_ParameterNotFound_Throws()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SingleMethodSource);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new ChangeSignatureOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ChangeSignatureParams
            {
                SourceFile = workspace.SourcePath,
                MethodName = "Process",
                Parameters =
                [
                    new() { OriginalName = "missing", Name = "missing" }
                ]
            }));

        Assert.Equal(ErrorCodes.ParameterNotFound, ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    #endregion

    #region Helpers

    private static string NormalizeNewlines(string text) => text.Replace("\r\n", "\n");

    private static List<MethodDeclarationSyntax> GetMethods(string source, string methodName) =>
        CSharpSyntaxTree.ParseText(source).GetRoot()
            .DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Where(m => m.Identifier.Text == methodName)
            .ToList();

    private static string[] ParameterNames(MethodDeclarationSyntax method) =>
        method.ParameterList.Parameters.Select(p => p.Identifier.Text).ToArray();

    private static bool HasParameters(string source, string methodName, params (string Type, string Name)[] expected)
    {
        var methods = GetMethods(source, methodName);
        return methods.Any(method =>
            method.ParameterList.Parameters.Count == expected.Length &&
            method.ParameterList.Parameters
                .Select((p, i) => p.Identifier.Text == expected[i].Name &&
                                  (p.Type?.ToString().Trim() ?? "") == expected[i].Type)
                .All(match => match));
    }

    private static string AbsoluteTestPath() =>
        Path.Combine(Path.GetTempPath(), "RoslynMcpChangeSignatureMissing.cs");

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

    private static int ColumnOf(string source, string snippet)
    {
        var index = source.IndexOf(snippet, StringComparison.Ordinal);
        if (index < 0)
            throw new InvalidOperationException($"Snippet not found: {snippet}");

        var lineStart = source.LastIndexOf('\n', index);
        return index - lineStart;
    }

    private sealed class TempWorkspace : IAsyncDisposable
    {
        public required string DirectoryPath { get; init; }
        public required string ProjectPath { get; init; }
        public required string SourcePath { get; init; }
        public required WorkspaceContext Context { get; init; }

        public static async Task<TempWorkspace> CreateAsync(string source, string fileName = "Worker.cs")
        {
            Skip.IfNot(ModuleInitializer.MsBuildAvailable, ModuleInitializer.MsBuildError ?? "MSBuild not available");

            var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpChangeSignature_" + Guid.NewGuid().ToString("N"));
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

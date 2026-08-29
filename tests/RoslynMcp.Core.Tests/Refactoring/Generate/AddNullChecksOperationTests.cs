using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Generate;
using RoslynMcp.Core.Workspace;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Generate;

/// <summary>
/// Operation-level tests for <see cref="AddNullChecksOperation"/> (leftover column).
/// </summary>
public class AddNullChecksOperationTests
{
    private const string SingleMethodSource = """
        namespace TestApp;

        public class Worker
        {
            public void Process(string name) { }
        }
        """;

    private const string IndentedMethodSource = """
        namespace TestApp;

        public class Worker
        {
            public void Process(string name) { }
        }
        """;

    private const string SameLineOverloadsSource = """
        namespace TestApp;

        public class Worker
        {
            public void Process(string name) { } public void Process(string name, string extra) { }
        }
        """;

    private const string TwoOverloadsOnSeparateLinesSource = """
        namespace TestApp;

        public class Worker
        {
            public void Process(string name) { }

            public void Process(string name, string extra) { }
        }
        """;

    private const string SameLineConstructorsSource = """
        namespace TestApp;

        public class Worker
        {
            public Worker(string name) { } public Worker(string name, string extra) { }
        }
        """;

    #region Input Validation

    [Fact]
    public void Validate_InvalidColumn_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            AddNullChecksOperation.Validate(new AddNullChecksParams
            {
                SourceFile = AbsoluteTestPath(),
                MethodName = "Process",
                Column = 0
            }));

        Assert.Equal(ErrorCodes.InvalidColumnNumber, ex.ErrorCode);
        Assert.Equal("1007", ex.ErrorCode);
    }

    [Fact]
    public void Validate_NegativeColumn_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            AddNullChecksOperation.Validate(new AddNullChecksParams
            {
                SourceFile = AbsoluteTestPath(),
                MethodName = "Process",
                Column = -1
            }));

        Assert.Equal(ErrorCodes.InvalidColumnNumber, ex.ErrorCode);
        Assert.Equal("1007", ex.ErrorCode);
    }

    [Fact]
    public void Validate_MissingMethodName_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            AddNullChecksOperation.Validate(new AddNullChecksParams
            {
                SourceFile = AbsoluteTestPath(),
                MethodName = ""
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    #endregion

    #region P0 omitted column keeps today's methodName + optional line start-line pick

    [SkippableFact]
    public async Task AddNullChecks_OmittedColumn_AddsChecksToTheNamedMethod()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SingleMethodSource);
        var operation = new AddNullChecksOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new AddNullChecksParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Process"
        });

        Assert.True(result.Success);
        var updated = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("ThrowIfNull(name)", updated, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task AddNullChecks_OmittedColumn_IndentedMethodStillGetsChecks()
    {
        await using var workspace = await TempWorkspace.CreateAsync(IndentedMethodSource);
        var operation = new AddNullChecksOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new AddNullChecksParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Process"
        });

        Assert.True(result.Success);
        var updated = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("ThrowIfNull(name)", updated, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task AddNullChecks_OmittedColumn_SeveralMethodsShareName_SilentFirstMatch()
    {
        await using var workspace = await TempWorkspace.CreateAsync(TwoOverloadsOnSeparateLinesSource);
        var operation = new AddNullChecksOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new AddNullChecksParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Process"
        });

        Assert.True(result.Success);
        var methods = GetMethods(await File.ReadAllTextAsync(workspace.SourcePath), "Process");
        Assert.Equal(2, methods.Count);
        Assert.Contains("ThrowIfNull(name)", methods[0].Body!.ToFullString(), StringComparison.Ordinal);
        Assert.DoesNotContain("ThrowIfNull", methods[1].Body!.ToFullString(), StringComparison.Ordinal);
        Assert.DoesNotContain("ThrowIfNull(extra)", methods[0].Body!.ToFullString(), StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task AddNullChecks_OmittedColumn_LinePicksStartLineMatch()
    {
        await using var workspace = await TempWorkspace.CreateAsync(TwoOverloadsOnSeparateLinesSource);
        var operation = new AddNullChecksOperation(workspace.Context);
        var secondLine = FindLine(TwoOverloadsOnSeparateLinesSource, "Process(string name, string extra)");

        var result = await operation.ExecuteAsync(new AddNullChecksParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Process",
            Line = secondLine
        });

        Assert.True(result.Success);
        var methods = GetMethods(await File.ReadAllTextAsync(workspace.SourcePath), "Process");
        Assert.Equal(2, methods.Count);
        Assert.DoesNotContain("ThrowIfNull", methods[0].Body!.ToFullString(), StringComparison.Ordinal);
        Assert.Contains("ThrowIfNull(name)", methods[1].Body!.ToFullString(), StringComparison.Ordinal);
        Assert.Contains("ThrowIfNull(extra)", methods[1].Body!.ToFullString(), StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task AddNullChecks_OmittedColumn_LineMiss_SilentFirstFallback()
    {
        await using var workspace = await TempWorkspace.CreateAsync(TwoOverloadsOnSeparateLinesSource);
        var operation = new AddNullChecksOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new AddNullChecksParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Process",
            Line = 1
        });

        Assert.True(result.Success);
        var methods = GetMethods(await File.ReadAllTextAsync(workspace.SourcePath), "Process");
        Assert.Contains("ThrowIfNull(name)", methods[0].Body!.ToFullString(), StringComparison.Ordinal);
        Assert.DoesNotContain("ThrowIfNull", methods[1].Body!.ToFullString(), StringComparison.Ordinal);
    }

    #endregion

    #region P0 column picks the intended method when two share a line

    [SkippableFact]
    public async Task AddNullChecks_Column_SelectsSecondOverloadOnSameLine()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SameLineOverloadsSource);
        var operation = new AddNullChecksOperation(workspace.Context);
        var line = FindLine(SameLineOverloadsSource, "public void Process(string name) { }");
        var secondColumn = ColumnOf(SameLineOverloadsSource, "Process(string name, string extra)");

        var result = await operation.ExecuteAsync(new AddNullChecksParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Process",
            Line = line,
            Column = secondColumn
        });

        Assert.True(result.Success);
        var methods = GetMethods(await File.ReadAllTextAsync(workspace.SourcePath), "Process");
        Assert.Equal(2, methods.Count);
        Assert.DoesNotContain("ThrowIfNull", methods[0].Body!.ToFullString(), StringComparison.Ordinal);
        Assert.Contains("ThrowIfNull(name)", methods[1].Body!.ToFullString(), StringComparison.Ordinal);
        Assert.Contains("ThrowIfNull(extra)", methods[1].Body!.ToFullString(), StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task AddNullChecks_Column_SelectsFirstOverloadOnSameLine()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SameLineOverloadsSource);
        var operation = new AddNullChecksOperation(workspace.Context);
        var line = FindLine(SameLineOverloadsSource, "public void Process(string name) { }");
        var firstColumn = ColumnOf(SameLineOverloadsSource, "Process(string name) { }");

        var result = await operation.ExecuteAsync(new AddNullChecksParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Process",
            Line = line,
            Column = firstColumn
        });

        Assert.True(result.Success);
        var methods = GetMethods(await File.ReadAllTextAsync(workspace.SourcePath), "Process");
        Assert.Equal(2, methods.Count);
        Assert.Contains("ThrowIfNull(name)", methods[0].Body!.ToFullString(), StringComparison.Ordinal);
        Assert.DoesNotContain("ThrowIfNull", methods[1].Body!.ToFullString(), StringComparison.Ordinal);
    }

    [Fact]
    public void FindMethod_ColumnPicksIdentifierCoverage()
    {
        var tree = CSharpSyntaxTree.ParseText(SameLineOverloadsSource);
        var root = tree.GetRoot();
        var line = FindLine(SameLineOverloadsSource, "public void Process(string name) { }");
        var first = AddNullChecksOperation.FindMethod(
            root, "Process", line, ColumnOf(SameLineOverloadsSource, "Process(string name) { }"));
        var second = AddNullChecksOperation.FindMethod(
            root, "Process", line, ColumnOf(SameLineOverloadsSource, "Process(string name, string extra)"));
        var omitted = AddNullChecksOperation.FindMethod(root, "Process", line, column: null);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotNull(omitted);
        Assert.Single(((MethodDeclarationSyntax)first).ParameterList.Parameters);
        Assert.Equal(2, ((MethodDeclarationSyntax)second).ParameterList.Parameters.Count);
        Assert.Single(((MethodDeclarationSyntax)omitted).ParameterList.Parameters);
    }

    [Fact]
    public void FindMethod_AdjacentMethods_ExclusiveEndDoesNotStealNextMethod()
    {
        const string source = """
            class C
            {
                public void Other(string name){}public void Process(string name){}
            }
            """;

        var tree = CSharpSyntaxTree.ParseText(source);
        var root = tree.GetRoot();
        var line = FindLine(source, "public void Other");
        var secondStart = ColumnOf(source, "public void Process");
        var secondId = ColumnOf(source, "Process(string name)");

        var atSecondStart = AddNullChecksOperation.FindMethod(root, "Process", line, secondStart);
        var atSecondId = AddNullChecksOperation.FindMethod(root, "Process", line, secondId);
        var atFirstId = AddNullChecksOperation.FindMethod(root, "Other", line, ColumnOf(source, "Other(string name)"));
        var firstAtSecondStart = AddNullChecksOperation.FindMethod(root, "Other", line, secondStart);

        Assert.NotNull(atSecondStart);
        Assert.NotNull(atSecondId);
        Assert.NotNull(atFirstId);
        Assert.Equal("Process", ((MethodDeclarationSyntax)atSecondStart).Identifier.Text);
        Assert.Equal("Process", ((MethodDeclarationSyntax)atSecondId).Identifier.Text);
        Assert.Equal("Other", ((MethodDeclarationSyntax)atFirstId).Identifier.Text);
        Assert.Null(firstAtSecondStart);
    }

    [Fact]
    public void SpanCoversColumn_TreatsEndAsExclusive()
    {
        const string source = "class C { public void A(string name){}public void B(string name){} }";
        var tree = CSharpSyntaxTree.ParseText(source);
        var method = tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>()
            .First(m => m.Identifier.Text == "A");
        var span = method.GetLocation().GetLineSpan();
        var line = span.StartLinePosition.Line + 1;
        var startCol = span.StartLinePosition.Character + 1;
        var endCol = span.EndLinePosition.Character + 1;

        Assert.True(AddNullChecksOperation.SpanCoversColumn(span, line, startCol));
        Assert.True(AddNullChecksOperation.SpanCoversColumn(span, line, endCol - 1));
        Assert.False(AddNullChecksOperation.SpanCoversColumn(span, line, endCol));
        Assert.False(AddNullChecksOperation.SpanCoversColumn(span, line, startCol - 1));
    }

    #endregion

    #region P0 column on a continuation-line identifier still picks the method

    [SkippableFact]
    public async Task AddNullChecks_ColumnOnContinuationLine_AddsChecksToThatMethod()
    {
        const string source = """
            namespace TestApp;

            public class Split
            {
                public void
                Process(string name) { }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new AddNullChecksOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new AddNullChecksParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Process",
            Line = FindLine(source, "Process(string name)"),
            Column = ColumnOf(source, "Process(string name)")
        });

        Assert.True(result.Success);
        var updated = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("ThrowIfNull(name)", updated, StringComparison.Ordinal);
    }

    [Fact]
    public void FindMethod_ColumnOnContinuationLine_PicksMethod()
    {
        const string source = """
            class C
            {
                public void
                Process(string name) { }

                public void Process(string name, string extra) { }
            }
            """;

        var tree = CSharpSyntaxTree.ParseText(source);
        var root = tree.GetRoot();
        var startLine = FindLine(source, "public void");
        var identifierLine = FindLine(source, "Process(string name) { }");
        Assert.NotEqual(startLine, identifierLine);

        // Omitted column keeps today's start-line filter, then silent
        // First() when line misses. The split signature does not start
        // on the identifier line. Column on the continuation identifier
        // still selects the split method.
        var byStartLineOnly = AddNullChecksOperation.FindMethod(root, "Process", identifierLine, column: null);
        var byColumn = AddNullChecksOperation.FindMethod(
            root, "Process", identifierLine, ColumnOf(source, "Process(string name) { }"));

        Assert.NotNull(byStartLineOnly);
        Assert.Single(((MethodDeclarationSyntax)byStartLineOnly).ParameterList.Parameters);
        Assert.NotNull(byColumn);
        Assert.Single(((MethodDeclarationSyntax)byColumn).ParameterList.Parameters);
    }

    [Fact]
    public void FindMethod_ColumnOnContinuationLine_DoesNotRequireStartLine()
    {
        const string source = """
            class C
            {
                public void
                Process(string name) { }

                public void Other(string name) { }
            }
            """;

        var tree = CSharpSyntaxTree.ParseText(source);
        var root = tree.GetRoot();
        var identifierLine = FindLine(source, "Process(string name) { }");

        // Only one Process. Omitted column + identifier line misses the
        // start-line filter and silently First()s the split method.
        // Column still selects it by identifier coverage.
        var omitted = AddNullChecksOperation.FindMethod(root, "Process", identifierLine, column: null);
        var byColumn = AddNullChecksOperation.FindMethod(
            root, "Process", identifierLine, ColumnOf(source, "Process(string name) { }"));

        Assert.NotNull(omitted);
        Assert.NotNull(byColumn);
        Assert.Single(((MethodDeclarationSyntax)omitted).ParameterList.Parameters);
        Assert.Single(((MethodDeclarationSyntax)byColumn).ParameterList.Parameters);
    }

    #endregion

    #region P0 column on a constructor identifier still picks the constructor

    [SkippableFact]
    public async Task AddNullChecks_Column_SelectsConstructorOnSameLine()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SameLineConstructorsSource);
        var operation = new AddNullChecksOperation(workspace.Context);
        var line = FindLine(SameLineConstructorsSource, "public Worker(string name) { }");
        var secondColumn = ColumnOf(SameLineConstructorsSource, "Worker(string name, string extra)");

        var result = await operation.ExecuteAsync(new AddNullChecksParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Worker",
            Line = line,
            Column = secondColumn
        });

        Assert.True(result.Success);
        var ctors = GetConstructors(await File.ReadAllTextAsync(workspace.SourcePath), "Worker");
        Assert.Equal(2, ctors.Count);
        Assert.DoesNotContain("ThrowIfNull", ctors[0].Body!.ToFullString(), StringComparison.Ordinal);
        Assert.Contains("ThrowIfNull(name)", ctors[1].Body!.ToFullString(), StringComparison.Ordinal);
        Assert.Contains("ThrowIfNull(extra)", ctors[1].Body!.ToFullString(), StringComparison.Ordinal);
    }

    [Fact]
    public void FindMethod_ColumnOnConstructorIdentifier_PicksConstructor()
    {
        var tree = CSharpSyntaxTree.ParseText(SameLineConstructorsSource);
        var root = tree.GetRoot();
        var line = FindLine(SameLineConstructorsSource, "public Worker(string name) { }");
        var first = AddNullChecksOperation.FindMethod(
            root, "Worker", line, ColumnOf(SameLineConstructorsSource, "Worker(string name) { }"));
        var second = AddNullChecksOperation.FindMethod(
            root, "Worker", line, ColumnOf(SameLineConstructorsSource, "Worker(string name, string extra)"));

        Assert.IsType<ConstructorDeclarationSyntax>(first);
        Assert.IsType<ConstructorDeclarationSyntax>(second);
        Assert.Single(((ConstructorDeclarationSyntax)first!).ParameterList.Parameters);
        Assert.Equal(2, ((ConstructorDeclarationSyntax)second!).ParameterList.Parameters.Count);
    }

    #endregion

    #region P0 preview describes the rewrite and writes nothing

    [SkippableFact]
    public async Task AddNullChecks_Preview_Column_DescribesRewriteAndWritesNothing()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SameLineOverloadsSource);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new AddNullChecksOperation(workspace.Context);
        var line = FindLine(SameLineOverloadsSource, "public void Process(string name) { }");
        var secondColumn = ColumnOf(SameLineOverloadsSource, "Process(string name, string extra)");

        var result = await operation.ExecuteAsync(new AddNullChecksParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Process",
            Line = line,
            Column = secondColumn,
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.Contains(result.PendingChanges, change =>
            change.Description.Contains("Add null checks to Process", StringComparison.Ordinal) &&
            change.AfterSnippet != null &&
            change.AfterSnippet.Contains("ThrowIfNull", StringComparison.Ordinal) &&
            change.AfterSnippet.Contains("extra", StringComparison.Ordinal));
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    #endregion

    #region Existing add_null_checks / reject cases

    [SkippableFact]
    public async Task AddNullChecks_MethodNotFound_Throws()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SingleMethodSource);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new AddNullChecksOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new AddNullChecksParams
            {
                SourceFile = workspace.SourcePath,
                MethodName = "Missing"
            }));

        Assert.Equal(ErrorCodes.MethodNotFound, ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task AddNullChecks_NoReferenceParameters_ThrowsNoMembersToGenerate()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                public void Process(int count) { }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new AddNullChecksOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new AddNullChecksParams
            {
                SourceFile = workspace.SourcePath,
                MethodName = "Process"
            }));

        Assert.Equal(ErrorCodes.NoMembersToGenerate, ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    #endregion

    #region Helpers

    private static IReadOnlyList<MethodDeclarationSyntax> GetMethods(string source, string name) =>
        CSharpSyntaxTree.ParseText(source).GetRoot()
            .DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Where(m => m.Identifier.Text == name)
            .ToList();

    private static IReadOnlyList<ConstructorDeclarationSyntax> GetConstructors(string source, string name) =>
        CSharpSyntaxTree.ParseText(source).GetRoot()
            .DescendantNodes()
            .OfType<ConstructorDeclarationSyntax>()
            .Where(c => c.Identifier.Text == name)
            .ToList();

    private static string AbsoluteTestPath() =>
        Path.Combine(Path.GetTempPath(), "RoslynMcpAddNullChecksMissing.cs");

    private static string NormalizeNewlines(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\r", "\n", StringComparison.Ordinal);

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
        public required string ProjectPath { get; init; }
        public required string SourcePath { get; init; }
        public required WorkspaceContext Context { get; init; }

        public static async Task<TempWorkspace> CreateAsync(string source, string fileName = "Worker.cs")
        {
            Skip.IfNot(ModuleInitializer.MsBuildAvailable, ModuleInitializer.MsBuildError ?? "MSBuild not available");

            var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpAddNullChecks_" + Guid.NewGuid().ToString("N"));
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

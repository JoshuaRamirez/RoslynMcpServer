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
/// Operation-level tests for <see cref="ConvertToInterpolatedStringOperation"/> (UC-CV4 column).
/// </summary>
public class ConvertToInterpolatedStringOperationTests
{
    private const string SingleFormatSource = """
        namespace TestApp;

        public class Greeter
        {
            public string Hello(string name)
            {
                return string.Format("Hello {0}", name);
            }
        }
        """;

    private const string SingleConcatSource = """
        namespace TestApp;

        public class Greeter
        {
            public string Hello(string name)
            {
                return "Hello " + name;
            }
        }
        """;

    private const string TripleConcatSource = """
        namespace TestApp;

        public class Greeter
        {
            public string Hello(string name, string suffix)
            {
                return "Hello " + name + suffix;
            }
        }
        """;

    private const string AdjacentTripleAndPairSource = """
        namespace TestApp;

        public class Pair
        {
            public string Both(string name, string suffix, string other)
            {
                var a = "Hello " + name + suffix; var b = "Bye " + other;
                return a + b;
            }
        }
        """;

    private const string SameLineFormatSource = """
        namespace TestApp;

        public class Pair
        {
            public string Both(string first, string second)
            {
                var a = string.Format("{0}", first); var b = string.Format("{0}", second);
                return a + b;
            }
        }
        """;

    private const string SameLineConcatSource = """
        namespace TestApp;

        public class Pair
        {
            public string Both(string first, string second)
            {
                var a = "Hi " + first; var b = "Bye " + second;
                return a + b;
            }
        }
        """;

    private const string AdjacentFormatSource = """
        namespace TestApp;

        public class Adjacent
        {
            public string Both(string first, string second)
            {
                var a = string.Format("{0}", first);var b = string.Format("{0}", second);
                return a + b;
            }
        }
        """;

    #region Input Validation

    [Fact]
    public void Validate_MissingSourceFile_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ConvertToInterpolatedStringOperation.Validate(new ConvertToInterpolatedStringParams
            {
                SourceFile = "",
                Line = 1
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public void Validate_InvalidLine_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ConvertToInterpolatedStringOperation.Validate(new ConvertToInterpolatedStringParams
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
            ConvertToInterpolatedStringOperation.Validate(new ConvertToInterpolatedStringParams
            {
                SourceFile = AbsoluteTestPath(),
                Line = 1,
                Column = 0
            }));

        Assert.Equal(ErrorCodes.InvalidColumnNumber, ex.ErrorCode);
    }

    [Fact]
    public void Validate_MissingFile_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ConvertToInterpolatedStringOperation.Validate(new ConvertToInterpolatedStringParams
            {
                SourceFile = AbsoluteTestPath(),
                Line = 1
            }));

        Assert.Equal(ErrorCodes.SourceFileNotFound, ex.ErrorCode);
    }

    #endregion

    #region Existing Format / concatenation cases

    [SkippableFact]
    public async Task Convert_StringFormat_RewritesToInterpolation()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SingleFormatSource);
        var operation = new ConvertToInterpolatedStringOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ConvertToInterpolatedStringParams
        {
            SourceFile = workspace.SourcePath,
            Line = FindLine(SingleFormatSource, "string.Format")
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.DoesNotContain("string.Format", updated, StringComparison.Ordinal);
        Assert.Contains("$\"Hello {name}\"", updated, StringComparison.Ordinal);
        await AssertCompilesAsync(workspace);
    }

    [SkippableFact]
    public async Task Convert_Concatenation_RewritesToInterpolation()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SingleConcatSource);
        var operation = new ConvertToInterpolatedStringOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ConvertToInterpolatedStringParams
        {
            SourceFile = workspace.SourcePath,
            Line = FindLine(SingleConcatSource, "\"Hello \" + name")
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.DoesNotContain("\"Hello \" + name", updated, StringComparison.Ordinal);
        Assert.Contains("$\"Hello {name}\"", updated, StringComparison.Ordinal);
        await AssertCompilesAsync(workspace);
    }

    [SkippableFact]
    public async Task Convert_TripleConcat_OmittedColumn_KeepsAllOperands()
    {
        await using var workspace = await TempWorkspace.CreateAsync(TripleConcatSource);
        var operation = new ConvertToInterpolatedStringOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ConvertToInterpolatedStringParams
        {
            SourceFile = workspace.SourcePath,
            Line = FindLine(TripleConcatSource, "\"Hello \" + name + suffix")
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        AssertKeepsTripleConcatOperands(updated);
        await AssertCompilesAsync(workspace);
    }

    [SkippableFact]
    public async Task Convert_TripleConcat_ColumnOnInnerOperand_KeepsAllOperands()
    {
        await using var workspace = await TempWorkspace.CreateAsync(TripleConcatSource);
        var operation = new ConvertToInterpolatedStringOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ConvertToInterpolatedStringParams
        {
            SourceFile = workspace.SourcePath,
            Line = FindLine(TripleConcatSource, "\"Hello \" + name + suffix"),
            Column = ColumnOf(TripleConcatSource, "name + suffix")
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        AssertKeepsTripleConcatOperands(updated);
        await AssertCompilesAsync(workspace);
    }

    [SkippableFact]
    public async Task Convert_TripleConcat_ColumnOnSecondAdjacentChain_DoesNotRewriteFirst()
    {
        await using var workspace = await TempWorkspace.CreateAsync(AdjacentTripleAndPairSource);
        var operation = new ConvertToInterpolatedStringOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ConvertToInterpolatedStringParams
        {
            SourceFile = workspace.SourcePath,
            Line = FindLine(AdjacentTripleAndPairSource, "\"Hello \" + name + suffix"),
            Column = ColumnOf(AdjacentTripleAndPairSource, "\"Bye \" + other")
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("\"Hello \" + name + suffix", updated, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Bye \" + other", updated, StringComparison.Ordinal);
        Assert.Contains("{other}", updated, StringComparison.Ordinal);
        Assert.DoesNotContain("{suffix}", updated, StringComparison.Ordinal);
        await AssertCompilesAsync(workspace);
    }

    #endregion

    #region P0 omitted column converts the first matching expression

    [SkippableFact]
    public async Task ConvertToInterpolatedString_OmittedColumn_ConvertsFirstFormatOnTheLine()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SameLineFormatSource);
        var operation = new ConvertToInterpolatedStringOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ConvertToInterpolatedStringParams
        {
            SourceFile = workspace.SourcePath,
            Line = FindLine(SameLineFormatSource, "string.Format(\"{0}\", first)")
        });

        Assert.True(result.Success);

        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.DoesNotContain("string.Format(\"{0}\", first)", updated, StringComparison.Ordinal);
        Assert.Contains("string.Format(\"{0}\", second)", updated, StringComparison.Ordinal);
        Assert.Contains("{first}", updated, StringComparison.Ordinal);
        await AssertCompilesAsync(workspace);
    }

    [SkippableFact]
    public async Task ConvertToInterpolatedString_OmittedColumn_ConvertsFirstConcatOnTheLine()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SameLineConcatSource);
        var operation = new ConvertToInterpolatedStringOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ConvertToInterpolatedStringParams
        {
            SourceFile = workspace.SourcePath,
            Line = FindLine(SameLineConcatSource, "\"Hi \" + first")
        });

        Assert.True(result.Success);

        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.DoesNotContain("\"Hi \" + first", updated, StringComparison.Ordinal);
        Assert.Contains("\"Bye \" + second", updated, StringComparison.Ordinal);
        Assert.Contains("{first}", updated, StringComparison.Ordinal);
        await AssertCompilesAsync(workspace);
    }

    #endregion

    #region P0 column picks the intended expression when two share a line

    [SkippableFact]
    public async Task ConvertToInterpolatedString_Column_SelectsSecondFormatOnSameLine()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SameLineFormatSource);
        var operation = new ConvertToInterpolatedStringOperation(workspace.Context);
        var line = FindLine(SameLineFormatSource, "string.Format(\"{0}\", first)");
        var secondColumn = ColumnOf(SameLineFormatSource, "string.Format(\"{0}\", second)");

        var result = await operation.ExecuteAsync(new ConvertToInterpolatedStringParams
        {
            SourceFile = workspace.SourcePath,
            Line = line,
            Column = secondColumn
        });

        Assert.True(result.Success);

        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("string.Format(\"{0}\", first)", updated, StringComparison.Ordinal);
        Assert.DoesNotContain("string.Format(\"{0}\", second)", updated, StringComparison.Ordinal);
        Assert.Contains("{second}", updated, StringComparison.Ordinal);
        await AssertCompilesAsync(workspace);
    }

    [SkippableFact]
    public async Task ConvertToInterpolatedString_Column_SelectsFirstFormatOnSameLine()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SameLineFormatSource);
        var operation = new ConvertToInterpolatedStringOperation(workspace.Context);
        var line = FindLine(SameLineFormatSource, "string.Format(\"{0}\", first)");
        var firstColumn = ColumnOf(SameLineFormatSource, "string.Format(\"{0}\", first)");

        var result = await operation.ExecuteAsync(new ConvertToInterpolatedStringParams
        {
            SourceFile = workspace.SourcePath,
            Line = line,
            Column = firstColumn
        });

        Assert.True(result.Success);

        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.DoesNotContain("string.Format(\"{0}\", first)", updated, StringComparison.Ordinal);
        Assert.Contains("string.Format(\"{0}\", second)", updated, StringComparison.Ordinal);
        Assert.Contains("{first}", updated, StringComparison.Ordinal);
        await AssertCompilesAsync(workspace);
    }

    [SkippableFact]
    public async Task ConvertToInterpolatedString_Column_SelectsSecondConcatOnSameLine()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SameLineConcatSource);
        var operation = new ConvertToInterpolatedStringOperation(workspace.Context);
        var line = FindLine(SameLineConcatSource, "\"Hi \" + first");
        var secondColumn = ColumnOf(SameLineConcatSource, "\"Bye \" + second");

        var result = await operation.ExecuteAsync(new ConvertToInterpolatedStringParams
        {
            SourceFile = workspace.SourcePath,
            Line = line,
            Column = secondColumn
        });

        Assert.True(result.Success);

        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("\"Hi \" + first", updated, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Bye \" + second", updated, StringComparison.Ordinal);
        Assert.Contains("{second}", updated, StringComparison.Ordinal);
        await AssertCompilesAsync(workspace);
    }

    [SkippableFact]
    public async Task ConvertToInterpolatedString_AdjacentExpressions_ColumnOnSecondDoesNotRewriteFirst()
    {
        await using var workspace = await TempWorkspace.CreateAsync(AdjacentFormatSource);
        var operation = new ConvertToInterpolatedStringOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ConvertToInterpolatedStringParams
        {
            SourceFile = workspace.SourcePath,
            Line = FindLine(AdjacentFormatSource, "string.Format(\"{0}\", first)"),
            Column = ColumnOf(AdjacentFormatSource, "string.Format(\"{0}\", second)")
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("string.Format(\"{0}\", first)", updated, StringComparison.Ordinal);
        Assert.DoesNotContain("string.Format(\"{0}\", second)", updated, StringComparison.Ordinal);
        Assert.Contains("{second}", updated, StringComparison.Ordinal);
        await AssertCompilesAsync(workspace);
    }

    [Fact]
    public void FindConvertibleExpression_ColumnPicksSpanCoverage()
    {
        var (root, model) = Compile(SameLineFormatSource);
        var line = FindLine(SameLineFormatSource, "string.Format(\"{0}\", first)");
        var first = ConvertToInterpolatedStringOperation.FindConvertibleExpression(
            root, model, line, ColumnOf(SameLineFormatSource, "string.Format(\"{0}\", first)"));
        var second = ConvertToInterpolatedStringOperation.FindConvertibleExpression(
            root, model, line, ColumnOf(SameLineFormatSource, "string.Format(\"{0}\", second)"));
        var omitted = ConvertToInterpolatedStringOperation.FindConvertibleExpression(
            root, model, line, column: null);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotNull(omitted);
        Assert.Contains("first", first.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("second", first.ToString(), StringComparison.Ordinal);
        Assert.Contains("second", second.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("first", second.ToString(), StringComparison.Ordinal);
        Assert.Equal(first.ToString(), omitted.ToString(), StringComparer.Ordinal);
    }

    [Fact]
    public void FindConvertibleExpression_ColumnOnInnerOperand_ReturnsOuterConcatenation()
    {
        var (root, model) = Compile(TripleConcatSource);
        var line = FindLine(TripleConcatSource, "\"Hello \" + name + suffix");
        var innerColumn = ColumnOf(TripleConcatSource, "name + suffix");
        var found = ConvertToInterpolatedStringOperation.FindConvertibleExpression(
            root, model, line, innerColumn);
        var omitted = ConvertToInterpolatedStringOperation.FindConvertibleExpression(
            root, model, line, column: null);

        Assert.NotNull(found);
        Assert.NotNull(omitted);
        Assert.Contains("suffix", found.ToString(), StringComparison.Ordinal);
        Assert.Contains("suffix", omitted.ToString(), StringComparison.Ordinal);
        Assert.IsType<BinaryExpressionSyntax>(found);
        Assert.Equal(
            ConvertToInterpolatedStringOperation.OuterConcatenation((BinaryExpressionSyntax)found),
            found);
    }

    [Fact]
    public void FindConvertibleExpression_AdjacentExpressions_ExclusiveEndDoesNotStealNext()
    {
        var (root, model) = Compile(AdjacentFormatSource);
        var line = FindLine(AdjacentFormatSource, "string.Format(\"{0}\", first)");
        var first = (InvocationExpressionSyntax)root.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .First(invocation => invocation.ToString().Contains("first", StringComparison.Ordinal));
        var firstEndCol = first.GetLocation().GetLineSpan().EndLinePosition.Character + 1;
        var secondId = ColumnOf(AdjacentFormatSource, "string.Format(\"{0}\", second)");

        var atExclusiveEnd = ConvertToInterpolatedStringOperation.FindConvertibleExpression(
            root, model, line, firstEndCol);
        var atSecondId = ConvertToInterpolatedStringOperation.FindConvertibleExpression(
            root, model, line, secondId);

        Assert.False(ConvertToInterpolatedStringOperation.SpanCoversColumn(
            first.GetLocation().GetLineSpan(), line, firstEndCol));
        Assert.True(atExclusiveEnd == null || !atExclusiveEnd.ToString().Contains("first", StringComparison.Ordinal));
        Assert.NotNull(atSecondId);
        Assert.Contains("second", atSecondId.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("first", atSecondId.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void SpanCoversColumn_TreatsEndAsExclusive()
    {
        const string source = "class C { string M(string x) => string.Format(\"{0}\", x); }";
        var tree = CSharpSyntaxTree.ParseText(source);
        var invocation = tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>().Single();
        var span = invocation.GetLocation().GetLineSpan();
        var line = span.StartLinePosition.Line + 1;
        var startCol = span.StartLinePosition.Character + 1;
        var endCol = span.EndLinePosition.Character + 1;

        Assert.True(ConvertToInterpolatedStringOperation.SpanCoversColumn(span, line, startCol));
        Assert.True(ConvertToInterpolatedStringOperation.SpanCoversColumn(span, line, endCol - 1));
        Assert.False(ConvertToInterpolatedStringOperation.SpanCoversColumn(span, line, endCol));
        Assert.False(ConvertToInterpolatedStringOperation.SpanCoversColumn(span, line, startCol - 1));
    }

    #endregion

    #region P0 preview describes the rewrite and writes nothing

    [SkippableFact]
    public async Task ConvertToInterpolatedString_Preview_DescribesRewriteAndWritesNothing()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SingleFormatSource);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new ConvertToInterpolatedStringOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ConvertToInterpolatedStringParams
        {
            SourceFile = workspace.SourcePath,
            Line = FindLine(SingleFormatSource, "string.Format"),
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.Contains(result.PendingChanges, change =>
            change.Description.Contains("interpolated string", StringComparison.Ordinal) &&
            change.AfterSnippet != null &&
            change.AfterSnippet.Contains("{name}", StringComparison.Ordinal));
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    #endregion

    #region Helpers

    private static void AssertKeepsTripleConcatOperands(string updated)
    {
        Assert.DoesNotContain("\"Hello \" + name + suffix", updated, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Hello \" + name;", updated, StringComparison.Ordinal);
        Assert.Contains("{name", updated, StringComparison.Ordinal);
        Assert.Contains("{suffix}", updated, StringComparison.Ordinal);
        Assert.DoesNotContain("+ suffix", updated, StringComparison.Ordinal);
    }

    private static (SyntaxNode Root, SemanticModel Model) Compile(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var compilation = CSharpCompilation.Create(
            "ConvertToInterpolatedStringFind",
            [tree],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        return (tree.GetRoot(), compilation.GetSemanticModel(tree));
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
        Path.Combine(Path.GetTempPath(), "RoslynMcpConvertToInterpolatedStringMissing.cs");

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

            var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpConvertToInterpolatedString_" + Guid.NewGuid().ToString("N"));
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

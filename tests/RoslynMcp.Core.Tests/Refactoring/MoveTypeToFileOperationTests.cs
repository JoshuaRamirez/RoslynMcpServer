using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Resolution;
using RoslynMcp.Core.Workspace;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring;

/// <summary>
/// Operation-level tests for <see cref="MoveTypeToFileOperation"/>,
/// including optional <c>line</c>, <c>column</c>, <c>createTargetFile</c>,
/// and <c>preview</c>.
/// </summary>
public class MoveTypeToFileOperationTests
{
    #region Input Validation

    [Fact]
    public void Column_DefaultsToNull()
    {
        var @params = new MoveTypeToFileParams
        {
            SourceFile = AbsoluteTestPath(),
            SymbolName = "Widget",
            TargetFile = AbsoluteTestPath("Target.cs")
        };

        Assert.Null(@params.Column);
    }

    [Fact]
    public void Validate_InvalidColumn_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            MoveTypeToFileOperation.Validate(new MoveTypeToFileParams
            {
                SourceFile = AbsoluteTestPath(),
                SymbolName = "Widget",
                TargetFile = AbsoluteTestPath("Target.cs"),
                Column = 0
            }));

        Assert.Equal(ErrorCodes.InvalidColumnNumber, ex.ErrorCode);
        Assert.Equal("1007", ex.ErrorCode);
        Assert.Equal("column must be >= 1.", ex.Message);
    }

    [Fact]
    public void Validate_NegativeColumn_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            MoveTypeToFileOperation.Validate(new MoveTypeToFileParams
            {
                SourceFile = AbsoluteTestPath(),
                SymbolName = "Widget",
                TargetFile = AbsoluteTestPath("Target.cs"),
                Column = -1
            }));

        Assert.Equal(ErrorCodes.InvalidColumnNumber, ex.ErrorCode);
        Assert.Equal("1007", ex.ErrorCode);
        Assert.Equal("column must be >= 1.", ex.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_EmptySymbolName_WithColumnAndLine_ThrowsMissingRequiredParam(string symbolName)
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            MoveTypeToFileOperation.Validate(new MoveTypeToFileParams
            {
                SourceFile = AbsoluteTestPath(),
                SymbolName = symbolName,
                TargetFile = AbsoluteTestPath("Target.cs"),
                Line = 1,
                Column = 1
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_EmptySourceFile_WithColumnAndLine_ThrowsMissingRequiredParam(string sourceFile)
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            MoveTypeToFileOperation.Validate(new MoveTypeToFileParams
            {
                SourceFile = sourceFile,
                SymbolName = "Widget",
                TargetFile = AbsoluteTestPath("Target.cs"),
                Line = 1,
                Column = 1
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_EmptyTargetFile_WithColumnAndLine_ThrowsMissingRequiredParam(string targetFile)
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            MoveTypeToFileOperation.Validate(new MoveTypeToFileParams
            {
                SourceFile = AbsoluteTestPath(),
                SymbolName = "Widget",
                TargetFile = targetFile,
                Line = 1,
                Column = 1
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    #endregion

    #region P0 optional column disambiguation

    private const string SameLineDualTopLevelWidgetSource =
        "namespace A { public class Widget { } /* a-widget */ } namespace B { public class Widget { } /* b-widget */ }\n";

    private const string SeparateLineDualTopLevelWidgetSource = """
        namespace A
        {
            public class Widget { } // a-widget
        }

        namespace B
        {
            public class Widget { } // b-widget
        }
        """;

    private const string SingleWidgetSource = """
        namespace TestApp;

        public class Widget
        {
        }
        """;

    private const string NestedWidgetSource = """
        namespace TestApp;

        public class Outer
        {
            public class Widget { } // nested-widget
        }
        """;

    [SkippableFact]
    public async Task MoveTypeToFile_OmittedColumn_LinePicksStartLineType()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SeparateLineDualTopLevelWidgetSource);
        var operation = new MoveTypeToFileOperation(workspace.Context);
        var target = workspace.TargetPath("AWidget.cs");

        var result = await operation.ExecuteAsync(new MoveTypeToFileParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Widget",
            Line = FindLine(SeparateLineDualTopLevelWidgetSource, "a-widget"),
            TargetFile = target
        });

        Assert.True(result.Success);
        var source = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var moved = NormalizeNewlines(await File.ReadAllTextAsync(target));
        Assert.DoesNotContain("a-widget", source);
        Assert.Contains("b-widget", source);
        Assert.Contains("namespace A", moved);
        Assert.Contains("class Widget", moved);
    }

    [SkippableFact]
    public async Task MoveTypeToFile_OmittedColumn_MultipleMatches_ThrowsSymbolAmbiguous()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SeparateLineDualTopLevelWidgetSource);
        var operation = new MoveTypeToFileOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new MoveTypeToFileParams
            {
                SourceFile = workspace.SourcePath,
                SymbolName = "Widget",
                TargetFile = workspace.TargetPath("Widget.cs")
            }));

        Assert.Equal(ErrorCodes.SymbolAmbiguous, ex.ErrorCode);
        Assert.Equal("2004", ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task MoveTypeToFile_OmittedColumn_SingleMatch_IgnoresLine()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SingleWidgetSource);
        var operation = new MoveTypeToFileOperation(workspace.Context);
        var target = workspace.TargetPath("Widget.cs");

        var result = await operation.ExecuteAsync(new MoveTypeToFileParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Widget",
            Line = 1,
            TargetFile = target
        });

        Assert.True(result.Success);
        Assert.True(File.Exists(target));
        var moved = NormalizeNewlines(await File.ReadAllTextAsync(target));
        Assert.Contains("class Widget", moved);
        Assert.False(File.Exists(workspace.SourcePath));
    }

    [Fact]
    public void FindCoveringType_OmittedColumn_StartLineEqualityPicksFirstOnSharedLine()
    {
        var root = Parse(SameLineDualTopLevelWidgetSource);
        var matches = TopLevelNamed(root, "Widget");
        Assert.Equal(2, matches.Count);

        var line = FindLine(SameLineDualTopLevelWidgetSource, "a-widget");
        var a = matches.First(t => NamespaceOf(t) == "A");
        var startLine = a.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
        Assert.Equal(line, startLine);

        var firstStartLine = matches.First(t =>
            t.GetLocation().GetLineSpan().StartLinePosition.Line + 1 == line);
        Assert.Equal("A", NamespaceOf(firstStartLine));
    }

    [Fact]
    public void FindCoveringType_ColumnOnAIdentifier_PicksA()
    {
        var root = Parse(SameLineDualTopLevelWidgetSource);
        var matches = TopLevelNamed(root, "Widget");
        var line = FindLine(SameLineDualTopLevelWidgetSource, "a-widget");
        var found = TypeSymbolResolver.FindCoveringType(
            matches, line, ColumnOf(SameLineDualTopLevelWidgetSource, "Widget { } /* a-widget */"));

        Assert.NotNull(found);
        Assert.Equal("A", NamespaceOf(found));
    }

    [Fact]
    public void FindCoveringType_ColumnOnBIdentifier_PicksB()
    {
        var root = Parse(SameLineDualTopLevelWidgetSource);
        var matches = TopLevelNamed(root, "Widget");
        var line = FindLine(SameLineDualTopLevelWidgetSource, "b-widget");
        var found = TypeSymbolResolver.FindCoveringType(
            matches, line, ColumnOf(SameLineDualTopLevelWidgetSource, "Widget { } /* b-widget */"));

        Assert.NotNull(found);
        Assert.Equal("B", NamespaceOf(found));
    }

    [SkippableFact]
    public async Task MoveTypeToFile_ColumnOnAIdentifier_PicksA()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SameLineDualTopLevelWidgetSource);
        var operation = new MoveTypeToFileOperation(workspace.Context);
        var target = workspace.TargetPath("AWidget.cs");
        var line = FindLine(SameLineDualTopLevelWidgetSource, "a-widget");

        var result = await operation.ExecuteAsync(new MoveTypeToFileParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Widget",
            Line = line,
            Column = ColumnOf(SameLineDualTopLevelWidgetSource, "Widget { } /* a-widget */"),
            TargetFile = target
        });

        Assert.True(result.Success);
        var source = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var moved = NormalizeNewlines(await File.ReadAllTextAsync(target));
        Assert.DoesNotContain("a-widget", source);
        Assert.Contains("b-widget", source);
        Assert.Contains("namespace A", moved);
        Assert.Contains("class Widget", moved);
        Assert.DoesNotContain("namespace B", moved);
    }

    [SkippableFact]
    public async Task MoveTypeToFile_ColumnOnBIdentifier_PicksB()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SameLineDualTopLevelWidgetSource);
        var operation = new MoveTypeToFileOperation(workspace.Context);
        var target = workspace.TargetPath("BWidget.cs");
        var line = FindLine(SameLineDualTopLevelWidgetSource, "b-widget");

        var result = await operation.ExecuteAsync(new MoveTypeToFileParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Widget",
            Line = line,
            Column = ColumnOf(SameLineDualTopLevelWidgetSource, "Widget { } /* b-widget */"),
            TargetFile = target
        });

        Assert.True(result.Success);
        var source = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var moved = NormalizeNewlines(await File.ReadAllTextAsync(target));
        Assert.Contains("a-widget", source);
        Assert.DoesNotContain("b-widget", source);
        Assert.Contains("namespace B", moved);
        Assert.Contains("class Widget", moved);
        Assert.DoesNotContain("namespace A", moved);
    }

    [Fact]
    public void FindCoveringType_ColumnWithoutLine_IsNotInvokedForOmittedLinePath()
    {
        var root = Parse(SameLineDualTopLevelWidgetSource);
        var matches = TopLevelNamed(root, "Widget");
        var bColumn = ColumnOf(SameLineDualTopLevelWidgetSource, "Widget { } /* b-widget */");
        var line = FindLine(SameLineDualTopLevelWidgetSource, "b-widget");
        var coveringB = TypeSymbolResolver.FindCoveringType(matches, line, bColumn);

        Assert.NotNull(coveringB);
        Assert.Equal("B", NamespaceOf(coveringB));
        Assert.Equal(2, matches.Count);
    }

    [SkippableFact]
    public async Task MoveTypeToFile_ColumnWithoutLine_KeepsOmittedLinePath_SymbolAmbiguous()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SameLineDualTopLevelWidgetSource);
        var operation = new MoveTypeToFileOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new MoveTypeToFileParams
            {
                SourceFile = workspace.SourcePath,
                SymbolName = "Widget",
                Column = ColumnOf(SameLineDualTopLevelWidgetSource, "Widget { } /* b-widget */"),
                TargetFile = workspace.TargetPath("Widget.cs")
            }));

        Assert.Equal(ErrorCodes.SymbolAmbiguous, ex.ErrorCode);
        Assert.Equal("2004", ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [Fact]
    public void FindCoveringType_ColumnOnContinuationIdentifier_PicksType()
    {
        const string source = """
            namespace A
            {
                public class
                    Widget /* split-type */
                {
                }
            }

            namespace B
            {
                public class Widget { } /* other-widget */
            }
            """;

        var root = Parse(source);
        var startLine = FindLine(source, "public class");
        var identifierLine = FindLine(source, "split-type");
        Assert.NotEqual(startLine, identifierLine);

        var matches = TopLevelNamed(root, "Widget");
        var found = TypeSymbolResolver.FindCoveringType(
            matches, identifierLine, ColumnOf(source, "Widget /* split-type */"));

        Assert.NotNull(found);
        Assert.Equal("A", NamespaceOf(found));
    }

    [SkippableFact]
    public async Task MoveTypeToFile_ColumnOnContinuationLine_PicksType()
    {
        const string source = """
            namespace A
            {
                public class
                    Widget /* split-type */
                {
                }
            }

            namespace B
            {
                public class Widget { } /* other-widget */
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new MoveTypeToFileOperation(workspace.Context);
        var target = workspace.TargetPath("AWidget.cs");
        var identifierLine = FindLine(source, "split-type");

        var result = await operation.ExecuteAsync(new MoveTypeToFileParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Widget",
            Line = identifierLine,
            Column = ColumnOf(source, "Widget /* split-type */"),
            TargetFile = target
        });

        Assert.True(result.Success);
        var remaining = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var moved = NormalizeNewlines(await File.ReadAllTextAsync(target));
        Assert.DoesNotContain("split-type", remaining);
        Assert.Contains("other-widget", remaining);
        Assert.Contains("namespace A", moved);
        Assert.Contains("class", moved);
        Assert.Contains("Widget", moved);
    }

    [Fact]
    public void FindCoveringType_ColumnAndLineMiss_DoesNotFallBackToFirst()
    {
        var root = Parse(SeparateLineDualTopLevelWidgetSource);
        var matches = TopLevelNamed(root, "Widget");
        var found = TypeSymbolResolver.FindCoveringType(matches, line: 1, column: 1);

        Assert.Null(found);
    }

    [SkippableFact]
    public async Task MoveTypeToFile_ColumnAndLineMiss_ThrowsSymbolNotFound()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SeparateLineDualTopLevelWidgetSource);
        var operation = new MoveTypeToFileOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new MoveTypeToFileParams
            {
                SourceFile = workspace.SourcePath,
                SymbolName = "Widget",
                Line = 1,
                Column = 1,
                TargetFile = workspace.TargetPath("Widget.cs")
            }));

        Assert.Equal(ErrorCodes.SymbolNotFound, ex.ErrorCode);
        Assert.Equal("2003", ex.ErrorCode);
        Assert.Contains("line 1, column 1", ex.Message);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task MoveTypeToFile_ColumnAndLine_UnknownSymbolName_ThrowsSymbolNotFound()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SingleWidgetSource);
        var operation = new MoveTypeToFileOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new MoveTypeToFileParams
            {
                SourceFile = workspace.SourcePath,
                SymbolName = "Missing",
                Line = 1,
                Column = 1,
                TargetFile = workspace.TargetPath("Missing.cs")
            }));

        Assert.Equal(ErrorCodes.SymbolNotFound, ex.ErrorCode);
        Assert.Equal("2003", ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task MoveTypeToFile_NestedType_StaysUnmoveable()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NestedWidgetSource);
        var operation = new MoveTypeToFileOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);
        var target = workspace.TargetPath("Nested.cs");
        var line = FindLine(NestedWidgetSource, "nested-widget");

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new MoveTypeToFileParams
            {
                SourceFile = workspace.SourcePath,
                SymbolName = "Widget",
                Line = line,
                Column = ColumnOf(NestedWidgetSource, "Widget { } // nested-widget"),
                TargetFile = target
            }));

        Assert.Equal(ErrorCodes.SymbolNotFound, ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.False(File.Exists(target));
    }

    [SkippableFact]
    public async Task MoveTypeToFile_Column_Preview_WritesNothing_AndDescribesMove()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SameLineDualTopLevelWidgetSource);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new MoveTypeToFileOperation(workspace.Context);
        var target = workspace.TargetPath("AWidget.cs");
        var line = FindLine(SameLineDualTopLevelWidgetSource, "a-widget");

        var result = await operation.ExecuteAsync(new MoveTypeToFileParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Widget",
            Line = line,
            Column = ColumnOf(SameLineDualTopLevelWidgetSource, "Widget { } /* a-widget */"),
            TargetFile = target,
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.Contains(result.PendingChanges, change =>
            change.Description.Contains("Widget", StringComparison.Ordinal));
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.False(File.Exists(target));
    }

    [Fact]
    public void SpanCoversColumn_TreatsEndAsExclusive()
    {
        const string source = "namespace A { class Widget { } } namespace B { class Widget { } }";
        var tree = CSharpSyntaxTree.ParseText(source);
        var first = tree.GetRoot().DescendantNodes().OfType<TypeDeclarationSyntax>()
            .First(t => t.Identifier.Text == "Widget");
        var span = first.Identifier.GetLocation().GetLineSpan();
        var line = span.StartLinePosition.Line + 1;
        var startCol = span.StartLinePosition.Character + 1;
        var endCol = span.EndLinePosition.Character + 1;

        Assert.True(TypeSymbolResolver.SpanCoversColumn(span, line, startCol));
        Assert.True(TypeSymbolResolver.SpanCoversColumn(span, line, endCol - 1));
        Assert.False(TypeSymbolResolver.SpanCoversColumn(span, line, endCol));
        Assert.False(TypeSymbolResolver.SpanCoversColumn(span, line, startCol - 1));
    }

    [SkippableFact]
    public async Task MoveTypeToFile_SequentialColumn_ReusedWorkspace_ActsOnSecondSelectedType()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SameLineDualTopLevelWidgetSource);
        var operation = new MoveTypeToFileOperation(workspace.Context);
        var line = FindLine(SameLineDualTopLevelWidgetSource, "a-widget");

        var first = await operation.ExecuteAsync(new MoveTypeToFileParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Widget",
            Line = line,
            Column = ColumnOf(SameLineDualTopLevelWidgetSource, "Widget { } /* a-widget */"),
            TargetFile = workspace.TargetPath("AWidget.cs")
        });
        Assert.True(first.Success);

        var afterFirst = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.DoesNotContain("a-widget", afterFirst);
        Assert.Contains("b-widget", afterFirst);

        var second = await operation.ExecuteAsync(new MoveTypeToFileParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Widget",
            Line = FindLine(afterFirst, "b-widget"),
            Column = ColumnOf(afterFirst, "Widget { } /* b-widget */"),
            TargetFile = workspace.TargetPath("BWidget.cs")
        });
        Assert.True(second.Success);

        Assert.True(File.Exists(workspace.TargetPath("AWidget.cs")));
        Assert.True(File.Exists(workspace.TargetPath("BWidget.cs")));
        var aMoved = NormalizeNewlines(await File.ReadAllTextAsync(workspace.TargetPath("AWidget.cs")));
        var bMoved = NormalizeNewlines(await File.ReadAllTextAsync(workspace.TargetPath("BWidget.cs")));
        Assert.Contains("namespace A", aMoved);
        Assert.Contains("namespace B", bMoved);
    }

    [SkippableFact]
    public async Task MoveTypeToFile_CreateTargetFile_CreatesMissingTarget()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SingleWidgetSource);
        var operation = new MoveTypeToFileOperation(workspace.Context);
        var target = workspace.TargetPath("Created.cs");

        var result = await operation.ExecuteAsync(new MoveTypeToFileParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Widget",
            TargetFile = target,
            CreateTargetFile = true
        });

        Assert.True(result.Success);
        Assert.True(File.Exists(target));
        Assert.Contains("class Widget", await File.ReadAllTextAsync(target));
    }

    [SkippableFact]
    public async Task MoveTypeToFile_Preview_WithoutColumn_WritesNothing()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SingleWidgetSource);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new MoveTypeToFileOperation(workspace.Context);
        var target = workspace.TargetPath("Widget.cs");

        var result = await operation.ExecuteAsync(new MoveTypeToFileParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Widget",
            TargetFile = target,
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.False(File.Exists(target));
    }

    #endregion

    #region Helpers

    private static string NormalizeNewlines(string text) => text.Replace("\r\n", "\n");

    private static string AbsoluteTestPath(string name = "Missing.cs") =>
        Path.Combine(Path.GetTempPath(), "RoslynMcpMoveTypeToFile_" + name);

    private static SyntaxNode Parse(string source) =>
        CSharpSyntaxTree.ParseText(NormalizeNewlines(source)).GetRoot();

    private static List<TypeDeclarationSyntax> TopLevelNamed(SyntaxNode root, string name) =>
        root.DescendantNodes()
            .OfType<TypeDeclarationSyntax>()
            .Where(t => t.Parent is CompilationUnitSyntax or BaseNamespaceDeclarationSyntax)
            .Where(t => t.Identifier.Text == name)
            .ToList();

    private static string NamespaceOf(TypeDeclarationSyntax type) =>
        type.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().First().Name.ToString();

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
        public required IReadOnlyDictionary<string, string> FilePaths { get; init; }
        public required WorkspaceContext Context { get; init; }

        public string TargetPath(string fileName) => Path.Combine(DirectoryPath, fileName);

        public static Task<TempWorkspace> CreateAsync(string source, string fileName = "Types.cs") =>
            CreateAsync((fileName, source));

        public static async Task<TempWorkspace> CreateAsync(params (string FileName, string Source)[] files)
        {
            Skip.IfNot(ModuleInitializer.MsBuildAvailable, ModuleInitializer.MsBuildError ?? "MSBuild not available");

            var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpMoveTypeToFile_" + Guid.NewGuid().ToString("N"));
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

            var filePaths = new Dictionary<string, string>(StringComparer.Ordinal);
            string? sourcePath = null;
            foreach (var (fileName, source) in files)
            {
                var path = Path.Combine(directory, fileName);
                var parent = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(parent))
                    Directory.CreateDirectory(parent);
                await File.WriteAllTextAsync(path, source);
                filePaths[fileName] = path;
                sourcePath ??= path;
            }

            sourcePath ??= Path.Combine(directory, "Types.cs");

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
                    FilePaths = filePaths,
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

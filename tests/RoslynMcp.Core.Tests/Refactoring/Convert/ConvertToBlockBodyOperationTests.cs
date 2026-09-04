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
/// Operation-level tests for <see cref="ConvertToBlockBodyOperation"/>.
/// </summary>
public class ConvertToBlockBodyOperationTests
{
    private const string SameLineExpressionSource = """
        namespace TestApp;

        public class Pair
        {
            public int Foo() => 1; public int Bar() => 2;
        }
        """;

    private const string ContinuationExpressionSource = """
        namespace TestApp;

        public class Split
        {
            public int
            Foo() => 1;
        }
        """;
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

    [Fact]
    public void Validate_InvalidColumn_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ConvertToBlockBodyOperation.Validate(new ConvertToBlockBodyParams
            {
                SourceFile = AbsoluteTestPath(),
                MemberName = "Foo",
                Column = 0
            }));

        Assert.Equal(ErrorCodes.InvalidColumnNumber, ex.ErrorCode);
        Assert.Equal("1007", ex.ErrorCode);
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

    [SkippableFact]
    public async Task Convert_AsyncTaskMethod_InsertsExpressionStatement()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                public async System.Threading.Tasks.Task Run() => await WorkAsync();

                private static System.Threading.Tasks.Task WorkAsync() => System.Threading.Tasks.Task.CompletedTask;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ConvertToBlockBodyOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ConvertToBlockBodyParams
        {
            SourceFile = workspace.SourcePath,
            MemberName = "Run"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("await WorkAsync();", updated);
        Assert.DoesNotContain("return await WorkAsync", updated);
    }

    [SkippableFact]
    public async Task Convert_AsyncValueTaskMethod_InsertsExpressionStatement()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                public async System.Threading.Tasks.ValueTask Run() => await WorkAsync();

                private static System.Threading.Tasks.ValueTask WorkAsync() => System.Threading.Tasks.ValueTask.CompletedTask;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ConvertToBlockBodyOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ConvertToBlockBodyParams
        {
            SourceFile = workspace.SourcePath,
            MemberName = "Run"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("await WorkAsync();", updated);
        Assert.DoesNotContain("return await WorkAsync", updated);
    }

    [SkippableFact]
    public async Task Convert_AsyncTaskOfTMethod_InsertsReturn()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                public async System.Threading.Tasks.Task<int> Get() => await WorkAsync();

                private static System.Threading.Tasks.Task<int> WorkAsync() => System.Threading.Tasks.Task.FromResult(1);
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ConvertToBlockBodyOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ConvertToBlockBodyParams
        {
            SourceFile = workspace.SourcePath,
            MemberName = "Get"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("return await WorkAsync();", updated);
    }

    [SkippableFact]
    public async Task Convert_AsyncTaskLocalFunction_InsertsExpressionStatement()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                public void Host()
                {
                    async System.Threading.Tasks.Task Run() => await WorkAsync();
                }

                private static System.Threading.Tasks.Task WorkAsync() => System.Threading.Tasks.Task.CompletedTask;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ConvertToBlockBodyOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ConvertToBlockBodyParams
        {
            SourceFile = workspace.SourcePath,
            MemberName = "Run"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("await WorkAsync();", updated);
        Assert.DoesNotContain("return await WorkAsync", updated);
    }

    [SkippableFact]
    public async Task Convert_InitAccessor_InsertsExpressionStatement()
    {
        const string source = """
            namespace TestApp;

            public class Person
            {
                private string _name = "Ada";
                public string Name
                {
                    get => _name;
                    init => _name = value;
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
        Assert.DoesNotContain("init =>", updated);
        Assert.Contains("return _name;", updated);
        Assert.Contains("_name = value;", updated);
        Assert.DoesNotContain("return _name = value", updated);
    }

    [SkippableFact]
    public async Task Convert_Accessor_PreservesAttributes()
    {
        const string source = """
            namespace TestApp;

            public class Person
            {
                private string _name = "Ada";
                public string Name
                {
                    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
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
        Assert.Contains("MethodImpl", updated);
        Assert.Contains("AggressiveInlining", updated);
        Assert.Contains("return _name;", updated);
        Assert.DoesNotContain("get =>", updated);
    }

    #endregion

    #region P0 omitted column keeps today's memberName + line pick

    [SkippableFact]
    public async Task Convert_OmittedColumn_KeepsMemberNameAndLinePick()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SameLineExpressionSource);
        var operation = new ConvertToBlockBodyOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ConvertToBlockBodyParams
        {
            SourceFile = workspace.SourcePath,
            MemberName = "Foo",
            Line = FindLine(SameLineExpressionSource, "public int Foo()")
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.DoesNotContain("public int Foo() => 1;", updated, StringComparison.Ordinal);
        Assert.Contains("public int Bar() => 2;", updated, StringComparison.Ordinal);
        Assert.Contains("return 1;", updated, StringComparison.Ordinal);
    }

    #endregion

    #region P0 column picks the intended member when two share a line

    [SkippableFact]
    public async Task Convert_Column_SelectsSecondMemberOnSameLine()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SameLineExpressionSource);
        var operation = new ConvertToBlockBodyOperation(workspace.Context);
        var line = FindLine(SameLineExpressionSource, "public int Foo()");
        var secondColumn = ColumnOf(SameLineExpressionSource, "Bar()");

        var result = await operation.ExecuteAsync(new ConvertToBlockBodyParams
        {
            SourceFile = workspace.SourcePath,
            Line = line,
            Column = secondColumn
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("public int Foo() => 1;", updated, StringComparison.Ordinal);
        Assert.DoesNotContain("public int Bar() => 2;", updated, StringComparison.Ordinal);
        Assert.Contains("return 2;", updated, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task Convert_ColumnOnContinuationLine_ConvertsThatMember()
    {
        await using var workspace = await TempWorkspace.CreateAsync(ContinuationExpressionSource);
        var operation = new ConvertToBlockBodyOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ConvertToBlockBodyParams
        {
            SourceFile = workspace.SourcePath,
            Line = FindLine(ContinuationExpressionSource, "Foo()"),
            Column = ColumnOf(ContinuationExpressionSource, "Foo()")
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("return 1;", updated, StringComparison.Ordinal);
        Assert.DoesNotContain("=>", updated, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task Convert_Preview_Column_WritesNothing()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SameLineExpressionSource);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new ConvertToBlockBodyOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ConvertToBlockBodyParams
        {
            SourceFile = workspace.SourcePath,
            Line = FindLine(SameLineExpressionSource, "public int Foo()"),
            Column = ColumnOf(SameLineExpressionSource, "Bar()"),
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.Contains(result.PendingChanges, change => change.Description.Contains("Bar", StringComparison.Ordinal));
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [Fact]
    public void FindMember_OmittedColumn_PicksSmallestContainingNode()
    {
        var tree = CSharpSyntaxTree.ParseText(SameLineExpressionSource);
        var root = tree.GetRoot();
        var line = FindLine(SameLineExpressionSource, "public int Foo()");
        var omitted = ConvertToBlockBodyOperation.FindMember(root, "Foo", line, column: null);
        var byNameAndLine = ConvertToBlockBodyOperation.FindMember(root, "Bar", line, column: null);

        Assert.NotNull(omitted);
        Assert.NotNull(byNameAndLine);
        Assert.Equal("Foo", ((MethodDeclarationSyntax)omitted).Identifier.Text);
        Assert.Equal("Bar", ((MethodDeclarationSyntax)byNameAndLine).Identifier.Text);
    }

    [Fact]
    public void FindMember_ColumnPicksIdentifierCoverage()
    {
        var tree = CSharpSyntaxTree.ParseText(SameLineExpressionSource);
        var root = tree.GetRoot();
        var line = FindLine(SameLineExpressionSource, "public int Foo()");
        var first = ConvertToBlockBodyOperation.FindMember(root, null, line, ColumnOf(SameLineExpressionSource, "Foo()"));
        var second = ConvertToBlockBodyOperation.FindMember(root, null, line, ColumnOf(SameLineExpressionSource, "Bar()"));
        var omitted = ConvertToBlockBodyOperation.FindMember(root, null, line, column: null);

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
        var tree = CSharpSyntaxTree.ParseText(ContinuationExpressionSource);
        var root = tree.GetRoot();
        var startLine = FindLine(ContinuationExpressionSource, "public int");
        var identifierLine = FindLine(ContinuationExpressionSource, "Foo()");
        Assert.NotEqual(startLine, identifierLine);

        // Omitted column keeps today's ContainsLine + smallest node, so the
        // continuation line still finds the declaration without column.
        var byLineOnly = ConvertToBlockBodyOperation.FindMember(root, null, identifierLine, column: null);
        var byColumn = ConvertToBlockBodyOperation.FindMember(
            root, null, identifierLine, ColumnOf(ContinuationExpressionSource, "Foo()"));

        Assert.NotNull(byLineOnly);
        Assert.Equal("Foo", ((MethodDeclarationSyntax)byLineOnly).Identifier.Text);
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

        var atSecondStart = ConvertToBlockBodyOperation.FindMember(root, null, line, secondStart);
        var atSecondId = ConvertToBlockBodyOperation.FindMember(root, null, line, secondId);

        Assert.NotNull(atSecondStart);
        Assert.NotNull(atSecondId);
        Assert.Equal("Longer", ((MethodDeclarationSyntax)atSecondStart).Identifier.Text);
        Assert.Equal("Longer", ((MethodDeclarationSyntax)atSecondId).Identifier.Text);
    }

    [Fact]
    public void FindMember_ColumnWithoutLine_SameIndentSameName_KeepsFirstMatch()
    {
        const string source = """
            class C
            {
                public int Foo() => 1000;
                public int Foo(int n) => n;
            }
            """;

        var tree = CSharpSyntaxTree.ParseText(source);
        var root = tree.GetRoot();
        var column = ColumnOf(source, "Foo()");
        Assert.Equal(column, ColumnOf(source, "Foo(int n)"));

        var found = ConvertToBlockBodyOperation.FindMember(root, "Foo", line: null, column);

        Assert.NotNull(found);
        var method = Assert.IsType<MethodDeclarationSyntax>(found);
        Assert.Empty(method.ParameterList.Parameters);
    }

    [Fact]
    public void SpanCoversColumn_TreatsEndAsExclusive()
    {
        const string source = "class C { public int A()=>1;public int B()=>2; }";
        var tree = CSharpSyntaxTree.ParseText(source);
        var method = tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>()
            .First(m => m.Identifier.Text == "A");
        var span = method.GetLocation().GetLineSpan();
        var line = span.StartLinePosition.Line + 1;
        var startCol = span.StartLinePosition.Character + 1;
        var endCol = span.EndLinePosition.Character + 1;

        Assert.True(ConvertToBlockBodyOperation.SpanCoversColumn(span, line, startCol));
        Assert.True(ConvertToBlockBodyOperation.SpanCoversColumn(span, line, endCol - 1));
        Assert.False(ConvertToBlockBodyOperation.SpanCoversColumn(span, line, endCol));
        Assert.False(ConvertToBlockBodyOperation.SpanCoversColumn(span, line, startCol - 1));
    }

    [Fact]
    public void ContainsLine_TreatsEndAsExclusive()
    {
        // Trailing newline puts the compilation unit's exclusive end at (2, 0),
        // the same FileLinePositionSpan EncapsulateFieldOperationTests pins.
        const string source = "class C\n{}\n";
        var node = CSharpSyntaxTree.ParseText(source).GetRoot();
        var span = node.GetLocation().GetLineSpan();

        Assert.Equal(new LinePosition(0, 0), span.StartLinePosition);
        Assert.Equal(new LinePosition(2, 0), span.EndLinePosition);

        Assert.True(ConvertToBlockBodyOperation.ContainsLine(node, 1));
        Assert.True(ConvertToBlockBodyOperation.ContainsLine(node, 2));
        Assert.False(ConvertToBlockBodyOperation.ContainsLine(node, 3));
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


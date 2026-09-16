using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.FileSystem;
using RoslynMcp.Core.Refactoring.Convert;
using RoslynMcp.Core.Refactoring.Utilities;
using RoslynMcp.Core.Resolution;
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

    [Fact]
    public void Validate_AllFilesFalse_WithoutSourceFile_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ConvertToBlockBodyOperation.Validate(new ConvertToBlockBodyParams
            {
                AllFiles = false,
                MemberName = "Get"
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public void Validate_AllFilesTrue_WithoutSourceFile_DoesNotThrow()
    {
        ConvertToBlockBodyOperation.Validate(new ConvertToBlockBodyParams
        {
            AllFiles = true
        });
    }

    [Fact]
    public void Validate_AllFilesTrue_WithRelativeSourceFile_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ConvertToBlockBodyOperation.Validate(new ConvertToBlockBodyParams
            {
                AllFiles = true,
                SourceFile = "Types.cs"
            }));

        Assert.Equal(ErrorCodes.InvalidSourcePath, ex.ErrorCode);
    }

    [Fact]
    public void Validate_AllFilesTrue_WithNonCSharpSourceFile_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ConvertToBlockBodyOperation.Validate(new ConvertToBlockBodyParams
            {
                AllFiles = true,
                SourceFile = "/tmp/Types.txt"
            }));

        Assert.Equal(ErrorCodes.InvalidSourcePath, ex.ErrorCode);
    }

    [Fact]
    public void Validate_AllFilesTrue_WithMissingSourceFile_DoesNotThrow()
    {
        ConvertToBlockBodyOperation.Validate(new ConvertToBlockBodyParams
        {
            AllFiles = true,
            SourceFile = AbsoluteTestPath()
        });
    }

    [Fact]
    public void Validate_AllFilesTrue_WithMemberName_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ConvertToBlockBodyOperation.Validate(new ConvertToBlockBodyParams
            {
                AllFiles = true,
                MemberName = "Get"
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("allFiles", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_AllFilesTrue_WithLine_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ConvertToBlockBodyOperation.Validate(new ConvertToBlockBodyParams
            {
                AllFiles = true,
                Line = 4
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("allFiles", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_AllFilesTrue_WithColumn_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ConvertToBlockBodyOperation.Validate(new ConvertToBlockBodyParams
            {
                AllFiles = true,
                Column = 1
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("allFiles", ex.Message, StringComparison.OrdinalIgnoreCase);
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

        Assert.True(SpanCoverage.SpanCoversColumn(span, line, startCol));
        Assert.True(SpanCoverage.SpanCoversColumn(span, line, endCol - 1));
        Assert.False(SpanCoverage.SpanCoversColumn(span, line, endCol));
        Assert.False(SpanCoverage.SpanCoversColumn(span, line, startCol - 1));
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
            DocumentEditableHelpers.ValidateDocumentIsEditable(document, workspace));

        Assert.Equal(ErrorCodes.DocumentNotEditable, ex.ErrorCode);
    }

    #endregion

    #region AllFiles

    private const string MixedExpressionFileA = """
        namespace TestApp;

        public class FileA
        {
            public int One() => 1;
            public int Two() => 2;
            public int Already() { return 3; }
            public int Multi()
            {
                var x = 1;
                return x;
            }
            public string Name { get => "Ada"; set => _ = value; }
        }
        """;

    private const string MixedExpressionFileB = """
        namespace TestApp;

        public class FileB
        {
            public int Value() => 4;
            public int Prop => 5;
        }
        """;

    private const string AlreadyBlockFileC = """
        namespace TestApp;

        public class FileC
        {
            public int Done() { return 6; }
        }
        """;

    [SkippableFact]
    public async Task Convert_AllFilesFalse_ConvertsOnlySpecifiedMember()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("FileA.cs", MixedExpressionFileA),
            ("FileB.cs", MixedExpressionFileB),
            ("FileC.cs", AlreadyBlockFileC));
        var operation = new ConvertToBlockBodyOperation(workspace.Context);
        var beforeB = await File.ReadAllTextAsync(workspace.SourcePaths["FileB.cs"]);
        var beforeC = await File.ReadAllTextAsync(workspace.SourcePaths["FileC.cs"]);

        var result = await operation.ExecuteAsync(new ConvertToBlockBodyParams
        {
            SourceFile = workspace.SourcePaths["FileA.cs"],
            AllFiles = false,
            MemberName = "One"
        });

        Assert.True(result.Success);
        Assert.False(result.Preview);
        var updatedA = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePaths["FileA.cs"]));
        AssertMethodIsBlockBodied(updatedA, "One");
        Assert.Contains("public int Two() => 2;", updatedA, StringComparison.Ordinal);
        Assert.Contains("public int Already() { return 3; }", updatedA, StringComparison.Ordinal);
        Assert.Contains("get =>", updatedA, StringComparison.Ordinal);
        Assert.Equal(beforeB, await File.ReadAllTextAsync(workspace.SourcePaths["FileB.cs"]));
        Assert.Equal(beforeC, await File.ReadAllTextAsync(workspace.SourcePaths["FileC.cs"]));
        Assert.Single(result.Changes!.FilesModified);
        Assert.Contains(result.Changes.FilesModified, p => PathEquals(p, workspace.SourcePaths["FileA.cs"]));
    }

    [SkippableFact]
    public async Task Convert_AllFilesTrue_ConvertsEligibleMembersAcrossFiles()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("FileA.cs", MixedExpressionFileA),
            ("FileB.cs", MixedExpressionFileB),
            ("FileC.cs", AlreadyBlockFileC));
        var operation = new ConvertToBlockBodyOperation(workspace.Context);
        var beforeC = await File.ReadAllTextAsync(workspace.SourcePaths["FileC.cs"]);

        var result = await operation.ExecuteAsync(new ConvertToBlockBodyParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        Assert.False(result.Preview);
        var updatedA = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePaths["FileA.cs"]));
        var updatedB = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePaths["FileB.cs"]));
        AssertMethodIsBlockBodied(updatedA, "One");
        AssertMethodIsBlockBodied(updatedA, "Two");
        Assert.Contains("public int Already() { return 3; }", updatedA, StringComparison.Ordinal);
        Assert.Contains("var x = 1;", updatedA, StringComparison.Ordinal);
        Assert.DoesNotContain("get =>", updatedA, StringComparison.Ordinal);
        Assert.DoesNotContain("set =>", updatedA, StringComparison.Ordinal);
        Assert.Contains("get", updatedA, StringComparison.Ordinal);
        Assert.Contains("return \"Ada\";", updatedA, StringComparison.Ordinal);
        AssertMethodIsBlockBodied(updatedB, "Value");
        AssertPropertyIsBlockBodied(updatedB, "Prop");
        Assert.Equal(beforeC, await File.ReadAllTextAsync(workspace.SourcePaths["FileC.cs"]));
        Assert.Equal(2, result.Changes!.FilesModified.Count);
        Assert.Contains(result.Changes.FilesModified, p => PathEquals(p, workspace.SourcePaths["FileA.cs"]));
        Assert.Contains(result.Changes.FilesModified, p => PathEquals(p, workspace.SourcePaths["FileB.cs"]));
        Assert.DoesNotContain(result.Changes.FilesModified, p => PathEquals(p, workspace.SourcePaths["FileC.cs"]));
    }

    [SkippableFact]
    public async Task Convert_AllFilesTrue_OptionalSourceFile_LimitsWalk()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("FileA.cs", MixedExpressionFileA),
            ("FileB.cs", MixedExpressionFileB),
            ("FileC.cs", AlreadyBlockFileC));
        var operation = new ConvertToBlockBodyOperation(workspace.Context);
        var beforeB = await File.ReadAllTextAsync(workspace.SourcePaths["FileB.cs"]);
        var beforeC = await File.ReadAllTextAsync(workspace.SourcePaths["FileC.cs"]);

        var result = await operation.ExecuteAsync(new ConvertToBlockBodyParams
        {
            AllFiles = true,
            SourceFile = workspace.SourcePaths["FileA.cs"]
        });

        Assert.True(result.Success);
        var updatedA = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePaths["FileA.cs"]));
        AssertMethodIsBlockBodied(updatedA, "One");
        AssertMethodIsBlockBodied(updatedA, "Two");
        Assert.Equal(beforeB, await File.ReadAllTextAsync(workspace.SourcePaths["FileB.cs"]));
        Assert.Equal(beforeC, await File.ReadAllTextAsync(workspace.SourcePaths["FileC.cs"]));
        Assert.Single(result.Changes!.FilesModified);
        Assert.Contains(result.Changes.FilesModified, p => PathEquals(p, workspace.SourcePaths["FileA.cs"]));
    }

    [SkippableFact]
    public async Task Convert_AllFilesTrue_OptionalSourceFile_MatchesIgnoreCase()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("FileA.cs", MixedExpressionFileA),
            ("FileB.cs", MixedExpressionFileB),
            ("FileC.cs", AlreadyBlockFileC));
        var operation = new ConvertToBlockBodyOperation(workspace.Context);
        var beforeB = await File.ReadAllTextAsync(workspace.SourcePaths["FileB.cs"]);
        var beforeC = await File.ReadAllTextAsync(workspace.SourcePaths["FileC.cs"]);
        var flipped = FlipPathCasing(workspace.SourcePaths["FileA.cs"]);

        if (File.Exists(flipped))
        {
            var result = await operation.ExecuteAsync(new ConvertToBlockBodyParams
            {
                AllFiles = true,
                SourceFile = flipped
            });

            Assert.True(result.Success);
            var updatedA = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePaths["FileA.cs"]));
            AssertMethodIsBlockBodied(updatedA, "One");
            AssertMethodIsBlockBodied(updatedA, "Two");
            Assert.Equal(beforeB, await File.ReadAllTextAsync(workspace.SourcePaths["FileB.cs"]));
            Assert.Equal(beforeC, await File.ReadAllTextAsync(workspace.SourcePaths["FileC.cs"]));
            Assert.Single(result.Changes!.FilesModified);
            Assert.Contains(result.Changes.FilesModified, p => PathEquals(p, workspace.SourcePaths["FileA.cs"]));
            return;
        }

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ConvertToBlockBodyParams
            {
                AllFiles = true,
                SourceFile = flipped
            }));

        Assert.Equal(ErrorCodes.SourceNotInWorkspace, ex.ErrorCode);
        Assert.Equal(beforeB, await File.ReadAllTextAsync(workspace.SourcePaths["FileB.cs"]));
        Assert.Equal(beforeC, await File.ReadAllTextAsync(workspace.SourcePaths["FileC.cs"]));
    }

    [SkippableFact]
    public async Task Convert_AllFilesTrue_OptionalSourceFile_OutsideWorkspace_Throws()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("FileA.cs", MixedExpressionFileA),
            ("FileB.cs", MixedExpressionFileB));
        var operation = new ConvertToBlockBodyOperation(workspace.Context);
        var outsideDir = Path.Combine(Path.GetTempPath(), "RoslynMcpConvertToBlockBody_Outside_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outsideDir);
        var outsidePath = Path.Combine(outsideDir, "Outside.cs");

        try
        {
            await File.WriteAllTextAsync(outsidePath, "class Outside { int Value() => 1; }");

            var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
                operation.ExecuteAsync(new ConvertToBlockBodyParams
                {
                    AllFiles = true,
                    SourceFile = outsidePath
                }));

            Assert.Equal(ErrorCodes.SourceNotInWorkspace, ex.ErrorCode);
        }
        finally
        {
            Directory.Delete(outsideDir, recursive: true);
        }
    }

    [SkippableFact]
    public async Task Convert_AllFilesTrue_OptionalSourceFile_ExactCase_PrefersSingleWorkspaceFile()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            [("FileA.cs", MixedExpressionFileA), ("filea.cs", MixedExpressionFileB)],
            explicitCompileItems: true);
        Skip.If(
            string.Equals(
                PathResolver.GetPathComparisonKey(workspace.SourcePaths["FileA.cs"]),
                PathResolver.GetPathComparisonKey(workspace.SourcePaths["filea.cs"]),
                StringComparison.Ordinal),
            "Volume does not preserve case-distinct paths.");
        var operation = new ConvertToBlockBodyOperation(workspace.Context);
        var beforeLower = await File.ReadAllTextAsync(workspace.SourcePaths["filea.cs"]);

        var result = await operation.ExecuteAsync(new ConvertToBlockBodyParams
        {
            AllFiles = true,
            SourceFile = workspace.SourcePaths["FileA.cs"]
        });

        Assert.True(result.Success);
        var updatedA = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePaths["FileA.cs"]));
        AssertMethodIsBlockBodied(updatedA, "One");
        AssertMethodIsBlockBodied(updatedA, "Two");
        Assert.Equal(beforeLower, await File.ReadAllTextAsync(workspace.SourcePaths["filea.cs"]));
        Assert.Single(result.Changes!.FilesModified);
        Assert.Contains(result.Changes.FilesModified, p => PathEquals(p, workspace.SourcePaths["FileA.cs"]));
    }

    [SkippableFact]
    public async Task Convert_AllFilesTrue_OptionalSourceFile_AmbiguousIgnoreCase_Throws()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            [("FileA.cs", MixedExpressionFileA), ("filea.cs", MixedExpressionFileB)],
            explicitCompileItems: true);
        Skip.If(
            string.Equals(
                PathResolver.GetPathComparisonKey(workspace.SourcePaths["FileA.cs"]),
                PathResolver.GetPathComparisonKey(workspace.SourcePaths["filea.cs"]),
                StringComparison.Ordinal),
            "Volume does not preserve case-distinct paths.");
        var operation = new ConvertToBlockBodyOperation(workspace.Context);
        var ambiguous = FlipPathCasing(workspace.SourcePaths["FileA.cs"]);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ConvertToBlockBodyParams
            {
                AllFiles = true,
                SourceFile = ambiguous
            }));

        Assert.Equal(ErrorCodes.SourceNotInWorkspace, ex.ErrorCode);
        // Shared filter is OrdinalIgnoreCase, so both case-distinct workspace
        // files match a flipped spelling whether or not File.Exists(ambiguous).
        Assert.Contains("exact file path casing", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task Convert_AllFilesTrue_CaseDistinctFiles_AreConvertedIndependently()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            [("FileA.cs", MixedExpressionFileA), ("filea.cs", MixedExpressionFileB)],
            explicitCompileItems: true);
        Skip.If(
            string.Equals(
                PathResolver.GetPathComparisonKey(workspace.SourcePaths["FileA.cs"]),
                PathResolver.GetPathComparisonKey(workspace.SourcePaths["filea.cs"]),
                StringComparison.Ordinal),
            "Volume does not preserve case-distinct paths.");
        var operation = new ConvertToBlockBodyOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ConvertToBlockBodyParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        var updatedUpper = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePaths["FileA.cs"]));
        var updatedLower = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePaths["filea.cs"]));
        AssertMethodIsBlockBodied(updatedUpper, "One");
        AssertMethodIsBlockBodied(updatedUpper, "Two");
        AssertMethodIsBlockBodied(updatedLower, "Value");
        AssertPropertyIsBlockBodied(updatedLower, "Prop");
        Assert.Equal(2, result.Changes!.FilesModified.Count);
        Assert.Contains(result.Changes.FilesModified, p => PathEquals(p, workspace.SourcePaths["FileA.cs"]));
        Assert.Contains(result.Changes.FilesModified, p => PathEquals(p, workspace.SourcePaths["filea.cs"]));
    }

    [SkippableFact]
    public async Task Convert_AllFilesTrue_EveryFileAlreadyBlock_SucceedsWithEmptyChanges()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("FileC.cs", AlreadyBlockFileC),
            ("FileC2.cs", AlreadyBlockFileC.Replace("FileC", "FileC2", StringComparison.Ordinal)));
        var operation = new ConvertToBlockBodyOperation(workspace.Context);
        var beforeA = await File.ReadAllTextAsync(workspace.SourcePaths["FileC.cs"]);
        var beforeB = await File.ReadAllTextAsync(workspace.SourcePaths["FileC2.cs"]);

        var result = await operation.ExecuteAsync(new ConvertToBlockBodyParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        Assert.False(result.Preview);
        Assert.NotNull(result.Changes);
        Assert.Empty(result.Changes.FilesModified);
        Assert.Equal(beforeA, await File.ReadAllTextAsync(workspace.SourcePaths["FileC.cs"]));
        Assert.Equal(beforeB, await File.ReadAllTextAsync(workspace.SourcePaths["FileC2.cs"]));
    }

    [SkippableFact]
    public async Task Convert_AllFilesFalse_WithoutSourceFile_MissingRequiredParam()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MixedExpressionFileA);
        var operation = new ConvertToBlockBodyOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ConvertToBlockBodyParams
            {
                AllFiles = false,
                MemberName = "One"
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [SkippableFact]
    public async Task Convert_AllFilesTrue_WithMemberName_Rejects()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MixedExpressionFileA);
        var operation = new ConvertToBlockBodyOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ConvertToBlockBodyParams
            {
                AllFiles = true,
                MemberName = "One"
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("memberName", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task Convert_AllFilesTrue_WithLine_Rejects()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MixedExpressionFileA);
        var operation = new ConvertToBlockBodyOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ConvertToBlockBodyParams
            {
                AllFiles = true,
                Line = 4
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("line", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task Convert_PreviewAllFiles_AggregatesChangedFilesAndWritesNothing()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("FileA.cs", MixedExpressionFileA),
            ("FileB.cs", MixedExpressionFileB),
            ("FileC.cs", AlreadyBlockFileC));
        var operation = new ConvertToBlockBodyOperation(workspace.Context);
        var beforeA = await File.ReadAllTextAsync(workspace.SourcePaths["FileA.cs"]);
        var beforeB = await File.ReadAllTextAsync(workspace.SourcePaths["FileB.cs"]);
        var beforeC = await File.ReadAllTextAsync(workspace.SourcePaths["FileC.cs"]);

        var result = await operation.ExecuteAsync(new ConvertToBlockBodyParams
        {
            AllFiles = true,
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.Equal(2, result.PendingChanges.Count);
        Assert.Contains(result.PendingChanges, c => PathEquals(c.File, workspace.SourcePaths["FileA.cs"]));
        Assert.Contains(result.PendingChanges, c => PathEquals(c.File, workspace.SourcePaths["FileB.cs"]));
        Assert.DoesNotContain(result.PendingChanges, c => PathEquals(c.File, workspace.SourcePaths["FileC.cs"]));
        Assert.Contains(result.PendingChanges, c =>
            c.Description.Contains("block body", StringComparison.OrdinalIgnoreCase) &&
            c.AfterSnippet != null &&
            c.AfterSnippet.Contains("return", StringComparison.Ordinal));
        Assert.Equal(beforeA, await File.ReadAllTextAsync(workspace.SourcePaths["FileA.cs"]));
        Assert.Equal(beforeB, await File.ReadAllTextAsync(workspace.SourcePaths["FileB.cs"]));
        Assert.Equal(beforeC, await File.ReadAllTextAsync(workspace.SourcePaths["FileC.cs"]));
    }

    [SkippableFact]
    public async Task Convert_AllFilesTrue_ConvertsNestedLocalFunctionInsideExpressionBody()
    {
        const string source = """
            namespace TestApp;

            public class Nested
            {
                public System.Func<int> Prop => () =>
                {
                    int Local() => 1;
                    return Local();
                };
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Nested.cs");
        var operation = new ConvertToBlockBodyOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ConvertToBlockBodyParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePaths["Nested.cs"]));
        AssertPropertyIsBlockBodied(updated, "Prop");
        var local = CSharpSyntaxTree.ParseText(updated).GetRoot()
            .DescendantNodes().OfType<LocalFunctionStatementSyntax>()
            .First(lf => lf.Identifier.Text == "Local");
        Assert.Null(local.ExpressionBody);
        Assert.NotNull(local.Body);
        Assert.Contains("return 1;", updated, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task Convert_AllFilesTrue_PreservesTrailingSemicolonComments()
    {
        const string source = """
            namespace TestApp;

            public class Comments
            {
                public int Value() => 1; // explanation
                public int Prop => 2; // property note
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Comments.cs");
        var operation = new ConvertToBlockBodyOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ConvertToBlockBodyParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePaths["Comments.cs"]));
        AssertMethodIsBlockBodied(updated, "Value");
        AssertPropertyIsBlockBodied(updated, "Prop");
        Assert.Contains("// explanation", updated, StringComparison.Ordinal);
        Assert.Contains("// property note", updated, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task Convert_AllFilesTrue_PreservesLeadingSemicolonComments()
    {
        const string source = """
            namespace TestApp;

            public class LeadingComments
            {
                public int Value() => 1
                    /* explanation */;
                public int Prop => 2
                    /* property note */;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "LeadingComments.cs");
        var operation = new ConvertToBlockBodyOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ConvertToBlockBodyParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePaths["LeadingComments.cs"]));
        AssertMethodIsBlockBodied(updated, "Value");
        AssertPropertyIsBlockBodied(updated, "Prop");
        Assert.Contains("/* explanation */", updated, StringComparison.Ordinal);
        Assert.Contains("/* property note */", updated, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task Convert_AllFilesTrue_PreservesArrowTokenComments()
    {
        const string source = """
            namespace TestApp;

            public class ArrowComments
            {
                public int Value() => /* rationale */ 1;
                public int Prop => /* property note */ 2;
                public int Acc
                {
                    get => /* getter note */ 3;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "ArrowComments.cs");
        var operation = new ConvertToBlockBodyOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ConvertToBlockBodyParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePaths["ArrowComments.cs"]));
        AssertMethodIsBlockBodied(updated, "Value");
        AssertPropertyIsBlockBodied(updated, "Prop");
        Assert.Contains("/* rationale */", updated, StringComparison.Ordinal);
        Assert.Contains("/* property note */", updated, StringComparison.Ordinal);
        Assert.Contains("/* getter note */", updated, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task Convert_AllFilesTrue_PreservesThrowKeywordComments()
    {
        const string source = """
            namespace TestApp;

            public class ThrowComments
            {
                public int Value() => throw /* reason */ new System.Exception();
                public int Prop => throw /* property reason */ new System.Exception();
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "ThrowComments.cs");
        var operation = new ConvertToBlockBodyOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ConvertToBlockBodyParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePaths["ThrowComments.cs"]));
        AssertMethodIsBlockBodied(updated, "Value");
        AssertPropertyIsBlockBodied(updated, "Prop");
        Assert.Contains("/* reason */", updated, StringComparison.Ordinal);
        Assert.Contains("/* property reason */", updated, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task Convert_AllFilesTrue_AsyncTaskAlias_ProducesExpressionStatementNotReturnAwait()
    {
        const string source = """
            using System.Threading.Tasks;
            using Work = System.Threading.Tasks.Task;
            using WorkValue = System.Threading.Tasks.ValueTask;

            namespace TestApp;

            public class AsyncAlias
            {
                public async Work Run() => await Delay();
                public async WorkValue RunValue() => await DelayValue();
                public void Host()
                {
                    async Work Nested() => await Delay();
                    _ = Nested();
                }
                private static Work Delay() => Task.CompletedTask;
                private static WorkValue DelayValue() => ValueTask.CompletedTask;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "AsyncAlias.cs");
        var operation = new ConvertToBlockBodyOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ConvertToBlockBodyParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePaths["AsyncAlias.cs"]));
        AssertMethodIsBlockBodied(updated, "Run");
        AssertMethodIsBlockBodied(updated, "RunValue");
        Assert.DoesNotContain("return await", updated, StringComparison.Ordinal);
        var root = CSharpSyntaxTree.ParseText(updated).GetRoot();
        var run = root.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .First(m => m.Identifier.Text == "Run");
        Assert.Single(run.Body!.Statements);
        Assert.IsType<ExpressionStatementSyntax>(run.Body.Statements[0]);
        var runValue = root.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .First(m => m.Identifier.Text == "RunValue");
        Assert.Single(runValue.Body!.Statements);
        Assert.IsType<ExpressionStatementSyntax>(runValue.Body.Statements[0]);
        var nested = root.DescendantNodes().OfType<LocalFunctionStatementSyntax>()
            .First(m => m.Identifier.Text == "Nested");
        Assert.Null(nested.ExpressionBody);
        Assert.NotNull(nested.Body);
        Assert.Single(nested.Body!.Statements);
        Assert.IsType<ExpressionStatementSyntax>(nested.Body.Statements[0]);
    }

    [SkippableFact]
    public async Task Convert_AllFilesTrue_SkipsCustomNonGenericAsyncTaskLike()
    {
        const string source = """
            using System;
            using System.Runtime.CompilerServices;
            using System.Threading.Tasks;

            namespace TestApp;

            [AsyncMethodBuilder(typeof(CustomTaskMethodBuilder))]
            public struct CustomTask
            {
                public CustomTaskAwaiter GetAwaiter() => default;
            }

            public struct CustomTaskAwaiter : INotifyCompletion
            {
                public bool IsCompleted => true;
                public void OnCompleted(Action continuation) { }
                public void GetResult() { }
            }

            public struct CustomTaskMethodBuilder
            {
                public static CustomTaskMethodBuilder Create() => default;
                public void Start<TStateMachine>(ref TStateMachine stateMachine)
                    where TStateMachine : IAsyncStateMachine => stateMachine.MoveNext();
                public void SetStateMachine(IAsyncStateMachine stateMachine) { }
                public void SetResult() { }
                public void SetException(Exception exception) { }
                public CustomTask Task => default;
                public void AwaitOnCompleted<TAwaiter, TStateMachine>(
                    ref TAwaiter awaiter, ref TStateMachine stateMachine)
                    where TAwaiter : INotifyCompletion
                    where TStateMachine : IAsyncStateMachine { }
                public void AwaitUnsafeOnCompleted<TAwaiter, TStateMachine>(
                    ref TAwaiter awaiter, ref TStateMachine stateMachine)
                    where TAwaiter : ICriticalNotifyCompletion
                    where TStateMachine : IAsyncStateMachine { }
            }

            public class CustomAsync
            {
                public async CustomTask Run() => await Delay();
                public async Task Safe() => await Task.CompletedTask;
                public void Host()
                {
                    async CustomTask Nested() => await Delay();
                    _ = Nested();
                }
                private static CustomTask Delay() => default;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "CustomAsync.cs");
        var operation = new ConvertToBlockBodyOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ConvertToBlockBodyParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePaths["CustomAsync.cs"]));
        var root = CSharpSyntaxTree.ParseText(updated).GetRoot();
        var run = root.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .First(m => m.Identifier.Text == "Run");
        Assert.NotNull(run.ExpressionBody);
        Assert.Null(run.Body);
        Assert.DoesNotContain("return await", updated, StringComparison.Ordinal);
        var nested = root.DescendantNodes().OfType<LocalFunctionStatementSyntax>()
            .First(m => m.Identifier.Text == "Nested");
        Assert.NotNull(nested.ExpressionBody);
        Assert.Null(nested.Body);
        var safe = root.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .First(m => m.Identifier.Text == "Safe");
        Assert.Null(safe.ExpressionBody);
        Assert.NotNull(safe.Body);
        Assert.IsType<ExpressionStatementSyntax>(Assert.Single(safe.Body!.Statements));
    }

    [SkippableFact]
    public async Task Convert_AllFilesTrue_SkipsCustomGenericAsyncTaskLikeWithParameterlessSetResult()
    {
        const string source = """
            using System;
            using System.Runtime.CompilerServices;
            using System.Threading.Tasks;

            namespace TestApp;

            [AsyncMethodBuilder(typeof(CustomTaskMethodBuilder<>))]
            public struct CustomTask<T>
            {
                public CustomTaskAwaiter<T> GetAwaiter() => default;
            }

            public struct CustomTaskAwaiter<T> : INotifyCompletion
            {
                public bool IsCompleted => true;
                public void OnCompleted(Action continuation) { }
                public void GetResult() { }
            }

            public struct CustomTaskMethodBuilder<T>
            {
                public static CustomTaskMethodBuilder<T> Create() => default;
                public void Start<TStateMachine>(ref TStateMachine stateMachine)
                    where TStateMachine : IAsyncStateMachine => stateMachine.MoveNext();
                public void SetStateMachine(IAsyncStateMachine stateMachine) { }
                public void SetResult() { }
                public void SetException(Exception exception) { }
                public CustomTask<T> Task => default;
                public void AwaitOnCompleted<TAwaiter, TStateMachine>(
                    ref TAwaiter awaiter, ref TStateMachine stateMachine)
                    where TAwaiter : INotifyCompletion
                    where TStateMachine : IAsyncStateMachine { }
                public void AwaitUnsafeOnCompleted<TAwaiter, TStateMachine>(
                    ref TAwaiter awaiter, ref TStateMachine stateMachine)
                    where TAwaiter : ICriticalNotifyCompletion
                    where TStateMachine : IAsyncStateMachine { }
            }

            public class CustomGenericAsync
            {
                public async CustomTask<int> Run() => await Delay();
                public async Task<int> Safe() => await Task.FromResult(1);
                public void Host()
                {
                    async CustomTask<int> Nested() => await Delay();
                    _ = Nested();
                }
                private static CustomTask<int> Delay() => default;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "CustomGenericAsync.cs");
        var operation = new ConvertToBlockBodyOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ConvertToBlockBodyParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePaths["CustomGenericAsync.cs"]));
        var root = CSharpSyntaxTree.ParseText(updated).GetRoot();
        var run = root.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .First(m => m.Identifier.Text == "Run");
        Assert.NotNull(run.ExpressionBody);
        Assert.Null(run.Body);
        Assert.DoesNotContain("return await", run.ToFullString(), StringComparison.Ordinal);
        var nested = root.DescendantNodes().OfType<LocalFunctionStatementSyntax>()
            .First(m => m.Identifier.Text == "Nested");
        Assert.NotNull(nested.ExpressionBody);
        Assert.Null(nested.Body);
        Assert.DoesNotContain("return await", nested.ToFullString(), StringComparison.Ordinal);
        var safe = root.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .First(m => m.Identifier.Text == "Safe");
        Assert.Null(safe.ExpressionBody);
        Assert.NotNull(safe.Body);
        Assert.IsType<ReturnStatementSyntax>(Assert.Single(safe.Body!.Statements));
    }

    [SkippableFact]
    public async Task Convert_AllFilesTrue_LinkedDocumentViewsThatRewriteDifferently_Throws()
    {
        const string sharedSource = """
            namespace TestApp;

            public partial class Shared
            {
                public async Work Run() => await Delay();
            }
            """;

        await using var workspace = await TempWorkspace.CreateWithLinkedProjectsAsync(sharedSource);
        var linkedDocuments = workspace.Context.Solution.Projects
            .SelectMany(p => p.Documents)
            .Where(d => PathEquals(d.FilePath!, workspace.SourcePaths["Shared.cs"]))
            .OrderBy(d => d.Project.Name, StringComparer.Ordinal)
            .ToList();
        Assert.Equal(2, linkedDocuments.Count);

        var rewrittenRuns = new List<string>();
        foreach (var document in linkedDocuments)
        {
            var root = await document.GetSyntaxRootAsync();
            var model = await document.GetSemanticModelAsync();
            var run = root!.DescendantNodes()
                .OfType<MethodDeclarationSyntax>()
                .First(m => m.Identifier.Text == "Run");
            Assert.True(ConvertToBlockBodyOperation.TryConvert(
                run,
                out var convertedRun,
                out _,
                out _,
                model));
            rewrittenRuns.Add(convertedRun.NormalizeWhitespace().ToFullString());
        }

        Assert.Equal(2, rewrittenRuns.Distinct(StringComparer.Ordinal).Count());
        var operation = new ConvertToBlockBodyOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ConvertToBlockBodyParams
            {
                AllFiles = true
            }));

        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
        Assert.Contains("Linked workspace documents", ex.Message, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task Convert_SingleFile_LinkedDocumentViewsThatRewriteDifferently_Throws()
    {
        const string sharedSource = """
            namespace TestApp;

            public partial class Shared
            {
                public async Work Run() => await Delay();
            }
            """;

        await using var workspace = await TempWorkspace.CreateWithLinkedProjectsAsync(sharedSource);
        var operation = new ConvertToBlockBodyOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ConvertToBlockBodyParams
            {
                SourceFile = workspace.SourcePaths["Shared.cs"],
                MemberName = "Run"
            }));

        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
        Assert.Contains("Linked workspace documents", ex.Message, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task Convert_AllFilesTrue_LinkedDocumentViewsThatRewriteIdentically_ReportSingleModifiedFile()
    {
        const string sharedSource = """
            namespace TestApp;

            public partial class Shared
            {
                public async Work Run() => await Delay();
            }
            """;
        const string anchorSource = """
            using System.Threading.Tasks;
            global using Work = System.Threading.Tasks.Task;

            namespace TestApp;

            public partial class Shared
            {
                private static Work Delay()
                {
                    return Task.CompletedTask;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateWithLinkedProjectsAsync(sharedSource, anchorSource, anchorSource);
        var linkedDocuments = workspace.Context.Solution.Projects
            .SelectMany(p => p.Documents)
            .Where(d => PathEquals(d.FilePath!, workspace.SourcePaths["Shared.cs"]))
            .ToList();
        Assert.Equal(2, linkedDocuments.Count);

        var operation = new ConvertToBlockBodyOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ConvertToBlockBodyParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        Assert.Single(result.Changes!.FilesModified);
        Assert.Contains(result.Changes.FilesModified, p => PathEquals(p, workspace.SourcePaths["Shared.cs"]));
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePaths["Shared.cs"]));
        AssertMethodIsBlockBodied(updated, "Run");
    }

    [SkippableFact]
    public async Task Convert_SingleFile_LinkedDocumentViewsThatRewriteIdentically_ReportSingleModifiedFile()
    {
        const string sharedSource = """
            namespace TestApp;

            public partial class Shared
            {
                public async Work Run() => await Delay();
            }
            """;
        const string anchorSource = """
            using System.Threading.Tasks;
            global using Work = System.Threading.Tasks.Task;

            namespace TestApp;

            public partial class Shared
            {
                private static Work Delay()
                {
                    return Task.CompletedTask;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateWithLinkedProjectsAsync(sharedSource, anchorSource, anchorSource);
        var operation = new ConvertToBlockBodyOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ConvertToBlockBodyParams
        {
            SourceFile = workspace.SourcePaths["Shared.cs"],
            MemberName = "Run"
        });

        Assert.True(result.Success);
        Assert.Single(result.Changes!.FilesModified);
        Assert.Contains(result.Changes.FilesModified, p => PathEquals(p, workspace.SourcePaths["Shared.cs"]));
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePaths["Shared.cs"]));
        AssertMethodIsBlockBodied(updated, "Run");
    }

    [SkippableFact]
    public async Task Convert_AllFilesTrue_SkipsPreprocessorConditionalExpressionBodies()
    {
        const string source = """
            namespace TestApp;

            public class ConditionalBodies
            {
                public int Safe() => 1;

                public int Conditional()
            #if DEBUG
                    => 2;
            #else
                    => 3;
            #endif
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "ConditionalBodies.cs");
        var operation = new ConvertToBlockBodyOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ConvertToBlockBodyParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePaths["ConditionalBodies.cs"]));
        AssertMethodIsBlockBodied(updated, "Safe");
        Assert.Contains("#if DEBUG", updated, StringComparison.Ordinal);
        Assert.Contains("#else", updated, StringComparison.Ordinal);
        Assert.Contains("#endif", updated, StringComparison.Ordinal);
        var conditional = CSharpSyntaxTree.ParseText(
                updated,
                CSharpParseOptions.Default.WithPreprocessorSymbols("DEBUG"))
            .GetRoot()
            .DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .First(m => m.Identifier.Text == "Conditional");
        Assert.NotNull(conditional.ExpressionBody);
        Assert.Null(conditional.Body);
    }

    #endregion

    #region Helpers

    private static string AbsoluteTestPath() =>
        Path.Combine(Path.GetTempPath(), "RoslynMcpConvertToBlockBodyMissing.cs");

    private static bool PathEquals(string left, string right) =>
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

    private static string FlipPathCasing(string path)
    {
        var chars = path.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (char.IsLetter(chars[i]))
                chars[i] = char.IsUpper(chars[i]) ? char.ToLowerInvariant(chars[i]) : char.ToUpperInvariant(chars[i]);
        }

        return new string(chars);
    }

    private static void AssertMethodIsBlockBodied(string source, string name)
    {
        var method = CSharpSyntaxTree.ParseText(source).GetRoot()
            .DescendantNodes().OfType<MethodDeclarationSyntax>()
            .First(m => m.Identifier.Text == name);
        Assert.Null(method.ExpressionBody);
        Assert.NotNull(method.Body);
    }

    private static void AssertPropertyIsBlockBodied(string source, string name)
    {
        var property = CSharpSyntaxTree.ParseText(source).GetRoot()
            .DescendantNodes().OfType<PropertyDeclarationSyntax>()
            .First(p => p.Identifier.Text == name);
        Assert.Null(property.ExpressionBody);
        Assert.NotNull(property.AccessorList);
        Assert.Contains(property.AccessorList.Accessors, a => a.Body != null);
    }


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
        public required Dictionary<string, string> SourcePaths { get; init; }
        public required WorkspaceContext Context { get; init; }

        public static Task<TempWorkspace> CreateAsync(string source, string fileName = "Types.cs") =>
            CreateWithFilesAsync([(fileName, source)]);

        public static Task<TempWorkspace> CreateWithFilesAsync(params (string FileName, string Source)[] files) =>
            CreateWithFilesAsync(files, explicitCompileItems: false);

        public static async Task<TempWorkspace> CreateWithFilesAsync(
            IReadOnlyList<(string FileName, string Source)> files,
            bool explicitCompileItems)
        {
            Skip.IfNot(ModuleInitializer.MsBuildAvailable, ModuleInitializer.MsBuildError ?? "MSBuild not available");

            var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpConvertToBlockBody_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);

            var projectPath = Path.Combine(directory, "TestApp.csproj");
            var sourcePaths = new Dictionary<string, string>(StringComparer.Ordinal);

            var compileItems = explicitCompileItems
                ? string.Join(Environment.NewLine, files.Select(f => $"    <Compile Include=\"{f.FileName}\" />"))
                : string.Empty;

            // Pin authored sources so generated AssemblyInfo / TFM attributes
            // are not hit by the allFiles .cs document walk.
            await File.WriteAllTextAsync(projectPath, $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net9.0</TargetFramework>
                    <Nullable>enable</Nullable>
                    <GenerateAssemblyInfo>false</GenerateAssemblyInfo>
                    <GenerateTargetFrameworkAttribute>false</GenerateTargetFrameworkAttribute>
                {(explicitCompileItems ? "    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>" : string.Empty)}
                  </PropertyGroup>
                {(explicitCompileItems ? $"  <ItemGroup>{Environment.NewLine}{compileItems}{Environment.NewLine}  </ItemGroup>" : string.Empty)}
                </Project>
                """);

            foreach (var (fileName, source) in files)
            {
                var sourcePath = Path.Combine(directory, fileName);
                await File.WriteAllTextAsync(sourcePath, source);
                sourcePaths[fileName] = sourcePath;
            }

            try
            {
                var provider = new MSBuildWorkspaceProvider();
                var context = await provider.CreateContextAsync(projectPath);
                foreach (var sourcePath in sourcePaths.Values)
                {
                    if (context.GetDocumentByPath(sourcePath) == null)
                    {
                        context.Dispose();
                        throw new InvalidOperationException($"Workspace loaded but did not include {sourcePath}.");
                    }
                }

                return new TempWorkspace
                {
                    DirectoryPath = directory,
                    ProjectPath = projectPath,
                    SourcePath = sourcePaths.Values.First(),
                    SourcePaths = sourcePaths,
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

        public static Task<TempWorkspace> CreateWithLinkedProjectsAsync(string sharedSource) =>
            CreateWithLinkedProjectsAsync(
                sharedSource,
                """
                using System.Threading.Tasks;
                global using Work = System.Threading.Tasks.Task;

                namespace TestApp;

                public partial class Shared
                {
                    private static Work Delay() => Task.CompletedTask;
                }
                """,
                """
                using System.Threading.Tasks;
                global using Work = System.Threading.Tasks.Task<int>;

                namespace TestApp;

                public partial class Shared
                {
                    private static Work Delay() => Task.FromResult(1);
                }
                """);

        public static async Task<TempWorkspace> CreateWithLinkedProjectsAsync(
            string sharedSource,
            string anchorASource,
            string anchorBSource)
        {
            Skip.IfNot(ModuleInitializer.MsBuildAvailable, ModuleInitializer.MsBuildError ?? "MSBuild not available");

            var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpConvertToBlockBodyLinked_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);

            var solutionPath = Path.Combine(directory, "TestApp.sln");
            var sharedPath = Path.Combine(directory, "Shared.cs");
            var rootProjectPath = Path.Combine(directory, "ProjectA.csproj");
            var referencedProjectPath = Path.Combine(directory, "ProjectB.csproj");
            var anchorAPath = Path.Combine(directory, "AnchorA.cs");
            var anchorBPath = Path.Combine(directory, "AnchorB.cs");
            var projectTypeGuid = "{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}";
            var projectAGuid = Guid.NewGuid().ToString("B").ToUpperInvariant();
            var projectBGuid = Guid.NewGuid().ToString("B").ToUpperInvariant();

            await File.WriteAllTextAsync(sharedPath, sharedSource);
            await File.WriteAllTextAsync(anchorAPath, anchorASource);
            await File.WriteAllTextAsync(anchorBPath, anchorBSource);
            await File.WriteAllTextAsync(solutionPath, $$"""
                Microsoft Visual Studio Solution File, Format Version 12.00
                # Visual Studio Version 17
                VisualStudioVersion = 17.0.31903.59
                MinimumVisualStudioVersion = 10.0.40219.1
                Project("{{projectTypeGuid}}") = "ProjectA", "ProjectA.csproj", "{{projectAGuid}}"
                EndProject
                Project("{{projectTypeGuid}}") = "ProjectB", "ProjectB.csproj", "{{projectBGuid}}"
                EndProject
                Global
                	GlobalSection(SolutionConfigurationPlatforms) = preSolution
                		Debug|Any CPU = Debug|Any CPU
                		Release|Any CPU = Release|Any CPU
                	EndGlobalSection
                	GlobalSection(ProjectConfigurationPlatforms) = postSolution
                		{{projectAGuid}}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
                		{{projectAGuid}}.Debug|Any CPU.Build.0 = Debug|Any CPU
                		{{projectAGuid}}.Release|Any CPU.ActiveCfg = Release|Any CPU
                		{{projectAGuid}}.Release|Any CPU.Build.0 = Release|Any CPU
                		{{projectBGuid}}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
                		{{projectBGuid}}.Debug|Any CPU.Build.0 = Debug|Any CPU
                		{{projectBGuid}}.Release|Any CPU.ActiveCfg = Release|Any CPU
                		{{projectBGuid}}.Release|Any CPU.Build.0 = Release|Any CPU
                	EndGlobalSection
                EndGlobal
                """);

            await File.WriteAllTextAsync(rootProjectPath, $$"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net9.0</TargetFramework>
                    <Nullable>enable</Nullable>
                    <GenerateAssemblyInfo>false</GenerateAssemblyInfo>
                    <GenerateTargetFrameworkAttribute>false</GenerateTargetFrameworkAttribute>
                    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
                  </PropertyGroup>
                  <ItemGroup>
                    <Compile Include="AnchorA.cs" />
                    <Compile Include="Shared.cs" Link="Shared.cs" />
                  </ItemGroup>
                </Project>
                """);

            await File.WriteAllTextAsync(referencedProjectPath, $$"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net9.0</TargetFramework>
                    <Nullable>enable</Nullable>
                    <GenerateAssemblyInfo>false</GenerateAssemblyInfo>
                    <GenerateTargetFrameworkAttribute>false</GenerateTargetFrameworkAttribute>
                    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
                    <DefineConstants>$(DefineConstants);DEBUG</DefineConstants>
                  </PropertyGroup>
                  <ItemGroup>
                    <Compile Include="AnchorB.cs" />
                    <Compile Include="Shared.cs" Link="Shared.cs" />
                  </ItemGroup>
                </Project>
                """);

            var sourcePaths = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Shared.cs"] = sharedPath,
                ["AnchorA.cs"] = anchorAPath,
                ["AnchorB.cs"] = anchorBPath
            };

            try
            {
                var provider = new MSBuildWorkspaceProvider();
                var context = await provider.CreateContextAsync(solutionPath);
                foreach (var sourcePath in sourcePaths.Values)
                {
                    if (context.GetDocumentByPath(sourcePath) == null)
                    {
                        context.Dispose();
                        throw new InvalidOperationException($"Workspace loaded but did not include {sourcePath}.");
                    }
                }

                return new TempWorkspace
                {
                    DirectoryPath = directory,
                    ProjectPath = rootProjectPath,
                    SourcePath = sharedPath,
                    SourcePaths = sourcePaths,
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

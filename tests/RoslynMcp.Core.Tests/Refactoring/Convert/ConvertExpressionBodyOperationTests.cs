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

    [Fact]
    public void Validate_AllFilesFalse_WithoutSourceFile_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ConvertExpressionBodyOperation.Validate(new ConvertExpressionBodyParams
            {
                AllFiles = false,
                Direction = "ToExpressionBody"
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("sourceFile", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_AllFilesTrue_WithoutSourceFile_DoesNotThrow()
    {
        ConvertExpressionBodyOperation.Validate(new ConvertExpressionBodyParams
        {
            AllFiles = true,
            Direction = "ToBlockBody"
        });
    }

    [Fact]
    public void Validate_AllFilesTrue_WithoutDirection_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ConvertExpressionBodyOperation.Validate(new ConvertExpressionBodyParams
            {
                AllFiles = true,
                Direction = ""
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("direction", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_AllFilesTrue_InvalidDirection_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ConvertExpressionBodyOperation.Validate(new ConvertExpressionBodyParams
            {
                AllFiles = true,
                Direction = "ToAutoProperty"
            }));

        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
    }

    [Fact]
    public void Validate_AllFilesTrue_WithMemberName_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ConvertExpressionBodyOperation.Validate(new ConvertExpressionBodyParams
            {
                AllFiles = true,
                MemberName = "Foo",
                Direction = "ToExpressionBody"
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("allFiles", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("memberName", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_AllFilesTrue_WithLine_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ConvertExpressionBodyOperation.Validate(new ConvertExpressionBodyParams
            {
                AllFiles = true,
                Line = 4,
                Direction = "ToExpressionBody"
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("allFiles", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("line", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_AllFilesTrue_WithColumn_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ConvertExpressionBodyOperation.Validate(new ConvertExpressionBodyParams
            {
                AllFiles = true,
                Column = 1,
                Direction = "ToBlockBody"
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("allFiles", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("column", ex.Message, StringComparison.OrdinalIgnoreCase);
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

    #region AllFiles

    private const string MixedBlockFileA = """
        namespace TestApp;

        public class FileA
        {
            public int One() { return 1; }
            public int Two() { return 2; }
            public int Already() => 3;
            public int Multi()
            {
                var x = 1;
                return x;
            }
            public int this[int i] { get { return i; } }
        }
        """;

    private const string MixedBlockFileB = """
        namespace TestApp;

        public class FileB
        {
            public int Value() { return 4; }
            public int Prop { get { return 5; } }
        }
        """;

    private const string AlreadyExpressionFileC = """
        namespace TestApp;

        public class FileC
        {
            public int Done() => 6;
        }
        """;

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
            public int this[int i] => i;
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
            ("FileA.cs", MixedBlockFileA),
            ("FileB.cs", MixedBlockFileB),
            ("FileC.cs", AlreadyExpressionFileC));
        var operation = new ConvertExpressionBodyOperation(workspace.Context);
        var beforeB = await File.ReadAllTextAsync(workspace.SourcePaths["FileB.cs"]);
        var beforeC = await File.ReadAllTextAsync(workspace.SourcePaths["FileC.cs"]);

        var result = await operation.ExecuteAsync(new ConvertExpressionBodyParams
        {
            SourceFile = workspace.SourcePaths["FileA.cs"],
            AllFiles = false,
            MemberName = "One",
            Direction = "ToExpressionBody"
        });

        Assert.True(result.Success);
        Assert.False(result.Preview);
        var updatedA = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePaths["FileA.cs"]));
        Assert.Contains("One()", updatedA, StringComparison.Ordinal);
        Assert.Contains("=>", updatedA, StringComparison.Ordinal);
        Assert.Contains("public int Two() { return 2; }", updatedA, StringComparison.Ordinal);
        Assert.Contains("public int Already() => 3;", updatedA, StringComparison.Ordinal);
        Assert.Contains("var x = 1;", updatedA, StringComparison.Ordinal);
        Assert.Contains("this[int i]", updatedA, StringComparison.Ordinal);
        Assert.Equal(beforeB, await File.ReadAllTextAsync(workspace.SourcePaths["FileB.cs"]));
        Assert.Equal(beforeC, await File.ReadAllTextAsync(workspace.SourcePaths["FileC.cs"]));
        Assert.Single(result.Changes!.FilesModified);
        Assert.Contains(result.Changes.FilesModified, p => PathEquals(p, workspace.SourcePaths["FileA.cs"]));
    }

    [SkippableFact]
    public async Task Convert_AllFilesTrue_ToExpressionBody_ConvertsEligibleMembersAcrossFiles()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("FileA.cs", MixedBlockFileA),
            ("FileB.cs", MixedBlockFileB),
            ("FileC.cs", AlreadyExpressionFileC));
        var operation = new ConvertExpressionBodyOperation(workspace.Context);
        var beforeC = await File.ReadAllTextAsync(workspace.SourcePaths["FileC.cs"]);

        var result = await operation.ExecuteAsync(new ConvertExpressionBodyParams
        {
            AllFiles = true,
            Direction = "ToExpressionBody"
        });

        Assert.True(result.Success);
        Assert.False(result.Preview);
        var updatedA = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePaths["FileA.cs"]));
        var updatedB = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePaths["FileB.cs"]));
        AssertMethodIsExpressionBodied(updatedA, "One");
        AssertMethodIsExpressionBodied(updatedA, "Two");
        Assert.Contains("public int Already() => 3;", updatedA, StringComparison.Ordinal);
        Assert.Contains("var x = 1;", updatedA, StringComparison.Ordinal);
        Assert.Contains("return x;", updatedA, StringComparison.Ordinal);
        Assert.Contains("this[int i]", updatedA, StringComparison.Ordinal);
        Assert.Contains("get { return i; }", updatedA, StringComparison.Ordinal);
        AssertMethodIsExpressionBodied(updatedB, "Value");
        AssertPropertyIsExpressionBodied(updatedB, "Prop");
        Assert.Equal(beforeC, await File.ReadAllTextAsync(workspace.SourcePaths["FileC.cs"]));
        Assert.Equal(2, result.Changes!.FilesModified.Count);
        Assert.Contains(result.Changes.FilesModified, p => PathEquals(p, workspace.SourcePaths["FileA.cs"]));
        Assert.Contains(result.Changes.FilesModified, p => PathEquals(p, workspace.SourcePaths["FileB.cs"]));
        Assert.DoesNotContain(result.Changes.FilesModified, p => PathEquals(p, workspace.SourcePaths["FileC.cs"]));
        await AssertCompilesAsync(workspace);
    }

    [SkippableFact]
    public async Task Convert_AllFilesTrue_ToBlockBody_ConvertsEligibleMembersAcrossFiles()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("FileA.cs", MixedExpressionFileA),
            ("FileB.cs", MixedExpressionFileB),
            ("FileC.cs", AlreadyBlockFileC));
        var operation = new ConvertExpressionBodyOperation(workspace.Context);
        var beforeC = await File.ReadAllTextAsync(workspace.SourcePaths["FileC.cs"]);

        var result = await operation.ExecuteAsync(new ConvertExpressionBodyParams
        {
            AllFiles = true,
            Direction = "ToBlockBody"
        });

        Assert.True(result.Success);
        Assert.False(result.Preview);
        var updatedA = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePaths["FileA.cs"]));
        var updatedB = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePaths["FileB.cs"]));
        AssertMethodIsBlockBodied(updatedA, "One");
        AssertMethodIsBlockBodied(updatedA, "Two");
        Assert.Contains("public int Already() { return 3; }", updatedA, StringComparison.Ordinal);
        Assert.Contains("var x = 1;", updatedA, StringComparison.Ordinal);
        Assert.Contains("this[int i] => i;", updatedA, StringComparison.Ordinal);
        AssertMethodIsBlockBodied(updatedB, "Value");
        AssertPropertyIsBlockBodied(updatedB, "Prop");
        Assert.Equal(beforeC, await File.ReadAllTextAsync(workspace.SourcePaths["FileC.cs"]));
        Assert.Equal(2, result.Changes!.FilesModified.Count);
        Assert.Contains(result.Changes.FilesModified, p => PathEquals(p, workspace.SourcePaths["FileA.cs"]));
        Assert.Contains(result.Changes.FilesModified, p => PathEquals(p, workspace.SourcePaths["FileB.cs"]));
        Assert.DoesNotContain(result.Changes.FilesModified, p => PathEquals(p, workspace.SourcePaths["FileC.cs"]));
        await AssertCompilesAsync(workspace);
    }

    [SkippableFact]
    public async Task Convert_AllFilesTrue_EveryFileAlreadyInTargetForm_SucceedsWithEmptyChanges()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("FileC.cs", AlreadyExpressionFileC),
            ("FileC2.cs", AlreadyExpressionFileC.Replace("FileC", "FileC2", StringComparison.Ordinal)));
        var operation = new ConvertExpressionBodyOperation(workspace.Context);
        var beforeA = await File.ReadAllTextAsync(workspace.SourcePaths["FileC.cs"]);
        var beforeB = await File.ReadAllTextAsync(workspace.SourcePaths["FileC2.cs"]);

        var result = await operation.ExecuteAsync(new ConvertExpressionBodyParams
        {
            AllFiles = true,
            Direction = "ToExpressionBody"
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
        await using var workspace = await TempWorkspace.CreateAsync(SingleBlockMethodSource);
        var operation = new ConvertExpressionBodyOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ConvertExpressionBodyParams
            {
                AllFiles = false,
                Direction = "ToExpressionBody"
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [SkippableFact]
    public async Task Convert_AllFilesTrue_WithMemberName_Rejects()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SingleBlockMethodSource);
        var operation = new ConvertExpressionBodyOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ConvertExpressionBodyParams
            {
                AllFiles = true,
                MemberName = "Add",
                Direction = "ToExpressionBody"
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("memberName", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task Convert_AllFilesTrue_WithLine_Rejects()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SingleBlockMethodSource);
        var operation = new ConvertExpressionBodyOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ConvertExpressionBodyParams
            {
                AllFiles = true,
                Line = 4,
                Direction = "ToExpressionBody"
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("line", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task Convert_PreviewAllFiles_AggregatesChangedFilesAndWritesNothing()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("FileA.cs", MixedBlockFileA),
            ("FileB.cs", MixedBlockFileB),
            ("FileC.cs", AlreadyExpressionFileC));
        var operation = new ConvertExpressionBodyOperation(workspace.Context);
        var beforeA = await File.ReadAllTextAsync(workspace.SourcePaths["FileA.cs"]);
        var beforeB = await File.ReadAllTextAsync(workspace.SourcePaths["FileB.cs"]);
        var beforeC = await File.ReadAllTextAsync(workspace.SourcePaths["FileC.cs"]);

        var result = await operation.ExecuteAsync(new ConvertExpressionBodyParams
        {
            AllFiles = true,
            Direction = "ToExpressionBody",
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
            c.Description.Contains("ToExpressionBody", StringComparison.Ordinal) &&
            c.AfterSnippet != null &&
            c.AfterSnippet.Contains("=>", StringComparison.Ordinal));
        Assert.Equal(beforeA, await File.ReadAllTextAsync(workspace.SourcePaths["FileA.cs"]));
        Assert.Equal(beforeB, await File.ReadAllTextAsync(workspace.SourcePaths["FileB.cs"]));
        Assert.Equal(beforeC, await File.ReadAllTextAsync(workspace.SourcePaths["FileC.cs"]));
    }

    [SkippableFact]
    public async Task Convert_AllFilesTrue_ToBlockBody_AsyncTask_ProducesExpressionStatementNotReturnAwait()
    {
        const string source = """
            using System.Threading.Tasks;

            namespace TestApp;

            public class AsyncWork
            {
                public async Task Run() => await Work();
                private static Task Work() => Task.CompletedTask;
            }
            """;

        await using var workspace = await TempWorkspace.CreateWithFilesAsync(("AsyncWork.cs", source));
        var operation = new ConvertExpressionBodyOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ConvertExpressionBodyParams
        {
            AllFiles = true,
            Direction = "ToBlockBody"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePaths["AsyncWork.cs"]));
        AssertMethodIsBlockBodied(updated, "Run");
        Assert.DoesNotContain("return await", updated, StringComparison.Ordinal);
        var run = CSharpSyntaxTree.ParseText(updated).GetRoot()
            .DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .First(m => m.Identifier.Text == "Run");
        Assert.Single(run.Body!.Statements);
        Assert.IsType<ExpressionStatementSyntax>(run.Body.Statements[0]);
        Assert.Contains("await Work()", run.Body.Statements[0].ToString(), StringComparison.Ordinal);
        await AssertCompilesAsync(workspace);
    }

    [SkippableFact]
    public async Task Convert_AllFilesTrue_ToBlockBody_ThrowBodiedMethod_EmitsThrowStatement()
    {
        const string source = """
            using System;

            namespace TestApp;

            public class Throws
            {
                public int Fail() => throw new Exception();
            }
            """;

        await using var workspace = await TempWorkspace.CreateWithFilesAsync(("Throws.cs", source));
        var operation = new ConvertExpressionBodyOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ConvertExpressionBodyParams
        {
            AllFiles = true,
            Direction = "ToBlockBody"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePaths["Throws.cs"]));
        Assert.DoesNotContain("return throw", updated, StringComparison.Ordinal);
        var method = CSharpSyntaxTree.ParseText(updated).GetRoot()
            .DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .First(m => m.Identifier.Text == "Fail");
        Assert.Null(method.ExpressionBody);
        Assert.Single(method.Body!.Statements);
        Assert.IsType<ThrowStatementSyntax>(method.Body.Statements[0]);
        await AssertCompilesAsync(workspace);
    }

    [SkippableFact]
    public async Task Convert_AllFilesTrue_ToBlockBody_ThrowBodiedProperty_EmitsThrowStatement()
    {
        const string source = """
            using System;

            namespace TestApp;

            public class Throws
            {
                public int Boom => throw new InvalidOperationException();
            }
            """;

        await using var workspace = await TempWorkspace.CreateWithFilesAsync(("Throws.cs", source));
        var operation = new ConvertExpressionBodyOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ConvertExpressionBodyParams
        {
            AllFiles = true,
            Direction = "ToBlockBody"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePaths["Throws.cs"]));
        Assert.DoesNotContain("return throw", updated, StringComparison.Ordinal);
        var property = CSharpSyntaxTree.ParseText(updated).GetRoot()
            .DescendantNodes()
            .OfType<PropertyDeclarationSyntax>()
            .First(p => p.Identifier.Text == "Boom");
        Assert.Null(property.ExpressionBody);
        var getter = Assert.Single(property.AccessorList!.Accessors);
        Assert.Single(getter.Body!.Statements);
        Assert.IsType<ThrowStatementSyntax>(getter.Body.Statements[0]);
        await AssertCompilesAsync(workspace);
    }

    [SkippableFact]
    public async Task Convert_AllFilesTrue_IndexerAndOperator_SkippedWithoutAbortingEligibleMethod()
    {
        const string source = """
            namespace TestApp;

            public class MixedKinds
            {
                public int Eligible() => 1;
                public int this[int i] => i;
                public static MixedKinds operator +(MixedKinds left, MixedKinds right) => left;
            }
            """;

        await using var workspace = await TempWorkspace.CreateWithFilesAsync(("MixedKinds.cs", source));
        var operation = new ConvertExpressionBodyOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ConvertExpressionBodyParams
        {
            AllFiles = true,
            Direction = "ToBlockBody"
        });

        Assert.True(result.Success);
        Assert.Single(result.Changes!.FilesModified);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePaths["MixedKinds.cs"]));
        AssertMethodIsBlockBodied(updated, "Eligible");
        Assert.Contains("this[int i] => i;", updated, StringComparison.Ordinal);
        Assert.Contains("operator +(MixedKinds left, MixedKinds right) => left;", updated, StringComparison.Ordinal);
        await AssertCompilesAsync(workspace);
    }

    #endregion

    #region SpanCoversColumn

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

        Assert.True(ConvertExpressionBodyOperation.SpanCoversColumn(span, line, startCol));
        Assert.True(ConvertExpressionBodyOperation.SpanCoversColumn(span, line, endCol - 1));
        Assert.False(ConvertExpressionBodyOperation.SpanCoversColumn(span, line, endCol));
        Assert.False(ConvertExpressionBodyOperation.SpanCoversColumn(span, line, startCol - 1));
    }

    #endregion

    #region Helpers

    private static void AssertMethodIsExpressionBodied(string source, string name)
    {
        var method = CSharpSyntaxTree.ParseText(source).GetRoot()
            .DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .First(m => m.Identifier.Text == name);
        Assert.NotNull(method.ExpressionBody);
        Assert.Null(method.Body);
    }

    private static void AssertMethodIsBlockBodied(string source, string name)
    {
        var method = CSharpSyntaxTree.ParseText(source).GetRoot()
            .DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .First(m => m.Identifier.Text == name);
        Assert.Null(method.ExpressionBody);
        Assert.NotNull(method.Body);
    }

    private static void AssertPropertyIsExpressionBodied(string source, string name)
    {
        var property = CSharpSyntaxTree.ParseText(source).GetRoot()
            .DescendantNodes()
            .OfType<PropertyDeclarationSyntax>()
            .First(p => p.Identifier.Text == name);
        Assert.NotNull(property.ExpressionBody);
        Assert.Null(property.AccessorList);
    }

    private static void AssertPropertyIsBlockBodied(string source, string name)
    {
        var property = CSharpSyntaxTree.ParseText(source).GetRoot()
            .DescendantNodes()
            .OfType<PropertyDeclarationSyntax>()
            .First(p => p.Identifier.Text == name);
        Assert.Null(property.ExpressionBody);
        Assert.NotNull(property.AccessorList);
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

    private static bool PathEquals(string left, string right) =>
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

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
        public required IReadOnlyDictionary<string, string> SourcePaths { get; init; }
        public required WorkspaceContext Context { get; init; }

        public static Task<TempWorkspace> CreateAsync(string source, string fileName = "Types.cs") =>
            CreateWithFilesAsync((fileName, source));

        public static async Task<TempWorkspace> CreateWithFilesAsync(params (string FileName, string Source)[] files)
        {
            Skip.IfNot(ModuleInitializer.MsBuildAvailable, ModuleInitializer.MsBuildError ?? "MSBuild not available");

            var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpConvertExpressionBody_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);

            var projectPath = Path.Combine(directory, "TestApp.csproj");
            var sourcePaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            // Pin authored sources so generated AssemblyInfo / TFM attributes
            // are not hit by the allFiles .cs document walk.
            await File.WriteAllTextAsync(projectPath, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net9.0</TargetFramework>
                    <Nullable>enable</Nullable>
                    <GenerateAssemblyInfo>false</GenerateAssemblyInfo>
                    <GenerateTargetFrameworkAttribute>false</GenerateTargetFrameworkAttribute>
                  </PropertyGroup>
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

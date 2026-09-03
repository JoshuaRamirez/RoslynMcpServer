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
/// Operation-level tests for <see cref="InvertIfOperation"/> (UC-A4).
/// </summary>
public class InvertIfOperationTests
{
    #region Input Validation

    [Fact]
    public void Validate_MissingSourceFile_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            InvertIfOperation.Validate(new InvertIfParams
            {
                SourceFile = "",
                Line = 1
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public void Validate_RelativePath_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            InvertIfOperation.Validate(new InvertIfParams
            {
                SourceFile = "Types.cs",
                Line = 1
            }));

        Assert.Equal(ErrorCodes.InvalidSourcePath, ex.ErrorCode);
    }

    [Fact]
    public void Validate_InvalidLine_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            InvertIfOperation.Validate(new InvertIfParams
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
            InvertIfOperation.Validate(new InvertIfParams
            {
                SourceFile = AbsoluteTestPath(),
                Line = 1,
                Column = 0
            }));

        Assert.Equal(ErrorCodes.InvalidColumnNumber, ex.ErrorCode);
        Assert.Equal("1007", ex.ErrorCode);
    }

    [Fact]
    public void Validate_ColumnWithoutLine_ThrowsMissingRequiredParam()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            InvertIfOperation.Validate(new InvertIfParams
            {
                SourceFile = AbsoluteTestPath(),
                Column = 4
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("line", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_MissingFile_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            InvertIfOperation.Validate(new InvertIfParams
            {
                SourceFile = AbsoluteTestPath(),
                Line = 1
            }));

        Assert.Equal(ErrorCodes.SourceFileNotFound, ex.ErrorCode);
    }

    [Fact]
    public void Validate_AllFilesFalse_WithoutSourceFile_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            InvertIfOperation.Validate(new InvertIfParams
            {
                AllFiles = false,
                Line = 1
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("sourceFile", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_AllFilesFalse_WithoutLine_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            InvertIfOperation.Validate(new InvertIfParams
            {
                SourceFile = AbsoluteTestPath(),
                AllFiles = false
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("line", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_AllFilesTrue_WithoutSourceFile_DoesNotThrow()
    {
        InvertIfOperation.Validate(new InvertIfParams
        {
            AllFiles = true
        });
    }

    [Fact]
    public void Validate_AllFilesTrue_WithLine_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            InvertIfOperation.Validate(new InvertIfParams
            {
                AllFiles = true,
                Line = 4
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("allFiles", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("line", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_AllFilesTrue_WithColumn_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            InvertIfOperation.Validate(new InvertIfParams
            {
                AllFiles = true,
                Column = 1
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("allFiles", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("column", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    #endregion

    #region P0 Happy Path

    [SkippableFact]
    public async Task InvertIf_SimpleComparison_FlipsOperator()
    {
        const string source = """
            namespace TestApp;

            public class Calculator
            {
                public string Classify(int x)
                {
                    if (x > 0)
                        return "positive";
                    else
                        return "non-positive";
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new InvertIfOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new InvertIfParams
        {
            SourceFile = workspace.SourcePath,
            Line = FindLine(source, "if (x > 0)")
        });

        Assert.True(result.Success);
        Assert.False(result.Preview);
        Assert.Equal("x > 0", result.OriginalCondition);
        Assert.Equal("x <= 0", result.InvertedCondition);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("if (x <= 0)", updated);
        Assert.DoesNotContain("if (x > 0)", updated);
        Assert.Contains("return \"non-positive\";", updated);
        Assert.Contains("return \"positive\";", updated);
        AssertPositiveAfterNonPositive(updated);
    }

    [SkippableFact]
    public async Task InvertIf_WithElse_SwapsBranches()
    {
        const string source = """
            namespace TestApp;

            public class Router
            {
                public void Route(bool flag)
                {
                    if (flag)
                    {
                        ThenBranch();
                    }
                    else
                    {
                        ElseBranch();
                    }
                }

                private static void ThenBranch() { }
                private static void ElseBranch() { }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new InvertIfOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new InvertIfParams
        {
            SourceFile = workspace.SourcePath,
            Line = FindLine(source, "if (flag)")
        });

        Assert.True(result.Success);
        Assert.Equal("flag", result.OriginalCondition);
        Assert.Equal("!flag", result.InvertedCondition);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("if (!flag)", updated);
        var thenIndex = updated.IndexOf("ThenBranch();", StringComparison.Ordinal);
        var elseIndex = updated.IndexOf("ElseBranch();", StringComparison.Ordinal);
        Assert.True(elseIndex >= 0 && thenIndex >= 0);
        Assert.True(elseIndex < thenIndex, "Else branch should become the if body.");
    }

    #endregion

    #region P0 Rejects

    [SkippableFact]
    public async Task InvertIf_NoIfAtLocation_Throws()
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
        var operation = new InvertIfOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new InvertIfParams
            {
                SourceFile = workspace.SourcePath,
                Line = FindLine(source, "var x = 1;")
            }));

        Assert.Equal(ErrorCodes.NoIfStatementAtLocation, ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task InvertIf_PatternVariable_Throws()
    {
        const string source = """
            namespace TestApp;

            public class Parser
            {
                public int Length(object value)
                {
                    if (value is string s)
                        return s.Length;
                    else
                        return 0;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new InvertIfOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new InvertIfParams
            {
                SourceFile = workspace.SourcePath,
                Line = FindLine(source, "if (value is string s)")
            }));

        Assert.Equal(ErrorCodes.ConditionNotInvertible, ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [Fact]
    public void InvertIf_UneditableDocument_Throws()
    {
        var workspace = new AdhocWorkspace();
        var project = workspace.AddProject("P", LanguageNames.CSharp);
        var document = workspace.AddDocument(project.Id, "Generated.cs", SourceText.From("class C {}"));

        var ex = Assert.Throws<RefactoringException>(() =>
            InvertIfOperation.ValidateDocumentIsEditable(document, workspace));

        Assert.Equal(ErrorCodes.DocumentNotEditable, ex.ErrorCode);
    }

    #endregion

    #region P0 Preview

    [SkippableFact]
    public async Task InvertIf_Preview_DoesNotModifyFile()
    {
        const string source = """
            namespace TestApp;

            public class Calculator
            {
                public string Classify(int x)
                {
                    if (x > 0)
                        return "positive";
                    else
                        return "non-positive";
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new InvertIfOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new InvertIfParams
        {
            SourceFile = workspace.SourcePath,
            Line = FindLine(source, "if (x > 0)"),
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.Equal("x > 0", result.OriginalCondition);
        Assert.Equal("x <= 0", result.InvertedCondition);
        Assert.NotNull(result.PendingChanges);
        Assert.Contains(result.PendingChanges, change =>
            change.Description.Contains("x > 0", StringComparison.Ordinal) &&
            change.Description.Contains("x <= 0", StringComparison.Ordinal) &&
            change.Description.Contains("swap if/else branches", StringComparison.Ordinal));
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    #endregion

    #region P1

    [SkippableFact]
    public async Task InvertIf_LogicalAnd_AppliesDeMorgan()
    {
        const string source = """
            namespace TestApp;

            public class Gate
            {
                public void Run(bool a, bool b)
                {
                    if (a && b)
                    {
                        Both();
                    }
                    else
                    {
                        Other();
                    }
                }

                private static void Both() { }
                private static void Other() { }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new InvertIfOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new InvertIfParams
        {
            SourceFile = workspace.SourcePath,
            Line = FindLine(source, "if (a && b)")
        });

        Assert.True(result.Success);
        Assert.Equal("a && b", result.OriginalCondition);
        Assert.Equal("!a || !b", result.InvertedCondition);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("if (!a || !b)", updated);
        var bothIndex = updated.IndexOf("Both();", StringComparison.Ordinal);
        var otherIndex = updated.IndexOf("Other();", StringComparison.Ordinal);
        Assert.True(otherIndex < bothIndex);
    }

    [SkippableFact]
    public async Task InvertIf_LogicalOr_AppliesDeMorgan()
    {
        const string source = """
            namespace TestApp;

            public class Gate
            {
                public void Run(bool a, bool b)
                {
                    if (a || b)
                    {
                        Either();
                    }
                    else
                    {
                        Neither();
                    }
                }

                private static void Either() { }
                private static void Neither() { }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new InvertIfOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new InvertIfParams
        {
            SourceFile = workspace.SourcePath,
            Line = FindLine(source, "if (a || b)")
        });

        Assert.True(result.Success);
        Assert.Equal("!a && !b", result.InvertedCondition);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("if (!a && !b)", updated);
    }

    [SkippableFact]
    public async Task InvertIf_WithoutElse_CreatesEmptyIfAndMovesBody()
    {
        const string source = """
            namespace TestApp;

            public class Logger
            {
                public void Log(bool enabled)
                {
                    if (enabled)
                    {
                        Write();
                    }
                }

                private static void Write() { }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new InvertIfOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new InvertIfParams
        {
            SourceFile = workspace.SourcePath,
            Line = FindLine(source, "if (enabled)")
        });

        Assert.True(result.Success);
        Assert.Equal("!enabled", result.InvertedCondition);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("if (!enabled)", updated);
        Assert.Contains("else", updated);
        Assert.Contains("Write();", updated);
        var ifIndex = updated.IndexOf("if (!enabled)", StringComparison.Ordinal);
        var elseIndex = updated.IndexOf("else", ifIndex, StringComparison.Ordinal);
        var writeIndex = updated.IndexOf("Write();", StringComparison.Ordinal);
        Assert.True(ifIndex < elseIndex);
        Assert.True(elseIndex < writeIndex);
    }

    [SkippableFact]
    public async Task InvertIf_NestedIf_InvertsOnlyTargetedIf()
    {
        const string source = """
            namespace TestApp;

            public class Nest
            {
                public void Run(bool outer, bool inner)
                {
                    if (outer)
                    {
                        if (inner)
                        {
                            InnerThen();
                        }
                        else
                        {
                            InnerElse();
                        }

                        AfterInner();
                    }
                    else
                    {
                        OuterElse();
                    }
                }

                private static void InnerThen() { }
                private static void InnerElse() { }
                private static void AfterInner() { }
                private static void OuterElse() { }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new InvertIfOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new InvertIfParams
        {
            SourceFile = workspace.SourcePath,
            Line = FindLine(source, "if (inner)")
        });

        Assert.True(result.Success);
        Assert.Equal("inner", result.OriginalCondition);
        Assert.Equal("!inner", result.InvertedCondition);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("if (outer)", updated);
        Assert.Contains("if (!inner)", updated);
        Assert.DoesNotContain("if (inner)", updated);
        Assert.Contains("OuterElse();", updated);
        Assert.Contains("AfterInner();", updated);
        var innerElseIndex = updated.IndexOf("InnerElse();", StringComparison.Ordinal);
        var innerThenIndex = updated.IndexOf("InnerThen();", StringComparison.Ordinal);
        Assert.True(innerElseIndex < innerThenIndex);
        var afterInner = updated.IndexOf("AfterInner();", StringComparison.Ordinal);
        Assert.True(innerThenIndex < afterInner);
    }

    [SkippableFact]
    public async Task InvertIf_ColumnSelectsInnerIfOnSameLine()
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
        var operation = new InvertIfOperation(workspace.Context);
        var line = FindLine(source, "if (a) if (b)");
        var innerIfColumn = source.IndexOf("if (b)", StringComparison.Ordinal)
            - source.LastIndexOf('\n', source.IndexOf("if (b)", StringComparison.Ordinal));

        var result = await operation.ExecuteAsync(new InvertIfParams
        {
            SourceFile = workspace.SourcePath,
            Line = line,
            Column = innerIfColumn
        });

        Assert.True(result.Success);
        Assert.Equal("b", result.OriginalCondition);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("if (a)", updated);
        Assert.Contains("if (!b)", updated);
    }

    [SkippableFact]
    public async Task InvertIf_OmittedColumn_InvertsFirstIfOnTheLine()
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
        var operation = new InvertIfOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new InvertIfParams
        {
            SourceFile = workspace.SourcePath,
            Line = FindLine(source, "if (a) if (b)")
        });

        Assert.True(result.Success);
        Assert.Equal("a", result.OriginalCondition);
        Assert.Equal("!a", result.InvertedCondition);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("if (!a)", updated);
        Assert.Contains("if (b)", updated);
        Assert.DoesNotContain("if (!b)", updated);
    }

    [SkippableFact]
    public async Task InvertIf_ExclusiveEndAtPreviousKeyword_ThrowsNoIf()
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
        var original = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new InvertIfOperation(workspace.Context);
        var line = FindLine(source, "if (a) if (b)");
        var firstKeywordEndCol = FirstIfKeywordEndColumn(source);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new InvertIfParams
            {
                SourceFile = workspace.SourcePath,
                Line = line,
                Column = firstKeywordEndCol
            }));

        Assert.Equal(ErrorCodes.NoIfStatementAtLocation, ex.ErrorCode);
        Assert.Equal("3153", ex.ErrorCode);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task InvertIf_Preview_ColumnSelectsInnerIf_DoesNotModifyFile()
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
        var before = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new InvertIfOperation(workspace.Context);
        var line = FindLine(source, "if (a) if (b)");
        var innerIfColumn = ColumnOf(source, "if (b)");

        var result = await operation.ExecuteAsync(new InvertIfParams
        {
            SourceFile = workspace.SourcePath,
            Line = line,
            Column = innerIfColumn,
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.Equal("b", result.OriginalCondition);
        Assert.Equal("!b", result.InvertedCondition);
        Assert.NotNull(result.PendingChanges);
        Assert.Contains(result.PendingChanges, change =>
            change.Description.Contains("b", StringComparison.Ordinal) &&
            change.Description.Contains("!b", StringComparison.Ordinal));
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task InvertIf_ElseIf_WrapsThenBranchToAvoidDanglingElse()
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
        var operation = new InvertIfOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new InvertIfParams
        {
            SourceFile = workspace.SourcePath,
            Line = FindLine(source, "if (a)")
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var tree = CSharpSyntaxTree.ParseText(updated);
        var outerIf = tree.GetRoot()
            .DescendantNodes()
            .OfType<IfStatementSyntax>()
            .First(statement => statement.Condition.ToString().Contains("!a", StringComparison.Ordinal));

        Assert.IsType<BlockSyntax>(outerIf.Statement);
        Assert.Contains("return \"a\";", outerIf.Else?.Statement.ToString() ?? "");
        Assert.DoesNotContain("return \"a\";", outerIf.Statement.ToString());
    }

    [SkippableFact]
    public async Task InvertIf_NullableRelational_WrapsWithNot()
    {
        const string source = """
            namespace TestApp;

            public class NullableCmp
            {
                public string Classify(int? x)
                {
                    if (x > 0)
                        return "positive";
                    else
                        return "other";
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new InvertIfOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new InvertIfParams
        {
            SourceFile = workspace.SourcePath,
            Line = FindLine(source, "if (x > 0)")
        });

        Assert.True(result.Success);
        Assert.Equal("!(x > 0)", result.InvertedCondition);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("!(x > 0)", updated);
        Assert.DoesNotContain("x <= 0", updated);
    }

    [SkippableFact]
    public async Task InvertIf_CustomTruthWithoutNot_Throws()
    {
        const string source = """
            namespace TestApp;

            public struct Flag
            {
                public static bool operator true(Flag value) => true;
                public static bool operator false(Flag value) => false;
            }

            public class UsesFlag
            {
                public void Run(Flag flag)
                {
                    if (flag)
                        Then();
                    else
                        Else();
                }

                private static void Then() { }
                private static void Else() { }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new InvertIfOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new InvertIfParams
            {
                SourceFile = workspace.SourcePath,
                Line = FindLine(source, "if (flag)")
            }));

        Assert.Equal(ErrorCodes.ConditionNotInvertible, ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task InvertIf_ComparisonTrivia_IsPreserved()
    {
        const string source = """
            namespace TestApp;

            public class Commented
            {
                public string Classify(int x)
                {
                    if (x /* boundary */ > 0)
                        return "positive";
                    else
                        return "other";
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new InvertIfOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new InvertIfParams
        {
            SourceFile = workspace.SourcePath,
            Line = FindLine(source, "if (x /* boundary */ > 0)")
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("/* boundary */", updated);
        Assert.Contains("<=", updated);
    }

    #endregion

    #region AllFiles

    private const string InvertFileA = """
        namespace TestApp;

        public class FileA
        {
            public string Classify(int x)
            {
                if (x > 0)
                    return "positive";
                else
                    return "non-positive";
            }
        }
        """;

    private const string InvertFileB = """
        namespace TestApp;

        public class FileB
        {
            public void Route(bool flag)
            {
                if (flag)
                {
                    ThenBranch();
                }
                else
                {
                    ElseBranch();
                }
            }

            private static void ThenBranch() { }
            private static void ElseBranch() { }
        }
        """;

    private const string NothingFileC = """
        namespace TestApp;

        public class FileC
        {
            public int Length(object value)
            {
                if (value is string s)
                    return s.Length;
                else
                    return 0;
            }
        }
        """;

    private const string MixedEligibleAndIncomplete = """
        namespace TestApp;

        public class Mixed
        {
            public void Run(bool flag)
            {
                if (flag)
                    Eligible();

                if ()
                    Incomplete();
            }

            private static void Eligible() { }
            private static void Incomplete() { }
        }
        """;

    private const string NestedIfs = """
        namespace TestApp;

        public class Nest
        {
            public void Run(bool outer, bool inner)
            {
                if (outer)
                {
                    if (inner)
                    {
                        InnerThen();
                    }
                    else
                    {
                        InnerElse();
                    }
                }
                else
                {
                    OuterElse();
                }
            }

            private static void InnerThen() { }
            private static void InnerElse() { }
            private static void OuterElse() { }
        }
        """;

    [SkippableFact]
    public async Task InvertIf_AllFilesFalse_InvertsOnlySpecifiedStatement()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("FileA.cs", InvertFileA),
            ("FileB.cs", InvertFileB),
            ("FileC.cs", NothingFileC));
        var operation = new InvertIfOperation(workspace.Context);
        var beforeB = await File.ReadAllTextAsync(workspace.SourcePaths["FileB.cs"]);
        var beforeC = await File.ReadAllTextAsync(workspace.SourcePaths["FileC.cs"]);

        var result = await operation.ExecuteAsync(new InvertIfParams
        {
            SourceFile = workspace.SourcePaths["FileA.cs"],
            AllFiles = false,
            Line = FindLine(InvertFileA, "if (x > 0)")
        });

        Assert.True(result.Success);
        Assert.False(result.Preview);
        var updatedA = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePaths["FileA.cs"]));
        Assert.Contains("if (x <= 0)", updatedA);
        Assert.DoesNotContain("if (x > 0)", updatedA);
        Assert.Equal(beforeB, await File.ReadAllTextAsync(workspace.SourcePaths["FileB.cs"]));
        Assert.Equal(beforeC, await File.ReadAllTextAsync(workspace.SourcePaths["FileC.cs"]));
        Assert.Single(result.Changes!.FilesModified);
        Assert.Contains(result.Changes.FilesModified, p => PathEquals(p, workspace.SourcePaths["FileA.cs"]));
    }

    [SkippableFact]
    public async Task InvertIf_OmittedAllFiles_KeepsSingleSiteInvert()
    {
        await using var workspace = await TempWorkspace.CreateAsync(InvertFileA);
        var operation = new InvertIfOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new InvertIfParams
        {
            SourceFile = workspace.SourcePath,
            Line = FindLine(InvertFileA, "if (x > 0)")
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("if (x <= 0)", updated);
        Assert.DoesNotContain("if (x > 0)", updated);
    }

    [SkippableFact]
    public async Task InvertIf_AllFilesTrue_InvertsEligibleIfsAcrossFiles()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("FileA.cs", InvertFileA),
            ("FileB.cs", InvertFileB),
            ("FileC.cs", NothingFileC));
        var operation = new InvertIfOperation(workspace.Context);
        var beforeC = await File.ReadAllTextAsync(workspace.SourcePaths["FileC.cs"]);

        var result = await operation.ExecuteAsync(new InvertIfParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        Assert.False(result.Preview);
        var updatedA = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePaths["FileA.cs"]));
        var updatedB = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePaths["FileB.cs"]));
        Assert.Contains("if (x <= 0)", updatedA);
        Assert.DoesNotContain("if (x > 0)", updatedA);
        Assert.Contains("if (!flag)", updatedB);
        Assert.DoesNotContain("if (flag)", updatedB);
        var thenIndex = updatedB.IndexOf("ThenBranch();", StringComparison.Ordinal);
        var elseIndex = updatedB.IndexOf("ElseBranch();", StringComparison.Ordinal);
        Assert.True(elseIndex < thenIndex, "Else branch should become the if body.");
        Assert.Equal(beforeC, await File.ReadAllTextAsync(workspace.SourcePaths["FileC.cs"]));
        Assert.Equal(2, result.Changes!.FilesModified.Count);
        Assert.Contains(result.Changes.FilesModified, p => PathEquals(p, workspace.SourcePaths["FileA.cs"]));
        Assert.Contains(result.Changes.FilesModified, p => PathEquals(p, workspace.SourcePaths["FileB.cs"]));
        Assert.DoesNotContain(result.Changes.FilesModified, p => PathEquals(p, workspace.SourcePaths["FileC.cs"]));
        await AssertCompilesAsync(workspace);
    }

    [SkippableFact]
    public async Task InvertIf_AllFilesTrue_WithoutSourceFileOrLine_Succeeds()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("FileA.cs", InvertFileA),
            ("FileB.cs", InvertFileB));
        var operation = new InvertIfOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new InvertIfParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        Assert.Equal(2, result.Changes!.FilesModified.Count);
    }

    [SkippableFact]
    public async Task InvertIf_AllFilesFalse_WithoutSourceFile_MissingRequiredParam()
    {
        await using var workspace = await TempWorkspace.CreateAsync(InvertFileA);
        var operation = new InvertIfOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new InvertIfParams
            {
                AllFiles = false,
                Line = 1
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("sourceFile", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task InvertIf_AllFilesFalse_WithoutLine_MissingRequiredParam()
    {
        await using var workspace = await TempWorkspace.CreateAsync(InvertFileA);
        var operation = new InvertIfOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new InvertIfParams
            {
                SourceFile = workspace.SourcePath,
                AllFiles = false
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("line", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task InvertIf_AllFilesTrue_WithLine_Rejects()
    {
        await using var workspace = await TempWorkspace.CreateAsync(InvertFileA);
        var operation = new InvertIfOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new InvertIfParams
            {
                AllFiles = true,
                Line = 4
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("line", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task InvertIf_AllFilesTrue_WithColumn_Rejects()
    {
        await using var workspace = await TempWorkspace.CreateAsync(InvertFileA);
        var operation = new InvertIfOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new InvertIfParams
            {
                AllFiles = true,
                Column = 1
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("column", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task InvertIf_PreviewAllFiles_AggregatesChangedFilesAndWritesNothing()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("FileA.cs", InvertFileA),
            ("FileB.cs", InvertFileB),
            ("FileC.cs", NothingFileC));
        var operation = new InvertIfOperation(workspace.Context);
        var beforeA = await File.ReadAllTextAsync(workspace.SourcePaths["FileA.cs"]);
        var beforeB = await File.ReadAllTextAsync(workspace.SourcePaths["FileB.cs"]);
        var beforeC = await File.ReadAllTextAsync(workspace.SourcePaths["FileC.cs"]);

        var result = await operation.ExecuteAsync(new InvertIfParams
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
            c.Description.Contains("Invert", StringComparison.OrdinalIgnoreCase) &&
            c.AfterSnippet != null &&
            (c.AfterSnippet.Contains("<=", StringComparison.Ordinal) ||
             c.AfterSnippet.Contains("!flag", StringComparison.Ordinal)));
        Assert.Equal(beforeA, await File.ReadAllTextAsync(workspace.SourcePaths["FileA.cs"]));
        Assert.Equal(beforeB, await File.ReadAllTextAsync(workspace.SourcePaths["FileB.cs"]));
        Assert.Equal(beforeC, await File.ReadAllTextAsync(workspace.SourcePaths["FileC.cs"]));
    }

    [SkippableFact]
    public async Task InvertIf_AllFilesTrue_EveryFileIneligible_SucceedsWithEmptyChanges()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("FileC.cs", NothingFileC),
            ("FileC2.cs", NothingFileC.Replace("FileC", "FileC2", StringComparison.Ordinal)));
        var operation = new InvertIfOperation(workspace.Context);
        var beforeA = await File.ReadAllTextAsync(workspace.SourcePaths["FileC.cs"]);
        var beforeB = await File.ReadAllTextAsync(workspace.SourcePaths["FileC2.cs"]);

        var result = await operation.ExecuteAsync(new InvertIfParams
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
    public async Task InvertIf_AllFilesTrue_MixedEligibleAndIncomplete_InvertsOnlyEligible()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("Mixed.cs", MixedEligibleAndIncomplete));
        var operation = new InvertIfOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new InvertIfParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePaths["Mixed.cs"]));
        Assert.Contains("if (!flag)", updated, StringComparison.Ordinal);
        Assert.DoesNotContain("if (flag)", updated, StringComparison.Ordinal);
        Assert.Contains("if ()", updated, StringComparison.Ordinal);
        Assert.Contains("Incomplete();", updated, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task InvertIf_AllFilesTrue_NestedIfs_InvertsEachOnce()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("Nest.cs", NestedIfs));
        var operation = new InvertIfOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new InvertIfParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePaths["Nest.cs"]));
        Assert.Contains("if (!outer)", updated, StringComparison.Ordinal);
        Assert.Contains("if (!inner)", updated, StringComparison.Ordinal);
        Assert.DoesNotContain("if (outer)", updated, StringComparison.Ordinal);
        Assert.DoesNotContain("if (inner)", updated, StringComparison.Ordinal);
        var outerElse = updated.IndexOf("OuterElse();", StringComparison.Ordinal);
        var innerElse = updated.IndexOf("InnerElse();", StringComparison.Ordinal);
        var innerThen = updated.IndexOf("InnerThen();", StringComparison.Ordinal);
        Assert.True(outerElse >= 0 && innerElse >= 0 && innerThen >= 0);
        Assert.True(outerElse < innerElse, "Outer else should become the if body.");
        Assert.True(innerElse < innerThen, "Inner else should become the inner if body.");
        await AssertCompilesAsync(workspace);
    }

    [Fact]
    public void CollectInvertibleIfs_MixedFile_ReturnsOnlyEligible()
    {
        var root = CSharpSyntaxTree.ParseText(MixedEligibleAndIncomplete).GetRoot();
        var collected = InvertIfOperation.CollectInvertibleIfs(root, model: null);

        Assert.Single(collected);
        Assert.Contains("flag", collected[0].Condition.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(collected, statement => statement.Condition.IsMissing);
    }

    [Fact]
    public void InvertAllIfs_NestedIfs_InvertsEachOnceWithoutDoubleInvert()
    {
        var root = CSharpSyntaxTree.ParseText(NestedIfs).GetRoot();
        var rewritten = InvertIfOperation.InvertAllIfs(root, model: null, out var invertedCount);

        Assert.Equal(2, invertedCount);
        var text = rewritten.ToFullString();
        Assert.Contains("if (!outer)", text, StringComparison.Ordinal);
        Assert.Contains("if (!inner)", text, StringComparison.Ordinal);
        Assert.DoesNotContain("if (!!outer)", text, StringComparison.Ordinal);
        Assert.DoesNotContain("if (!!inner)", text, StringComparison.Ordinal);

        // A second pass inverts the already-inverted conditions back — proof
        // the first pass inverted each node once rather than twice.
        var second = InvertIfOperation.InvertAllIfs(rewritten, model: null, out var secondCount);
        Assert.Equal(2, secondCount);
        var secondText = second.ToFullString();
        Assert.Contains("if (outer)", secondText, StringComparison.Ordinal);
        Assert.Contains("if (inner)", secondText, StringComparison.Ordinal);
        Assert.DoesNotContain("if (!!outer)", secondText, StringComparison.Ordinal);
        Assert.DoesNotContain("if (!outer)", secondText, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildAllFilesDescription_SingularAndPlural()
    {
        Assert.Equal("Invert if condition", InvertIfOperation.BuildAllFilesDescription(1));
        Assert.Equal("Invert 2 if conditions", InvertIfOperation.BuildAllFilesDescription(2));
    }

    #endregion

    #region Covering-span column

    private const string SameLineIfsSource = """
        class C
        {
            string M(bool a, bool b)
            {
                if (a) if (b) return "both"; else return "a"; else return "none";
            }
        }
        """;

    private const string IndentedIfSource = """
        class C
        {
            void M(bool flag)
            {
                if (flag) return;
            }
        }
        """;

    [Fact]
    public void FindIfStatement_OmittedColumn_PicksFirstIfKeywordBySpanStart()
    {
        var root = CSharpSyntaxTree.ParseText(SameLineIfsSource).GetRoot();
        var line = FindLine(SameLineIfsSource, "if (a) if (b)");

        var found = InvertIfOperation.FindIfStatement(root, line, column: null);

        Assert.NotNull(found);
        Assert.Equal("a", found.Condition.ToString());
    }

    [Fact]
    public void FindIfStatement_OmittedColumn_IndentedIf_DoesNotForceColumn1()
    {
        var root = CSharpSyntaxTree.ParseText(IndentedIfSource).GetRoot();
        var line = FindLine(IndentedIfSource, "if (flag)");
        var ifStmt = root.DescendantNodes().OfType<IfStatementSyntax>().Single();
        var startCol = ifStmt.IfKeyword.GetLocation().GetLineSpan().StartLinePosition.Character + 1;
        Assert.True(startCol > 1);

        var found = InvertIfOperation.FindIfStatement(root, line, column: null);

        Assert.NotNull(found);
        Assert.Equal("flag", found.Condition.ToString());
    }

    [Fact]
    public void FindIfStatement_ColumnSelectsInnerIfOnSameLine()
    {
        var root = CSharpSyntaxTree.ParseText(SameLineIfsSource).GetRoot();
        var line = FindLine(SameLineIfsSource, "if (a) if (b)");

        var outer = InvertIfOperation.FindIfStatement(root, line, ColumnOf(SameLineIfsSource, "if (a)"));
        var inner = InvertIfOperation.FindIfStatement(root, line, ColumnOf(SameLineIfsSource, "if (b)"));

        Assert.NotNull(outer);
        Assert.Equal("a", outer.Condition.ToString());
        Assert.NotNull(inner);
        Assert.Equal("b", inner.Condition.ToString());
    }

    [Fact]
    public void FindIfStatement_AdjacentKeywords_ExclusiveEndDoesNotStealNext()
    {
        var root = CSharpSyntaxTree.ParseText(SameLineIfsSource).GetRoot();
        var line = FindLine(SameLineIfsSource, "if (a) if (b)");
        var first = root.DescendantNodes().OfType<IfStatementSyntax>()
            .First(statement => statement.Condition.ToString() == "a");
        var firstKeywordEndCol = first.IfKeyword.GetLocation().GetLineSpan().EndLinePosition.Character + 1;
        var secondKeyword = ColumnOf(SameLineIfsSource, "if (b)");

        var atExclusiveEnd = InvertIfOperation.FindIfStatement(root, line, firstKeywordEndCol);
        var atSecond = InvertIfOperation.FindIfStatement(root, line, secondKeyword);

        Assert.False(InvertIfOperation.SpanCoversColumn(
            first.IfKeyword.GetLocation().GetLineSpan(), line, firstKeywordEndCol));
        Assert.True(atExclusiveEnd == null || atExclusiveEnd.Condition.ToString() != "a");
        Assert.NotNull(atSecond);
        Assert.Equal("b", atSecond.Condition.ToString());
    }

    [Fact]
    public void SpanCoversColumn_TreatsEndAsExclusive()
    {
        var tree = CSharpSyntaxTree.ParseText(SameLineIfsSource);
        var first = tree.GetRoot().DescendantNodes().OfType<IfStatementSyntax>()
            .First(statement => statement.Condition.ToString() == "a");
        var span = first.IfKeyword.GetLocation().GetLineSpan();
        var line = span.StartLinePosition.Line + 1;
        var startCol = span.StartLinePosition.Character + 1;
        var endCol = span.EndLinePosition.Character + 1;

        Assert.True(InvertIfOperation.SpanCoversColumn(span, line, startCol));
        Assert.True(InvertIfOperation.SpanCoversColumn(span, line, endCol - 1));
        Assert.False(InvertIfOperation.SpanCoversColumn(span, line, endCol));
        Assert.False(InvertIfOperation.SpanCoversColumn(span, line, startCol - 1));
    }

    #endregion

    #region Helpers

    private static void AssertPositiveAfterNonPositive(string updated)
    {
        var nonPositive = updated.IndexOf("return \"non-positive\";", StringComparison.Ordinal);
        var positive = updated.IndexOf("return \"positive\";", StringComparison.Ordinal);
        Assert.True(nonPositive >= 0 && positive >= 0);
        Assert.True(nonPositive < positive);
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
        Path.Combine(Path.GetTempPath(), "RoslynMcpInvertIfMissing.cs");

    private static string NormalizeNewlines(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal);

    private static int ColumnOf(string source, string snippet)
    {
        var index = source.IndexOf(snippet, StringComparison.Ordinal);
        if (index < 0)
            throw new InvalidOperationException($"Snippet not found: {snippet}");

        var lineStart = source.LastIndexOf('\n', index) + 1;
        return index - lineStart + 1;
    }

    private static int FirstIfKeywordEndColumn(string source)
    {
        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var first = root.DescendantNodes().OfType<IfStatementSyntax>()
            .OrderBy(statement => statement.IfKeyword.SpanStart)
            .First();
        return first.IfKeyword.GetLocation().GetLineSpan().EndLinePosition.Character + 1;
    }

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
        public required IReadOnlyDictionary<string, string> SourcePaths { get; init; }
        public required WorkspaceContext Context { get; init; }

        public static Task<TempWorkspace> CreateAsync(string source, string fileName = "Types.cs") =>
            CreateWithFilesAsync((fileName, source));

        public static async Task<TempWorkspace> CreateWithFilesAsync(params (string FileName, string Source)[] files)
        {
            Skip.IfNot(ModuleInitializer.MsBuildAvailable, ModuleInitializer.MsBuildError ?? "MSBuild not available");

            var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpInvertIf_" + Guid.NewGuid().ToString("N"));
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

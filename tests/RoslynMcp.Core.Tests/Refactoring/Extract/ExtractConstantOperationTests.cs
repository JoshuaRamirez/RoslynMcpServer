using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Extract;
using RoslynMcp.Core.Workspace;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Extract;

/// <summary>
/// Operation-level tests for <see cref="ExtractConstantOperation"/> allFiles.
/// </summary>
public class ExtractConstantOperationTests
{
    private const string EligibleFileA = """
        namespace TestApp;

        public class FileA
        {
            public int Run()
            {
                return 42 + 42;
            }

            public string Greet()
            {
                return "hi";
            }
        }
        """;

    private const string EligibleFileB = """
        namespace TestApp;

        public class FileB
        {
            public int Capacity()
            {
                return 10;
            }
        }
        """;

    private const string IneligibleFileC = """
        namespace TestApp;

        public class FileC
        {
            private const int Already = 99;

            public int Prop => Already;

            public void NoLiterals()
            {
            }
        }
        """;

    private const string CollisionFile = """
        namespace TestApp;

        public class CollisionHost
        {
            private const int _42 = 1;

            public int Run()
            {
                return 42;
            }
        }
        """;

    private const string TypeAttributeLiteralFile = """
        using System;

        [Obsolete("hi")]
        public class AttributeHost
        {
            public int Run()
            {
                return 42;
            }
        }
        """;

    [SkippableFact]
    public async Task ExtractConstant_OmittedAllFiles_KeepsSingleSiteExtract()
    {
        const string source = """
            namespace TestApp;

            public class Calculator
            {
                public int Run()
                {
                    return 42;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ExtractConstantOperation(workspace.Context);
        var span = FindSpan(source, "42");

        var result = await operation.ExecuteAsync(new ExtractConstantParams
        {
            SourceFile = workspace.SourcePath,
            StartLine = span.StartLine,
            StartColumn = span.StartColumn,
            EndLine = span.EndLine,
            EndColumn = span.EndColumn,
            ConstantName = "MaxRetries"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("const int MaxRetries", updated, StringComparison.Ordinal);
        Assert.Contains("return MaxRetries;", updated, StringComparison.Ordinal);
        Assert.DoesNotContain("return 42;", updated, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task ExtractConstant_AllFilesTrue_ExtractsEligibleLiteralsAcrossFiles()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("FileA.cs", EligibleFileA),
            ("FileB.cs", EligibleFileB),
            ("FileC.cs", IneligibleFileC));
        var operation = new ExtractConstantOperation(workspace.Context);
        var beforeC = await File.ReadAllTextAsync(workspace.SourcePaths["FileC.cs"]);

        var result = await operation.ExecuteAsync(new ExtractConstantParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        Assert.False(result.Preview);
        var updatedA = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePaths["FileA.cs"]));
        var updatedB = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePaths["FileB.cs"]));
        Assert.Contains("const int _42", updatedA, StringComparison.Ordinal);
        Assert.Contains("const string Hi", updatedA, StringComparison.Ordinal);
        // Without replaceAll, only the first matching literal is rewritten; the
        // second 42 skips on name collision with the newly introduced const.
        Assert.Contains("return _42 + 42;", updatedA, StringComparison.Ordinal);
        Assert.Contains("return Hi;", updatedA, StringComparison.Ordinal);
        Assert.Contains("const int _10", updatedB, StringComparison.Ordinal);
        Assert.Contains("return _10;", updatedB, StringComparison.Ordinal);
        Assert.Equal(beforeC, await File.ReadAllTextAsync(workspace.SourcePaths["FileC.cs"]));
        Assert.True(result.Changes!.FilesModified.Count >= 2);
        Assert.Contains(result.Changes.FilesModified, p => PathEquals(p, workspace.SourcePaths["FileA.cs"]));
        Assert.Contains(result.Changes.FilesModified, p => PathEquals(p, workspace.SourcePaths["FileB.cs"]));
        Assert.DoesNotContain(result.Changes.FilesModified, p => PathEquals(p, workspace.SourcePaths["FileC.cs"]));
    }

    [SkippableFact]
    public async Task ExtractConstant_AllFilesTrue_WithoutSourceFileOrConstantName_Succeeds()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("FileA.cs", EligibleFileA),
            ("FileB.cs", EligibleFileB));
        var operation = new ExtractConstantOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ExtractConstantParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        Assert.True(result.Changes!.FilesModified.Count >= 2);
    }

    [SkippableFact]
    public async Task ExtractConstant_AllFilesFalse_WithoutSourceFile_MissingRequiredParam()
    {
        await using var workspace = await TempWorkspace.CreateAsync(EligibleFileA);
        var operation = new ExtractConstantOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ExtractConstantParams
            {
                AllFiles = false,
                ConstantName = "MaxRetries",
                StartLine = 8,
                StartColumn = 1,
                EndLine = 8,
                EndColumn = 5
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("sourceFile", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task ExtractConstant_AllFilesTrue_WithConstantName_Rejects()
    {
        await using var workspace = await TempWorkspace.CreateAsync(EligibleFileA);
        var operation = new ExtractConstantOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ExtractConstantParams
            {
                AllFiles = true,
                ConstantName = "MaxRetries"
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("constantName", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task ExtractConstant_AllFilesTrue_WithStartLine_Rejects()
    {
        await using var workspace = await TempWorkspace.CreateAsync(EligibleFileA);
        var operation = new ExtractConstantOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ExtractConstantParams
            {
                AllFiles = true,
                StartLine = 8
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("startLine", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task ExtractConstant_PreviewAllFiles_AggregatesChangedFilesAndWritesNothing()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("FileA.cs", EligibleFileA),
            ("FileB.cs", EligibleFileB),
            ("FileC.cs", IneligibleFileC));
        var operation = new ExtractConstantOperation(workspace.Context);
        var beforeA = await File.ReadAllTextAsync(workspace.SourcePaths["FileA.cs"]);
        var beforeB = await File.ReadAllTextAsync(workspace.SourcePaths["FileB.cs"]);
        var beforeC = await File.ReadAllTextAsync(workspace.SourcePaths["FileC.cs"]);

        var result = await operation.ExecuteAsync(new ExtractConstantParams
        {
            AllFiles = true,
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.True(result.PendingChanges.Count >= 2);
        Assert.Contains(result.PendingChanges, c => PathEquals(c.File, workspace.SourcePaths["FileA.cs"]));
        Assert.Contains(result.PendingChanges, c => PathEquals(c.File, workspace.SourcePaths["FileB.cs"]));
        Assert.DoesNotContain(result.PendingChanges, c => PathEquals(c.File, workspace.SourcePaths["FileC.cs"]));
        Assert.Contains(result.PendingChanges, c =>
            c.Description.Contains("Extract", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(beforeA, await File.ReadAllTextAsync(workspace.SourcePaths["FileA.cs"]));
        Assert.Equal(beforeB, await File.ReadAllTextAsync(workspace.SourcePaths["FileB.cs"]));
        Assert.Equal(beforeC, await File.ReadAllTextAsync(workspace.SourcePaths["FileC.cs"]));
    }

    [SkippableFact]
    public async Task ExtractConstant_AllFilesTrue_EveryFileIneligible_SucceedsWithEmptyChanges()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("FileC.cs", IneligibleFileC),
            ("FileC2.cs", IneligibleFileC));
        var operation = new ExtractConstantOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ExtractConstantParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        Assert.Empty(result.Changes!.FilesModified);
        Assert.Empty(result.Changes.FilesCreated);
        Assert.Empty(result.Changes.FilesDeleted);
    }

    [SkippableFact]
    public async Task ExtractConstant_AllFilesTrue_OptionalSourceFile_LimitsWalk()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("FileA.cs", EligibleFileA),
            ("FileB.cs", EligibleFileB),
            ("FileC.cs", IneligibleFileC));
        var operation = new ExtractConstantOperation(workspace.Context);
        var beforeB = await File.ReadAllTextAsync(workspace.SourcePaths["FileB.cs"]);
        var beforeC = await File.ReadAllTextAsync(workspace.SourcePaths["FileC.cs"]);

        var result = await operation.ExecuteAsync(new ExtractConstantParams
        {
            AllFiles = true,
            SourceFile = workspace.SourcePaths["FileA.cs"]
        });

        Assert.True(result.Success);
        var updatedA = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePaths["FileA.cs"]));
        Assert.Contains("const int _42", updatedA, StringComparison.Ordinal);
        Assert.Equal(beforeB, await File.ReadAllTextAsync(workspace.SourcePaths["FileB.cs"]));
        Assert.Equal(beforeC, await File.ReadAllTextAsync(workspace.SourcePaths["FileC.cs"]));
        Assert.Single(result.Changes!.FilesModified);
        Assert.Contains(result.Changes.FilesModified, p => PathEquals(p, workspace.SourcePaths["FileA.cs"]));
    }

    [SkippableFact]
    public async Task ExtractConstant_AllFilesTrue_SkipsNameCollision()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("Collision.cs", CollisionFile));
        var operation = new ExtractConstantOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePaths["Collision.cs"]);

        var result = await operation.ExecuteAsync(new ExtractConstantParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePaths["Collision.cs"]));
        Assert.Empty(result.Changes!.FilesModified);
    }


    [SkippableFact]
    public async Task ExtractConstant_AllFilesTrue_SkipsPropertyNameCollision()
    {
        const string source = """
            namespace TestApp;

            public class Host
            {
                public string Name { get; set; }

                public string Run()
                {
                    return "name";
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ExtractConstantOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var result = await operation.ExecuteAsync(new ExtractConstantParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Empty(result.Changes!.FilesModified);
    }

    [SkippableFact]
    public async Task ExtractConstant_AllFilesTrue_SkipsParameterShadowing()
    {
        const string source = """
            namespace TestApp;

            public class Host
            {
                public int Run(int _42)
                {
                    return 42;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ExtractConstantOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var result = await operation.ExecuteAsync(new ExtractConstantParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Empty(result.Changes!.FilesModified);
    }

    [SkippableFact]
    public async Task ExtractConstant_AllFilesTrue_UsesEnumConvertedTypeForZeroLiteral()
    {
        const string source = """
            namespace TestApp;

            public enum State { Off = 0, On = 1 }

            public class Host
            {
                public State Run()
                {
                    State value = 0;
                    return value;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ExtractConstantOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ExtractConstantParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("const", updated, StringComparison.Ordinal);
        Assert.Contains("const State _0", updated, StringComparison.Ordinal);
        Assert.Contains("State value = _0;", updated, StringComparison.Ordinal);
        Assert.DoesNotContain("const int _0", updated, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task ExtractConstant_AllFilesTrue_UsesEnumConvertedTypeForNullableZeroLiteral()
    {
        const string source = """
            namespace TestApp;

            public enum State { Off = 0, On = 1 }

            public class Host
            {
                public State? Run()
                {
                    State? value = 0;
                    return value;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ExtractConstantOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ExtractConstantParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("const State _0", updated, StringComparison.Ordinal);
        Assert.Contains("State? value = _0;", updated, StringComparison.Ordinal);
        Assert.DoesNotContain("const int _0", updated, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task ExtractConstant_AllFilesTrue_ReplaceAll_SkipsNestedTypeMemberShadowedSites()
    {
        const string source = """
            namespace TestApp;

            public class Outer
            {
                public int Run()
                {
                    return 42;
                }

                public class Nested
                {
                    private const int _42 = 7;

                    public int Run()
                    {
                        return 42;
                    }
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ExtractConstantOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ExtractConstantParams
        {
            AllFiles = true,
            ReplaceAll = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("const int _42 = 42", updated, StringComparison.Ordinal);
        // Nested keeps its own const and its literal 42 (collision skip or untouched).
        Assert.Contains("private const int _42 = 7;", updated, StringComparison.Ordinal);
        Assert.Contains("return _42;", updated, StringComparison.Ordinal);
        Assert.True(CountOccurrences(updated, "return 42;") >= 1);
    }

    [Fact]
    public void IsVisibilityIncompatible_ProtectedOnStaticClass_True()
    {
        var tree = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText("""
            static class Host { public static int Run() => 1; }
            """);
        var type = tree.GetRoot().DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax>().Single();
        Assert.True(ExtractConstantOperation.IsVisibilityIncompatibleWithContainingType("protected", type));
        Assert.False(ExtractConstantOperation.IsVisibilityIncompatibleWithContainingType("private", type));
    }

    [Fact]
    public void IsVisibilityIncompatible_ProtectedOnStruct_True()
    {
        var tree = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText("""
            struct Point { public int Run() => 1; }
            """);
        var type = tree.GetRoot().DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.StructDeclarationSyntax>().Single();
        Assert.True(ExtractConstantOperation.IsVisibilityIncompatibleWithContainingType("protected", type));
        Assert.True(ExtractConstantOperation.IsVisibilityIncompatibleWithContainingType("protected internal", type));
        Assert.False(ExtractConstantOperation.IsVisibilityIncompatibleWithContainingType("public", type));
    }

    [SkippableFact]
    public async Task ExtractConstant_AllFilesTrue_Protected_SkipsStaticClassLiterals()
    {
        const string source = """
            namespace TestApp;

            public static class Host
            {
                public static int Run()
                {
                    return 42;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ExtractConstantOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var result = await operation.ExecuteAsync(new ExtractConstantParams
        {
            AllFiles = true,
            Visibility = "protected"
        });

        Assert.True(result.Success);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Empty(result.Changes!.FilesModified);
    }


    [SkippableFact]
    public async Task ExtractConstant_AllFilesTrue_ReplaceAll_SkipsIncompatibleContextualTypes()
    {
        const string source = """
            namespace TestApp;

            public enum State { Off = 0, On = 1 }

            public class Host
            {
                public State Run()
                {
                    State state = 0;
                    int count = 0;
                    return state;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ExtractConstantOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ExtractConstantParams
        {
            AllFiles = true,
            ReplaceAll = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("State state = _0;", updated, StringComparison.Ordinal);
        Assert.Contains("int count = 0;", updated, StringComparison.Ordinal);
        Assert.DoesNotContain("int count = _0;", updated, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task ExtractConstant_AllFilesTrue_SameNamedTypes_InsertsIntoCorrectDeclaration()
    {
        // Top-level Host and Outer.Host share an identifier; replace must insert
        // the const into Outer.Host (span identity), not the unrelated top-level Host.
        const string source = """
            namespace TestApp;

            public class Host
            {
                public int Unrelated() => 1;
            }

            public class Outer
            {
                public class Host
                {
                    public int Run()
                    {
                        return 42;
                    }
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ExtractConstantOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ExtractConstantParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("return _42;", updated, StringComparison.Ordinal);
        // Nested Host has the constant; top-level Host does not.
        Assert.Contains("public class Outer", updated, StringComparison.Ordinal);
        var outerHostIdx = updated.IndexOf("public class Outer", StringComparison.Ordinal);
        var nestedHostIdx = updated.IndexOf("public class Host", outerHostIdx, StringComparison.Ordinal);
        var constIdx = updated.IndexOf("const int _42", StringComparison.Ordinal);
        Assert.True(constIdx > nestedHostIdx, "const should be inside Outer.Host");
        var topHostIdx = updated.IndexOf("public class Host", StringComparison.Ordinal);
        Assert.True(topHostIdx >= 0 && topHostIdx < outerHostIdx);
        Assert.True(constIdx > outerHostIdx);
    }

    [SkippableFact]
    public async Task ExtractConstant_AllFilesTrue_SkipsDerivedNameEqualToContainingType()
    {
        const string source = """
            namespace TestApp;

            public class Foo
            {
                public string Run()
                {
                    return "foo";
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ExtractConstantOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var result = await operation.ExecuteAsync(new ExtractConstantParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Empty(result.Changes!.FilesModified);
    }

    [SkippableFact]
    public async Task ExtractConstant_AllFilesTrue_AllowsHidingInheritedMemberName()
    {
        const string source = """
            namespace TestApp;

            public class Base
            {
                protected const int _42 = 7;
            }

            public class Derived : Base
            {
                public int Run()
                {
                    return 42;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ExtractConstantOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ExtractConstantParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("class Derived", updated, StringComparison.Ordinal);
        Assert.Contains("const int _42", updated, StringComparison.Ordinal);
        Assert.Contains("return _42;", updated, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task ExtractConstant_AllFilesTrue_Protected_AllowsProtectedEnumFromBaseType()
    {
        const string source = """
            namespace TestApp;

            public class Base
            {
                protected enum State { Off = 0, On = 1 }
            }

            public class Host : Base
            {
                public State Run()
                {
                    State value = 0;
                    return value;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ExtractConstantOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ExtractConstantParams
        {
            AllFiles = true,
            Visibility = "protected"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("_0 = 0;", updated, StringComparison.Ordinal);
        Assert.Contains("State value = _0;", updated, StringComparison.Ordinal);
        Assert.DoesNotContain("State value = 0;", updated, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task ExtractConstant_AllFilesTrue_Protected_SkipsProtectedEnumOnSiblingNestedType()
    {
        // Outer.State is protected; Inner is a public nested type — protected const
        // State on Inner expands the accessibility domain beyond Outer (CS0052).
        const string source = """
            namespace TestApp;

            public class Outer
            {
                protected enum State { Off = 0, On = 1 }

                public class Inner
                {
                    private State Run()
                    {
                        State value = 0;
                        return value;
                    }
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ExtractConstantOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var result = await operation.ExecuteAsync(new ExtractConstantParams
        {
            AllFiles = true,
            Visibility = "protected"
        });

        Assert.True(result.Success);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Empty(result.Changes!.FilesModified);
    }

    [SkippableFact]
    public async Task ExtractConstant_AllFilesTrue_SemicolonRecord_PreservesLeadingTriviaOnSemicolon()
    {
        const string source = """
            namespace TestApp;

            public record R(int X = 42)
                // keep this
                ;
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ExtractConstantOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ExtractConstantParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("keep this", updated, StringComparison.Ordinal);
        Assert.Contains("const int _42", updated, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task ExtractConstant_AllFilesTrue_SemicolonRecord_AddsBracesAndConstant()
    {
        const string source = """
            namespace TestApp;

            public record R(int X = 42);
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ExtractConstantOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ExtractConstantParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("const int _42", updated, StringComparison.Ordinal);
        Assert.Contains("int X = _42", updated, StringComparison.Ordinal);
        Assert.Contains("{", updated, StringComparison.Ordinal);
        Assert.DoesNotContain("record R(int X = 42);", updated, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task ExtractConstant_AllFilesTrue_SkipsTypeParameterShadowing()
    {
        const string source = """
            namespace TestApp;

            public class Host
            {
                public int Run<_42>()
                {
                    int x = 42;
                    return x;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ExtractConstantOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var result = await operation.ExecuteAsync(new ExtractConstantParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Empty(result.Changes!.FilesModified);
    }

    [SkippableFact]
    public async Task ExtractConstant_AllFilesTrue_SkipsLocalFunctionShadowing()
    {
        const string source = """
            namespace TestApp;

            public class Host
            {
                public int Run()
                {
                    int _42() => 1;
                    int x = 42;
                    return x + _42();
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ExtractConstantOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ExtractConstantParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("int x = 42;", updated, StringComparison.Ordinal);
        Assert.DoesNotContain("const int _42", updated, StringComparison.Ordinal);
        Assert.DoesNotContain("int x = _42;", updated, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task ExtractConstant_AllFilesTrue_Public_SkipsLessAccessibleEnumType()
    {
        const string source = """
            namespace TestApp;

            public class Host
            {
                private enum State { Off = 0, On = 1 }

                public State Run()
                {
                    State value = 0;
                    return value;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ExtractConstantOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var result = await operation.ExecuteAsync(new ExtractConstantParams
        {
            AllFiles = true,
            Visibility = "public"
        });

        Assert.True(result.Success);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Empty(result.Changes!.FilesModified);
    }

    [SkippableFact]
    public async Task ExtractConstant_AllFilesTrue_Public_InInternalType_AllowsInternalEnumType()
    {
        // Codex P2: requested public is capped by internal Host → effective internal,
        // so an internal enum State is valid for the inserted const.
        const string source = """
            namespace TestApp;

            internal enum State { Off = 0, On = 1 }

            internal class Host
            {
                public State Run()
                {
                    State value = 0;
                    return value;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ExtractConstantOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ExtractConstantParams
        {
            AllFiles = true,
            Visibility = "public"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("public const State _0 = 0;", updated, StringComparison.Ordinal);
        Assert.Contains("State value = _0;", updated, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task ExtractConstant_AllFilesTrue_Protected_SkipsPrivateProtectedEffectiveEnumType()
    {
        const string source = """
            namespace TestApp;

            public class Outer
            {
                protected class Host
                {
                    internal enum State { Off = 0, On = 1 }

                    private State Run()
                    {
                        State value = 0;
                        return value;
                    }
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ExtractConstantOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var result = await operation.ExecuteAsync(new ExtractConstantParams
        {
            AllFiles = true,
            Visibility = "protected"
        });

        Assert.True(result.Success);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Empty(result.Changes!.FilesModified);
    }

    [SkippableFact]
    public async Task ExtractConstant_AllFilesTrue_SkipsIntMinValueUnaryOperand()
    {
        // Digit separators must still be detected (Token.Text alone misses them).
        const string source = """
            namespace TestApp;

            public class Host
            {
                public int Run()
                {
                    int x = -2_147_483_648;
                    return x;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ExtractConstantOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var result = await operation.ExecuteAsync(new ExtractConstantParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Empty(result.Changes!.FilesModified);
    }

    [SkippableFact]
    public async Task ExtractConstant_AllFilesTrue_Protected_SkipsInternalEnumType()
    {
        // protected vs internal are incomparable — do not emit protected const State.
        const string source = """
            namespace TestApp;

            internal enum State { Off = 0, On = 1 }

            public class Host
            {
                public State Run()
                {
                    State value = 0;
                    return value;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ExtractConstantOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var result = await operation.ExecuteAsync(new ExtractConstantParams
        {
            AllFiles = true,
            Visibility = "protected"
        });

        Assert.True(result.Success);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Empty(result.Changes!.FilesModified);
    }

    [SkippableFact]
    public async Task ExtractConstant_AllFilesTrue_SkipsInterfaceDefaultMemberLiterals()
    {
        const string source = """
            namespace TestApp;

            public interface IHost
            {
                int Run() => 42;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ExtractConstantOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var result = await operation.ExecuteAsync(new ExtractConstantParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Empty(result.Changes!.FilesModified);
    }

    [SkippableFact]
    public async Task ExtractConstant_AllFilesTrue_ReplaceAll_ReplacesMatchingLiteralsInType()
    {
        const string source = """
            namespace TestApp;

            public class Host
            {
                public int Run()
                {
                    return 7 + 7;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ExtractConstantOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ExtractConstantParams
        {
            AllFiles = true,
            ReplaceAll = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("const int _7", updated, StringComparison.Ordinal);
        Assert.Contains("return _7 + _7;", updated, StringComparison.Ordinal);
        Assert.DoesNotContain("return 7 + 7;", updated, StringComparison.Ordinal);
        // Only one const field for the value when replaceAll collapses matches.
        Assert.Equal(1, CountOccurrences(updated, "const int _7"));
    }

    [SkippableFact]
    public async Task ExtractConstant_AllFilesTrue_ReplaceAll_ReusesIntroducedConstantAcrossPartialFiles()
    {
        const string partA = """
            namespace TestApp;

            public partial class Host
            {
                public int Run()
                {
                    return 7;
                }
            }
            """;
        const string partB = """
            namespace TestApp;

            public partial class Host
            {
                public int Sum()
                {
                    return 7 + 7;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("PartA.cs", partA),
            ("PartB.cs", partB));
        var operation = new ExtractConstantOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ExtractConstantParams
        {
            AllFiles = true,
            ReplaceAll = true
        });

        Assert.True(result.Success);
        var updatedA = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePaths["PartA.cs"]));
        var updatedB = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePaths["PartB.cs"]));
        Assert.Contains("const int _7", updatedA, StringComparison.Ordinal);
        Assert.Contains("return _7;", updatedA, StringComparison.Ordinal);
        Assert.Contains("return _7 + _7;", updatedB, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(updatedA + updatedB, "const int _7"));
    }

    [SkippableFact]
    public async Task ExtractConstant_AllFilesTrue_ReplaceAll_PartialTypes_StableReuseKeyAcrossEarlierInsertions()
    {
        // A.cs extracts B._2 first. B.cs then extracts into partial A (before
        // partial B); that insertion must not shift B's reuse key so the later
        // B literal is left unreplaced under replaceAll (Codex P2).
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("A.cs", """
                namespace TestApp;

                public partial class B
                {
                    public int FromB() => 2;
                }
                """),
            ("B.cs", """
                namespace TestApp;

                public partial class A
                {
                    public int FromA() => 9;
                }

                public partial class B
                {
                    public int OtherB() => 2;
                }
                """));
        var operation = new ExtractConstantOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ExtractConstantParams
        {
            AllFiles = true,
            ReplaceAll = true
        });

        Assert.True(result.Success);
        var a = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePaths["A.cs"]));
        var b = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePaths["B.cs"]));
        Assert.Contains("const int _2", a, StringComparison.Ordinal);
        Assert.Contains("const int _9", b, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(a + b, "const int _2"));
        Assert.DoesNotContain("=> 2;", a, StringComparison.Ordinal);
        Assert.DoesNotContain("=> 2;", b, StringComparison.Ordinal);
        Assert.Contains("=> _2;", b, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task ExtractConstant_AllFilesTrue_UsesContextValidOrGlobalEnumTypeName()
    {
        // Local type External shadows the External namespace segment — ordinary
        // ToDisplayString() would emit External.State which binds to the local
        // type. Prefer alias / context-valid spelling, else global::.
        const string source = """
            namespace External
            {
                public enum State { Off = 0, On = 1 }
            }

            namespace TestApp
            {
                using S = global::External.State;

                public class External
                {
                }

                public class Host
                {
                    public S Run()
                    {
                        S value = 0;
                        return value;
                    }
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ExtractConstantOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ExtractConstantParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("S value = _0;", updated, StringComparison.Ordinal);
        Assert.True(
            updated.Contains("const S _0", StringComparison.Ordinal) ||
            updated.Contains("const global::External.State _0", StringComparison.Ordinal),
            $"Expected alias or global:: enum type, got:\n{updated}");
        Assert.DoesNotContain("const External.State", updated, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task ExtractConstant_AllFilesTrue_SkipsAttributeLiterals()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("AttributeHost.cs", TypeAttributeLiteralFile));
        var operation = new ExtractConstantOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ExtractConstantParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePaths["AttributeHost.cs"]));
        Assert.Contains("[Obsolete(\"hi\")]", updated, StringComparison.Ordinal);
        Assert.Contains("const int _42", updated, StringComparison.Ordinal);
        Assert.Contains("return _42;", updated, StringComparison.Ordinal);
        Assert.DoesNotContain("[Obsolete(Hi)]", updated, StringComparison.Ordinal);
    }


    [SkippableFact]
    public async Task ExtractConstant_AllFilesTrue_LinkedDocument_SkipsWhenSiblingCannotHonor()
    {
        // Shared physical file linked into two projects. ProjectB also owns a
        // partial that already declares _42 — primary (ProjectA) could extract,
        // but sibling validation must skip so we do not copy a duplicate member.
        const string sharedSource = """
            namespace TestApp;

            public partial class Host
            {
                public int Run()
                {
                    return 42;
                }
            }
            """;
        const string anchorASource = """
            namespace TestApp;

            public static class AnchorA
            {
            }
            """;
        const string anchorBSource = """
            namespace TestApp;

            public partial class Host
            {
                private const int _42 = 7;
            }
            """;

        await using var workspace = await TempWorkspace.CreateWithLinkedProjectsAsync(
            sharedSource, anchorASource, anchorBSource);
        var linkedDocuments = workspace.Context.Solution.Projects
            .SelectMany(p => p.Documents)
            .Where(d => PathEquals(d.FilePath!, workspace.SourcePaths["Shared.cs"]))
            .ToList();
        Assert.Equal(2, linkedDocuments.Count);

        var before = await File.ReadAllTextAsync(workspace.SourcePaths["Shared.cs"]);
        var operation = new ExtractConstantOperation(workspace.Context);
        var result = await operation.ExecuteAsync(new ExtractConstantParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePaths["Shared.cs"]));
        Assert.Empty(result.Changes!.FilesModified);
    }

    [SkippableFact]
    public async Task ExtractConstant_AllFilesTrue_LinkedDocument_CoalescesIdenticalRewrites()
    {
        const string sharedSource = """
            namespace TestApp;

            public class Host
            {
                public int Run()
                {
                    return 42;
                }
            }
            """;
        const string anchorASource = """
            namespace TestApp;

            public static class AnchorA
            {
            }
            """;
        const string anchorBSource = """
            namespace TestApp;

            public static class AnchorB
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateWithLinkedProjectsAsync(
            sharedSource, anchorASource, anchorBSource);
        var linkedDocuments = workspace.Context.Solution.Projects
            .SelectMany(p => p.Documents)
            .Where(d => PathEquals(d.FilePath!, workspace.SourcePaths["Shared.cs"]))
            .ToList();
        Assert.Equal(2, linkedDocuments.Count);

        var operation = new ExtractConstantOperation(workspace.Context);
        var result = await operation.ExecuteAsync(new ExtractConstantParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        Assert.Contains(result.Changes!.FilesModified, p => PathEquals(p, workspace.SourcePaths["Shared.cs"]));
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePaths["Shared.cs"]));
        Assert.Contains("const int _42 = 42", updated, StringComparison.Ordinal);
        Assert.Contains("return _42;", updated, StringComparison.Ordinal);

        var texts = new List<string>();
        foreach (var document in linkedDocuments)
        {
            var current = workspace.Context.Solution.GetDocument(document.Id);
            Assert.NotNull(current);
            texts.Add((await current!.GetTextAsync()).ToString());
        }

        Assert.Equal(2, texts.Count);
        Assert.Equal(texts[0], texts[1], StringComparer.Ordinal);
    }

    #region Helpers

    private static bool PathEquals(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static string NormalizeNewlines(string text) =>
        text.Replace("\r\n", "\n");

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    private static (int StartLine, int StartColumn, int EndLine, int EndColumn) FindSpan(string source, string snippet)
    {
        var index = source.IndexOf(snippet, StringComparison.Ordinal);
        if (index < 0)
            throw new InvalidOperationException($"Snippet not found: {snippet}");

        return (GetLineColumn(source, index).Line, GetLineColumn(source, index).Column,
            GetLineColumn(source, index + snippet.Length).Line, GetLineColumn(source, index + snippet.Length).Column);
    }

    private static (int Line, int Column) GetLineColumn(string source, int index)
    {
        var line = 1;
        var column = 1;
        for (var i = 0; i < index; i++)
        {
            if (source[i] == '\n')
            {
                line++;
                column = 1;
            }
            else if (source[i] != '\r')
            {
                column++;
            }
        }

        return (line, column);
    }

    private sealed class TempWorkspace : IAsyncDisposable
    {
        public required string DirectoryPath { get; init; }
        public required string ProjectPath { get; init; }
        public required string SourcePath { get; init; }
        public required IReadOnlyDictionary<string, string> SourcePaths { get; init; }
        public required WorkspaceContext Context { get; init; }

        public static Task<TempWorkspace> CreateAsync(string source, string fileName = "Types.cs") =>
            CreateMultiFileAsync((fileName, source));

        public static Task<TempWorkspace> CreateWithFilesAsync(params (string FileName, string Source)[] files) =>
            CreateMultiFileAsync(files);

        public static async Task<TempWorkspace> CreateMultiFileAsync(params (string FileName, string Source)[] files)
        {
            Skip.IfNot(ModuleInitializer.MsBuildAvailable, ModuleInitializer.MsBuildError ?? "MSBuild not available");

            var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpExtractConstant_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);

            var projectPath = Path.Combine(directory, "TestApp.csproj");
            var sourcePaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

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

            string? firstSource = null;
            foreach (var (fileName, source) in files)
            {
                var sourcePath = Path.Combine(directory, fileName);
                Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
                await File.WriteAllTextAsync(sourcePath, source);
                sourcePaths[fileName] = sourcePath;
                firstSource ??= sourcePath;
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
                    SourcePath = firstSource!,
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

        public static async Task<TempWorkspace> CreateWithLinkedProjectsAsync(
            string sharedSource,
            string anchorASource,
            string anchorBSource)
        {
            Skip.IfNot(ModuleInitializer.MsBuildAvailable, ModuleInitializer.MsBuildError ?? "MSBuild not available");

            var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpExtractConstantLinked_" + Guid.NewGuid().ToString("N"));
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

            await File.WriteAllTextAsync(rootProjectPath, """
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

            await File.WriteAllTextAsync(referencedProjectPath, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net9.0</TargetFramework>
                    <Nullable>enable</Nullable>
                    <GenerateAssemblyInfo>false</GenerateAssemblyInfo>
                    <GenerateTargetFrameworkAttribute>false</GenerateTargetFrameworkAttribute>
                    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
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

                var linkedCount = context.Solution.Projects
                    .SelectMany(proj => proj.Documents)
                    .Count(d => d.FilePath != null &&
                                string.Equals(Path.GetFullPath(d.FilePath), Path.GetFullPath(sharedPath), StringComparison.OrdinalIgnoreCase));
                if (linkedCount < 2)
                {
                    context.Dispose();
                    throw new InvalidOperationException($"Expected Shared.cs linked into 2 projects, found {linkedCount}.");
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

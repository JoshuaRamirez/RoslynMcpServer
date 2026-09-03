using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Signature;
using RoslynMcp.Core.Workspace;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Signature;

/// <summary>
/// Operation-level tests for <see cref="RemoveParameterOperation"/>.
/// </summary>
public class RemoveParameterOperationTests
{
    #region Input Validation

    [Fact]
    public void Validate_MissingSourceFile_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            RemoveParameterOperation.Validate(ValidParams(sourceFile: "")));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public void Validate_MissingMethodName_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            RemoveParameterOperation.Validate(ValidParams(methodName: "")));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public void Validate_MissingParameterName_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            RemoveParameterOperation.Validate(ValidParams(parameterName: "")));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public void Validate_RelativePath_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            RemoveParameterOperation.Validate(ValidParams(sourceFile: "Worker.cs")));

        Assert.Equal(ErrorCodes.InvalidSourcePath, ex.ErrorCode);
    }

    [Fact]
    public void Validate_MissingFile_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            RemoveParameterOperation.Validate(ValidParams()));

        Assert.Equal(ErrorCodes.SourceFileNotFound, ex.ErrorCode);
    }

    #endregion

    #region Happy Path

    [SkippableFact]
    public async Task RemoveParameter_UnusedParam_RemovesDeclarationAndCallSiteArg()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                public void Process(int count, bool unused)
                {
                    System.Console.WriteLine(count);
                }

                public void Run()
                {
                    Process(3, false);
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new RemoveParameterOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new RemoveParameterParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Process",
            ParameterName = "unused"
        });

        Assert.True(result.Success);
        Assert.False(result.Preview);

        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("public void Process(int count)", text);
        Assert.DoesNotContain("bool unused", text);
        Assert.Contains("Process(3)", text);
        Assert.DoesNotContain("Process(3, false)", text);
    }

    [SkippableFact]
    public async Task RemoveParameter_NamedArgs_RemovesNamedArgument()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                public void Process(int count, string name, bool unused)
                {
                    System.Console.WriteLine(count + name);
                }

                public void Run()
                {
                    Process(count: 3, name: "a", unused: false);
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new RemoveParameterOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new RemoveParameterParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Process",
            ParameterName = "unused"
        });

        Assert.True(result.Success);

        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("public void Process(int count, string name)", text);
        Assert.Contains("Process(count: 3, name: \"a\")", text);
        Assert.DoesNotContain("unused", text);
    }

    [SkippableFact]
    public async Task RemoveParameter_OverrideAndInterface_UpdatesChain()
    {
        const string source = """
            namespace TestApp;

            public interface IWorker
            {
                void Process(int count, bool unused);
            }

            public class Worker : IWorker
            {
                public virtual void Process(int count, bool unused)
                {
                }
            }

            public class Derived : Worker
            {
                public override void Process(int count, bool unused)
                {
                }
            }

            public static class Runner
            {
                public static void Run(IWorker worker, Derived derived)
                {
                    worker.Process(1, false);
                    derived.Process(2, true);
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new RemoveParameterOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new RemoveParameterParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Process",
            ParameterName = "unused",
            Line = 10
        });

        Assert.True(result.Success);

        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("void Process(int count);", text);
        Assert.Contains("public virtual void Process(int count)", text);
        Assert.Contains("public override void Process(int count)", text);
        Assert.Contains("worker.Process(1)", text);
        Assert.Contains("derived.Process(2)", text);
        Assert.DoesNotContain("bool unused", text);
    }

    [SkippableFact]
    public async Task RemoveParameter_Preview_ReturnsChangesAndWritesNothing()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                public void Process(int count, bool unused)
                {
                }

                public void Run() => Process(3, false);
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var original = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new RemoveParameterOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new RemoveParameterParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Process",
            ParameterName = "unused",
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.NotEmpty(result.PendingChanges);
        Assert.Contains(result.PendingChanges, c =>
            c.AfterSnippet != null &&
            c.AfterSnippet.Contains("public void Process(int count)") &&
            c.AfterSnippet.Contains("Process(3)") &&
            !c.AfterSnippet.Contains("bool unused"));

        var after = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Equal(original, after);
    }

    [SkippableFact]
    public async Task RemoveParameter_ForceTrue_ReplacesBodyUsages()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                public int Process(int unused)
                {
                    return unused;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new RemoveParameterOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new RemoveParameterParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Process",
            ParameterName = "unused",
            Force = true
        });

        Assert.True(result.Success);

        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("public int Process()", text);
        Assert.Contains("return default(int);", text);
        Assert.DoesNotContain("int unused", text);
    }

    [SkippableFact]
    public async Task RemoveParameter_ForceTrue_VarCopy_UsesTypedDefault()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                public int Process(int unused)
                {
                    var copy = unused;
                    return copy;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new RemoveParameterOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new RemoveParameterParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Process",
            ParameterName = "unused",
            Force = true
        });

        Assert.True(result.Success);

        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("public int Process()", text);
        Assert.Contains("var copy = default(int);", text);
        Assert.DoesNotContain("unused", text);
    }

    [SkippableFact]
    public async Task RemoveParameter_ReducedExtensionCall_DropsExplicitArg()
    {
        const string source = """
            namespace TestApp;

            public static class Exts
            {
                public static void Ext(this string value, int unused)
                {
                }
            }

            public class Worker
            {
                public void Run()
                {
                    var text = "hi";
                    text.Ext(1);
                    Exts.Ext(text, 2);
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new RemoveParameterOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new RemoveParameterParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Ext",
            ParameterName = "unused"
        });

        Assert.True(result.Success);

        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("public static void Ext(this string value)", text);
        Assert.Contains("text.Ext();", text);
        Assert.Contains("Exts.Ext(text);", text);
        Assert.DoesNotContain("text.Ext(1)", text);
        Assert.DoesNotContain("Exts.Ext(text, 2)", text);
        Assert.DoesNotContain("int unused", text);
    }

    [SkippableFact]
    public async Task RemoveParameter_EscapedNamedArg_UsesValueText()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                public void Process(int count, int @class)
                {
                    System.Console.WriteLine(count);
                }

                public void Run()
                {
                    Process(count: 3, @class: 1);
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new RemoveParameterOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new RemoveParameterParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Process",
            ParameterName = "@class"
        });

        Assert.True(result.Success);

        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("public void Process(int count)", text);
        Assert.Contains("Process(count: 3)", text);
        Assert.DoesNotContain("@class", text);
    }

    [SkippableFact]
    public async Task RemoveParameter_PreservesSurvivingSeparatorTrivia()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                public void Process(int a, // explanation
                    int b, int unused)
                {
                    System.Console.WriteLine(a + b);
                }

                public void Run()
                {
                    Process(1, 2, 3);
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new RemoveParameterOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new RemoveParameterParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Process",
            ParameterName = "unused"
        });

        Assert.True(result.Success);

        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("explanation", text);
        Assert.Contains("public void Process(int a, // explanation", text);
        Assert.Contains("Process(1, 2)", text);
        Assert.DoesNotContain("int unused", text);
    }

    #endregion

    #region Rejects

    [SkippableFact]
    public async Task RemoveParameter_UsedInBody_ForceFalse_Throws()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                public int Process(int unused)
                {
                    return unused;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var original = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new RemoveParameterOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new RemoveParameterParams
            {
                SourceFile = workspace.SourcePath,
                MethodName = "Process",
                ParameterName = "unused"
            }));

        Assert.Equal(ErrorCodes.ParameterUsedInBody, ex.ErrorCode);
        Assert.Equal("3131", ex.ErrorCode);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task RemoveParameter_MethodGroup_ThrowsAndWritesNothing()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                public int Process(int unused) => 0;

                public void Run()
                {
                    System.Func<int, int> handler = Process;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var original = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new RemoveParameterOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new RemoveParameterParams
            {
                SourceFile = workspace.SourcePath,
                MethodName = "Process",
                ParameterName = "unused"
            }));

        Assert.Equal(ErrorCodes.UnsupportedCallSite, ex.ErrorCode);
        Assert.Equal("3130", ex.ErrorCode);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task RemoveParameter_ParameterNotFound_Throws()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                public void Process(int count)
                {
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new RemoveParameterOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new RemoveParameterParams
            {
                SourceFile = workspace.SourcePath,
                MethodName = "Process",
                ParameterName = "missing"
            }));

        Assert.Equal(ErrorCodes.ParameterNotFound, ex.ErrorCode);
        Assert.Equal("2016", ex.ErrorCode);
    }

    [SkippableFact]
    public async Task RemoveParameter_MissingMethod_Throws()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                public void Process(int count)
                {
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new RemoveParameterOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new RemoveParameterParams
            {
                SourceFile = workspace.SourcePath,
                MethodName = "DoesNotExist",
                ParameterName = "count"
            }));

        Assert.Equal(ErrorCodes.MethodNotFound, ex.ErrorCode);
    }

    [Fact]
    public void RemoveParameter_UneditableDocument_Throws()
    {
        var workspace = new AdhocWorkspace();
        var project = workspace.AddProject("P", LanguageNames.CSharp);
        var document = workspace.AddDocument(project.Id, "Generated.cs", SourceText.From("class C {}"));

        var ex = Assert.Throws<RefactoringException>(() =>
            RemoveParameterOperation.ValidateDocumentIsEditable(document, workspace));

        Assert.Equal(ErrorCodes.DocumentNotEditable, ex.ErrorCode);
    }

    #endregion

    #region Covering-span column

    private const string SameLineOverloadsSource = """
        namespace TestApp;

        public class Worker
        {
            public void Process(int x, bool flag) { } public void Process(int x, int y, bool extra) { }
        }
        """;

    [Fact]
    public void Validate_InvalidColumn_Throws()
    {
        var path = Path.Combine(Path.GetTempPath(), "RoslynMcpRemoveParameterInvalidColumn.cs");
        File.WriteAllText(path, "class C {}");
        try
        {
            var ex = Assert.Throws<RefactoringException>(() =>
                RemoveParameterOperation.Validate(new RemoveParameterParams
                {
                    SourceFile = path,
                    MethodName = "Process",
                    ParameterName = "unused",
                    Column = 0
                }));

            Assert.Equal(ErrorCodes.InvalidColumnNumber, ex.ErrorCode);
            Assert.Equal("1007", ex.ErrorCode);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Validate_NegativeColumn_Throws()
    {
        var path = Path.Combine(Path.GetTempPath(), "RoslynMcpRemoveParameterNegativeColumn.cs");
        File.WriteAllText(path, "class C {}");
        try
        {
            var ex = Assert.Throws<RefactoringException>(() =>
                RemoveParameterOperation.Validate(new RemoveParameterParams
                {
                    SourceFile = path,
                    MethodName = "Process",
                    ParameterName = "unused",
                    Column = -1
                }));

            Assert.Equal(ErrorCodes.InvalidColumnNumber, ex.ErrorCode);
            Assert.Equal("1007", ex.ErrorCode);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void FindMethod_ColumnPicksIdentifierCoverage()
    {
        var tree = CSharpSyntaxTree.ParseText(SameLineOverloadsSource);
        var root = tree.GetRoot();
        var line = FindLine(SameLineOverloadsSource, "public void Process(int x, bool flag) { }");
        var first = RemoveParameterOperation.FindMethod(
            root, "Process", line, ColumnOf(SameLineOverloadsSource, "Process(int x, bool flag)"));
        var second = RemoveParameterOperation.FindMethod(
            root, "Process", line, ColumnOf(SameLineOverloadsSource, "Process(int x, int y, bool extra)"));
        var omitted = RemoveParameterOperation.FindMethod(root, "Process", line, column: null);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Null(omitted);
        Assert.Equal(["x", "flag"], ParameterNames(first));
        Assert.Equal(["x", "y", "extra"], ParameterNames(second));
    }

    [Fact]
    public void FindMethod_ColumnOnContinuationLine_PicksMethod()
    {
        const string source = """
            class C
            {
                public void
                Process(int x, bool unused) { }

                public void Process(int x, int y, bool unused) { }
            }
            """;

        var tree = CSharpSyntaxTree.ParseText(source);
        var root = tree.GetRoot();
        var startLine = FindLine(source, "public void");
        var identifierLine = FindLine(source, "Process(int x, bool unused) { }");
        Assert.NotEqual(startLine, identifierLine);

        // Omitted column keeps today's start-line filter — the split
        // signature does not start on the identifier line. Column still
        // selects it.
        var byStartLineOnly = RemoveParameterOperation.FindMethod(root, "Process", identifierLine, column: null);
        var byColumn = RemoveParameterOperation.FindMethod(
            root, "Process", identifierLine, ColumnOf(source, "Process(int x, bool unused) { }"));

        Assert.Null(byStartLineOnly);
        Assert.NotNull(byColumn);
        Assert.Equal(["x", "unused"], ParameterNames(byColumn));
    }

    [Fact]
    public void FindMethod_AdjacentMethods_ExclusiveEndDoesNotStealNextMethod()
    {
        const string source = """
            class C
            {
                public void Other(int x, bool unused){}public void Process(int x, bool unused){}
            }
            """;

        var tree = CSharpSyntaxTree.ParseText(source);
        var root = tree.GetRoot();
        var line = FindLine(source, "public void Other");
        var secondStart = ColumnOf(source, "public void Process");
        var secondId = ColumnOf(source, "Process(int x, bool unused){}");

        var atSecondStart = RemoveParameterOperation.FindMethod(root, "Process", line, secondStart);
        var atSecondId = RemoveParameterOperation.FindMethod(root, "Process", line, secondId);
        var atFirstId = RemoveParameterOperation.FindMethod(root, "Other", line, ColumnOf(source, "Other(int x, bool unused)"));
        var firstAtSecondStart = RemoveParameterOperation.FindMethod(root, "Other", line, secondStart);

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

        Assert.True(RemoveParameterOperation.SpanCoversColumn(span, line, startCol));
        Assert.True(RemoveParameterOperation.SpanCoversColumn(span, line, endCol - 1));
        Assert.False(RemoveParameterOperation.SpanCoversColumn(span, line, endCol));
        Assert.False(RemoveParameterOperation.SpanCoversColumn(span, line, startCol - 1));
    }

    [SkippableFact]
    public async Task RemoveParameter_OmittedColumn_SameLineOverloads_ThrowsSymbolAmbiguous()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SameLineOverloadsSource);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new RemoveParameterOperation(workspace.Context);
        var line = FindLine(SameLineOverloadsSource, "public void Process(int x, bool flag) { }");

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new RemoveParameterParams
            {
                SourceFile = workspace.SourcePath,
                MethodName = "Process",
                ParameterName = "flag",
                Line = line
            }));

        Assert.Equal(ErrorCodes.SymbolAmbiguous, ex.ErrorCode);
        Assert.Equal("2004", ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task RemoveParameter_Column_SelectsSecondOverloadOnSameLine()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SameLineOverloadsSource);
        var operation = new RemoveParameterOperation(workspace.Context);
        var line = FindLine(SameLineOverloadsSource, "public void Process(int x, bool flag) { }");
        var secondColumn = ColumnOf(SameLineOverloadsSource, "Process(int x, int y, bool extra)");

        var result = await operation.ExecuteAsync(new RemoveParameterParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Process",
            ParameterName = "extra",
            Line = line,
            Column = secondColumn
        });

        Assert.True(result.Success);
        var updated = await File.ReadAllTextAsync(workspace.SourcePath);
        var processMethods = GetMethods(updated, "Process");
        Assert.Equal(2, processMethods.Count);
        Assert.Contains(processMethods, m => ParameterNames(m) is ["x", "flag"]);
        Assert.Contains(processMethods, m => ParameterNames(m) is ["x", "y"]);
        Assert.DoesNotContain(processMethods, m => ParameterNames(m) is ["x"]);
    }

    [SkippableFact]
    public async Task RemoveParameter_Column_SelectsFirstOverloadOnSameLine()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SameLineOverloadsSource);
        var operation = new RemoveParameterOperation(workspace.Context);
        var line = FindLine(SameLineOverloadsSource, "public void Process(int x, bool flag) { }");
        var firstColumn = ColumnOf(SameLineOverloadsSource, "Process(int x, bool flag)");

        var result = await operation.ExecuteAsync(new RemoveParameterParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Process",
            ParameterName = "flag",
            Line = line,
            Column = firstColumn
        });

        Assert.True(result.Success);
        var updated = await File.ReadAllTextAsync(workspace.SourcePath);
        var processMethods = GetMethods(updated, "Process");
        Assert.Equal(2, processMethods.Count);
        Assert.Contains(processMethods, m => ParameterNames(m) is ["x"]);
        Assert.Contains(processMethods, m => ParameterNames(m) is ["x", "y", "extra"]);
        Assert.DoesNotContain(processMethods, m => ParameterNames(m) is ["x", "y"]);
    }

    [SkippableFact]
    public async Task RemoveParameter_ColumnOnContinuationLine_ChangesThatMethod()
    {
        const string source = """
            namespace TestApp;

            public class Split
            {
                public void
                Process(int x, bool unused) { }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new RemoveParameterOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new RemoveParameterParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Process",
            ParameterName = "unused",
            Line = FindLine(source, "Process(int x, bool unused)"),
            Column = ColumnOf(source, "Process(int x, bool unused)")
        });

        Assert.True(result.Success);
        var updated = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("public void Process(int x)", updated.Replace("\r\n", "\n"));
        Assert.DoesNotContain("bool unused", updated);
    }

    [SkippableFact]
    public async Task RemoveParameter_OmittedColumn_ContinuationLineIdentifier_ThrowsMethodNotFound()
    {
        const string source = """
            namespace TestApp;

            public class Split
            {
                public void
                Process(int x, bool unused) { }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new RemoveParameterOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new RemoveParameterParams
            {
                SourceFile = workspace.SourcePath,
                MethodName = "Process",
                ParameterName = "unused",
                Line = FindLine(source, "Process(int x, bool unused)")
            }));

        Assert.Equal(ErrorCodes.MethodNotFound, ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task RemoveParameter_AdjacentMethods_ColumnOnSecondDoesNotRewriteFirst()
    {
        const string source = """
            namespace TestApp;

            public class Adjacent
            {
                public void Other(int x, bool unused){}public void Process(int x, bool unused){}
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new RemoveParameterOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new RemoveParameterParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Process",
            ParameterName = "unused",
            Line = FindLine(source, "public void Other"),
            Column = ColumnOf(source, "Process(int x, bool unused)")
        });

        Assert.True(result.Success);
        var updated = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("public void Other(int x, bool unused)", updated);
        Assert.Contains("public void Process(int x)", updated);
        Assert.DoesNotContain("public void Other(int x){}", updated);
    }

    [SkippableFact]
    public async Task RemoveParameter_ColumnWithoutLine_SameIndentOverloads_ThrowsSymbolAmbiguous()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                public void Foo(int x, bool unused)
                {
                }

                public void Foo(int x, int y, bool unused)
                {
                }
            }
            """;

        var column = ColumnOf(source, "Foo(int x, bool unused)");
        Assert.Equal(column, ColumnOf(source, "Foo(int x, int y, bool unused)"));

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new RemoveParameterOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new RemoveParameterParams
            {
                SourceFile = workspace.SourcePath,
                MethodName = "Foo",
                ParameterName = "unused",
                Column = column
            }));

        Assert.Equal(ErrorCodes.SymbolAmbiguous, ex.ErrorCode);
        Assert.Equal("2004", ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("bool unused", (await File.ReadAllTextAsync(workspace.SourcePath)).Replace("\r\n", "\n"));
    }

    [SkippableFact]
    public async Task RemoveParameter_Preview_Column_DescribesRewriteAndWritesNothing()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SameLineOverloadsSource);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new RemoveParameterOperation(workspace.Context);
        var line = FindLine(SameLineOverloadsSource, "public void Process(int x, bool flag) { }");
        var secondColumn = ColumnOf(SameLineOverloadsSource, "Process(int x, int y, bool extra)");

        var result = await operation.ExecuteAsync(new RemoveParameterParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Process",
            ParameterName = "extra",
            Line = line,
            Column = secondColumn,
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.Contains(result.PendingChanges, change =>
            change.AfterSnippet != null &&
            change.AfterSnippet.Contains("Process(int x, int y)", StringComparison.Ordinal) &&
            change.AfterSnippet.Contains("bool flag", StringComparison.Ordinal) &&
            !change.AfterSnippet.Contains("bool extra", StringComparison.Ordinal));
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task RemoveParameter_Force_Column_ReplacesBodyUsagesOnSelectedOverload()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                public int Process(int unused) { return unused; } public int Process(int unused, int y) { return y; }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new RemoveParameterOperation(workspace.Context);
        var line = FindLine(source, "public int Process(int unused) { return unused; }");

        var result = await operation.ExecuteAsync(new RemoveParameterParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Process",
            ParameterName = "unused",
            Line = line,
            Column = ColumnOf(source, "Process(int unused) { return unused; }"),
            Force = true
        });

        Assert.True(result.Success);
        var updated = await File.ReadAllTextAsync(workspace.SourcePath);
        var processMethods = GetMethods(updated, "Process");
        Assert.Equal(2, processMethods.Count);
        Assert.Contains(processMethods, m => ParameterNames(m) is []);
        Assert.Contains(processMethods, m => ParameterNames(m) is ["unused", "y"]);
        Assert.Contains("return default(int);", updated);
        Assert.Contains("return y;", updated);
    }

    [SkippableFact]
    public async Task RemoveParameter_Column_UpdateOverridesAndImplementations_StillUpdatesChain()
    {
        const string source = """
            namespace TestApp;

            public interface IWorker
            {
                void Process(int count, bool unused);
            }

            public class Worker : IWorker
            {
                public virtual void Process(int count, bool unused) { } public void Process(string name, bool unused) { }
            }

            public class Derived : Worker
            {
                public override void Process(int count, bool unused)
                {
                }
            }

            public static class Runner
            {
                public static void Run(IWorker worker, Derived derived, Worker host)
                {
                    worker.Process(1, false);
                    derived.Process(2, true);
                    host.Process("a", false);
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new RemoveParameterOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new RemoveParameterParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Process",
            ParameterName = "unused",
            Line = FindLine(source, "public virtual void Process"),
            Column = ColumnOf(source, "Process(int count, bool unused) { }"),
            UpdateOverrides = true,
            UpdateImplementations = true
        });

        Assert.True(result.Success);
        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("void Process(int count);", text);
        Assert.Contains("public virtual void Process(int count)", text);
        Assert.Contains("public override void Process(int count)", text);
        Assert.Contains("public void Process(string name, bool unused)", text);
        Assert.Contains("worker.Process(1)", text);
        Assert.Contains("derived.Process(2)", text);
        Assert.Contains("host.Process(\"a\", false)", text);
    }

    [SkippableFact]
    public async Task RemoveParameter_Column_UpdateOverridesAndImplementationsFalse_OnlySelectedMethod()
    {
        const string source = """
            namespace TestApp;

            public interface IWorker
            {
                void Process(int count, bool unused);
            }

            public class Worker : IWorker
            {
                public virtual void Process(int count, bool unused) { } public void Process(string name, bool unused) { }
            }

            public class Derived : Worker
            {
                public override void Process(int count, bool unused)
                {
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new RemoveParameterOperation(workspace.Context);

        // Column picks the non-virtual same-line sibling so false flags stay
        // compiling: only that declaration changes. The virtual / interface /
        // override chain is left alone — same rewrite rules as today.
        var result = await operation.ExecuteAsync(new RemoveParameterParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Process",
            ParameterName = "unused",
            Line = FindLine(source, "public virtual void Process"),
            Column = ColumnOf(source, "Process(string name, bool unused)"),
            UpdateOverrides = false,
            UpdateImplementations = false
        });

        Assert.True(result.Success);
        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("void Process(int count, bool unused);", text);
        Assert.Contains("public virtual void Process(int count, bool unused)", text);
        Assert.Contains("public override void Process(int count, bool unused)", text);
        Assert.Contains("public void Process(string name)", text);
        Assert.DoesNotContain("Process(string name, bool unused)", text);
    }

    #endregion

    #region Helpers

    private static RemoveParameterParams ValidParams(
        string? sourceFile = null,
        string methodName = "Process",
        string parameterName = "unused") => new()
        {
            SourceFile = sourceFile ?? Path.Combine(Path.GetTempPath(), "RoslynMcpRemoveParameterMissing.cs"),
            MethodName = methodName,
            ParameterName = parameterName
        };

    private static List<MethodDeclarationSyntax> GetMethods(string source, string methodName) =>
        CSharpSyntaxTree.ParseText(source).GetRoot()
            .DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Where(m => m.Identifier.Text == methodName)
            .ToList();

    private static string[] ParameterNames(MethodDeclarationSyntax method) =>
        method.ParameterList.Parameters.Select(p => p.Identifier.Text).ToArray();

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

            var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpRemoveParameter_" + Guid.NewGuid().ToString("N"));
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

            var sourcePath = Path.Combine(directory, fileName);
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

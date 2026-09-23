using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Signature;
using RoslynMcp.Core.Refactoring.Utilities;
using RoslynMcp.Core.Resolution;
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

    [Fact]
    public void Validate_AllFilesFalse_WithoutSourceFile_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            RemoveParameterOperation.Validate(new RemoveParameterParams
            {
                AllFiles = false,
                MethodName = "Process",
                ParameterName = "unused"
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public void Validate_AllFilesTrue_WithoutSourceFileOrMethodName_DoesNotThrow()
    {
        RemoveParameterOperation.Validate(new RemoveParameterParams
        {
            AllFiles = true,
            ParameterName = "unused"
        });
    }

    [Fact]
    public void Validate_AllFilesTrue_WithRelativeSourceFile_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            RemoveParameterOperation.Validate(new RemoveParameterParams
            {
                AllFiles = true,
                SourceFile = "Worker.cs",
                ParameterName = "unused"
            }));

        Assert.Equal(ErrorCodes.InvalidSourcePath, ex.ErrorCode);
    }

    [Fact]
    public void Validate_AllFilesTrue_WithNonCSharpSourceFile_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            RemoveParameterOperation.Validate(new RemoveParameterParams
            {
                AllFiles = true,
                SourceFile = Path.Combine(Path.GetTempPath(), "Worker.txt"),
                ParameterName = "unused"
            }));

        Assert.Equal(ErrorCodes.InvalidSourcePath, ex.ErrorCode);
    }

    [Fact]
    public void Validate_AllFilesTrue_WithMissingSourceFile_DoesNotThrow()
    {
        RemoveParameterOperation.Validate(new RemoveParameterParams
        {
            AllFiles = true,
            SourceFile = Path.Combine(Path.GetTempPath(), "RoslynMcpRemoveParameterMissingAllFiles.cs"),
            ParameterName = "unused"
        });
    }

    [Fact]
    public void Validate_AllFilesTrue_WithMethodName_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            RemoveParameterOperation.Validate(new RemoveParameterParams
            {
                AllFiles = true,
                MethodName = "Process",
                ParameterName = "unused"
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("methodName", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_AllFilesTrue_WithLine_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            RemoveParameterOperation.Validate(new RemoveParameterParams
            {
                AllFiles = true,
                Line = 1,
                ParameterName = "unused"
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public void Validate_AllFilesTrue_WithColumn_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            RemoveParameterOperation.Validate(new RemoveParameterParams
            {
                AllFiles = true,
                Column = 1,
                ParameterName = "unused"
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public void BuildAllFilesDescription_SingularAndPlural()
    {
        Assert.Equal("Remove parameter", RemoveParameterOperation.BuildAllFilesDescription(1));
        Assert.Equal("Remove 2 parameters", RemoveParameterOperation.BuildAllFilesDescription(2));
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
            DocumentEditableHelpers.ValidateDocumentIsEditable(document, workspace));

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
        var first = FindMethodHelpers.FindMethod(
            root, "Process", line, ColumnOf(SameLineOverloadsSource, "Process(int x, bool flag)"));
        var second = FindMethodHelpers.FindMethod(
            root, "Process", line, ColumnOf(SameLineOverloadsSource, "Process(int x, int y, bool extra)"));
        var omitted = FindMethodHelpers.FindMethod(root, "Process", line, column: null);

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
        var byStartLineOnly = FindMethodHelpers.FindMethod(root, "Process", identifierLine, column: null);
        var byColumn = FindMethodHelpers.FindMethod(
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

        var atSecondStart = FindMethodHelpers.FindMethod(root, "Process", line, secondStart);
        var atSecondId = FindMethodHelpers.FindMethod(root, "Process", line, secondId);
        var atFirstId = FindMethodHelpers.FindMethod(root, "Other", line, ColumnOf(source, "Other(int x, bool unused)"));
        var firstAtSecondStart = FindMethodHelpers.FindMethod(root, "Other", line, secondStart);

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

        Assert.True(SpanCoverage.SpanCoversColumn(span, line, startCol));
        Assert.True(SpanCoverage.SpanCoversColumn(span, line, endCol - 1));
        Assert.False(SpanCoverage.SpanCoversColumn(span, line, endCol));
        Assert.False(SpanCoverage.SpanCoversColumn(span, line, startCol - 1));
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
        var updated = (await File.ReadAllTextAsync(workspace.SourcePath)).Replace("\r\n", "\n");
        Assert.Contains("Process(int x)", updated);
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

    #region allFiles

    private const string EligibleFileA = """
        public class FileA
        {
            public void Process(int count, int unused) { }
            public void Other(int unused) { }
            public void AlreadyGone(int count) { }
        }
        """;

    private const string EligibleFileB = """
        public class FileB
        {
            public void Process(int count, int unused) { }
        }
        """;

    private const string IneligibleFileC = """
        public class FileC
        {
            public void AlreadyGone(int count) { }
            public void AlsoGone(string name) { }
        }
        """;

    [SkippableFact]
    public async Task RemoveParameter_OmittedAllFiles_KeepsSingleSiteRewrite()
    {
        const string source = """
            public class Worker
            {
                public void Process(int count, int unused) { }
                public void Other(int count, int unused) { }
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
        var updated = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.DoesNotContain("unused", ParameterNames(GetMethods(updated, "Process").Single()));
        Assert.Contains("unused", ParameterNames(GetMethods(updated, "Other").Single()));
    }

    [SkippableFact]
    public async Task RemoveParameter_AllFilesTrue_AppliesToEligibleMethodsAcrossFiles()
    {
        await using var workspace = await TempWorkspace.CreateAsync(
            ("FileA.cs", EligibleFileA),
            ("FileB.cs", EligibleFileB),
            ("FileC.cs", IneligibleFileC));
        var operation = new RemoveParameterOperation(workspace.Context);
        var pathA = Path.Combine(workspace.DirectoryPath, "FileA.cs");
        var pathB = Path.Combine(workspace.DirectoryPath, "FileB.cs");
        var pathC = Path.Combine(workspace.DirectoryPath, "FileC.cs");
        var beforeC = await File.ReadAllTextAsync(pathC);

        var result = await operation.ExecuteAsync(new RemoveParameterParams
        {
            AllFiles = true,
            ParameterName = "unused"
        });

        Assert.True(result.Success);
        Assert.False(result.Preview);
        var updatedA = await File.ReadAllTextAsync(pathA);
        var updatedB = await File.ReadAllTextAsync(pathB);
        Assert.DoesNotContain("unused", ParameterNames(GetMethods(updatedA, "Process").Single()));
        Assert.DoesNotContain("unused", ParameterNames(GetMethods(updatedA, "Other").Single()));
        Assert.Equal(["count"], ParameterNames(GetMethods(updatedA, "AlreadyGone").Single()));
        Assert.DoesNotContain("unused", ParameterNames(GetMethods(updatedB, "Process").Single()));
        Assert.Equal(beforeC, await File.ReadAllTextAsync(pathC));
        Assert.True(result.Changes!.FilesModified.Count >= 2);
        Assert.Contains(result.Changes.FilesModified, p => PathsEqual(p, pathA));
        Assert.Contains(result.Changes.FilesModified, p => PathsEqual(p, pathB));
        Assert.DoesNotContain(result.Changes.FilesModified, p => PathsEqual(p, pathC));
    }

    [SkippableFact]
    public async Task RemoveParameter_AllFilesTrue_WithoutSourceFileOrMethodName_Succeeds()
    {
        await using var workspace = await TempWorkspace.CreateAsync(
            ("FileA.cs", EligibleFileA),
            ("FileB.cs", EligibleFileB));
        var operation = new RemoveParameterOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new RemoveParameterParams
        {
            AllFiles = true,
            ParameterName = "unused"
        });

        Assert.True(result.Success);
        Assert.True(result.Changes!.FilesModified.Count >= 2);
    }

    [SkippableFact]
    public async Task RemoveParameter_AllFilesFalse_WithoutSourceFile_MissingRequiredParam()
    {
        await using var workspace = await TempWorkspace.CreateAsync(EligibleFileA);
        var operation = new RemoveParameterOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new RemoveParameterParams
            {
                AllFiles = false,
                MethodName = "Process",
                ParameterName = "unused"
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("sourceFile", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task RemoveParameter_AllFilesTrue_WithMethodName_Rejects()
    {
        await using var workspace = await TempWorkspace.CreateAsync(EligibleFileA);
        var operation = new RemoveParameterOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new RemoveParameterParams
            {
                AllFiles = true,
                MethodName = "Process",
                ParameterName = "unused"
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("methodName", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task RemoveParameter_PreviewAllFiles_AggregatesChangedFilesAndWritesNothing()
    {
        await using var workspace = await TempWorkspace.CreateAsync(
            ("FileA.cs", EligibleFileA),
            ("FileB.cs", EligibleFileB),
            ("FileC.cs", IneligibleFileC));
        var operation = new RemoveParameterOperation(workspace.Context);
        var pathA = Path.Combine(workspace.DirectoryPath, "FileA.cs");
        var pathB = Path.Combine(workspace.DirectoryPath, "FileB.cs");
        var pathC = Path.Combine(workspace.DirectoryPath, "FileC.cs");
        var beforeA = await File.ReadAllTextAsync(pathA);
        var beforeB = await File.ReadAllTextAsync(pathB);
        var beforeC = await File.ReadAllTextAsync(pathC);

        var result = await operation.ExecuteAsync(new RemoveParameterParams
        {
            AllFiles = true,
            ParameterName = "unused",
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.Contains(result.PendingChanges, c => PathsEqual(c.File, pathA));
        Assert.Contains(result.PendingChanges, c => PathsEqual(c.File, pathB));
        Assert.DoesNotContain(result.PendingChanges, c => PathsEqual(c.File, pathC));
        Assert.Equal(beforeA, await File.ReadAllTextAsync(pathA));
        Assert.Equal(beforeB, await File.ReadAllTextAsync(pathB));
        Assert.Equal(beforeC, await File.ReadAllTextAsync(pathC));
    }

    [SkippableFact]
    public async Task RemoveParameter_AllFilesTrue_EveryFileIneligible_SucceedsWithEmptyChanges()
    {
        await using var workspace = await TempWorkspace.CreateAsync(
            ("FileC.cs", IneligibleFileC),
            ("FileC2.cs", IneligibleFileC));
        var operation = new RemoveParameterOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new RemoveParameterParams
        {
            AllFiles = true,
            ParameterName = "unused"
        });

        Assert.True(result.Success);
        Assert.Empty(result.Changes!.FilesModified);
    }

    [SkippableFact]
    public async Task RemoveParameter_AllFilesTrue_OptionalSourceFile_LimitsWalk()
    {
        await using var workspace = await TempWorkspace.CreateAsync(
            ("FileA.cs", EligibleFileA),
            ("FileB.cs", EligibleFileB),
            ("FileC.cs", IneligibleFileC));
        var operation = new RemoveParameterOperation(workspace.Context);
        var pathA = Path.Combine(workspace.DirectoryPath, "FileA.cs");
        var pathB = Path.Combine(workspace.DirectoryPath, "FileB.cs");
        var beforeB = await File.ReadAllTextAsync(pathB);

        var result = await operation.ExecuteAsync(new RemoveParameterParams
        {
            AllFiles = true,
            SourceFile = pathA,
            ParameterName = "unused"
        });

        Assert.True(result.Success);
        var updatedA = await File.ReadAllTextAsync(pathA);
        Assert.DoesNotContain("unused", ParameterNames(GetMethods(updatedA, "Process").Single()));
        Assert.Equal(beforeB, await File.ReadAllTextAsync(pathB));
        Assert.Contains(result.Changes!.FilesModified, p => PathsEqual(p, pathA));
        Assert.DoesNotContain(result.Changes.FilesModified, p => PathsEqual(p, pathB));
    }

    [SkippableFact]
    public async Task RemoveParameter_AllFilesTrue_SkipsWhenTargetWouldCollapseOverloads()
    {
        const string source = """
            public class Worker
            {
                public void Process(int unused) { }
                public void Process() { }
            }
            """;
        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new RemoveParameterOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var result = await operation.ExecuteAsync(new RemoveParameterParams
        {
            AllFiles = true,
            ParameterName = "unused"
        });

        Assert.True(result.Success);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Empty(result.Changes!.FilesModified);
    }

    [SkippableFact]
    public async Task RemoveParameter_AllFilesTrue_SkipsOverrideEqualsRatherThanBreakingContract()
    {
        const string source = """
            public class Worker
            {
                public override bool Equals(object? unused) => false;
                public void NeedsIt(int count, int unused) { }
            }
            """;
        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new RemoveParameterOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new RemoveParameterParams
        {
            AllFiles = true,
            ParameterName = "unused"
        });

        Assert.True(result.Success);
        var updated = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("Equals(object? unused)", updated.Replace("\r", ""));
        Assert.DoesNotContain("unused", ParameterNames(GetMethods(updated, "NeedsIt").Single()));
    }

    [SkippableFact]
    public async Task RemoveParameter_AllFilesTrue_SkipsVirtualBaseWithOverrides()
    {
        const string source = """
            public class Base
            {
                public virtual void Process(int count, int unused) { }
            }

            public class Derived : Base
            {
                public override void Process(int count, int unused) { }
            }
            """;
        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new RemoveParameterOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var result = await operation.ExecuteAsync(new RemoveParameterParams
        {
            AllFiles = true,
            ParameterName = "unused"
        });

        Assert.True(result.Success);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task RemoveParameter_AllFilesTrue_SkipsWhenParameterMissing()
    {
        const string source = """
            public class Worker
            {
                public void AlreadyGone(int count) { }
                public void NeedsIt(int count, int unused) { }
            }
            """;
        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new RemoveParameterOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new RemoveParameterParams
        {
            AllFiles = true,
            ParameterName = "unused"
        });

        Assert.True(result.Success);
        var updated = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Equal(["count"], ParameterNames(GetMethods(updated, "AlreadyGone").Single()));
        Assert.DoesNotContain("unused", ParameterNames(GetMethods(updated, "NeedsIt").Single()));
    }

    [SkippableFact]
    public async Task RemoveParameter_AllFilesTrue_SkipsModuleInitializer()
    {
        // ModuleInitializer must remain parameterless; eligibility gate skips it
        // so a bulk remove cannot leave CS8815 if callers later re-add params.
        const string source = """
            namespace TestApp;
            using System.Runtime.CompilerServices;
            public static class Startup
            {
                [ModuleInitializer]
                public static void Init() { }
                public static void NeedsIt(int count, int unused) { }
            }
            """;
        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new RemoveParameterOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new RemoveParameterParams
        {
            AllFiles = true,
            ParameterName = "unused"
        });

        Assert.True(result.Success);
        var updated = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("voidInit()", updated.Replace(" ", "").Replace("\r", ""));
        Assert.DoesNotContain("unused", ParameterNames(GetMethods(updated, "NeedsIt").Single()));
    }

    [SkippableFact]
    public async Task RemoveParameter_AllFilesTrue_SkipsUnmanagedCallersOnly()
    {
        const string source = """
            namespace TestApp;
            using System.Runtime.InteropServices;
            public static class Native
            {
                [UnmanagedCallersOnly]
                public static void Entry(int unused) { }
                public static void NeedsIt(int count, int unused) { }
            }
            """;
        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new RemoveParameterOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new RemoveParameterParams
        {
            AllFiles = true,
            ParameterName = "unused"
        });

        Assert.True(result.Success);
        var updated = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("Entry(int unused)", updated.Replace("\r", ""));
        Assert.DoesNotContain("unused", ParameterNames(GetMethods(updated, "NeedsIt").Single()));
    }

    [SkippableFact]
    public async Task RemoveParameter_AllFilesTrue_SkipsExtern()
    {
        const string source = """
            using System.Runtime.InteropServices;
            public static class Native
            {
                [DllImport("demo")]
                public static extern void Import(int unused);
                public static void NeedsIt(int count, int unused) { }
            }
            """;
        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new RemoveParameterOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new RemoveParameterParams
        {
            AllFiles = true,
            ParameterName = "unused"
        });

        Assert.True(result.Success);
        var updated = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("Import(int unused)", updated.Replace("\r", ""));
        Assert.DoesNotContain("unused", ParameterNames(GetMethods(updated, "NeedsIt").Single()));
    }

    [SkippableFact]
    public async Task RemoveParameter_AllFilesTrue_SkipsInterfaceImplementation()
    {
        const string source = """
            public interface IWorker
            {
                void Process(int unused);
            }

            public class Worker : IWorker
            {
                public void Process(int unused) { }
                public void NeedsIt(int count, int unused) { }
            }
            """;
        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new RemoveParameterOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new RemoveParameterParams
        {
            AllFiles = true,
            ParameterName = "unused"
        });

        Assert.True(result.Success);
        var updated = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("void Process(int unused);", updated);
        Assert.Contains("public void Process(int unused)", updated);
        Assert.DoesNotContain("unused", ParameterNames(GetMethods(updated, "NeedsIt").Single()));
    }

    [SkippableFact]
    public async Task RemoveParameter_AllFilesTrue_SkipsPartial()
    {
        const string source = """
            public partial class Worker
            {
                public partial void Process(int unused);
            }

            public partial class Worker
            {
                public partial void Process(int unused) { }
                public void NeedsIt(int count, int unused) { }
            }
            """;
        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new RemoveParameterOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new RemoveParameterParams
        {
            AllFiles = true,
            ParameterName = "unused"
        });

        Assert.True(result.Success);
        var updated = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("partial void Process(int unused)", updated);
        Assert.DoesNotContain("unused", ParameterNames(GetMethods(updated, "NeedsIt").Single()));
    }

    [SkippableFact]
    public async Task RemoveParameter_AllFilesTrue_SkipsWhenCallSiteOnLinkedMultiViewPath()
    {
        const string sharedSource = """
            namespace TestApp;

            public static class Caller
            {
                public static void Use() => Worker.Process(1);
            }
            """;
        const string anchorASource = """
            namespace TestApp;

            public static class Worker
            {
                public static void Process(int unused) { }
            }
            """;
        const string anchorBSource = """
            namespace TestApp;

            public static class Worker
            {
                public static void Process(int unused) { }
            }
            """;

        await using var workspace = await TempWorkspace.CreateWithLinkedProjectsAsync(
            sharedSource, anchorASource, anchorBSource);
        var counts = RemoveParameterOperation.BuildLinkedPathCounts(workspace.Context.Solution);
        var sharedKey = RoslynMcp.Core.FileSystem.PathResolver.GetPathComparisonKey(
            workspace.SourcePaths["Shared.cs"]);
        Assert.True(counts.TryGetValue(sharedKey, out var sharedCount) && sharedCount > 1);

        var beforeShared = await File.ReadAllTextAsync(workspace.SourcePaths["Shared.cs"]);
        var beforeA = await File.ReadAllTextAsync(workspace.SourcePaths["AnchorA.cs"]);
        var beforeB = await File.ReadAllTextAsync(workspace.SourcePaths["AnchorB.cs"]);

        var operation = new RemoveParameterOperation(workspace.Context);
        var result = await operation.ExecuteAsync(new RemoveParameterParams
        {
            AllFiles = true,
            ParameterName = "unused"
        });

        Assert.True(result.Success);
        Assert.Equal(beforeShared, await File.ReadAllTextAsync(workspace.SourcePaths["Shared.cs"]));
        Assert.Equal(beforeA, await File.ReadAllTextAsync(workspace.SourcePaths["AnchorA.cs"]));
        Assert.Equal(beforeB, await File.ReadAllTextAsync(workspace.SourcePaths["AnchorB.cs"]));
        Assert.Empty(result.Changes!.FilesModified);
    }

    #endregion

    #region Helpers

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left).Replace('\\', '/'),
            Path.GetFullPath(right).Replace('\\', '/'),
            StringComparison.OrdinalIgnoreCase);

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
        public Dictionary<string, string> SourcePaths { get; init; } = new(StringComparer.Ordinal);
        public required WorkspaceContext Context { get; init; }

        public static Task<TempWorkspace> CreateAsync(string source, string fileName = "Worker.cs") =>
            CreateAsync((fileName, source));

        public static async Task<TempWorkspace> CreateAsync(params (string FileName, string Source)[] files)
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

            string? sourcePath = null;
            foreach (var (fileName, source) in files)
            {
                var path = Path.Combine(directory, fileName);
                await File.WriteAllTextAsync(path, source);
                sourcePath ??= path;
            }

            sourcePath ??= Path.Combine(directory, "Worker.cs");

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

        public static async Task<TempWorkspace> CreateWithLinkedProjectsAsync(
            string sharedSource,
            string anchorASource,
            string anchorBSource)
        {
            Skip.IfNot(ModuleInitializer.MsBuildAvailable, ModuleInitializer.MsBuildError ?? "MSBuild not available");

            var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpRemoveParameterLinked_" + Guid.NewGuid().ToString("N"));
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
                    throw new InvalidOperationException($"Expected linked document in both projects, found {linkedCount}.");
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

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
/// Operation-level tests for <see cref="ReorderParametersOperation"/>.
/// </summary>
public class ReorderParametersOperationTests
{
    #region Input Validation

    [Fact]
    public void Validate_MissingSourceFile_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ReorderParametersOperation.Validate(ValidParams(sourceFile: "")));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public void Validate_MissingMethodName_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ReorderParametersOperation.Validate(ValidParams(methodName: "")));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public void Validate_MissingNewOrder_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ReorderParametersOperation.Validate(ValidParams(newOrder: Array.Empty<int>())));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public void Validate_RelativePath_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ReorderParametersOperation.Validate(ValidParams(sourceFile: "Worker.cs")));

        Assert.Equal(ErrorCodes.InvalidSourcePath, ex.ErrorCode);
    }

    [Fact]
    public void Validate_MissingFile_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ReorderParametersOperation.Validate(ValidParams()));

        Assert.Equal(ErrorCodes.SourceFileNotFound, ex.ErrorCode);
    }

    [Fact]
    public void Validate_InvalidPermutation_Throws()
    {
        var path = Path.Combine(Path.GetTempPath(), "RoslynMcpReorderInvalidPerm.cs");
        File.WriteAllText(path, "class C {}");
        try
        {
            var ex = Assert.Throws<RefactoringException>(() =>
                ReorderParametersOperation.Validate(ValidParams(sourceFile: path, newOrder: new[] { 0, 0 })));

            Assert.Equal(ErrorCodes.InvalidParameterPosition, ex.ErrorCode);
            Assert.Equal("1011", ex.ErrorCode);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ValidateNewOrder_LengthMismatch_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ReorderParametersOperation.ValidateNewOrder(3, new[] { 1, 0 }));

        Assert.Equal(ErrorCodes.InvalidParameterPosition, ex.ErrorCode);
    }

    #endregion

    #region Happy Path

    [SkippableFact]
    public async Task ReorderParameters_SimpleSwap_ReordersDeclarationAndCallSite()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                public void Process(int count, string name)
                {
                    System.Console.WriteLine(count + name);
                }

                public void Run()
                {
                    Process(3, "a");
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ReorderParametersOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ReorderParametersParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Process",
            NewOrder = new[] { 1, 0 }
        });

        Assert.True(result.Success);
        Assert.False(result.Preview);

        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("public void Process(string name, int count)", text);
        Assert.Contains("Process(\"a\", 3)", text);
        Assert.DoesNotContain("Process(3, \"a\")", text);
    }

    [SkippableFact]
    public async Task ReorderParameters_NamedArgs_LeftInPlace()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                public void Process(int count, string name, bool flag)
                {
                    System.Console.WriteLine(count + name + flag);
                }

                public void Run()
                {
                    Process(count: 3, name: "a", flag: false);
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ReorderParametersOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ReorderParametersParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Process",
            NewOrder = new[] { 2, 0, 1 }
        });

        Assert.True(result.Success);

        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("public void Process(bool flag, int count, string name)", text);
        Assert.Contains("Process(count: 3, name: \"a\", flag: false)", text);
    }

    [SkippableFact]
    public async Task ReorderParameters_MixedNamedAndPositional_ReordersPositionalLeavesNamed()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                public void Process(int count, string name)
                {
                    System.Console.WriteLine(count + name);
                }

                public void Run()
                {
                    Process(3, name: "a");
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ReorderParametersOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ReorderParametersParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Process",
            NewOrder = new[] { 1, 0 }
        });

        Assert.True(result.Success);

        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("public void Process(string name, int count)", text);
        Assert.Contains("Process(name: \"a\", 3)", text);
    }

    [SkippableFact]
    public async Task ReorderParameters_OverrideAndInterface_UpdatesChain()
    {
        const string source = """
            namespace TestApp;

            public interface IWorker
            {
                void Process(int count, string name);
            }

            public class Worker : IWorker
            {
                public virtual void Process(int count, string name)
                {
                }
            }

            public class Derived : Worker
            {
                public override void Process(int count, string name)
                {
                }
            }

            public static class Runner
            {
                public static void Run(IWorker worker, Derived derived)
                {
                    worker.Process(1, "a");
                    derived.Process(2, "b");
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ReorderParametersOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ReorderParametersParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Process",
            NewOrder = new[] { 1, 0 },
            Line = 10
        });

        Assert.True(result.Success);

        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("void Process(string name, int count);", text);
        Assert.Contains("public virtual void Process(string name, int count)", text);
        Assert.Contains("public override void Process(string name, int count)", text);
        Assert.Contains("worker.Process(\"a\", 1)", text);
        Assert.Contains("derived.Process(\"b\", 2)", text);
    }

    [SkippableFact]
    public async Task ReorderParameters_ReducedExtensionCall_OffsetsThis()
    {
        const string source = """
            namespace TestApp;

            public static class Exts
            {
                public static void Ext(this string value, int first, int second)
                {
                }
            }

            public class Worker
            {
                public void Run()
                {
                    var text = "hi";
                    text.Ext(1, 2);
                    Exts.Ext(text, 1, 2);
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ReorderParametersOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ReorderParametersParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Ext",
            NewOrder = new[] { 0, 2, 1 }
        });

        Assert.True(result.Success);

        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("public static void Ext(this string value, int second, int first)", text);
        Assert.Contains("text.Ext(2, 1)", text);
        Assert.Contains("Exts.Ext(text, 2, 1)", text);
        Assert.DoesNotContain("text.Ext(1, 2)", text);
        Assert.DoesNotContain("Exts.Ext(text, 1, 2)", text);
    }

    [SkippableFact]
    public async Task ReorderParameters_EscapedNamedArg_UsesValueText()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                public void Process(int count, int @class)
                {
                    System.Console.WriteLine(count + @class);
                }

                public void Run()
                {
                    Process(count: 3, @class: 1);
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ReorderParametersOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ReorderParametersParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Process",
            NewOrder = new[] { 1, 0 }
        });

        Assert.True(result.Success);

        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("public void Process(int @class, int count)", text);
        Assert.Contains("Process(count: 3, @class: 1)", text);
    }

    [SkippableFact]
    public async Task ReorderParameters_PreservesSurvivingSeparatorTrivia()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                public void Process(int a, // explanation
                    int b, int c)
                {
                    System.Console.WriteLine(a + b + c);
                }

                public void Run()
                {
                    Process(1, 2, 3);
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ReorderParametersOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ReorderParametersParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Process",
            NewOrder = new[] { 0, 1, 2 }
        });

        Assert.True(result.Success);

        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("explanation", text);
        Assert.Contains("public void Process(int a, // explanation", text);
        Assert.Contains("Process(1, 2, 3)", text);
    }

    [SkippableFact]
    public async Task ReorderParameters_Preview_ReturnsChangesAndWritesNothing()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                public void Process(int count, string name)
                {
                }

                public void Run() => Process(3, "a");
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var original = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new ReorderParametersOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ReorderParametersParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Process",
            NewOrder = new[] { 1, 0 },
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.NotEmpty(result.PendingChanges);
        Assert.Contains(result.PendingChanges, c =>
            c.AfterSnippet != null &&
            c.AfterSnippet.Contains("public void Process(string name, int count)") &&
            c.AfterSnippet.Contains("Process(\"a\", 3)"));

        var after = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Equal(original, after);
    }

    [SkippableFact]
    public async Task ReorderParameters_OmittedOptional_ConvertsPositionalToNamed()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                public void Process(int a = 1, int b = 2)
                {
                    System.Console.WriteLine(a + b);
                }

                public void Run()
                {
                    Process(3);
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ReorderParametersOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ReorderParametersParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Process",
            NewOrder = new[] { 1, 0 }
        });

        Assert.True(result.Success);

        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("public void Process(int b = 2, int a = 1)", text);
        Assert.Contains("Process(a: 3)", text);
        Assert.DoesNotContain("Process(3)", text);
    }

    [SkippableFact]
    public async Task ReorderParameters_ImplNamedArgs_UseInvokedParameterNames()
    {
        const string source = """
            namespace TestApp;

            public interface IWorker
            {
                void Process(int count, string name);
            }

            public class Worker : IWorker
            {
                public void Process(int value, string text)
                {
                    System.Console.WriteLine(value + text);
                }
            }

            public static class Runner
            {
                public static void Run(Worker worker)
                {
                    worker.Process(1, text: "x");
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ReorderParametersOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ReorderParametersParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Process",
            NewOrder = new[] { 1, 0 },
            Line = 5
        });

        Assert.True(result.Success);

        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("void Process(string name, int count);", text);
        Assert.Contains("public void Process(string text, int value)", text);
        Assert.Contains("worker.Process(text: \"x\", 1)", text);
    }

    #endregion

    #region Rejects

    [SkippableFact]
    public async Task ReorderParameters_InvalidPermutation_Throws()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                public void Process(int count, string name)
                {
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var original = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new ReorderParametersOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ReorderParametersParams
            {
                SourceFile = workspace.SourcePath,
                MethodName = "Process",
                NewOrder = new[] { 0, 2 }
            }));

        Assert.Equal(ErrorCodes.InvalidParameterPosition, ex.ErrorCode);
        Assert.Equal("1011", ex.ErrorCode);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task ReorderParameters_OverloadCollision_ThrowsAndWritesNothing()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                public void Process(int count, string name)
                {
                }

                public void Process(string name, int count)
                {
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var original = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new ReorderParametersOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ReorderParametersParams
            {
                SourceFile = workspace.SourcePath,
                MethodName = "Process",
                NewOrder = new[] { 1, 0 },
                Line = 5
            }));

        Assert.Equal(ErrorCodes.SignatureMatchesOverload, ex.ErrorCode);
        Assert.Equal("3132", ex.ErrorCode);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task ReorderParameters_ParamsNotLast_Throws()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                public void Process(int count, params int[] values)
                {
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var original = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new ReorderParametersOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ReorderParametersParams
            {
                SourceFile = workspace.SourcePath,
                MethodName = "Process",
                NewOrder = new[] { 1, 0 }
            }));

        Assert.Equal(ErrorCodes.ParamsNotLast, ex.ErrorCode);
        Assert.Equal("3129", ex.ErrorCode);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task ReorderParameters_OptionalBeforeRequired_Throws()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                public void Process(int count, string name = "a")
                {
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var original = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new ReorderParametersOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ReorderParametersParams
            {
                SourceFile = workspace.SourcePath,
                MethodName = "Process",
                NewOrder = new[] { 1, 0 }
            }));

        Assert.Equal(ErrorCodes.RequiredAfterOptional, ex.ErrorCode);
        Assert.Equal("3128", ex.ErrorCode);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task ReorderParameters_MethodGroup_ThrowsAndWritesNothing()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                public int Process(int count, string name) => 0;

                public void Run()
                {
                    System.Func<int, string, int> handler = Process;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var original = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new ReorderParametersOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ReorderParametersParams
            {
                SourceFile = workspace.SourcePath,
                MethodName = "Process",
                NewOrder = new[] { 1, 0 }
            }));

        Assert.Equal(ErrorCodes.UnsupportedCallSite, ex.ErrorCode);
        Assert.Equal("3130", ex.ErrorCode);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task ReorderParameters_RelatedInterfaceOptional_ThrowsAndWritesNothing()
    {
        const string source = """
            namespace TestApp;

            public interface IWorker
            {
                void Process(int required, int optional = 0);
            }

            public class Worker : IWorker
            {
                public void Process(int required, int optional)
                {
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var original = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new ReorderParametersOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ReorderParametersParams
            {
                SourceFile = workspace.SourcePath,
                MethodName = "Process",
                NewOrder = new[] { 1, 0 },
                Line = 10
            }));

        Assert.Equal(ErrorCodes.RequiredAfterOptional, ex.ErrorCode);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task ReorderParameters_MissingMethod_Throws()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                public void Process(int count, string name)
                {
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ReorderParametersOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ReorderParametersParams
            {
                SourceFile = workspace.SourcePath,
                MethodName = "DoesNotExist",
                NewOrder = new[] { 1, 0 }
            }));

        Assert.Equal(ErrorCodes.MethodNotFound, ex.ErrorCode);
    }

    [Fact]
    public void ReorderParameters_UneditableDocument_Throws()
    {
        var workspace = new AdhocWorkspace();
        var project = workspace.AddProject("P", LanguageNames.CSharp);
        var document = workspace.AddDocument(project.Id, "Generated.cs", SourceText.From("class C {}"));

        var ex = Assert.Throws<RefactoringException>(() =>
            ReorderParametersOperation.ValidateDocumentIsEditable(document, workspace));

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
        var path = Path.Combine(Path.GetTempPath(), "RoslynMcpReorderParametersInvalidColumn.cs");
        File.WriteAllText(path, "class C {}");
        try
        {
            var ex = Assert.Throws<RefactoringException>(() =>
                ReorderParametersOperation.Validate(new ReorderParametersParams
                {
                    SourceFile = path,
                    MethodName = "Process",
                    NewOrder = new[] { 1, 0 },
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
        var path = Path.Combine(Path.GetTempPath(), "RoslynMcpReorderParametersNegativeColumn.cs");
        File.WriteAllText(path, "class C {}");
        try
        {
            var ex = Assert.Throws<RefactoringException>(() =>
                ReorderParametersOperation.Validate(new ReorderParametersParams
                {
                    SourceFile = path,
                    MethodName = "Process",
                    NewOrder = new[] { 1, 0 },
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
        var first = ReorderParametersOperation.FindMethod(
            root, "Process", line, ColumnOf(SameLineOverloadsSource, "Process(int x, bool flag)"));
        var second = ReorderParametersOperation.FindMethod(
            root, "Process", line, ColumnOf(SameLineOverloadsSource, "Process(int x, int y, bool extra)"));
        var omitted = ReorderParametersOperation.FindMethod(root, "Process", line, column: null);

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
        var byStartLineOnly = ReorderParametersOperation.FindMethod(root, "Process", identifierLine, column: null);
        var byColumn = ReorderParametersOperation.FindMethod(
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

        var atSecondStart = ReorderParametersOperation.FindMethod(root, "Process", line, secondStart);
        var atSecondId = ReorderParametersOperation.FindMethod(root, "Process", line, secondId);
        var atFirstId = ReorderParametersOperation.FindMethod(root, "Other", line, ColumnOf(source, "Other(int x, bool unused)"));
        var firstAtSecondStart = ReorderParametersOperation.FindMethod(root, "Other", line, secondStart);

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

        Assert.True(ReorderParametersOperation.SpanCoversColumn(span, line, startCol));
        Assert.True(ReorderParametersOperation.SpanCoversColumn(span, line, endCol - 1));
        Assert.False(ReorderParametersOperation.SpanCoversColumn(span, line, endCol));
        Assert.False(ReorderParametersOperation.SpanCoversColumn(span, line, startCol - 1));
    }

    [SkippableFact]
    public async Task ReorderParameters_OmittedColumn_SameLineOverloads_ThrowsSymbolAmbiguous()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SameLineOverloadsSource);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new ReorderParametersOperation(workspace.Context);
        var line = FindLine(SameLineOverloadsSource, "public void Process(int x, bool flag) { }");

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ReorderParametersParams
            {
                SourceFile = workspace.SourcePath,
                MethodName = "Process",
                NewOrder = new[] { 1, 0 },
                Line = line
            }));

        Assert.Equal(ErrorCodes.SymbolAmbiguous, ex.ErrorCode);
        Assert.Equal("2004", ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task ReorderParameters_Column_SelectsSecondOverloadOnSameLine()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SameLineOverloadsSource);
        var operation = new ReorderParametersOperation(workspace.Context);
        var line = FindLine(SameLineOverloadsSource, "public void Process(int x, bool flag) { }");
        var secondColumn = ColumnOf(SameLineOverloadsSource, "Process(int x, int y, bool extra)");

        var result = await operation.ExecuteAsync(new ReorderParametersParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Process",
            NewOrder = new[] { 1, 0, 2 },
            Line = line,
            Column = secondColumn
        });

        Assert.True(result.Success);
        var updated = await File.ReadAllTextAsync(workspace.SourcePath);
        var processMethods = GetMethods(updated, "Process");
        Assert.Equal(2, processMethods.Count);
        Assert.Contains(processMethods, m => ParameterNames(m) is ["x", "flag"]);
        Assert.Contains(processMethods, m => ParameterNames(m) is ["y", "x", "extra"]);
        Assert.DoesNotContain(processMethods, m => ParameterNames(m) is ["x", "y", "extra"]);
    }

    [SkippableFact]
    public async Task ReorderParameters_Column_SelectsFirstOverloadOnSameLine()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SameLineOverloadsSource);
        var operation = new ReorderParametersOperation(workspace.Context);
        var line = FindLine(SameLineOverloadsSource, "public void Process(int x, bool flag) { }");
        var firstColumn = ColumnOf(SameLineOverloadsSource, "Process(int x, bool flag)");

        var result = await operation.ExecuteAsync(new ReorderParametersParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Process",
            NewOrder = new[] { 1, 0 },
            Line = line,
            Column = firstColumn
        });

        Assert.True(result.Success);
        var updated = await File.ReadAllTextAsync(workspace.SourcePath);
        var processMethods = GetMethods(updated, "Process");
        Assert.Equal(2, processMethods.Count);
        Assert.Contains(processMethods, m => ParameterNames(m) is ["flag", "x"]);
        Assert.Contains(processMethods, m => ParameterNames(m) is ["x", "y", "extra"]);
        Assert.DoesNotContain(processMethods, m => ParameterNames(m) is ["x", "flag"]);
    }

    [SkippableFact]
    public async Task ReorderParameters_ColumnOnContinuationLine_ChangesThatMethod()
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
        var operation = new ReorderParametersOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ReorderParametersParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Process",
            NewOrder = new[] { 1, 0 },
            Line = FindLine(source, "Process(int x, bool unused)"),
            Column = ColumnOf(source, "Process(int x, bool unused)")
        });

        Assert.True(result.Success);
        var updated = (await File.ReadAllTextAsync(workspace.SourcePath)).Replace("\r\n", "\n");
        Assert.Contains("Process(bool unused, int x)", updated);
        Assert.DoesNotContain("Process(int x, bool unused)", updated);
    }

    [SkippableFact]
    public async Task ReorderParameters_OmittedColumn_ContinuationLineIdentifier_ThrowsMethodNotFound()
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
        var operation = new ReorderParametersOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ReorderParametersParams
            {
                SourceFile = workspace.SourcePath,
                MethodName = "Process",
                NewOrder = new[] { 1, 0 },
                Line = FindLine(source, "Process(int x, bool unused)")
            }));

        Assert.Equal(ErrorCodes.MethodNotFound, ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task ReorderParameters_AdjacentMethods_ColumnOnSecondDoesNotRewriteFirst()
    {
        const string source = """
            namespace TestApp;

            public class Adjacent
            {
                public void Other(int x, bool unused){}public void Process(int x, bool unused){}
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ReorderParametersOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ReorderParametersParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Process",
            NewOrder = new[] { 1, 0 },
            Line = FindLine(source, "public void Other"),
            Column = ColumnOf(source, "Process(int x, bool unused)")
        });

        Assert.True(result.Success);
        var updated = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("public void Other(int x, bool unused)", updated);
        Assert.Contains("public void Process(bool unused, int x)", updated);
        Assert.DoesNotContain("public void Other(bool unused, int x)", updated);
    }

    [SkippableFact]
    public async Task ReorderParameters_ColumnWithoutLine_SameIndentOverloads_ThrowsSymbolAmbiguous()
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
        var operation = new ReorderParametersOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ReorderParametersParams
            {
                SourceFile = workspace.SourcePath,
                MethodName = "Foo",
                NewOrder = new[] { 1, 0 },
                Column = column
            }));

        Assert.Equal(ErrorCodes.SymbolAmbiguous, ex.ErrorCode);
        Assert.Equal("2004", ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("bool unused", (await File.ReadAllTextAsync(workspace.SourcePath)).Replace("\r\n", "\n"));
    }

    [SkippableFact]
    public async Task ReorderParameters_Preview_Column_DescribesRewriteAndWritesNothing()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SameLineOverloadsSource);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new ReorderParametersOperation(workspace.Context);
        var line = FindLine(SameLineOverloadsSource, "public void Process(int x, bool flag) { }");
        var secondColumn = ColumnOf(SameLineOverloadsSource, "Process(int x, int y, bool extra)");

        var result = await operation.ExecuteAsync(new ReorderParametersParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Process",
            NewOrder = new[] { 1, 0, 2 },
            Line = line,
            Column = secondColumn,
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.Contains(result.PendingChanges, change =>
            change.AfterSnippet != null &&
            change.AfterSnippet.Contains("Process(int y, int x, bool extra)", StringComparison.Ordinal) &&
            change.AfterSnippet.Contains("bool flag", StringComparison.Ordinal) &&
            !change.AfterSnippet.Contains("Process(int x, int y, bool extra)", StringComparison.Ordinal));
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task ReorderParameters_Column_UpdateOverridesAndImplementations_StillUpdatesChain()
    {
        const string source = """
            namespace TestApp;

            public interface IWorker
            {
                void Process(int count, string name);
            }

            public class Worker : IWorker
            {
                public virtual void Process(int count, string name) { } public void Process(string name, bool unused) { }
            }

            public class Derived : Worker
            {
                public override void Process(int count, string name)
                {
                }
            }

            public static class Runner
            {
                public static void Run(IWorker worker, Derived derived, Worker host)
                {
                    worker.Process(1, "a");
                    derived.Process(2, "b");
                    host.Process("c", false);
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ReorderParametersOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ReorderParametersParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Process",
            NewOrder = new[] { 1, 0 },
            Line = FindLine(source, "public virtual void Process"),
            Column = ColumnOf(source, "Process(int count, string name) { }"),
            UpdateOverrides = true,
            UpdateImplementations = true
        });

        Assert.True(result.Success);
        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("void Process(string name, int count);", text);
        Assert.Contains("public virtual void Process(string name, int count)", text);
        Assert.Contains("public override void Process(string name, int count)", text);
        Assert.Contains("public void Process(string name, bool unused)", text);
        Assert.Contains("worker.Process(\"a\", 1)", text);
        Assert.Contains("derived.Process(\"b\", 2)", text);
        Assert.Contains("host.Process(\"c\", false)", text);
    }

    [SkippableFact]
    public async Task ReorderParameters_Column_UpdateOverridesAndImplementationsFalse_OnlySelectedMethod()
    {
        const string source = """
            namespace TestApp;

            public interface IWorker
            {
                void Process(int count, string name);
            }

            public class Worker : IWorker
            {
                public virtual void Process(int count, string name) { } public void Process(string name, bool unused) { }
            }

            public class Derived : Worker
            {
                public override void Process(int count, string name)
                {
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ReorderParametersOperation(workspace.Context);

        // Column picks the non-virtual same-line sibling so false flags stay
        // compiling: only that declaration changes. The virtual / interface /
        // override chain is left alone — same rewrite rules as today.
        var result = await operation.ExecuteAsync(new ReorderParametersParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Process",
            NewOrder = new[] { 1, 0 },
            Line = FindLine(source, "public virtual void Process"),
            Column = ColumnOf(source, "Process(string name, bool unused)"),
            UpdateOverrides = false,
            UpdateImplementations = false
        });

        Assert.True(result.Success);
        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("void Process(int count, string name);", text);
        Assert.Contains("public virtual void Process(int count, string name)", text);
        Assert.Contains("public override void Process(int count, string name)", text);
        Assert.Contains("public void Process(bool unused, string name)", text);
        Assert.DoesNotContain("Process(string name, bool unused)", text);
    }

    #endregion

    #region Helpers

    private static ReorderParametersParams ValidParams(
        string? sourceFile = null,
        string methodName = "Process",
        int[]? newOrder = null) => new()
        {
            SourceFile = sourceFile ?? Path.Combine(Path.GetTempPath(), "RoslynMcpReorderParametersMissing.cs"),
            MethodName = methodName,
            NewOrder = newOrder ?? new[] { 1, 0 }
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

            var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpReorderParameters_" + Guid.NewGuid().ToString("N"));
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

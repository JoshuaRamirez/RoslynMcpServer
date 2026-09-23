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


    [Fact]
    public void Validate_AllFilesFalse_WithoutSourceFile_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ReorderParametersOperation.Validate(new ReorderParametersParams
            {
                AllFiles = false,
                MethodName = "Process",
                NewOrder = new[] { 1, 0 }
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public void Validate_AllFilesTrue_WithoutSourceFileOrMethodName_DoesNotThrow()
    {
        ReorderParametersOperation.Validate(new ReorderParametersParams
        {
            AllFiles = true,
            NewOrder = new[] { 1, 0 }
        });
    }

    [Fact]
    public void Validate_AllFilesTrue_WithRelativeSourceFile_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ReorderParametersOperation.Validate(new ReorderParametersParams
            {
                AllFiles = true,
                SourceFile = "Worker.cs",
                NewOrder = new[] { 1, 0 }
            }));

        Assert.Equal(ErrorCodes.InvalidSourcePath, ex.ErrorCode);
    }

    [Fact]
    public void Validate_AllFilesTrue_WithNonCSharpSourceFile_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ReorderParametersOperation.Validate(new ReorderParametersParams
            {
                AllFiles = true,
                SourceFile = Path.Combine(Path.GetTempPath(), "Worker.txt"),
                NewOrder = new[] { 1, 0 }
            }));

        Assert.Equal(ErrorCodes.InvalidSourcePath, ex.ErrorCode);
    }

    [Fact]
    public void Validate_AllFilesTrue_WithMissingSourceFile_DoesNotThrow()
    {
        ReorderParametersOperation.Validate(new ReorderParametersParams
        {
            AllFiles = true,
            SourceFile = Path.Combine(Path.GetTempPath(), "RoslynMcpReorderMissingAllFiles.cs"),
            NewOrder = new[] { 1, 0 }
        });
    }

    [Fact]
    public void Validate_AllFilesTrue_WithMethodName_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ReorderParametersOperation.Validate(new ReorderParametersParams
            {
                AllFiles = true,
                MethodName = "Process",
                NewOrder = new[] { 1, 0 }
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("methodName", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_AllFilesTrue_WithLine_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ReorderParametersOperation.Validate(new ReorderParametersParams
            {
                AllFiles = true,
                Line = 1,
                NewOrder = new[] { 1, 0 }
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public void Validate_AllFilesTrue_WithColumn_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ReorderParametersOperation.Validate(new ReorderParametersParams
            {
                AllFiles = true,
                Column = 1,
                NewOrder = new[] { 1, 0 }
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public void BuildAllFilesDescription_SingularAndPlural()
    {
        Assert.Equal("Reorder parameters", ReorderParametersOperation.BuildAllFilesDescription(1));
        Assert.Equal("Reorder parameters on 2 methods", ReorderParametersOperation.BuildAllFilesDescription(2));
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

    #region allFiles

    private const string EligibleFileA = """
        public class FileA
        {
            public void Process(int count, string name) { }
            public void Other(int count, string name) { }
            public void AlreadyTwo(int a, int b, int c) { }
        }
        """;

    private const string EligibleFileB = """
        public class FileB
        {
            public void Process(int count, string name) { }
        }
        """;

    private const string IneligibleFileC = """
        public class FileC
        {
            public void Single(int count) { }
            public void Triple(int a, int b, int c) { }
        }
        """;

    [SkippableFact]
    public async Task ReorderParameters_OmittedAllFiles_KeepsSingleSiteRewrite()
    {
        const string source = """
            public class Worker
            {
                public void Process(int count, string name) { }
                public void Other(int count, string name) { }
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
        var updated = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Equal(["name", "count"], ParameterNames(GetMethods(updated, "Process").Single()));
        Assert.Equal(["count", "name"], ParameterNames(GetMethods(updated, "Other").Single()));
    }

    [SkippableFact]
    public async Task ReorderParameters_AllFilesTrue_AppliesToEligibleMethodsAcrossFiles()
    {
        await using var workspace = await TempWorkspace.CreateAsync(
            ("FileA.cs", EligibleFileA),
            ("FileB.cs", EligibleFileB),
            ("FileC.cs", IneligibleFileC));
        var operation = new ReorderParametersOperation(workspace.Context);
        var pathA = Path.Combine(workspace.DirectoryPath, "FileA.cs");
        var pathB = Path.Combine(workspace.DirectoryPath, "FileB.cs");
        var pathC = Path.Combine(workspace.DirectoryPath, "FileC.cs");
        var beforeC = await File.ReadAllTextAsync(pathC);

        var result = await operation.ExecuteAsync(new ReorderParametersParams
        {
            AllFiles = true,
            NewOrder = new[] { 1, 0 }
        });

        Assert.True(result.Success);
        Assert.False(result.Preview);
        var updatedA = await File.ReadAllTextAsync(pathA);
        var updatedB = await File.ReadAllTextAsync(pathB);
        Assert.Equal(["name", "count"], ParameterNames(GetMethods(updatedA, "Process").Single()));
        Assert.Equal(["name", "count"], ParameterNames(GetMethods(updatedA, "Other").Single()));
        Assert.Equal(["a", "b", "c"], ParameterNames(GetMethods(updatedA, "AlreadyTwo").Single()));
        Assert.Equal(["name", "count"], ParameterNames(GetMethods(updatedB, "Process").Single()));
        Assert.Equal(beforeC, await File.ReadAllTextAsync(pathC));
        Assert.True(result.Changes!.FilesModified.Count >= 2);
        Assert.Contains(result.Changes.FilesModified, p => PathsEqual(p, pathA));
        Assert.Contains(result.Changes.FilesModified, p => PathsEqual(p, pathB));
        Assert.DoesNotContain(result.Changes.FilesModified, p => PathsEqual(p, pathC));
    }

    [SkippableFact]
    public async Task ReorderParameters_AllFilesTrue_WithoutSourceFileOrMethodName_Succeeds()
    {
        await using var workspace = await TempWorkspace.CreateAsync(
            ("FileA.cs", EligibleFileA),
            ("FileB.cs", EligibleFileB));
        var operation = new ReorderParametersOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ReorderParametersParams
        {
            AllFiles = true,
            NewOrder = new[] { 1, 0 }
        });

        Assert.True(result.Success);
        Assert.True(result.Changes!.FilesModified.Count >= 2);
    }

    [SkippableFact]
    public async Task ReorderParameters_AllFilesFalse_WithoutSourceFile_MissingRequiredParam()
    {
        await using var workspace = await TempWorkspace.CreateAsync(EligibleFileA);
        var operation = new ReorderParametersOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ReorderParametersParams
            {
                AllFiles = false,
                MethodName = "Process",
                NewOrder = new[] { 1, 0 }
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("sourceFile", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task ReorderParameters_AllFilesTrue_WithMethodName_Rejects()
    {
        await using var workspace = await TempWorkspace.CreateAsync(EligibleFileA);
        var operation = new ReorderParametersOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ReorderParametersParams
            {
                AllFiles = true,
                MethodName = "Process",
                NewOrder = new[] { 1, 0 }
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("methodName", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task ReorderParameters_PreviewAllFiles_AggregatesChangedFilesAndWritesNothing()
    {
        await using var workspace = await TempWorkspace.CreateAsync(
            ("FileA.cs", EligibleFileA),
            ("FileB.cs", EligibleFileB),
            ("FileC.cs", IneligibleFileC));
        var operation = new ReorderParametersOperation(workspace.Context);
        var pathA = Path.Combine(workspace.DirectoryPath, "FileA.cs");
        var pathB = Path.Combine(workspace.DirectoryPath, "FileB.cs");
        var pathC = Path.Combine(workspace.DirectoryPath, "FileC.cs");
        var beforeA = await File.ReadAllTextAsync(pathA);
        var beforeB = await File.ReadAllTextAsync(pathB);
        var beforeC = await File.ReadAllTextAsync(pathC);

        var result = await operation.ExecuteAsync(new ReorderParametersParams
        {
            AllFiles = true,
            NewOrder = new[] { 1, 0 },
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
    public async Task ReorderParameters_AllFilesTrue_EveryFileIneligible_SucceedsWithEmptyChanges()
    {
        await using var workspace = await TempWorkspace.CreateAsync(
            ("FileC.cs", IneligibleFileC),
            ("FileC2.cs", IneligibleFileC));
        var operation = new ReorderParametersOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ReorderParametersParams
        {
            AllFiles = true,
            NewOrder = new[] { 1, 0 }
        });

        Assert.True(result.Success);
        Assert.Empty(result.Changes!.FilesModified);
    }

    [SkippableFact]
    public async Task ReorderParameters_AllFilesTrue_OptionalSourceFile_LimitsWalk()
    {
        await using var workspace = await TempWorkspace.CreateAsync(
            ("FileA.cs", EligibleFileA),
            ("FileB.cs", EligibleFileB),
            ("FileC.cs", IneligibleFileC));
        var operation = new ReorderParametersOperation(workspace.Context);
        var pathA = Path.Combine(workspace.DirectoryPath, "FileA.cs");
        var pathB = Path.Combine(workspace.DirectoryPath, "FileB.cs");
        var beforeB = await File.ReadAllTextAsync(pathB);

        var result = await operation.ExecuteAsync(new ReorderParametersParams
        {
            AllFiles = true,
            SourceFile = pathA,
            NewOrder = new[] { 1, 0 }
        });

        Assert.True(result.Success);
        var updatedA = await File.ReadAllTextAsync(pathA);
        Assert.Equal(["name", "count"], ParameterNames(GetMethods(updatedA, "Process").Single()));
        Assert.Equal(beforeB, await File.ReadAllTextAsync(pathB));
        Assert.Contains(result.Changes!.FilesModified, p => PathsEqual(p, pathA));
        Assert.DoesNotContain(result.Changes.FilesModified, p => PathsEqual(p, pathB));
    }

    [SkippableFact]
    public async Task ReorderParameters_AllFilesTrue_SkipsWhenTargetWouldMatchOverload()
    {
        const string source = """
            public class Worker
            {
                public void Process(int a, string b) { }
                public void Process(string b, int a) { }
            }
            """;
        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ReorderParametersOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var result = await operation.ExecuteAsync(new ReorderParametersParams
        {
            AllFiles = true,
            NewOrder = new[] { 1, 0 }
        });

        Assert.True(result.Success);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Empty(result.Changes!.FilesModified);
    }

    [SkippableFact]
    public async Task ReorderParameters_AllFilesTrue_SkipsOverrideEqualsRatherThanBreakingContract()
    {
        const string source = """
            public class Worker
            {
                public override bool Equals(object? unused) => false;
                public void NeedsIt(int count, string name) { }
            }
            """;
        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ReorderParametersOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ReorderParametersParams
        {
            AllFiles = true,
            NewOrder = new[] { 1, 0 }
        });

        Assert.True(result.Success);
        var updated = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("Equals(object? unused)", updated.Replace("\r", ""));
        Assert.Equal(["name", "count"], ParameterNames(GetMethods(updated, "NeedsIt").Single()));
    }

    [SkippableFact]
    public async Task ReorderParameters_AllFilesTrue_SkipsVirtualBaseWithOverrides()
    {
        const string source = """
            public class Base
            {
                public virtual void Process(int count, string name) { }
            }

            public class Derived : Base
            {
                public override void Process(int count, string name) { }
            }
            """;
        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ReorderParametersOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var result = await operation.ExecuteAsync(new ReorderParametersParams
        {
            AllFiles = true,
            NewOrder = new[] { 1, 0 }
        });

        Assert.True(result.Success);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task ReorderParameters_AllFilesTrue_SkipsWhenArityMismatch()
    {
        const string source = """
            public class Worker
            {
                public void AlreadyThree(int a, int b, int c) { }
                public void NeedsIt(int count, string name) { }
            }
            """;
        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ReorderParametersOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ReorderParametersParams
        {
            AllFiles = true,
            NewOrder = new[] { 1, 0 }
        });

        Assert.True(result.Success);
        var updated = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Equal(["a", "b", "c"], ParameterNames(GetMethods(updated, "AlreadyThree").Single()));
        Assert.Equal(["name", "count"], ParameterNames(GetMethods(updated, "NeedsIt").Single()));
    }

    [SkippableFact]
    public async Task ReorderParameters_AllFilesTrue_SkipsModuleInitializer()
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
                public static void NeedsIt(int count, string name) { }
            }
            """;
        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ReorderParametersOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ReorderParametersParams
        {
            AllFiles = true,
            NewOrder = new[] { 1, 0 }
        });

        Assert.True(result.Success);
        var updated = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("voidInit()", updated.Replace(" ", "").Replace("\r", ""));
        Assert.Equal(["name", "count"], ParameterNames(GetMethods(updated, "NeedsIt").Single()));
    }

    [SkippableFact]
    public async Task ReorderParameters_AllFilesTrue_SkipsUnmanagedCallersOnly()
    {
        const string source = """
            namespace TestApp;
            using System.Runtime.InteropServices;
            public static class Native
            {
                [UnmanagedCallersOnly]
                public static void Entry(int count, string name) { }
                public static void NeedsIt(int count, string name) { }
            }
            """;
        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ReorderParametersOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ReorderParametersParams
        {
            AllFiles = true,
            NewOrder = new[] { 1, 0 }
        });

        Assert.True(result.Success);
        var updated = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("Entry(int count, string name)", updated.Replace("\r", ""));
        Assert.Equal(["name", "count"], ParameterNames(GetMethods(updated, "NeedsIt").Single()));
    }

    [SkippableFact]
    public async Task ReorderParameters_AllFilesTrue_SkipsExtern()
    {
        const string source = """
            using System.Runtime.InteropServices;
            public static class Native
            {
                [DllImport("demo")]
                public static extern void Import(int count, string name);
                public static void NeedsIt(int count, string name) { }
            }
            """;
        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ReorderParametersOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ReorderParametersParams
        {
            AllFiles = true,
            NewOrder = new[] { 1, 0 }
        });

        Assert.True(result.Success);
        var updated = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("Import(int count, string name)", updated.Replace("\r", ""));
        Assert.Equal(["name", "count"], ParameterNames(GetMethods(updated, "NeedsIt").Single()));
    }

    [SkippableFact]
    public async Task ReorderParameters_AllFilesTrue_SkipsInterfaceImplementation()
    {
        const string source = """
            public interface IWorker
            {
                void Process(int count, string name);
            }

            public class Worker : IWorker
            {
                public void Process(int count, string name) { }
                public void NeedsIt(int count, string name) { }
            }
            """;
        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ReorderParametersOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ReorderParametersParams
        {
            AllFiles = true,
            NewOrder = new[] { 1, 0 }
        });

        Assert.True(result.Success);
        var updated = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("void Process(int count, string name);", updated);
        Assert.Contains("public void Process(int count, string name)", updated);
        Assert.Equal(["name", "count"], ParameterNames(GetMethods(updated, "NeedsIt").Single()));
    }

    [SkippableFact]
    public async Task ReorderParameters_AllFilesTrue_SkipsPartial()
    {
        const string source = """
            public partial class Worker
            {
                public partial void Process(int count, string name);
            }

            public partial class Worker
            {
                public partial void Process(int count, string name) { }
                public void NeedsIt(int count, string name) { }
            }
            """;
        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ReorderParametersOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ReorderParametersParams
        {
            AllFiles = true,
            NewOrder = new[] { 1, 0 }
        });

        Assert.True(result.Success);
        var updated = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("partial void Process(int count, string name)", updated);
        Assert.Equal(["name", "count"], ParameterNames(GetMethods(updated, "NeedsIt").Single()));
    }

    [SkippableFact]
    public async Task ReorderParameters_AllFilesTrue_SkipsWhenCallSiteOnLinkedMultiViewPath()
    {
        const string sharedSource = """
            namespace TestApp;

            public static class Caller
            {
                public static void Use() => Worker.Process(1, "x");
            }
            """;
        const string anchorASource = """
            namespace TestApp;

            public static class Worker
            {
                public static void Process(int count, string name) { }
            }
            """;
        const string anchorBSource = """
            namespace TestApp;

            public static class Worker
            {
                public static void Process(int count, string name) { }
            }
            """;

        await using var workspace = await TempWorkspace.CreateWithLinkedProjectsAsync(
            sharedSource, anchorASource, anchorBSource);
        var counts = ReorderParametersOperation.BuildLinkedPathCounts(workspace.Context.Solution);
        var sharedKey = RoslynMcp.Core.FileSystem.PathResolver.GetPathComparisonKey(
            workspace.SourcePaths["Shared.cs"]);
        Assert.True(counts.TryGetValue(sharedKey, out var sharedCount) && sharedCount > 1);

        var beforeShared = await File.ReadAllTextAsync(workspace.SourcePaths["Shared.cs"]);
        var beforeA = await File.ReadAllTextAsync(workspace.SourcePaths["AnchorA.cs"]);
        var beforeB = await File.ReadAllTextAsync(workspace.SourcePaths["AnchorB.cs"]);

        var operation = new ReorderParametersOperation(workspace.Context);
        var result = await operation.ExecuteAsync(new ReorderParametersParams
        {
            AllFiles = true,
            NewOrder = new[] { 1, 0 }
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
        public Dictionary<string, string> SourcePaths { get; init; } = new(StringComparer.Ordinal);
        public required WorkspaceContext Context { get; init; }

        public static Task<TempWorkspace> CreateAsync(string source, string fileName = "Worker.cs") =>
            CreateAsync((fileName, source));

        public static async Task<TempWorkspace> CreateAsync(params (string FileName, string Source)[] files)
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

            var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpReorderParametersLinked_" + Guid.NewGuid().ToString("N"));
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

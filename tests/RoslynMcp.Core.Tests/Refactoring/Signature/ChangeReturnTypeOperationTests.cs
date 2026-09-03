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
/// Operation-level tests for <see cref="ChangeReturnTypeOperation"/>.
/// </summary>
public class ChangeReturnTypeOperationTests
{
    #region Input Validation

    [Fact]
    public void Validate_MissingSourceFile_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ChangeReturnTypeOperation.Validate(ValidParams(sourceFile: "")));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public void Validate_MissingMethodName_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ChangeReturnTypeOperation.Validate(ValidParams(methodName: "")));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public void Validate_MissingNewReturnType_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ChangeReturnTypeOperation.Validate(ValidParams(newReturnType: "")));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public void Validate_RelativePath_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ChangeReturnTypeOperation.Validate(ValidParams(sourceFile: "Worker.cs")));

        Assert.Equal(ErrorCodes.InvalidSourcePath, ex.ErrorCode);
    }

    [Fact]
    public void Validate_MissingFile_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ChangeReturnTypeOperation.Validate(ValidParams()));

        Assert.Equal(ErrorCodes.SourceFileNotFound, ex.ErrorCode);
    }

    [Fact]
    public void Validate_InvalidTypeSyntax_Throws()
    {
        var path = Path.Combine(Path.GetTempPath(), "RoslynMcpChangeReturnInvalidType.cs");
        File.WriteAllText(path, "class C {}");
        try
        {
            var ex = Assert.Throws<RefactoringException>(() =>
                ChangeReturnTypeOperation.Validate(ValidParams(sourceFile: path, newReturnType: "int int")));

            Assert.Equal(ErrorCodes.InvalidReturnType, ex.ErrorCode);
            Assert.Equal("1015", ex.ErrorCode);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void IsValidReturnType_RejectsInvalidSyntax()
    {
        Assert.False(ChangeReturnTypeOperation.IsValidReturnType("int int"));
        Assert.False(ChangeReturnTypeOperation.IsValidReturnType("@@@"));
        Assert.True(ChangeReturnTypeOperation.IsValidReturnType("int"));
        Assert.True(ChangeReturnTypeOperation.IsValidReturnType("void"));
        Assert.True(ChangeReturnTypeOperation.IsValidReturnType("List<string>"));
    }

    #endregion

    #region Happy Path

    [SkippableFact]
    public async Task ChangeReturnType_SimpleChange_UpdatesDeclaration()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                public int Process()
                {
                    return 1;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ChangeReturnTypeOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ChangeReturnTypeParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Process",
            NewReturnType = "long"
        });

        Assert.True(result.Success);
        Assert.False(result.Preview);

        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("public long Process()", text);
        Assert.Contains("return 1;", text);
        Assert.DoesNotContain("public int Process()", text);
    }

    [SkippableFact]
    public async Task ChangeReturnType_ImplicitConversion_LeavesReturnExpression()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                public int Process()
                {
                    return 1;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ChangeReturnTypeOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ChangeReturnTypeParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Process",
            NewReturnType = "object"
        });

        Assert.True(result.Success);

        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("public object Process()", text);
        Assert.Contains("return 1;", text);
    }

    [SkippableFact]
    public async Task ChangeReturnType_NonVoidToVoid_StripsReturnExpressions()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                public int Process()
                {
                    return 1;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ChangeReturnTypeOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ChangeReturnTypeParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Process",
            NewReturnType = "void"
        });

        Assert.True(result.Success);

        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("public void Process()", text);
        Assert.Contains("return;", text);
        Assert.DoesNotContain("return 1;", text);
    }

    [SkippableFact]
    public async Task ChangeReturnType_VoidToNonVoid_AddsDefaultReturn()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                public void Process()
                {
                    System.Console.WriteLine("hi");
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ChangeReturnTypeOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ChangeReturnTypeParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Process",
            NewReturnType = "int"
        });

        Assert.True(result.Success);

        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("public int Process()", text);
        Assert.Contains("return default(int);", text);
    }

    [SkippableFact]
    public async Task ChangeReturnType_VoidReturnToNonVoid_ReplacesBareReturn()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                public void Process(bool skip)
                {
                    if (skip)
                        return;
                    System.Console.WriteLine("go");
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ChangeReturnTypeOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ChangeReturnTypeParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Process",
            NewReturnType = "int"
        });

        Assert.True(result.Success);

        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("public int Process(bool skip)", text);
        Assert.Contains("return default(int);", text);
        Assert.DoesNotContain("\n            return;", text);
    }

    [SkippableFact]
    public async Task ChangeReturnType_OverrideAndInterface_UpdatesChain()
    {
        const string source = """
            namespace TestApp;

            public interface IWorker
            {
                int Process();
            }

            public class Worker : IWorker
            {
                public virtual int Process()
                {
                    return 1;
                }
            }

            public class Derived : Worker
            {
                public override int Process()
                {
                    return 2;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ChangeReturnTypeOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ChangeReturnTypeParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Process",
            NewReturnType = "object",
            Line = 10
        });

        Assert.True(result.Success);

        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("object Process();", text);
        Assert.Contains("public virtual object Process()", text);
        Assert.Contains("public override object Process()", text);
        Assert.Contains("return 1;", text);
        Assert.Contains("return 2;", text);
        Assert.DoesNotContain("int Process()", text);
        Assert.DoesNotContain("int Process();", text);
    }

    [SkippableFact]
    public async Task ChangeReturnType_Preview_ReturnsChangesAndWritesNothing()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                public int Process()
                {
                    return 1;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var original = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new ChangeReturnTypeOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ChangeReturnTypeParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Process",
            NewReturnType = "long",
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.NotEmpty(result.PendingChanges);
        Assert.Contains(result.PendingChanges, c =>
            c.AfterSnippet != null &&
            c.AfterSnippet.Contains("public long Process()") &&
            c.AfterSnippet.Contains("return 1;"));

        var after = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Equal(original, after);
    }

    [SkippableFact]
    public async Task ChangeReturnType_ExpressionBodiedVoidToValue_AddsDefault()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                public void Process() => System.Console.WriteLine("hi");
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ChangeReturnTypeOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ChangeReturnTypeParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Process",
            NewReturnType = "int"
        });

        Assert.True(result.Success);

        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("public int Process()", text);
        Assert.Contains("return default(int);", text);
    }

    #endregion

    #region Rejects

    [SkippableFact]
    public async Task ChangeReturnType_SameType_ThrowsAndWritesNothing()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                public int Process()
                {
                    return 1;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var original = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new ChangeReturnTypeOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ChangeReturnTypeParams
            {
                SourceFile = workspace.SourcePath,
                MethodName = "Process",
                NewReturnType = "int"
            }));

        Assert.Equal(ErrorCodes.SameLocation, ex.ErrorCode);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task ChangeReturnType_Incompatible_ThrowsAndWritesNothing()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                public string Process()
                {
                    return "hi";
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var original = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new ChangeReturnTypeOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ChangeReturnTypeParams
            {
                SourceFile = workspace.SourcePath,
                MethodName = "Process",
                NewReturnType = "int"
            }));

        Assert.Equal(ErrorCodes.ReturnTypeIncompatible, ex.ErrorCode);
        Assert.Equal("3133", ex.ErrorCode);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task ChangeReturnType_OverloadCollision_ThrowsAndWritesNothing()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                public int Process()
                {
                    return 1;
                }

                public string Process()
                {
                    return "a";
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var original = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new ChangeReturnTypeOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ChangeReturnTypeParams
            {
                SourceFile = workspace.SourcePath,
                MethodName = "Process",
                NewReturnType = "string",
                Line = 5
            }));

        Assert.Equal(ErrorCodes.SignatureMatchesOverload, ex.ErrorCode);
        Assert.Equal("3132", ex.ErrorCode);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task ChangeReturnType_MethodGroup_ThrowsAndWritesNothing()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                public int Process()
                {
                    return 1;
                }

                public void Run()
                {
                    System.Func<int> fn = Process;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var original = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new ChangeReturnTypeOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ChangeReturnTypeParams
            {
                SourceFile = workspace.SourcePath,
                MethodName = "Process",
                NewReturnType = "object"
            }));

        Assert.Equal(ErrorCodes.UnsupportedCallSite, ex.ErrorCode);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task ChangeReturnType_MissingMethod_Throws()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                public int Other() => 1;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ChangeReturnTypeOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ChangeReturnTypeParams
            {
                SourceFile = workspace.SourcePath,
                MethodName = "Process",
                NewReturnType = "long"
            }));

        Assert.Equal(ErrorCodes.MethodNotFound, ex.ErrorCode);
    }

    [Fact]
    public void ChangeReturnType_UneditableDocument_Throws()
    {
        var workspace = new AdhocWorkspace();
        var project = workspace.AddProject("P", LanguageNames.CSharp);
        var document = workspace.AddDocument(project.Id, "Generated.cs", SourceText.From("class C {}"));

        var ex = Assert.Throws<RefactoringException>(() =>
            ChangeReturnTypeOperation.ValidateDocumentIsEditable(document, workspace));

        Assert.Equal(ErrorCodes.DocumentNotEditable, ex.ErrorCode);
    }

    [SkippableFact]
    public async Task ChangeReturnType_ConvertDisabledVoidToValue_Throws()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                public void Process()
                {
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var original = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new ChangeReturnTypeOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ChangeReturnTypeParams
            {
                SourceFile = workspace.SourcePath,
                MethodName = "Process",
                NewReturnType = "int",
                ConvertReturnStatements = false
            }));

        Assert.Equal(ErrorCodes.CannotConvertReturn, ex.ErrorCode);
        Assert.Equal("3134", ex.ErrorCode);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task ChangeReturnType_InvocationResultContext_ThrowsAndWritesNothing()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                public int Process()
                {
                    return 1;
                }

                public void Run()
                {
                    int value = Process();
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var original = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new ChangeReturnTypeOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ChangeReturnTypeParams
            {
                SourceFile = workspace.SourcePath,
                MethodName = "Process",
                NewReturnType = "long"
            }));

        Assert.Equal(ErrorCodes.ReturnTypeIncompatible, ex.ErrorCode);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task ChangeReturnType_DiscardedInvocation_AllowsWidening()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                public int Process()
                {
                    return 1;
                }

                public void Run()
                {
                    Process();
                    var inferred = Process();
                    object boxed = Process();
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ChangeReturnTypeOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ChangeReturnTypeParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Process",
            NewReturnType = "long"
        });

        Assert.True(result.Success);
        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("public long Process()", text);
    }

    [SkippableFact]
    public async Task ChangeReturnType_UneditableOverrideContract_ThrowsAndWritesNothing()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                public override int GetHashCode()
                {
                    return 1;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var original = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new ChangeReturnTypeOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ChangeReturnTypeParams
            {
                SourceFile = workspace.SourcePath,
                MethodName = "GetHashCode",
                NewReturnType = "long"
            }));

        Assert.Equal(ErrorCodes.ReturnTypeIncompatible, ex.ErrorCode);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task ChangeReturnType_UneditableInterfaceContract_ThrowsAndWritesNothing()
    {
        const string source = """
            namespace TestApp;

            public class Worker : System.IDisposable
            {
                public void Dispose()
                {
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var original = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new ChangeReturnTypeOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ChangeReturnTypeParams
            {
                SourceFile = workspace.SourcePath,
                MethodName = "Dispose",
                NewReturnType = "int"
            }));

        Assert.Equal(ErrorCodes.ReturnTypeIncompatible, ex.ErrorCode);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task ChangeReturnType_AsyncTask_ThrowsAndWritesNothing()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                public async System.Threading.Tasks.Task Process()
                {
                    return;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var original = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new ChangeReturnTypeOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ChangeReturnTypeParams
            {
                SourceFile = workspace.SourcePath,
                MethodName = "Process",
                NewReturnType = "System.Threading.Tasks.Task<int>"
            }));

        Assert.Equal(ErrorCodes.AsyncReturnTypeUnsupported, ex.ErrorCode);
        Assert.Equal("3135", ex.ErrorCode);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task ChangeReturnType_Iterator_ThrowsAndWritesNothing()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                public System.Collections.Generic.IEnumerable<int> Process()
                {
                    yield return 1;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var original = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new ChangeReturnTypeOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ChangeReturnTypeParams
            {
                SourceFile = workspace.SourcePath,
                MethodName = "Process",
                NewReturnType = "System.Collections.Generic.IEnumerable<string>"
            }));

        Assert.Equal(ErrorCodes.ContainsYield, ex.ErrorCode);
        Assert.Equal("3031", ex.ErrorCode);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task ChangeReturnType_QualifiesTypeInOtherDocument()
    {
        const string worker = """
            using Text = System.String;

            namespace TestApp;

            public class Worker
            {
                public virtual object Process()
                {
                    return "";
                }
            }
            """;
        const string derived = """
            namespace TestApp;

            public class Derived : Worker
            {
                public override object Process()
                {
                    return "";
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(
            ("Worker.cs", worker),
            ("Derived.cs", derived));
        var operation = new ChangeReturnTypeOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ChangeReturnTypeParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Process",
            NewReturnType = "Text",
            Line = 7
        });

        Assert.True(result.Success);

        var workerText = await File.ReadAllTextAsync(workspace.SourcePath);
        var derivedText = await File.ReadAllTextAsync(Path.Combine(workspace.DirectoryPath, "Derived.cs"));
        Assert.Contains("virtual Text Process()", workerText);
        Assert.DoesNotContain("Text Process()", derivedText);
        Assert.True(
            derivedText.Contains("override string Process()") ||
            derivedText.Contains("override System.String Process()"),
            "Derived file should use a context-valid string type, not the originating alias.\n" + derivedText);
    }

    #endregion

    #region Covering-span column

    private const string SameLineOverloadsSource = """
        namespace TestApp;

        public class Worker
        {
            public int Process(int x) { return x; } public int Process(int x, int y) { return x + y; }
        }
        """;

    [Fact]
    public void Validate_InvalidColumn_Throws()
    {
        var path = Path.Combine(Path.GetTempPath(), "RoslynMcpChangeReturnTypeInvalidColumn.cs");
        File.WriteAllText(path, "class C {}");
        try
        {
            var ex = Assert.Throws<RefactoringException>(() =>
                ChangeReturnTypeOperation.Validate(new ChangeReturnTypeParams
                {
                    SourceFile = path,
                    MethodName = "Process",
                    NewReturnType = "long",
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
        var path = Path.Combine(Path.GetTempPath(), "RoslynMcpChangeReturnTypeNegativeColumn.cs");
        File.WriteAllText(path, "class C {}");
        try
        {
            var ex = Assert.Throws<RefactoringException>(() =>
                ChangeReturnTypeOperation.Validate(new ChangeReturnTypeParams
                {
                    SourceFile = path,
                    MethodName = "Process",
                    NewReturnType = "long",
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
        var line = FindLine(SameLineOverloadsSource, "public int Process(int x) { return x; }");
        var first = ChangeReturnTypeOperation.FindMethod(
            root, "Process", line, ColumnOf(SameLineOverloadsSource, "Process(int x) { return x; }"));
        var second = ChangeReturnTypeOperation.FindMethod(
            root, "Process", line, ColumnOf(SameLineOverloadsSource, "Process(int x, int y) { return x + y; }"));
        var omitted = ChangeReturnTypeOperation.FindMethod(root, "Process", line, column: null);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Null(omitted);
        Assert.Equal(["x"], ParameterNames(first));
        Assert.Equal(["x", "y"], ParameterNames(second));
    }

    [Fact]
    public void FindMethod_ColumnOnContinuationLine_PicksMethod()
    {
        const string source = """
            class C
            {
                public int
                Process(int x) { return x; }

                public int Process(int x, int y) { return x + y; }
            }
            """;

        var tree = CSharpSyntaxTree.ParseText(source);
        var root = tree.GetRoot();
        var startLine = FindLine(source, "public int");
        var identifierLine = FindLine(source, "Process(int x) { return x; }");
        Assert.NotEqual(startLine, identifierLine);

        // Omitted column keeps today's start-line filter — the split
        // signature does not start on the identifier line. Column still
        // selects it.
        var byStartLineOnly = ChangeReturnTypeOperation.FindMethod(root, "Process", identifierLine, column: null);
        var byColumn = ChangeReturnTypeOperation.FindMethod(
            root, "Process", identifierLine, ColumnOf(source, "Process(int x) { return x; }"));

        Assert.Null(byStartLineOnly);
        Assert.NotNull(byColumn);
        Assert.Equal(["x"], ParameterNames(byColumn));
    }

    [Fact]
    public void FindMethod_AdjacentMethods_ExclusiveEndDoesNotStealNextMethod()
    {
        const string source = """
            class C
            {
                public int Other(int x){return x;}public int Process(int x){return x;}
            }
            """;

        var tree = CSharpSyntaxTree.ParseText(source);
        var root = tree.GetRoot();
        var line = FindLine(source, "public int Other");
        var secondStart = ColumnOf(source, "public int Process");
        var secondId = ColumnOf(source, "Process(int x){return x;}");

        var atSecondStart = ChangeReturnTypeOperation.FindMethod(root, "Process", line, secondStart);
        var atSecondId = ChangeReturnTypeOperation.FindMethod(root, "Process", line, secondId);
        var atFirstId = ChangeReturnTypeOperation.FindMethod(root, "Other", line, ColumnOf(source, "Other(int x)"));
        var firstAtSecondStart = ChangeReturnTypeOperation.FindMethod(root, "Other", line, secondStart);

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
        const string source = "class C { public int A(int x){return x;}public int B(int x){return x;} }";
        var tree = CSharpSyntaxTree.ParseText(source);
        var method = tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>()
            .First(m => m.Identifier.Text == "A");
        var span = method.GetLocation().GetLineSpan();
        var line = span.StartLinePosition.Line + 1;
        var startCol = span.StartLinePosition.Character + 1;
        var endCol = span.EndLinePosition.Character + 1;

        Assert.True(ChangeReturnTypeOperation.SpanCoversColumn(span, line, startCol));
        Assert.True(ChangeReturnTypeOperation.SpanCoversColumn(span, line, endCol - 1));
        Assert.False(ChangeReturnTypeOperation.SpanCoversColumn(span, line, endCol));
        Assert.False(ChangeReturnTypeOperation.SpanCoversColumn(span, line, startCol - 1));
    }

    [SkippableFact]
    public async Task ChangeReturnType_OmittedColumn_SameLineOverloads_ThrowsSymbolAmbiguous()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SameLineOverloadsSource);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new ChangeReturnTypeOperation(workspace.Context);
        var line = FindLine(SameLineOverloadsSource, "public int Process(int x) { return x; }");

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ChangeReturnTypeParams
            {
                SourceFile = workspace.SourcePath,
                MethodName = "Process",
                NewReturnType = "long",
                Line = line
            }));

        Assert.Equal(ErrorCodes.SymbolAmbiguous, ex.ErrorCode);
        Assert.Equal("2004", ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task ChangeReturnType_Column_SelectsSecondOverloadOnSameLine()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SameLineOverloadsSource);
        var operation = new ChangeReturnTypeOperation(workspace.Context);
        var line = FindLine(SameLineOverloadsSource, "public int Process(int x) { return x; }");
        var secondColumn = ColumnOf(SameLineOverloadsSource, "Process(int x, int y) { return x + y; }");

        var result = await operation.ExecuteAsync(new ChangeReturnTypeParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Process",
            NewReturnType = "long",
            Line = line,
            Column = secondColumn
        });

        Assert.True(result.Success);
        var updated = await File.ReadAllTextAsync(workspace.SourcePath);
        var processMethods = GetMethods(updated, "Process");
        Assert.Equal(2, processMethods.Count);
        Assert.Contains(processMethods, m => ParameterNames(m) is ["x"] && ReturnTypeText(m) == "int");
        Assert.Contains(processMethods, m => ParameterNames(m) is ["x", "y"] && ReturnTypeText(m) == "long");
        Assert.DoesNotContain(processMethods, m => ParameterNames(m) is ["x", "y"] && ReturnTypeText(m) == "int");
    }

    [SkippableFact]
    public async Task ChangeReturnType_Column_SelectsFirstOverloadOnSameLine()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SameLineOverloadsSource);
        var operation = new ChangeReturnTypeOperation(workspace.Context);
        var line = FindLine(SameLineOverloadsSource, "public int Process(int x) { return x; }");
        var firstColumn = ColumnOf(SameLineOverloadsSource, "Process(int x) { return x; }");

        var result = await operation.ExecuteAsync(new ChangeReturnTypeParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Process",
            NewReturnType = "long",
            Line = line,
            Column = firstColumn
        });

        Assert.True(result.Success);
        var updated = await File.ReadAllTextAsync(workspace.SourcePath);
        var processMethods = GetMethods(updated, "Process");
        Assert.Equal(2, processMethods.Count);
        Assert.Contains(processMethods, m => ParameterNames(m) is ["x"] && ReturnTypeText(m) == "long");
        Assert.Contains(processMethods, m => ParameterNames(m) is ["x", "y"] && ReturnTypeText(m) == "int");
        Assert.DoesNotContain(processMethods, m => ParameterNames(m) is ["x"] && ReturnTypeText(m) == "int");
    }

    [SkippableFact]
    public async Task ChangeReturnType_ColumnOnContinuationLine_ChangesThatMethod()
    {
        const string source = """
            namespace TestApp;

            public class Split
            {
                public int
                Process(int x) { return x; }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ChangeReturnTypeOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ChangeReturnTypeParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Process",
            NewReturnType = "long",
            Line = FindLine(source, "Process(int x) { return x; }"),
            Column = ColumnOf(source, "Process(int x) { return x; }")
        });

        Assert.True(result.Success);
        var updated = (await File.ReadAllTextAsync(workspace.SourcePath)).Replace("\r\n", "\n");
        Assert.Contains("public long", updated);
        Assert.Contains("Process(int x)", updated);
        Assert.DoesNotContain("public int\n                Process", updated);
    }

    [SkippableFact]
    public async Task ChangeReturnType_OmittedColumn_ContinuationLineIdentifier_ThrowsMethodNotFound()
    {
        const string source = """
            namespace TestApp;

            public class Split
            {
                public int
                Process(int x) { return x; }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new ChangeReturnTypeOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ChangeReturnTypeParams
            {
                SourceFile = workspace.SourcePath,
                MethodName = "Process",
                NewReturnType = "long",
                Line = FindLine(source, "Process(int x) { return x; }")
            }));

        Assert.Equal(ErrorCodes.MethodNotFound, ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task ChangeReturnType_AdjacentMethods_ColumnOnSecondDoesNotRewriteFirst()
    {
        const string source = """
            namespace TestApp;

            public class Adjacent
            {
                public int Other(int x){return x;}public int Process(int x){return x;}
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ChangeReturnTypeOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ChangeReturnTypeParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Process",
            NewReturnType = "long",
            Line = FindLine(source, "public int Other"),
            Column = ColumnOf(source, "Process(int x){return x;}")
        });

        Assert.True(result.Success);
        var updated = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("public int Other(int x)", updated);
        Assert.Contains("public long Process(int x)", updated);
        Assert.DoesNotContain("public long Other", updated);
        Assert.DoesNotContain("public int Process", updated);
    }

    [SkippableFact]
    public async Task ChangeReturnType_ColumnWithoutLine_SameIndentOverloads_ThrowsSymbolAmbiguous()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                public int Foo(int x)
                {
                    return x;
                }

                public int Foo(int x, int y)
                {
                    return x + y;
                }
            }
            """;

        var column = ColumnOf(source, "Foo(int x)");
        Assert.Equal(column, ColumnOf(source, "Foo(int x, int y)"));

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new ChangeReturnTypeOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ChangeReturnTypeParams
            {
                SourceFile = workspace.SourcePath,
                MethodName = "Foo",
                NewReturnType = "long",
                Column = column
            }));

        Assert.Equal(ErrorCodes.SymbolAmbiguous, ex.ErrorCode);
        Assert.Equal("2004", ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task ChangeReturnType_Preview_Column_DescribesRewriteAndWritesNothing()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SameLineOverloadsSource);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new ChangeReturnTypeOperation(workspace.Context);
        var line = FindLine(SameLineOverloadsSource, "public int Process(int x) { return x; }");
        var secondColumn = ColumnOf(SameLineOverloadsSource, "Process(int x, int y) { return x + y; }");

        var result = await operation.ExecuteAsync(new ChangeReturnTypeParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Process",
            NewReturnType = "long",
            Line = line,
            Column = secondColumn,
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.Contains(result.PendingChanges, change =>
            change.AfterSnippet != null &&
            change.AfterSnippet.Contains("long Process(int x, int y)", StringComparison.Ordinal) &&
            change.AfterSnippet.Contains("int Process(int x)", StringComparison.Ordinal) &&
            !change.AfterSnippet.Contains("int Process(int x, int y)", StringComparison.Ordinal));
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task ChangeReturnType_Column_UpdateOverridesAndImplementations_StillUpdatesChain()
    {
        const string source = """
            namespace TestApp;

            public interface IWorker
            {
                int Process();
            }

            public class Worker : IWorker
            {
                public virtual int Process() { return 1; } public int Process(string name) { return 2; }
            }

            public class Derived : Worker
            {
                public override int Process()
                {
                    return 3;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ChangeReturnTypeOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ChangeReturnTypeParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Process",
            NewReturnType = "object",
            Line = FindLine(source, "public virtual int Process"),
            Column = ColumnOf(source, "Process() { return 1; }"),
            UpdateOverrides = true,
            UpdateImplementations = true
        });

        Assert.True(result.Success);
        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("object Process();", text);
        Assert.Contains("public virtual object Process()", text);
        Assert.Contains("public override object Process()", text);
        Assert.Contains("public int Process(string name)", text);
        Assert.Contains("return 1;", text);
        Assert.Contains("return 2;", text);
        Assert.Contains("return 3;", text);
        Assert.DoesNotContain("int Process();", text);
        Assert.DoesNotContain("virtual int Process()", text);
        Assert.DoesNotContain("override int Process()", text);
    }

    [SkippableFact]
    public async Task ChangeReturnType_Column_UpdateOverridesAndImplementationsFalse_OnlySelectedMethod()
    {
        const string source = """
            namespace TestApp;

            public interface IWorker
            {
                int Process();
            }

            public class Worker : IWorker
            {
                public virtual int Process() { return 1; } public int Process(string name) { return 2; }
            }

            public class Derived : Worker
            {
                public override int Process()
                {
                    return 3;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ChangeReturnTypeOperation(workspace.Context);

        // Column picks the non-virtual same-line sibling so false flags stay
        // compiling: only that declaration changes. The virtual / interface /
        // override chain is left alone — same rewrite rules as today.
        var result = await operation.ExecuteAsync(new ChangeReturnTypeParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Process",
            NewReturnType = "long",
            Line = FindLine(source, "public virtual int Process"),
            Column = ColumnOf(source, "Process(string name) { return 2; }"),
            UpdateOverrides = false,
            UpdateImplementations = false
        });

        Assert.True(result.Success);
        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("int Process();", text);
        Assert.Contains("public virtual int Process()", text);
        Assert.Contains("public override int Process()", text);
        Assert.Contains("public long Process(string name)", text);
        Assert.DoesNotContain("public int Process(string name)", text);
    }

    [SkippableFact]
    public async Task ChangeReturnType_Column_ConvertReturnStatements_StillConvertsSelected()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                public int Process(int x) { return x; } public void Process(int x, int y) { }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ChangeReturnTypeOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ChangeReturnTypeParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Process",
            NewReturnType = "int",
            Line = FindLine(source, "public int Process(int x)"),
            Column = ColumnOf(source, "Process(int x, int y)"),
            ConvertReturnStatements = true
        });

        Assert.True(result.Success);
        var updated = await File.ReadAllTextAsync(workspace.SourcePath);
        var processMethods = GetMethods(updated, "Process");
        Assert.Equal(2, processMethods.Count);
        Assert.Contains(processMethods, m => ParameterNames(m) is ["x"] && ReturnTypeText(m) == "int");
        Assert.Contains(processMethods, m => ParameterNames(m) is ["x", "y"] && ReturnTypeText(m) == "int");
        Assert.Contains("return default(int);", updated.Replace("\r\n", "\n"));
        Assert.DoesNotContain("void Process", updated);
    }

    [SkippableFact]
    public async Task ChangeReturnType_Column_ConvertReturnStatementsFalse_ThrowsAndWritesNothing()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                public int Process(int x) { return x; } public void Process(int x, int y) { }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new ChangeReturnTypeOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ChangeReturnTypeParams
            {
                SourceFile = workspace.SourcePath,
                MethodName = "Process",
                NewReturnType = "int",
                Line = FindLine(source, "public int Process(int x)"),
                Column = ColumnOf(source, "Process(int x, int y)"),
                ConvertReturnStatements = false
            }));

        Assert.Equal(ErrorCodes.CannotConvertReturn, ex.ErrorCode);
        Assert.Equal("3134", ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    #endregion

    #region Helpers

    private static List<MethodDeclarationSyntax> GetMethods(string source, string methodName) =>
        CSharpSyntaxTree.ParseText(source).GetRoot()
            .DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Where(m => m.Identifier.Text == methodName)
            .ToList();

    private static string[] ParameterNames(MethodDeclarationSyntax method) =>
        method.ParameterList.Parameters.Select(p => p.Identifier.Text).ToArray();

    private static string ReturnTypeText(MethodDeclarationSyntax method) =>
        method.ReturnType.ToString();

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

    private static ChangeReturnTypeParams ValidParams(
        string? sourceFile = null,
        string methodName = "Process",
        string newReturnType = "long") => new()
        {
            SourceFile = sourceFile ?? Path.Combine(Path.GetTempPath(), "RoslynMcpChangeReturnTypeMissing.cs"),
            MethodName = methodName,
            NewReturnType = newReturnType
        };

    private sealed class TempWorkspace : IAsyncDisposable
    {
        public required string DirectoryPath { get; init; }
        public required string ProjectPath { get; init; }
        public required string SourcePath { get; init; }
        public required WorkspaceContext Context { get; init; }

        public static Task<TempWorkspace> CreateAsync(string source, string fileName = "Worker.cs") =>
            CreateAsync((fileName, source));

        public static async Task<TempWorkspace> CreateAsync(params (string FileName, string Source)[] files)
        {
            Skip.IfNot(ModuleInitializer.MsBuildAvailable, ModuleInitializer.MsBuildError ?? "MSBuild not available");

            var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpChangeReturnType_" + Guid.NewGuid().ToString("N"));
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

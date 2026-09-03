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
/// Operation-level tests for <see cref="AddParameterOperation"/>.
/// </summary>
public class AddParameterOperationTests
{
    #region Input Validation

    [Fact]
    public void Validate_MissingSourceFile_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            AddParameterOperation.Validate(ValidParams(sourceFile: "")));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public void Validate_MissingMethodName_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            AddParameterOperation.Validate(ValidParams(methodName: "")));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public void Validate_MissingParameterName_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            AddParameterOperation.Validate(ValidParams(parameterName: "")));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public void Validate_MissingParameterType_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            AddParameterOperation.Validate(ValidParams(parameterType: "")));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public void Validate_RelativePath_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            AddParameterOperation.Validate(ValidParams(sourceFile: "Worker.cs")));

        Assert.Equal(ErrorCodes.InvalidSourcePath, ex.ErrorCode);
    }

    [Fact]
    public void Validate_MissingFile_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            AddParameterOperation.Validate(ValidParams()));

        Assert.Equal(ErrorCodes.SourceFileNotFound, ex.ErrorCode);
    }

    [Fact]
    public void Validate_InvalidParameterType_Throws()
    {
        var path = Path.Combine(Path.GetTempPath(), "RoslynMcpAddParameterInvalidType.cs");
        File.WriteAllText(path, "class C {}");
        try
        {
            var ex = Assert.Throws<RefactoringException>(() =>
                AddParameterOperation.Validate(ValidParams(sourceFile: path, parameterType: "int int")));

            Assert.Equal(ErrorCodes.InvalidParameterType, ex.ErrorCode);
            Assert.Equal("1010", ex.ErrorCode);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Validate_InvalidPosition_Throws()
    {
        var path = Path.Combine(Path.GetTempPath(), "RoslynMcpAddParameterInvalidPos.cs");
        File.WriteAllText(path, "class C {}");
        try
        {
            var ex = Assert.Throws<RefactoringException>(() =>
                AddParameterOperation.Validate(ValidParams(sourceFile: path, position: -2)));

            Assert.Equal(ErrorCodes.InvalidParameterPosition, ex.ErrorCode);
            Assert.Equal("1011", ex.ErrorCode);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void IsValidParameterType_RejectsUnparseableTypes()
    {
        Assert.True(AddParameterOperation.IsValidParameterType("int"));
        Assert.True(AddParameterOperation.IsValidParameterType("string?"));
        Assert.True(AddParameterOperation.IsValidParameterType("List<int>"));
        Assert.False(AddParameterOperation.IsValidParameterType(""));
        Assert.False(AddParameterOperation.IsValidParameterType("int int"));
        Assert.False(AddParameterOperation.IsValidParameterType("???"));
    }

    #endregion

    #region Happy Path

    [SkippableFact]
    public async Task AddParameter_SimpleAdd_AppendsAtEnd()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                public void Process(int count)
                {
                }

                public void Run()
                {
                    Process(3);
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new AddParameterOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new AddParameterParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Process",
            ParameterName = "label",
            ParameterType = "string",
            DefaultValue = "\"ok\""
        });

        Assert.True(result.Success);
        Assert.False(result.Preview);

        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("public void Process(int count, string label = \"ok\")", text);
        Assert.Contains("Process(3, \"ok\")", text);
    }

    [SkippableFact]
    public async Task AddParameter_DefaultValue_UpdatesCallSites()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                public int Process(int count)
                {
                    return count;
                }

                public int Run() => Process(3) + Process(4);
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new AddParameterOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new AddParameterParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Process",
            ParameterName = "timeout",
            ParameterType = "int",
            DefaultValue = "30"
        });

        Assert.True(result.Success);
        Assert.Equal(2, result.ReferencesUpdated);

        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("public int Process(int count, int timeout = 30)", text);
        Assert.Contains("Process(3, 30)", text);
        Assert.Contains("Process(4, 30)", text);
    }

    [SkippableFact]
    public async Task AddParameter_NamedArgs_InsertsNamedArgument()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                public void Process(int count, string name)
                {
                }

                public void Run()
                {
                    Process(count: 3, name: "a");
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new AddParameterOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new AddParameterParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Process",
            ParameterName = "flag",
            ParameterType = "bool",
            DefaultValue = "true",
            Position = 1
        });

        Assert.True(result.Success);

        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("public void Process(int count, bool flag, string name)", text);
        Assert.Contains("flag:", text);
        Assert.Contains("true", text);
        Assert.Contains("name:", text);
    }

    [SkippableFact]
    public async Task AddParameter_OverrideAndInterface_UpdatesChain()
    {
        const string source = """
            namespace TestApp;

            public interface IWorker
            {
                void Process(int count);
            }

            public class WorkerBase : IWorker
            {
                public virtual void Process(int count)
                {
                }
            }

            public class Worker : WorkerBase
            {
                public override void Process(int count)
                {
                }

                public void Run(IWorker worker)
                {
                    worker.Process(1);
                    Process(2);
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new AddParameterOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new AddParameterParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Process",
            ParameterName = "timeout",
            ParameterType = "int",
            DefaultValue = "30",
            Line = 17
        });

        Assert.True(result.Success);

        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("void Process(int count, int timeout = 30);", text);
        Assert.Contains("public virtual void Process(int count, int timeout = 30)", text);
        Assert.Contains("public override void Process(int count, int timeout = 30)", text);
        Assert.Contains("worker.Process(1, 30)", text);
        Assert.Contains("Process(2, 30)", text);
    }

    [SkippableFact]
    public async Task AddParameter_Preview_ReturnsChangesAndWritesNothing()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                public void Process(int count)
                {
                }

                public void Run() => Process(3);
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var original = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new AddParameterOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new AddParameterParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Process",
            ParameterName = "label",
            ParameterType = "string",
            DefaultValue = "\"ok\"",
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.NotEmpty(result.PendingChanges);
        Assert.Contains(result.PendingChanges, c =>
            c.AfterSnippet != null &&
            c.AfterSnippet.Contains("string label") &&
            c.AfterSnippet.Contains("Process(3, \"ok\")"));

        var after = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Equal(original, after);
    }

    [SkippableFact]
    public async Task AddParameter_BeforeParams_KeepsParamsLast()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                public void Process(int count, params string[] rest)
                {
                }

                public void Run() => Process(1, "a", "b");
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new AddParameterOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new AddParameterParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Process",
            ParameterName = "label",
            ParameterType = "string",
            DefaultValue = "\"x\""
        });

        Assert.True(result.Success);

        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("public void Process(int count, string label = \"x\", params string[] rest)", text);
    }

    [SkippableFact]
    public async Task AddParameter_SameNameOnOtherType_LeavesUnselectedMethodUntouched()
    {
        const string source = """
            namespace TestApp;

            public class Alpha
            {
                public void Process(int count)
                {
                }

                public void Run() => Process(1);
            }

            public class Beta
            {
                public void Process(string name)
                {
                }

                public void Run() => Process("x");
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new AddParameterOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new AddParameterParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Process",
            ParameterName = "label",
            ParameterType = "string",
            DefaultValue = "\"ok\"",
            Line = 5
        });

        Assert.True(result.Success);

        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("public void Process(int count, string label = \"ok\")", text);
        Assert.Contains("public void Run() => Process(1, \"ok\");", text);
        Assert.Contains("public void Process(string name)", text);
        Assert.Contains("public void Run() => Process(\"x\");", text);
        Assert.DoesNotContain("Process(string name, string label", text);
        Assert.DoesNotContain("Process(\"x\", \"ok\")", text);
    }

    #endregion

    #region Rejects

    [SkippableFact]
    public async Task AddParameter_DuplicateName_Throws()
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
        var operation = new AddParameterOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new AddParameterParams
            {
                SourceFile = workspace.SourcePath,
                MethodName = "Process",
                ParameterName = "count",
                ParameterType = "string"
            }));

        Assert.Equal(ErrorCodes.ParameterAlreadyExists, ex.ErrorCode);
        Assert.Equal("3127", ex.ErrorCode);
    }

    [SkippableFact]
    public async Task AddParameter_PositionOutOfRange_Throws()
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
        var operation = new AddParameterOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new AddParameterParams
            {
                SourceFile = workspace.SourcePath,
                MethodName = "Process",
                ParameterName = "label",
                ParameterType = "string",
                Position = 4
            }));

        Assert.Equal(ErrorCodes.InvalidParameterPosition, ex.ErrorCode);
        Assert.Equal("1011", ex.ErrorCode);
    }

    [SkippableFact]
    public async Task AddParameter_RequiredAfterOptional_Throws()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                public void Process(int count = 1)
                {
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new AddParameterOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new AddParameterParams
            {
                SourceFile = workspace.SourcePath,
                MethodName = "Process",
                ParameterName = "label",
                ParameterType = "string",
                Position = 1
            }));

        Assert.Equal(ErrorCodes.RequiredAfterOptional, ex.ErrorCode);
        Assert.Equal("3128", ex.ErrorCode);
    }

    [SkippableFact]
    public async Task AddParameter_ParamsNotLast_Throws()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                public void Process(params int[] values)
                {
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new AddParameterOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new AddParameterParams
            {
                SourceFile = workspace.SourcePath,
                MethodName = "Process",
                ParameterName = "label",
                ParameterType = "string",
                Position = 1
            }));

        Assert.Equal(ErrorCodes.ParamsNotLast, ex.ErrorCode);
        Assert.Equal("3129", ex.ErrorCode);
    }

    [SkippableFact]
    public async Task AddParameter_MissingMethod_Throws()
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
        var operation = new AddParameterOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new AddParameterParams
            {
                SourceFile = workspace.SourcePath,
                MethodName = "DoesNotExist",
                ParameterName = "label",
                ParameterType = "string"
            }));

        Assert.Equal(ErrorCodes.MethodNotFound, ex.ErrorCode);
    }

    [Fact]
    public void AddParameter_UneditableDocument_Throws()
    {
        var workspace = new AdhocWorkspace();
        var project = workspace.AddProject("P", LanguageNames.CSharp);
        var document = workspace.AddDocument(project.Id, "Generated.cs", SourceText.From("class C {}"));

        var ex = Assert.Throws<RefactoringException>(() =>
            AddParameterOperation.ValidateDocumentIsEditable(document, workspace));

        Assert.Equal(ErrorCodes.DocumentNotEditable, ex.ErrorCode);
    }

    [SkippableFact]
    public async Task AddParameter_MethodGroup_ThrowsAndWritesNothing()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                public int Process(int count) => count;

                public void Run()
                {
                    System.Func<int, int> handler = Process;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var original = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new AddParameterOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new AddParameterParams
            {
                SourceFile = workspace.SourcePath,
                MethodName = "Process",
                ParameterName = "timeout",
                ParameterType = "int",
                DefaultValue = "30"
            }));

        Assert.Equal(ErrorCodes.UnsupportedCallSite, ex.ErrorCode);
        Assert.Equal("3130", ex.ErrorCode);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task AddParameter_NonConstantDefault_DateTimeNow_Throws()
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
        var original = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new AddParameterOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new AddParameterParams
            {
                SourceFile = workspace.SourcePath,
                MethodName = "Process",
                ParameterName = "when",
                ParameterType = "System.DateTime",
                DefaultValue = "System.DateTime.Now"
            }));

        Assert.Equal(ErrorCodes.InvalidDefaultValue, ex.ErrorCode);
        Assert.Equal("1013", ex.ErrorCode);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task AddParameter_NonConstantDefault_NewObject_Throws()
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
        var original = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new AddParameterOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new AddParameterParams
            {
                SourceFile = workspace.SourcePath,
                MethodName = "Process",
                ParameterName = "state",
                ParameterType = "object",
                DefaultValue = "new object()"
            }));

        Assert.Equal(ErrorCodes.InvalidDefaultValue, ex.ErrorCode);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task AddParameter_IncompatibleDefault_Throws()
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
        var original = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new AddParameterOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new AddParameterParams
            {
                SourceFile = workspace.SourcePath,
                MethodName = "Process",
                ParameterName = "timeout",
                ParameterType = "int",
                DefaultValue = "\"hello\""
            }));

        Assert.Equal(ErrorCodes.InvalidDefaultValue, ex.ErrorCode);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    #endregion

    #region Covering-span column

    private const string SameLineOverloadsSource = """
        namespace TestApp;

        public class Worker
        {
            public void Process(int x) { } public void Process(int x, int y) { }
        }
        """;

    [Fact]
    public void Validate_InvalidColumn_Throws()
    {
        var path = Path.Combine(Path.GetTempPath(), "RoslynMcpAddParameterInvalidColumn.cs");
        File.WriteAllText(path, "class C {}");
        try
        {
            var ex = Assert.Throws<RefactoringException>(() =>
                AddParameterOperation.Validate(new AddParameterParams
                {
                    SourceFile = path,
                    MethodName = "Process",
                    ParameterName = "timeout",
                    ParameterType = "int",
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
        var path = Path.Combine(Path.GetTempPath(), "RoslynMcpAddParameterNegativeColumn.cs");
        File.WriteAllText(path, "class C {}");
        try
        {
            var ex = Assert.Throws<RefactoringException>(() =>
                AddParameterOperation.Validate(new AddParameterParams
                {
                    SourceFile = path,
                    MethodName = "Process",
                    ParameterName = "timeout",
                    ParameterType = "int",
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
        var line = FindLine(SameLineOverloadsSource, "public void Process(int x) { }");
        var first = AddParameterOperation.FindMethod(
            root, "Process", line, ColumnOf(SameLineOverloadsSource, "Process(int x) { }"));
        var second = AddParameterOperation.FindMethod(
            root, "Process", line, ColumnOf(SameLineOverloadsSource, "Process(int x, int y)"));
        var omitted = AddParameterOperation.FindMethod(root, "Process", line, column: null);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Null(omitted);
        Assert.Single(first.ParameterList.Parameters);
        Assert.Equal(2, second.ParameterList.Parameters.Count);
    }

    [Fact]
    public void FindMethod_ColumnOnContinuationLine_PicksMethod()
    {
        const string source = """
            class C
            {
                public void
                Process(int x) { }

                public void Process(int x, int y) { }
            }
            """;

        var tree = CSharpSyntaxTree.ParseText(source);
        var root = tree.GetRoot();
        var startLine = FindLine(source, "public void");
        var identifierLine = FindLine(source, "Process(int x) { }");
        Assert.NotEqual(startLine, identifierLine);

        // Omitted column keeps today's start-line filter — the split
        // signature does not start on the identifier line. Column still
        // selects it.
        var byStartLineOnly = AddParameterOperation.FindMethod(root, "Process", identifierLine, column: null);
        var byColumn = AddParameterOperation.FindMethod(
            root, "Process", identifierLine, ColumnOf(source, "Process(int x) { }"));

        Assert.Null(byStartLineOnly);
        Assert.NotNull(byColumn);
        Assert.Single(byColumn.ParameterList.Parameters);
    }

    [Fact]
    public void FindMethod_AdjacentMethods_ExclusiveEndDoesNotStealNextMethod()
    {
        const string source = """
            class C
            {
                public void Other(int x){}public void Process(int x){}
            }
            """;

        var tree = CSharpSyntaxTree.ParseText(source);
        var root = tree.GetRoot();
        var line = FindLine(source, "public void Other");
        var secondStart = ColumnOf(source, "public void Process");
        var secondId = ColumnOf(source, "Process(int x)");

        var atSecondStart = AddParameterOperation.FindMethod(root, "Process", line, secondStart);
        var atSecondId = AddParameterOperation.FindMethod(root, "Process", line, secondId);
        var atFirstId = AddParameterOperation.FindMethod(root, "Other", line, ColumnOf(source, "Other(int x)"));
        var firstAtSecondStart = AddParameterOperation.FindMethod(root, "Other", line, secondStart);

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

        Assert.True(AddParameterOperation.SpanCoversColumn(span, line, startCol));
        Assert.True(AddParameterOperation.SpanCoversColumn(span, line, endCol - 1));
        Assert.False(AddParameterOperation.SpanCoversColumn(span, line, endCol));
        Assert.False(AddParameterOperation.SpanCoversColumn(span, line, startCol - 1));
    }

    [SkippableFact]
    public async Task AddParameter_OmittedColumn_SameLineOverloads_ThrowsSymbolAmbiguous()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SameLineOverloadsSource);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new AddParameterOperation(workspace.Context);
        var line = FindLine(SameLineOverloadsSource, "public void Process(int x) { }");

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new AddParameterParams
            {
                SourceFile = workspace.SourcePath,
                MethodName = "Process",
                ParameterName = "flag",
                ParameterType = "bool",
                DefaultValue = "true",
                Line = line
            }));

        Assert.Equal(ErrorCodes.SymbolAmbiguous, ex.ErrorCode);
        Assert.Equal("2004", ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task AddParameter_Column_SelectsSecondOverloadOnSameLine()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SameLineOverloadsSource);
        var operation = new AddParameterOperation(workspace.Context);
        var line = FindLine(SameLineOverloadsSource, "public void Process(int x) { }");
        var secondColumn = ColumnOf(SameLineOverloadsSource, "Process(int x, int y)");

        var result = await operation.ExecuteAsync(new AddParameterParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Process",
            ParameterName = "flag",
            ParameterType = "bool",
            DefaultValue = "true",
            Line = line,
            Column = secondColumn
        });

        Assert.True(result.Success);
        var updated = await File.ReadAllTextAsync(workspace.SourcePath);
        var processMethods = GetMethods(updated, "Process");
        Assert.Equal(2, processMethods.Count);
        Assert.Contains(processMethods, m => ParameterNames(m) is ["x"]);
        Assert.Contains(processMethods, m => ParameterNames(m) is ["x", "y", "flag"]);
        Assert.DoesNotContain(processMethods, m => ParameterNames(m) is ["x", "flag"]);
    }

    [SkippableFact]
    public async Task AddParameter_Column_SelectsFirstOverloadOnSameLine()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SameLineOverloadsSource);
        var operation = new AddParameterOperation(workspace.Context);
        var line = FindLine(SameLineOverloadsSource, "public void Process(int x) { }");
        var firstColumn = ColumnOf(SameLineOverloadsSource, "Process(int x) { }");

        var result = await operation.ExecuteAsync(new AddParameterParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Process",
            ParameterName = "flag",
            ParameterType = "bool",
            DefaultValue = "true",
            Line = line,
            Column = firstColumn
        });

        Assert.True(result.Success);
        var updated = await File.ReadAllTextAsync(workspace.SourcePath);
        var processMethods = GetMethods(updated, "Process");
        Assert.Equal(2, processMethods.Count);
        Assert.Contains(processMethods, m => ParameterNames(m) is ["x", "flag"]);
        Assert.Contains(processMethods, m => ParameterNames(m) is ["x", "y"]);
        Assert.DoesNotContain(processMethods, m => ParameterNames(m) is ["x", "y", "flag"]);
    }

    [SkippableFact]
    public async Task AddParameter_ColumnOnContinuationLine_ChangesThatMethod()
    {
        const string source = """
            namespace TestApp;

            public class Split
            {
                public void
                Process(int x) { }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new AddParameterOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new AddParameterParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Process",
            ParameterName = "flag",
            ParameterType = "bool",
            DefaultValue = "true",
            Line = FindLine(source, "Process(int x)"),
            Column = ColumnOf(source, "Process(int x)")
        });

        Assert.True(result.Success);
        var updated = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("bool flag", updated);
        Assert.Contains("Process(int x, bool flag = true)", updated.Replace("\r\n", "\n"));
    }

    [SkippableFact]
    public async Task AddParameter_AdjacentMethods_ColumnOnSecondDoesNotRewriteFirst()
    {
        const string source = """
            namespace TestApp;

            public class Adjacent
            {
                public void Other(int x){}public void Process(int x){}
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new AddParameterOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new AddParameterParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Process",
            ParameterName = "flag",
            ParameterType = "bool",
            DefaultValue = "true",
            Line = FindLine(source, "public void Other"),
            Column = ColumnOf(source, "Process(int x)")
        });

        Assert.True(result.Success);
        var updated = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("public void Other(int x)", updated);
        Assert.Contains("public void Process(int x, bool flag = true)", updated);
        Assert.DoesNotContain("Other(int x, bool flag", updated);
    }

    [SkippableFact]
    public async Task AddParameter_ColumnWithoutLine_SameIndentOverloads_ThrowsSymbolAmbiguous()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                public void Foo(int x)
                {
                }

                public void Foo(int x, int y)
                {
                }
            }
            """;

        var column = ColumnOf(source, "Foo(int x)");
        Assert.Equal(column, ColumnOf(source, "Foo(int x, int y)"));

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new AddParameterOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new AddParameterParams
            {
                SourceFile = workspace.SourcePath,
                MethodName = "Foo",
                ParameterName = "flag",
                ParameterType = "bool",
                DefaultValue = "true",
                Column = column
            }));

        Assert.Equal(ErrorCodes.SymbolAmbiguous, ex.ErrorCode);
        Assert.Equal("2004", ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.DoesNotContain("bool flag", (await File.ReadAllTextAsync(workspace.SourcePath)).Replace("\r\n", "\n"));
    }

    [SkippableFact]
    public async Task AddParameter_Preview_Column_DescribesRewriteAndWritesNothing()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SameLineOverloadsSource);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new AddParameterOperation(workspace.Context);
        var line = FindLine(SameLineOverloadsSource, "public void Process(int x) { }");
        var secondColumn = ColumnOf(SameLineOverloadsSource, "Process(int x, int y)");

        var result = await operation.ExecuteAsync(new AddParameterParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Process",
            ParameterName = "flag",
            ParameterType = "bool",
            DefaultValue = "true",
            Line = line,
            Column = secondColumn,
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.Contains(result.PendingChanges, change =>
            change.AfterSnippet != null &&
            change.AfterSnippet.Contains("flag", StringComparison.Ordinal) &&
            change.AfterSnippet.Contains("Process(int x, int y, bool flag", StringComparison.Ordinal));
        Assert.DoesNotContain(result.PendingChanges, change =>
            change.AfterSnippet != null &&
            change.AfterSnippet.Contains("Process(int x, bool flag", StringComparison.Ordinal));
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    #endregion

    #region Helpers

    private static AddParameterParams ValidParams(
        string? sourceFile = null,
        string methodName = "Process",
        string parameterName = "timeout",
        string parameterType = "int",
        int position = -1) => new()
        {
            SourceFile = sourceFile ?? Path.Combine(Path.GetTempPath(), "RoslynMcpAddParameterMissing.cs"),
            MethodName = methodName,
            ParameterName = parameterName,
            ParameterType = parameterType,
            Position = position
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

            var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpAddParameter_" + Guid.NewGuid().ToString("N"));
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

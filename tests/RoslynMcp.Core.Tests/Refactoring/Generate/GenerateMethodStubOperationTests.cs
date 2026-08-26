using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Generate;
using RoslynMcp.Core.Workspace;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Generate;

/// <summary>
/// Operation-level tests for <see cref="GenerateMethodStubOperation"/>.
/// </summary>
public class GenerateMethodStubOperationTests
{
    #region Input Validation

    [Fact]
    public void Validate_MissingSourceFile_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            GenerateMethodStubOperation.Validate(new GenerateMethodStubParams
            {
                SourceFile = "",
                Line = 1,
                Column = 1
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public void Validate_RelativePath_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            GenerateMethodStubOperation.Validate(new GenerateMethodStubParams
            {
                SourceFile = "Types.cs",
                Line = 1,
                Column = 1
            }));

        Assert.Equal(ErrorCodes.InvalidSourcePath, ex.ErrorCode);
    }

    [Fact]
    public void Validate_InvalidLine_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            GenerateMethodStubOperation.Validate(new GenerateMethodStubParams
            {
                SourceFile = AbsoluteTestPath(),
                Line = 0,
                Column = 1
            }));

        Assert.Equal(ErrorCodes.InvalidLineNumber, ex.ErrorCode);
    }

    [Fact]
    public void Validate_InvalidColumn_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            GenerateMethodStubOperation.Validate(new GenerateMethodStubParams
            {
                SourceFile = AbsoluteTestPath(),
                Line = 1,
                Column = 0
            }));

        Assert.Equal(ErrorCodes.InvalidColumnNumber, ex.ErrorCode);
    }

    [Fact]
    public void Validate_MissingFile_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            GenerateMethodStubOperation.Validate(new GenerateMethodStubParams
            {
                SourceFile = AbsoluteTestPath(),
                Line = 1,
                Column = 1
            }));

        Assert.Equal(ErrorCodes.SourceFileNotFound, ex.ErrorCode);
    }

    [Fact]
    public void Validate_InvalidVisibility_Throws()
    {
        var path = Path.Combine(Path.GetTempPath(), "RoslynMcpGenerateMethodStubVisibility.cs");
        File.WriteAllText(path, "class C {}");
        try
        {
            var ex = Assert.Throws<RefactoringException>(() =>
                GenerateMethodStubOperation.Validate(new GenerateMethodStubParams
                {
                    SourceFile = path,
                    Line = 1,
                    Column = 1,
                    Visibility = "secret"
                }));

            Assert.Equal(ErrorCodes.InvalidVisibility, ex.ErrorCode);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Validate_ReservedKeywordMethodName_Throws()
    {
        var path = Path.Combine(Path.GetTempPath(), "RoslynMcpGenerateMethodStubKeyword.cs");
        File.WriteAllText(path, "class C {}");
        try
        {
            var ex = Assert.Throws<RefactoringException>(() =>
                GenerateMethodStubOperation.Validate(new GenerateMethodStubParams
                {
                    SourceFile = path,
                    Line = 1,
                    Column = 1,
                    MethodName = "class"
                }));

            Assert.Equal(ErrorCodes.InvalidSymbolName, ex.ErrorCode);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void IsValidIdentifier_ReservedKeyword_IsFalse()
    {
        Assert.False(GenerateMethodStubOperation.IsValidIdentifier("class"));
        Assert.False(GenerateMethodStubOperation.IsValidIdentifier("namespace"));
        Assert.True(GenerateMethodStubOperation.IsValidIdentifier("DoWork"));
        Assert.True(GenerateMethodStubOperation.IsValidIdentifier("@class"));
    }

    #endregion

    #region P0 Happy Path

    [SkippableFact]
    public async Task GenerateMethodStub_VoidOnSameClass_AddsPrivateMethod()
    {
        const string source = """
            namespace TestApp;

            public class Widget
            {
                public void Run()
                {
                    DoWork();
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var (line, column) = FindIdentifier(source, "DoWork");
        var operation = new GenerateMethodStubOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateMethodStubParams
        {
            SourceFile = workspace.SourcePath,
            Line = line,
            Column = column
        });

        Assert.True(result.Success);
        Assert.False(result.Preview);
        Assert.NotNull(result.Symbol);
        Assert.Equal("DoWork", result.Symbol.Name);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("private void DoWork()", updated);
        Assert.Contains("throw new global::System.NotImplementedException();", updated);
    }

    [SkippableFact]
    public async Task GenerateMethodStub_Assignment_InfersReturnType()
    {
        const string source = """
            namespace TestApp;

            public class Widget
            {
                public void Run()
                {
                    int value = Compute();
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var (line, column) = FindIdentifier(source, "Compute");
        var operation = new GenerateMethodStubOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateMethodStubParams
        {
            SourceFile = workspace.SourcePath,
            Line = line,
            Column = column
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("private int Compute()", updated);
        Assert.Contains("throw new global::System.NotImplementedException();", updated);
    }

    [SkippableFact]
    public async Task GenerateMethodStub_Parameters_InfersArgumentTypesAndNames()
    {
        const string source = """
            namespace TestApp;

            public class Widget
            {
                public void Run(string name)
                {
                    Process(name, 42);
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var (line, column) = FindIdentifier(source, "Process");
        var operation = new GenerateMethodStubOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateMethodStubParams
        {
            SourceFile = workspace.SourcePath,
            Line = line,
            Column = column
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("private void Process(string name, int arg2)", updated);
        Assert.Contains("throw new global::System.NotImplementedException();", updated);
    }

    [SkippableFact]
    public async Task GenerateMethodStub_MethodOnOtherType_AddsPublicMethod()
    {
        const string source = """
            namespace TestApp;

            public class Caller
            {
                public void Run(Processor processor)
                {
                    processor.Transform();
                }
            }

            public class Processor
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var (line, column) = FindIdentifier(source, "Transform");
        var operation = new GenerateMethodStubOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateMethodStubParams
        {
            SourceFile = workspace.SourcePath,
            Line = line,
            Column = column
        });

        Assert.True(result.Success);
        Assert.Equal("Transform", result.Symbol!.Name);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("public void Transform()", updated);
        Assert.Contains("class Processor", updated);
        Assert.Contains("throw new global::System.NotImplementedException();", updated);
    }

    [SkippableFact]
    public async Task GenerateMethodStub_StaticTypeCall_AddsStaticMethod()
    {
        const string source = """
            namespace TestApp;

            public class Widget
            {
                public void Run()
                {
                    Helper.Compute(1);
                }
            }

            public class Helper
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var (line, column) = FindIdentifier(source, "Compute");
        var operation = new GenerateMethodStubOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateMethodStubParams
        {
            SourceFile = workspace.SourcePath,
            Line = line,
            Column = column
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("public static void Compute(int arg1)", updated);
        Assert.Contains("throw new global::System.NotImplementedException();", updated);
    }

    [SkippableFact]
    public async Task GenerateMethodStub_ThisReceiver_AddsPrivateMethodOnSameClass()
    {
        const string source = """
            namespace TestApp;

            public class Widget
            {
                public void Run()
                {
                    this.DoWork();
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var (line, column) = FindIdentifier(source, "DoWork");
        var operation = new GenerateMethodStubOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateMethodStubParams
        {
            SourceFile = workspace.SourcePath,
            Line = line,
            Column = column
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("private void DoWork()", updated);
    }

    [SkippableFact]
    public async Task GenerateMethodStub_Preview_DoesNotWriteFiles()
    {
        const string source = """
            namespace TestApp;

            public class Widget
            {
                public void Run()
                {
                    DoWork();
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var (line, column) = FindIdentifier(source, "DoWork");
        var operation = new GenerateMethodStubOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var result = await operation.ExecuteAsync(new GenerateMethodStubParams
        {
            SourceFile = workspace.SourcePath,
            Line = line,
            Column = column,
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.NotEmpty(result.PendingChanges);
        Assert.Contains("DoWork", result.PendingChanges[0].AfterSnippet);
        Assert.Contains("NotImplementedException", result.PendingChanges[0].AfterSnippet);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task GenerateMethodStub_ExplicitReturnType_OverridesInference()
    {
        const string source = """
            namespace TestApp;

            public class Widget
            {
                public void Run()
                {
                    DoWork();
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var (line, column) = FindIdentifier(source, "DoWork");
        var operation = new GenerateMethodStubOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateMethodStubParams
        {
            SourceFile = workspace.SourcePath,
            Line = line,
            Column = column,
            ReturnType = "string"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("private string DoWork()", updated);
    }

    #endregion

    #region Reject Cases

    [SkippableFact]
    public async Task GenerateMethodStub_NoCallSite_Throws()
    {
        const string source = """
            namespace TestApp;

            public class Widget
            {
                public void Run()
                {
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var (line, column) = FindIdentifier(source, "Widget");
        var operation = new GenerateMethodStubOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GenerateMethodStubParams
            {
                SourceFile = workspace.SourcePath,
                Line = line,
                Column = column
            }));

        Assert.Equal(ErrorCodes.MethodNotFound, ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task GenerateMethodStub_MethodAlreadyExists_Throws()
    {
        const string source = """
            namespace TestApp;

            public class Widget
            {
                public void Run()
                {
                    DoWork();
                }

                private void DoWork()
                {
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var (line, column) = FindIdentifier(source, "DoWork");
        var operation = new GenerateMethodStubOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GenerateMethodStubParams
            {
                SourceFile = workspace.SourcePath,
                Line = line,
                Column = column
            }));

        Assert.Equal(ErrorCodes.NameCollision, ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task GenerateMethodStub_CannotInferReturnType_Throws()
    {
        const string source = """
            namespace TestApp;

            public class Widget
            {
                public void Run()
                {
                    var value = Mystery();
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var (line, column) = FindIdentifier(source, "Mystery");
        var operation = new GenerateMethodStubOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GenerateMethodStubParams
            {
                SourceFile = workspace.SourcePath,
                Line = line,
                Column = column
            }));

        Assert.Equal(ErrorCodes.CannotInferReturnType, ex.ErrorCode);
        Assert.Contains("returnType", ex.Message);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task GenerateMethodStub_ExternalTarget_Throws()
    {
        const string source = """
            namespace TestApp;

            public class Widget
            {
                public void Run()
                {
                    "hello".Missing();
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var (line, column) = FindIdentifier(source, "Missing");
        var operation = new GenerateMethodStubOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GenerateMethodStubParams
            {
                SourceFile = workspace.SourcePath,
                Line = line,
                Column = column
            }));

        Assert.Equal(ErrorCodes.DocumentNotEditable, ex.ErrorCode);
        Assert.Contains("external", ex.Message);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [Fact]
    public void GenerateMethodStub_UneditableDocument_Throws()
    {
        var workspace = new AdhocWorkspace();
        var project = workspace.AddProject("P", LanguageNames.CSharp);
        var document = workspace.AddDocument(project.Id, "Generated.cs", SourceText.From("class C {}"));

        var ex = Assert.Throws<RefactoringException>(() =>
            GenerateMethodStubOperation.ValidateDocumentIsEditable(document, workspace));

        Assert.Equal(ErrorCodes.DocumentNotEditable, ex.ErrorCode);
    }

    [SkippableFact]
    public async Task GenerateMethodStub_CannotInferReturnType_ExplicitTypeSucceeds()
    {
        const string source = """
            namespace TestApp;

            public class Widget
            {
                public void Run()
                {
                    var value = Mystery();
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var (line, column) = FindIdentifier(source, "Mystery");
        var operation = new GenerateMethodStubOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateMethodStubParams
        {
            SourceFile = workspace.SourcePath,
            Line = line,
            Column = column,
            ReturnType = "int"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("private int Mystery()", updated);
    }

    #endregion

    #region Review Fold

    [SkippableFact]
    public async Task GenerateMethodStub_AlreadyResolvedImplicitConversion_Throws()
    {
        const string source = """
            namespace TestApp;

            public class Widget
            {
                public void Run()
                {
                    M(1);
                }

                private void M(long value)
                {
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var (line, column) = FindIdentifier(source, "M");
        var operation = new GenerateMethodStubOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GenerateMethodStubParams
            {
                SourceFile = workspace.SourcePath,
                Line = line,
                Column = column
            }));

        Assert.Equal(ErrorCodes.NameCollision, ex.ErrorCode);
        Assert.Contains("already resolves", ex.Message);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task GenerateMethodStub_AlreadyResolvedOptionalParameter_Throws()
    {
        const string source = """
            namespace TestApp;

            public class Widget
            {
                public void Run()
                {
                    M();
                }

                private void M(int value = 0)
                {
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var (line, column) = FindIdentifier(source, "M");
        var operation = new GenerateMethodStubOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GenerateMethodStubParams
            {
                SourceFile = workspace.SourcePath,
                Line = line,
                Column = column
            }));

        Assert.Equal(ErrorCodes.NameCollision, ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task GenerateMethodStub_RefParameter_PreservesRefKind()
    {
        const string source = """
            namespace TestApp;

            public class Widget
            {
                public void Run()
                {
                    int value = 1;
                    Process(ref value);
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var (line, column) = FindIdentifier(source, "Process");
        var operation = new GenerateMethodStubOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateMethodStubParams
        {
            SourceFile = workspace.SourcePath,
            Line = line,
            Column = column
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("private void Process(ref int value)", updated);
        Assert.Contains("throw new global::System.NotImplementedException();", updated);
    }

    [SkippableFact]
    public async Task GenerateMethodStub_OutAndInParameters_PreserveRefKind()
    {
        const string source = """
            namespace TestApp;

            public class Widget
            {
                public void Run()
                {
                    int value = 1;
                    Combine(in value, out int result);
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var (line, column) = FindIdentifier(source, "Combine");
        var operation = new GenerateMethodStubOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateMethodStubParams
        {
            SourceFile = workspace.SourcePath,
            Line = line,
            Column = column
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("private void Combine(in int value, out int result)", updated);
    }

    [SkippableFact]
    public async Task GenerateMethodStub_GenericInvocation_PreservesTypeParameters()
    {
        const string source = """
            namespace TestApp;

            public class Widget
            {
                public void Run()
                {
                    Missing<int, string>();
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var (line, column) = FindIdentifier(source, "Missing");
        var operation = new GenerateMethodStubOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateMethodStubParams
        {
            SourceFile = workspace.SourcePath,
            Line = line,
            Column = column
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("private void Missing<T, T2>()", updated);
        Assert.Contains("Missing<int, string>();", updated);
    }

    [SkippableFact]
    public async Task GenerateMethodStub_StaticConstructor_GeneratesStaticMethod()
    {
        const string source = """
            namespace TestApp;

            public class Widget
            {
                static Widget()
                {
                    DoWork();
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var (line, column) = FindIdentifier(source, "DoWork");
        var operation = new GenerateMethodStubOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateMethodStubParams
        {
            SourceFile = workspace.SourcePath,
            Line = line,
            Column = column
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("private static void DoWork()", updated);
    }

    [SkippableFact]
    public async Task GenerateMethodStub_StaticPropertyAccessor_GeneratesStaticMethod()
    {
        const string source = """
            namespace TestApp;

            public class Widget
            {
                public static int Value
                {
                    get
                    {
                        Init();
                        return 0;
                    }
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var (line, column) = FindIdentifier(source, "Init");
        var operation = new GenerateMethodStubOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateMethodStubParams
        {
            SourceFile = workspace.SourcePath,
            Line = line,
            Column = column
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("private static void Init()", updated);
    }

    [SkippableFact]
    public async Task GenerateMethodStub_MethodNameOverride_RewritesCallSite()
    {
        const string source = """
            namespace TestApp;

            public class Widget
            {
                public void Run()
                {
                    DoWork();
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var (line, column) = FindIdentifier(source, "DoWork");
        var operation = new GenerateMethodStubOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateMethodStubParams
        {
            SourceFile = workspace.SourcePath,
            Line = line,
            Column = column,
            MethodName = "Execute"
        });

        Assert.True(result.Success);
        Assert.Equal("Execute", result.Symbol!.Name);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("Execute();", updated);
        Assert.DoesNotContain("DoWork();", updated);
        Assert.Contains("private void Execute()", updated);
        Assert.DoesNotContain("private void DoWork()", updated);
    }

    [SkippableFact]
    public async Task GenerateMethodStub_MethodNameOverride_PreviewRewritesCallSiteWithoutWriting()
    {
        const string source = """
            namespace TestApp;

            public class Widget
            {
                public void Run()
                {
                    DoWork();
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var (line, column) = FindIdentifier(source, "DoWork");
        var operation = new GenerateMethodStubOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var result = await operation.ExecuteAsync(new GenerateMethodStubParams
        {
            SourceFile = workspace.SourcePath,
            Line = line,
            Column = column,
            MethodName = "Execute",
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.Contains(result.PendingChanges, change => change.AfterSnippet?.Contains("Execute") == true);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    #endregion

    #region Helpers

    private static string AbsoluteTestPath() =>
        Path.Combine(Path.GetTempPath(), "RoslynMcpGenerateMethodStubMissing.cs");

    private static string NormalizeNewlines(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal);

    private static (int Line, int Column) FindIdentifier(string source, string name)
    {
        var normalized = source.Replace("\r\n", "\n", StringComparison.Ordinal);
        var lines = normalized.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var idx = 0;
            while ((idx = lines[i].IndexOf(name, idx, StringComparison.Ordinal)) >= 0)
            {
                var beforeOk = idx == 0 || !SyntaxFacts.IsIdentifierPartCharacter(lines[i][idx - 1]);
                var after = idx + name.Length;
                var afterOk = after >= lines[i].Length || !SyntaxFacts.IsIdentifierPartCharacter(lines[i][after]);
                if (beforeOk && afterOk)
                    return (i + 1, idx + 1);

                idx += name.Length;
            }
        }

        throw new InvalidOperationException($"Identifier '{name}' not found in source.");
    }

    private sealed class TempWorkspace : IAsyncDisposable
    {
        public required string DirectoryPath { get; init; }
        public required string ProjectPath { get; init; }
        public required string SourcePath { get; init; }
        public required WorkspaceContext Context { get; init; }

        public static async Task<TempWorkspace> CreateAsync(string source, string fileName = "Types.cs")
        {
            Skip.IfNot(ModuleInitializer.MsBuildAvailable, ModuleInitializer.MsBuildError ?? "MSBuild not available");

            var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpGenerateMethodStub_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);

            var projectPath = Path.Combine(directory, "TestApp.csproj");
            var sourcePath = Path.Combine(directory, fileName);

            await File.WriteAllTextAsync(projectPath, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net9.0</TargetFramework>
                    <Nullable>enable</Nullable>
                  </PropertyGroup>
                </Project>
                """);
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

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Inline;
using RoslynMcp.Core.Workspace;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Inline;

/// <summary>
/// Operation-level tests for <see cref="InlineConstantOperation"/>.
/// These execute the real refactoring against a loaded workspace.
/// </summary>
public class InlineConstantOperationTests
{
    #region Input Validation

    [Fact]
    public void Validate_MissingSourceFile_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            InlineConstantOperation.Validate(new InlineConstantParams
            {
                SourceFile = "",
                ConstantName = "Max"
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public void Validate_MissingConstantName_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            InlineConstantOperation.Validate(new InlineConstantParams
            {
                SourceFile = AbsoluteTestPath(),
                ConstantName = ""
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public void Validate_RelativePath_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            InlineConstantOperation.Validate(new InlineConstantParams
            {
                SourceFile = "Limits.cs",
                ConstantName = "Max"
            }));

        Assert.Equal(ErrorCodes.InvalidSourcePath, ex.ErrorCode);
    }

    [Fact]
    public void Validate_MissingFile_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            InlineConstantOperation.Validate(new InlineConstantParams
            {
                SourceFile = AbsoluteTestPath(),
                ConstantName = "Max"
            }));

        Assert.Equal(ErrorCodes.SourceFileNotFound, ex.ErrorCode);
    }

    [Fact]
    public void Validate_InvalidConstantName_Throws()
    {
        var path = Path.Combine(Path.GetTempPath(), "RoslynMcpInlineConstantInvalid.cs");
        File.WriteAllText(path, "class C {}");
        try
        {
            var ex = Assert.Throws<RefactoringException>(() =>
                InlineConstantOperation.Validate(new InlineConstantParams
                {
                    SourceFile = path,
                    ConstantName = "123bad"
                }));

            Assert.Equal(ErrorCodes.InvalidSymbolName, ex.ErrorCode);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void IsValidIdentifier_AcceptsVerbatimAndUnicode()
    {
        Assert.True(InlineConstantOperation.IsValidIdentifier("@default"));
        Assert.True(InlineConstantOperation.IsValidIdentifier("Δ"));
        Assert.True(InlineConstantOperation.IsValidIdentifier("MaxRetries"));
        Assert.False(InlineConstantOperation.IsValidIdentifier("123bad"));
        Assert.False(InlineConstantOperation.IsValidIdentifier("class"));
    }

    [Fact]
    public void LineAndColumn_DefaultToNull()
    {
        var @params = new InlineConstantParams
        {
            SourceFile = AbsoluteTestPath(),
            ConstantName = "Max"
        };

        Assert.Null(@params.Line);
        Assert.Null(@params.Column);
    }

    [Fact]
    public void Validate_InvalidLine_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            InlineConstantOperation.Validate(new InlineConstantParams
            {
                SourceFile = AbsoluteTestPath(),
                ConstantName = "Max",
                Line = 0
            }));

        Assert.Equal(ErrorCodes.InvalidLineNumber, ex.ErrorCode);
        Assert.Equal("1006", ex.ErrorCode);
    }

    [Fact]
    public void Validate_NegativeLine_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            InlineConstantOperation.Validate(new InlineConstantParams
            {
                SourceFile = AbsoluteTestPath(),
                ConstantName = "Max",
                Line = -1
            }));

        Assert.Equal(ErrorCodes.InvalidLineNumber, ex.ErrorCode);
        Assert.Equal("1006", ex.ErrorCode);
    }

    [Fact]
    public void Validate_InvalidColumn_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            InlineConstantOperation.Validate(new InlineConstantParams
            {
                SourceFile = AbsoluteTestPath(),
                ConstantName = "Max",
                Column = 0
            }));

        Assert.Equal(ErrorCodes.InvalidColumnNumber, ex.ErrorCode);
        Assert.Equal("1007", ex.ErrorCode);
    }

    [Fact]
    public void Validate_NegativeColumn_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            InlineConstantOperation.Validate(new InlineConstantParams
            {
                SourceFile = AbsoluteTestPath(),
                ConstantName = "Max",
                Column = -1
            }));

        Assert.Equal(ErrorCodes.InvalidColumnNumber, ex.ErrorCode);
        Assert.Equal("1007", ex.ErrorCode);
    }

    [Fact]
    public void Validate_MissingConstantName_WithLineAndColumn_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            InlineConstantOperation.Validate(new InlineConstantParams
            {
                SourceFile = AbsoluteTestPath(),
                ConstantName = "",
                Line = 1,
                Column = 1
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public void Validate_AllFilesFalse_WithoutSourceFile_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            InlineConstantOperation.Validate(new InlineConstantParams
            {
                AllFiles = false,
                ConstantName = "Max"
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("sourceFile", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_AllFilesFalse_WithoutConstantName_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            InlineConstantOperation.Validate(new InlineConstantParams
            {
                AllFiles = false,
                SourceFile = AbsoluteTestPath()
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("constantName", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_AllFilesTrue_WithoutSourceFileOrConstantName_DoesNotThrow()
    {
        InlineConstantOperation.Validate(new InlineConstantParams
        {
            AllFiles = true
        });
    }

    [Fact]
    public void Validate_AllFilesTrue_WithConstantName_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            InlineConstantOperation.Validate(new InlineConstantParams
            {
                AllFiles = true,
                ConstantName = "Max"
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("allFiles", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("constantName", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_AllFilesTrue_WithTypeName_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            InlineConstantOperation.Validate(new InlineConstantParams
            {
                AllFiles = true,
                TypeName = "Limits"
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("allFiles", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("typeName", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_AllFilesTrue_WithLine_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            InlineConstantOperation.Validate(new InlineConstantParams
            {
                AllFiles = true,
                Line = 8
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("allFiles", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("line", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_AllFilesTrue_WithColumn_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            InlineConstantOperation.Validate(new InlineConstantParams
            {
                AllFiles = true,
                Column = 1
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("allFiles", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("column", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildAllFilesDescription_SingularAndPlural()
    {
        Assert.Equal("Inline constant", InlineConstantOperation.BuildAllFilesDescription(1));
        Assert.Equal("Inline 2 constants", InlineConstantOperation.BuildAllFilesDescription(2));
    }

    #endregion

    #region §9.3 Happy Path

    [SkippableFact]
    public async Task InlineConstant_Int_ReplacesWithoutSuffixAndRemovesDeclaration()
    {
        const string source = """
            namespace TestApp;

            public class Limits
            {
                private const int MaxRetries = 5;

                public int Run()
                {
                    return MaxRetries + MaxRetries;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new InlineConstantOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new InlineConstantParams
        {
            SourceFile = workspace.SourcePath,
            ConstantName = "MaxRetries"
        });

        Assert.True(result.Success);
        Assert.False(result.Preview);
        Assert.Equal(2, result.ReferencesUpdated);

        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.DoesNotContain("MaxRetries", text);
        Assert.Contains("return 5 + 5;", text);
        Assert.DoesNotContain("5L", text);
        Assert.DoesNotContain("5F", text);
        Assert.DoesNotContain("5M", text);
    }

    [SkippableFact]
    public async Task InlineConstant_Long_ReplacesWithLSuffix()
    {
        const string source = """
            namespace TestApp;

            public class Limits
            {
                private const long Capacity = 10L;

                public long Run()
                {
                    return Capacity;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new InlineConstantOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new InlineConstantParams
        {
            SourceFile = workspace.SourcePath,
            ConstantName = "Capacity"
        });

        Assert.True(result.Success);

        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.DoesNotContain("Capacity", text);
        Assert.Contains("return 10L;", text);
    }

    [SkippableFact]
    public async Task InlineConstant_String_ReplacesWithEscapedLiteral()
    {
        const string source = """
            namespace TestApp;

            public class Messages
            {
                private const string Greeting = "hello\n\"world\"";

                public string Run()
                {
                    return Greeting;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new InlineConstantOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new InlineConstantParams
        {
            SourceFile = workspace.SourcePath,
            ConstantName = "Greeting"
        });

        Assert.True(result.Success);

        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.DoesNotContain("const string Greeting", text);
        Assert.Contains("return \"hello\\n\\\"world\\\"\";", text);
    }

    [SkippableFact]
    public async Task InlineConstant_Null_ReplacesWithNullKeyword()
    {
        const string source = """
            namespace TestApp;

            public class Messages
            {
                private const string? Missing = null;

                public string? Run()
                {
                    return Missing;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new InlineConstantOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new InlineConstantParams
        {
            SourceFile = workspace.SourcePath,
            ConstantName = "Missing"
        });

        Assert.True(result.Success);

        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.DoesNotContain("Missing", text);
        Assert.Contains("return (string?)null;", text);
        Assert.DoesNotContain("return null;", text);
    }

    [SkippableFact]
    public async Task InlineConstant_Null_InArrayInitializer_EmitsTypedCast()
    {
        const string source = """
            namespace TestApp;

            public class Messages
            {
                private const string? Missing = null;

                public string?[] Run() => new[] { Missing };
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new InlineConstantOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new InlineConstantParams
        {
            SourceFile = workspace.SourcePath,
            ConstantName = "Missing"
        });

        Assert.True(result.Success);
        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("new[] { (string?)null }", text);
        Assert.DoesNotContain("new[] { null }", text);
    }

    [SkippableFact]
    public async Task InlineConstant_CrossFile_UpdatesAllReferences()
    {
        const string constants = """
            namespace TestApp;

            internal static class Limits
            {
                internal const int MaxRetries = 5;
            }
            """;
        const string usage = """
            namespace TestApp;

            public class Worker
            {
                public int Run() => Limits.MaxRetries;
            }
            """;

        await using var workspace = await TempWorkspace.CreateMultiFileAsync(
            ("Limits.cs", constants),
            ("Worker.cs", usage));
        var operation = new InlineConstantOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new InlineConstantParams
        {
            SourceFile = workspace.GetPath("Limits.cs"),
            ConstantName = "MaxRetries"
        });

        Assert.True(result.Success);
        Assert.Equal(1, result.ReferencesUpdated);

        var limitsText = await File.ReadAllTextAsync(workspace.GetPath("Limits.cs"));
        var workerText = await File.ReadAllTextAsync(workspace.GetPath("Worker.cs"));
        Assert.DoesNotContain("MaxRetries", limitsText);
        Assert.Contains("public int Run() => 5;", workerText);
        Assert.DoesNotContain("Limits.MaxRetries", workerText);
    }

    [SkippableFact]
    public async Task InlineConstant_CrossProject_UpdatesReferencingProject()
    {
        await using var workspace = await TempWorkspace.CreateCrossProjectAsync();
        var operation = new InlineConstantOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new InlineConstantParams
        {
            SourceFile = workspace.GetPath(Path.Combine("Lib", "Limits.cs")),
            ConstantName = "MaxRetries",
            RemoveConstant = false
        });

        Assert.True(result.Success);
        Assert.Equal(1, result.ReferencesUpdated);

        var libText = await File.ReadAllTextAsync(workspace.GetPath(Path.Combine("Lib", "Limits.cs")));
        var appText = await File.ReadAllTextAsync(workspace.GetPath(Path.Combine("App", "Worker.cs")));
        Assert.Contains("public const int MaxRetries = 5;", libText);
        Assert.Contains("public int Run() => 5;", appText);
        Assert.DoesNotContain("Limits.MaxRetries", appText);
    }

    #endregion

    #region Additional Happy Path

    [SkippableFact]
    public async Task InlineConstant_Preview_ReturnsChangesAndWritesNothing()
    {
        const string source = """
            namespace TestApp;

            public class Limits
            {
                private const int MaxRetries = 5;

                public int Run() => MaxRetries;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var original = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new InlineConstantOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new InlineConstantParams
        {
            SourceFile = workspace.SourcePath,
            ConstantName = "MaxRetries",
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.NotEmpty(result.PendingChanges);
        Assert.Contains(result.PendingChanges, c => c.AfterSnippet != null && c.AfterSnippet.Contains("=> 5"));

        var after = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Equal(original, after);
    }

    [SkippableFact]
    public async Task InlineConstant_RemoveConstantFalse_LeavesDeclaration()
    {
        const string source = """
            namespace TestApp;

            public class Limits
            {
                private const int MaxRetries = 5;

                public int Run() => MaxRetries;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new InlineConstantOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new InlineConstantParams
        {
            SourceFile = workspace.SourcePath,
            ConstantName = "MaxRetries",
            RemoveConstant = false
        });

        Assert.True(result.Success);

        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("private const int MaxRetries = 5;", text);
        Assert.Contains("public int Run() => 5;", text);
    }

    [SkippableFact]
    public async Task InlineConstant_StaticReadonly_Throws()
    {
        const string source = """
            namespace TestApp;

            public class Limits
            {
                private static readonly int MaxRetries = 5;

                public int Run() => MaxRetries;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new InlineConstantOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new InlineConstantParams
            {
                SourceFile = workspace.SourcePath,
                ConstantName = "MaxRetries"
            }));

        Assert.Equal(ErrorCodes.NotAConstant, ex.ErrorCode);
        Assert.Equal("3055", ex.ErrorCode);
    }

    [SkippableFact]
    public async Task InlineConstant_TypeName_Disambiguates()
    {
        const string source = """
            namespace TestApp;

            public class Alpha
            {
                private const int MaxRetries = 3;
            }

            public class Beta
            {
                private const int MaxRetries = 7;

                public int Run() => MaxRetries;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new InlineConstantOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new InlineConstantParams
        {
            SourceFile = workspace.SourcePath,
            ConstantName = "MaxRetries",
            TypeName = "Beta"
        });

        Assert.True(result.Success);

        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("private const int MaxRetries = 3;", text);
        Assert.DoesNotContain("private const int MaxRetries = 7;", text);
        Assert.Contains("public int Run() => 7;", text);
    }

    [SkippableFact]
    public async Task InlineConstant_Float_UsesFSuffix()
    {
        const string source = """
            namespace TestApp;

            public class Numbers
            {
                private const float Ratio = 1.5F;

                public float FloatValue() => Ratio;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new InlineConstantOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new InlineConstantParams
        {
            SourceFile = workspace.SourcePath,
            ConstantName = "Ratio"
        });

        Assert.True(result.Success);
        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("public float FloatValue() => 1.5F;", text);
    }

    [SkippableFact]
    public async Task InlineConstant_NegativeInt_ParenthesizesMemberAccessReceiver()
    {
        const string source = """
            namespace TestApp;

            public class Limits
            {
                private const int Offset = -1;

                public string Run() => Offset.ToString();
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new InlineConstantOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new InlineConstantParams
        {
            SourceFile = workspace.SourcePath,
            ConstantName = "Offset"
        });

        Assert.True(result.Success);
        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("public string Run() => (-1).ToString();", text);
        Assert.DoesNotContain("-1.ToString()", text);
    }

    [SkippableFact]
    public async Task InlineConstant_VerbatimIdentifier_InlinesByValueText()
    {
        const string source = """
            namespace TestApp;

            public class Limits
            {
                private const int @default = 4;

                public int Run() => @default;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new InlineConstantOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new InlineConstantParams
        {
            SourceFile = workspace.SourcePath,
            ConstantName = "@default"
        });

        Assert.True(result.Success);
        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.DoesNotContain("@default", text);
        Assert.Contains("public int Run() => 4;", text);
    }

    [SkippableFact]
    public async Task InlineConstant_NullAssignedToVar_EmitsTypedCast()
    {
        const string source = """
            namespace TestApp;

            public class Messages
            {
                private const string? Missing = null;

                public string? Run()
                {
                    var value = Missing;
                    return value;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new InlineConstantOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new InlineConstantParams
        {
            SourceFile = workspace.SourcePath,
            ConstantName = "Missing"
        });

        Assert.True(result.Success);
        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("var value = (string?)null;", text);
    }

    [SkippableFact]
    public async Task InlineConstant_Decimal_UsesMSuffix()
    {
        const string source = """
            namespace TestApp;

            public class Numbers
            {
                private const decimal Price = 2.5M;

                public decimal DecimalValue() => Price;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new InlineConstantOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new InlineConstantParams
        {
            SourceFile = workspace.SourcePath,
            ConstantName = "Price"
        });

        Assert.True(result.Success);
        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("public decimal DecimalValue() => 2.5M;", text);
    }

    #endregion

    #region Rejects

    [SkippableFact]
    public async Task InlineConstant_InAttribute_Throws()
    {
        const string source = """
            namespace TestApp;

            internal static class Limits
            {
                internal const string Reason = "old";
            }

            [System.Obsolete(Limits.Reason)]
            public class Widget
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new InlineConstantOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new InlineConstantParams
            {
                SourceFile = workspace.SourcePath,
                ConstantName = "Reason"
            }));

        Assert.Equal(ErrorCodes.ConstantInAttribute, ex.ErrorCode);
        Assert.Equal("3059", ex.ErrorCode);
    }

    [SkippableFact]
    public async Task InlineConstant_NotAConstant_Throws()
    {
        const string source = """
            namespace TestApp;

            public class Limits
            {
                private int MaxRetries = 5;

                public int Run() => MaxRetries;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new InlineConstantOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new InlineConstantParams
            {
                SourceFile = workspace.SourcePath,
                ConstantName = "MaxRetries"
            }));

        Assert.Equal(ErrorCodes.NotAConstant, ex.ErrorCode);
        Assert.Equal("3055", ex.ErrorCode);
    }

    [SkippableFact]
    public async Task InlineConstant_PublicApi_ThrowsWhenRemoving()
    {
        const string source = """
            namespace TestApp;

            public class Limits
            {
                public const int MaxRetries = 5;

                public int Run() => MaxRetries;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new InlineConstantOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new InlineConstantParams
            {
                SourceFile = workspace.SourcePath,
                ConstantName = "MaxRetries"
            }));

        Assert.Equal(ErrorCodes.PublicApiConstant, ex.ErrorCode);
        Assert.Equal("3056", ex.ErrorCode);
    }

    [SkippableFact]
    public async Task InlineConstant_PublicApi_AllowsInlineWithoutRemoval()
    {
        const string source = """
            namespace TestApp;

            public class Limits
            {
                public const int MaxRetries = 5;

                public int Run() => MaxRetries;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new InlineConstantOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new InlineConstantParams
        {
            SourceFile = workspace.SourcePath,
            ConstantName = "MaxRetries",
            RemoveConstant = false
        });

        Assert.True(result.Success);
        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("public const int MaxRetries = 5;", text);
        Assert.Contains("public int Run() => 5;", text);
    }

    [SkippableFact]
    public async Task InlineConstant_MissingSymbol_Throws()
    {
        const string source = """
            namespace TestApp;

            public class Limits
            {
                private const int MaxRetries = 5;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new InlineConstantOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new InlineConstantParams
            {
                SourceFile = workspace.SourcePath,
                ConstantName = "DoesNotExist"
            }));

        Assert.Equal(ErrorCodes.FieldNotFound, ex.ErrorCode);
    }

    [Fact]
    public void InlineConstant_UneditableDocument_Throws()
    {
        var workspace = new AdhocWorkspace();
        var project = workspace.AddProject("P", LanguageNames.CSharp);
        var document = workspace.AddDocument(project.Id, "Generated.cs", SourceText.From("class C {}"));

        var ex = Assert.Throws<RefactoringException>(() =>
            InlineConstantOperation.ValidateDocumentIsEditable(document, workspace));

        Assert.Equal(ErrorCodes.DocumentNotEditable, ex.ErrorCode);
    }

    #endregion

    #region Line / Column

    private const string SameNameConstSource = """
        namespace TestApp;

        public class Alpha
        {
            private const int MaxRetries = 3; /* alpha-const */

            public int Run() => MaxRetries;
        }

        public class Beta
        {
            private const int MaxRetries = 7; /* beta-const */

            public int Run() => MaxRetries;
        }
        """;

    private const string NestedSameNameConstSource = """
        namespace TestApp;

        public class Limits
        {
            private const int MaxRetries = 3; /* outer-const */

            public int Run() => MaxRetries;

            public class Nested
            {
                private const int MaxRetries = 7; /* nested-const */

                public int Run() => MaxRetries;
            }
        }
        """;

    private const string SameLineNestedConstSource = """
        namespace TestApp;

        public class Limits { private const int MaxRetries = 3; /* outer-const */ public int Run() => MaxRetries; public class Nested { private const int MaxRetries = 7; /* nested-const */ public int Run() => MaxRetries; } }
        """;

    [SkippableFact]
    public async Task InlineConstant_OmittedLine_SameName_ThrowsSymbolAmbiguous()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SameNameConstSource);
        var operation = new InlineConstantOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new InlineConstantParams
            {
                SourceFile = workspace.SourcePath,
                ConstantName = "MaxRetries"
            }));

        Assert.Equal(ErrorCodes.SymbolAmbiguous, ex.ErrorCode);
        Assert.Equal("2004", ex.ErrorCode);
        Assert.Contains("typeName", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task InlineConstant_LineOnBetaIdentifier_PicksBeta()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SameNameConstSource);
        var operation = new InlineConstantOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new InlineConstantParams
        {
            SourceFile = workspace.SourcePath,
            ConstantName = "MaxRetries",
            Line = FindLine(SameNameConstSource, "beta-const")
        });

        Assert.True(result.Success);
        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("private const int MaxRetries = 3;", text);
        Assert.DoesNotContain("private const int MaxRetries = 7;", text);
        Assert.Contains("public int Run() => 7;", text);
        Assert.DoesNotContain("public int Run() => 3;", text);
    }

    [SkippableFact]
    public async Task InlineConstant_LineOnNestedIdentifier_PicksNested()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NestedSameNameConstSource);
        var operation = new InlineConstantOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new InlineConstantParams
        {
            SourceFile = workspace.SourcePath,
            ConstantName = "MaxRetries",
            Line = FindLine(NestedSameNameConstSource, "nested-const")
        });

        Assert.True(result.Success);
        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        var (outer, nested) = SplitOuterAndNested(text);
        Assert.Contains("private const int MaxRetries = 3;", outer);
        Assert.Contains("public int Run() => MaxRetries;", outer);
        Assert.DoesNotContain("private const int MaxRetries = 7;", nested);
        Assert.Contains("public int Run() => 7;", nested);
    }

    [SkippableFact]
    public async Task InlineConstant_LineOnOuterIdentifier_PicksOuter()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NestedSameNameConstSource);
        var operation = new InlineConstantOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new InlineConstantParams
        {
            SourceFile = workspace.SourcePath,
            ConstantName = "MaxRetries",
            Line = FindLine(NestedSameNameConstSource, "outer-const")
        });

        Assert.True(result.Success);
        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        var (outer, nested) = SplitOuterAndNested(text);
        Assert.DoesNotContain("private const int MaxRetries = 3;", outer);
        Assert.Contains("public int Run() => 3;", outer);
        Assert.Contains("private const int MaxRetries = 7;", nested);
        Assert.Contains("public int Run() => MaxRetries;", nested);
    }

    [SkippableFact]
    public async Task InlineConstant_LineSetWithNoCoveringMatch_ThrowsFieldNotFound()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SameNameConstSource);
        var operation = new InlineConstantOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new InlineConstantParams
            {
                SourceFile = workspace.SourcePath,
                ConstantName = "MaxRetries",
                Line = 1
            }));

        Assert.Equal(ErrorCodes.FieldNotFound, ex.ErrorCode);
        Assert.Equal("2008", ex.ErrorCode);
    }

    [SkippableFact]
    public async Task InlineConstant_TypeNameAndLineMiss_ThrowsFieldNotFound()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SameNameConstSource);
        var operation = new InlineConstantOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new InlineConstantParams
            {
                SourceFile = workspace.SourcePath,
                ConstantName = "MaxRetries",
                TypeName = "Beta",
                Line = FindLine(SameNameConstSource, "alpha-const")
            }));

        Assert.Equal(ErrorCodes.FieldNotFound, ex.ErrorCode);
        Assert.Contains("Beta", ex.Message);
    }

    [SkippableFact]
    public async Task InlineConstant_ColumnOnNestedIdentifier_PicksNested()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SameLineNestedConstSource);
        var operation = new InlineConstantOperation(workspace.Context);
        var line = FindLine(SameLineNestedConstSource, "public class Limits { private const int MaxRetries");

        var result = await operation.ExecuteAsync(new InlineConstantParams
        {
            SourceFile = workspace.SourcePath,
            ConstantName = "MaxRetries",
            Line = line,
            Column = ColumnOf(SameLineNestedConstSource, "MaxRetries = 7; /* nested-const */")
        });

        Assert.True(result.Success);
        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("private const int MaxRetries = 3;", text);
        Assert.DoesNotContain("private const int MaxRetries = 7;", text);
        Assert.Contains("public int Run() => 7;", text);
    }

    [SkippableFact]
    public async Task InlineConstant_ColumnOnOuterIdentifier_PicksOuter()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SameLineNestedConstSource);
        var operation = new InlineConstantOperation(workspace.Context);
        var line = FindLine(SameLineNestedConstSource, "public class Limits { private const int MaxRetries");

        var result = await operation.ExecuteAsync(new InlineConstantParams
        {
            SourceFile = workspace.SourcePath,
            ConstantName = "MaxRetries",
            Line = line,
            Column = ColumnOf(SameLineNestedConstSource, "MaxRetries = 3; /* outer-const */")
        });

        Assert.True(result.Success);
        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.DoesNotContain("private const int MaxRetries = 3;", text);
        Assert.Contains("public int Run() => 3;", text);
        Assert.Contains("private const int MaxRetries = 7;", text);
        Assert.Contains("public int Run() => MaxRetries;", text);
    }

    [SkippableFact]
    public async Task InlineConstant_ColumnWithoutLine_KeepsOmittedLineSymbolAmbiguous()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SameNameConstSource);
        var operation = new InlineConstantOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new InlineConstantParams
            {
                SourceFile = workspace.SourcePath,
                ConstantName = "MaxRetries",
                Column = ColumnOf(SameNameConstSource, "MaxRetries = 7; /* beta-const */")
            }));

        Assert.Equal(ErrorCodes.SymbolAmbiguous, ex.ErrorCode);
        Assert.Contains("typeName", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task InlineConstant_ColumnAndLineMiss_ThrowsFieldNotFound()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SameNameConstSource);
        var operation = new InlineConstantOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new InlineConstantParams
            {
                SourceFile = workspace.SourcePath,
                ConstantName = "MaxRetries",
                Line = 1,
                Column = 1
            }));

        Assert.Equal(ErrorCodes.FieldNotFound, ex.ErrorCode);
        Assert.Equal("2008", ex.ErrorCode);
    }

    [SkippableFact]
    public async Task InlineConstant_Preview_LineOnBeta_WritesNothing()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SameNameConstSource);
        var original = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new InlineConstantOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new InlineConstantParams
        {
            SourceFile = workspace.SourcePath,
            ConstantName = "MaxRetries",
            Line = FindLine(SameNameConstSource, "beta-const"),
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.NotEmpty(result.PendingChanges);
        Assert.Contains(result.PendingChanges, c => c.AfterSnippet != null && c.AfterSnippet.Contains("=> 7"));

        var after = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Equal(original, after);
    }

    [SkippableFact]
    public async Task InlineConstant_ColumnOnContinuationLine_PicksField()
    {
        const string source = """
            namespace TestApp;

            public class Limits
            {
                private const int
                    MaxRetries = 5; /* split-const */

                public int Run() => MaxRetries;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var startLine = FindLine(source, "private const int");
        var identifierLine = FindLine(source, "split-const");
        Assert.NotEqual(startLine, identifierLine);

        var operation = new InlineConstantOperation(workspace.Context);
        var result = await operation.ExecuteAsync(new InlineConstantParams
        {
            SourceFile = workspace.SourcePath,
            ConstantName = "MaxRetries",
            Line = identifierLine,
            Column = ColumnOf(source, "MaxRetries = 5; /* split-const */")
        });

        Assert.True(result.Success);
        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.DoesNotContain("MaxRetries", text);
        Assert.Contains("public int Run() => 5;", text);
    }

    [Fact]
    public void FindConstantDeclarator_OmittedLine_ThrowsSymbolAmbiguous()
    {
        var root = Parse(SameNameConstSource);
        var ex = Assert.Throws<RefactoringException>(() =>
            InlineConstantOperation.FindConstantDeclarator(
                root,
                semanticModel: null,
                new InlineConstantParams
                {
                    SourceFile = AbsoluteTestPath(),
                    ConstantName = "MaxRetries"
                },
                CancellationToken.None));

        Assert.Equal(ErrorCodes.SymbolAmbiguous, ex.ErrorCode);
    }

    [Fact]
    public void FindConstantDeclarator_LineOnNestedIdentifier_PicksNested()
    {
        var root = Parse(NestedSameNameConstSource);
        var found = InlineConstantOperation.FindConstantDeclarator(
            root,
            semanticModel: null,
            new InlineConstantParams
            {
                SourceFile = AbsoluteTestPath(),
                ConstantName = "MaxRetries",
                Line = FindLine(NestedSameNameConstSource, "nested-const")
            },
            CancellationToken.None);

        var type = found.Ancestors().OfType<TypeDeclarationSyntax>().First();
        Assert.Equal("Nested", type.Identifier.Text);
        Assert.True(type.Parent is TypeDeclarationSyntax outer && outer.Identifier.Text == "Limits");
    }

    [Fact]
    public void FindConstantDeclarator_LineOnOuterIdentifier_PicksOuter()
    {
        var root = Parse(NestedSameNameConstSource);
        var found = InlineConstantOperation.FindConstantDeclarator(
            root,
            semanticModel: null,
            new InlineConstantParams
            {
                SourceFile = AbsoluteTestPath(),
                ConstantName = "MaxRetries",
                Line = FindLine(NestedSameNameConstSource, "outer-const")
            },
            CancellationToken.None);

        var type = found.Ancestors().OfType<TypeDeclarationSyntax>().First();
        Assert.Equal("Limits", type.Identifier.Text);
        Assert.False(type.Parent is TypeDeclarationSyntax);
    }

    [Fact]
    public void FindConstantDeclarator_LineMiss_ThrowsFieldNotFound()
    {
        var root = Parse(NestedSameNameConstSource);
        var ex = Assert.Throws<RefactoringException>(() =>
            InlineConstantOperation.FindConstantDeclarator(
                root,
                semanticModel: null,
                new InlineConstantParams
                {
                    SourceFile = AbsoluteTestPath(),
                    ConstantName = "MaxRetries",
                    Line = 1
                },
                CancellationToken.None));

        Assert.Equal(ErrorCodes.FieldNotFound, ex.ErrorCode);
    }

    [Fact]
    public void FindConstantDeclarator_LineOnLocal_DoesNotPickLocal()
    {
        const string source = """
            namespace TestApp;

            public class Limits
            {
                private const int MaxRetries = 3; /* field-const */

                public int Run()
                {
                    const int MaxRetries = 9; /* local-const */
                    return MaxRetries;
                }
            }
            """;

        var root = Parse(source);
        var omitted = InlineConstantOperation.FindConstantDeclarator(
            root,
            semanticModel: null,
            new InlineConstantParams
            {
                SourceFile = AbsoluteTestPath(),
                ConstantName = "MaxRetries"
            },
            CancellationToken.None);
        var onLocal = Assert.Throws<RefactoringException>(() =>
            InlineConstantOperation.FindConstantDeclarator(
                root,
                semanticModel: null,
                new InlineConstantParams
                {
                    SourceFile = AbsoluteTestPath(),
                    ConstantName = "MaxRetries",
                    Line = FindLine(source, "local-const")
                },
                CancellationToken.None));

        Assert.True(omitted.Parent?.Parent is FieldDeclarationSyntax);
        Assert.Equal(ErrorCodes.FieldNotFound, onLocal.ErrorCode);
    }

    [Fact]
    public void FindConstantDeclarator_LineOnContinuationIdentifier_PicksField()
    {
        const string source = """
            namespace TestApp;

            public class Limits
            {
                private const int
                    MaxRetries = 5; /* split-const */
            }
            """;

        var root = Parse(source);
        var startLine = FindLine(source, "private const int");
        var identifierLine = FindLine(source, "split-const");
        Assert.NotEqual(startLine, identifierLine);

        var found = InlineConstantOperation.FindConstantDeclarator(
            root,
            semanticModel: null,
            new InlineConstantParams
            {
                SourceFile = AbsoluteTestPath(),
                ConstantName = "MaxRetries",
                Line = identifierLine
            },
            CancellationToken.None);

        Assert.Equal("MaxRetries", found.Identifier.Text);
        Assert.True(found.Parent?.Parent is FieldDeclarationSyntax);
    }

    [Fact]
    public void FindConstantDeclarator_ColumnOnNestedIdentifier_PicksNested()
    {
        var root = Parse(SameLineNestedConstSource);
        var line = FindLine(SameLineNestedConstSource, "public class Limits { private const int MaxRetries");
        var found = InlineConstantOperation.FindConstantDeclarator(
            root,
            semanticModel: null,
            new InlineConstantParams
            {
                SourceFile = AbsoluteTestPath(),
                ConstantName = "MaxRetries",
                Line = line,
                Column = ColumnOf(SameLineNestedConstSource, "MaxRetries = 7; /* nested-const */")
            },
            CancellationToken.None);

        var type = found.Ancestors().OfType<TypeDeclarationSyntax>().First();
        Assert.Equal("Nested", type.Identifier.Text);
    }

    [Fact]
    public void FindConstantDeclarator_ColumnOnOuterIdentifier_PicksOuter()
    {
        var root = Parse(SameLineNestedConstSource);
        var line = FindLine(SameLineNestedConstSource, "public class Limits { private const int MaxRetries");
        var found = InlineConstantOperation.FindConstantDeclarator(
            root,
            semanticModel: null,
            new InlineConstantParams
            {
                SourceFile = AbsoluteTestPath(),
                ConstantName = "MaxRetries",
                Line = line,
                Column = ColumnOf(SameLineNestedConstSource, "MaxRetries = 3; /* outer-const */")
            },
            CancellationToken.None);

        var type = found.Ancestors().OfType<TypeDeclarationSyntax>().First();
        Assert.Equal("Limits", type.Identifier.Text);
        Assert.False(type.Parent is TypeDeclarationSyntax);
    }

    [Fact]
    public void FindConstantDeclarator_ColumnWithoutLine_ThrowsSymbolAmbiguous()
    {
        var root = Parse(SameNameConstSource);
        var ex = Assert.Throws<RefactoringException>(() =>
            InlineConstantOperation.FindConstantDeclarator(
                root,
                semanticModel: null,
                new InlineConstantParams
                {
                    SourceFile = AbsoluteTestPath(),
                    ConstantName = "MaxRetries",
                    Column = ColumnOf(SameNameConstSource, "MaxRetries = 7; /* beta-const */")
                },
                CancellationToken.None));

        Assert.Equal(ErrorCodes.SymbolAmbiguous, ex.ErrorCode);
    }

    [Fact]
    public void FindConstantDeclarator_ColumnAndLineMiss_ThrowsFieldNotFound()
    {
        var root = Parse(SameNameConstSource);
        var ex = Assert.Throws<RefactoringException>(() =>
            InlineConstantOperation.FindConstantDeclarator(
                root,
                semanticModel: null,
                new InlineConstantParams
                {
                    SourceFile = AbsoluteTestPath(),
                    ConstantName = "MaxRetries",
                    Line = 1,
                    Column = 1
                },
                CancellationToken.None));

        Assert.Equal(ErrorCodes.FieldNotFound, ex.ErrorCode);
    }

    [Fact]
    public void SpanCoversLine_TreatsEndAsExclusive()
    {
        var span = new FileLinePositionSpan(
            "",
            new LinePosition(0, 0),
            new LinePosition(2, 0));

        Assert.True(InlineConstantOperation.SpanCoversLine(span, 1));
        Assert.True(InlineConstantOperation.SpanCoversLine(span, 2));
        Assert.False(InlineConstantOperation.SpanCoversLine(span, 3));
    }

    [Fact]
    public void SpanCoversColumn_TreatsEndAsExclusive()
    {
        var root = Parse(SameLineNestedConstSource);
        var nested = root.DescendantNodes()
            .OfType<VariableDeclaratorSyntax>()
            .Last(v => v.Identifier.Text == "MaxRetries");
        var span = nested.GetLocation().GetLineSpan();
        var line = span.StartLinePosition.Line + 1;
        var startCol = span.StartLinePosition.Character + 1;
        var endCol = span.EndLinePosition.Character + 1;

        Assert.True(InlineConstantOperation.SpanCoversColumn(span, line, startCol));
        Assert.True(InlineConstantOperation.SpanCoversColumn(span, line, endCol - 1));
        Assert.False(InlineConstantOperation.SpanCoversColumn(span, line, endCol));
        Assert.False(InlineConstantOperation.SpanCoversColumn(span, line, startCol - 1));
    }

    #endregion

    #region AllFiles

    private const string EligibleFileA = """
        namespace TestApp;

        public class FileA
        {
            private const int MaxRetries = 5;
            private const string Greeting = "hi";

            public int Run() => MaxRetries;
            public string Hello() => Greeting;

            public void UseLocal()
            {
                const int skipped = 1;
                _ = skipped;
            }
        }
        """;

    private const string EligibleFileB = """
        namespace TestApp;

        public class FileB
        {
            private const int Capacity = 10;

            public int Run() => Capacity;
        }
        """;

    private const string IneligibleFileC = """
        namespace TestApp;

        internal static class Limits
        {
            internal const string Reason = "old";
        }

        [System.Obsolete(Limits.Reason)]
        public class Widget
        {
        }

        public class FileC
        {
            public const int PublicMax = 5;
            private int NotConst = 3;

            public int Run() => PublicMax + NotConst;
        }
        """;

    private const string MixedEligibleAndSkipped = """
        namespace TestApp;

        internal static class MixedLimits
        {
            internal const string Reason = "old";
        }

        [System.Obsolete(MixedLimits.Reason)]
        public class MixedWidget
        {
        }

        public class Mixed
        {
            private const int Eligible = 7;
            public const int PublicSkip = 2;
            private int NotConst = 3;

            public int Run() => Eligible + PublicSkip + NotConst;

            public void UseLocal()
            {
                const int skipped = 1;
                _ = skipped;
            }
        }
        """;

    [SkippableFact]
    public async Task InlineConstant_AllFilesFalse_InlinesOnlySpecifiedConstant()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("FileA.cs", EligibleFileA),
            ("FileB.cs", EligibleFileB),
            ("FileC.cs", IneligibleFileC));
        var operation = new InlineConstantOperation(workspace.Context);
        var beforeB = await File.ReadAllTextAsync(workspace.SourcePaths["FileB.cs"]);
        var beforeC = await File.ReadAllTextAsync(workspace.SourcePaths["FileC.cs"]);

        var result = await operation.ExecuteAsync(new InlineConstantParams
        {
            SourceFile = workspace.SourcePaths["FileA.cs"],
            AllFiles = false,
            ConstantName = "MaxRetries"
        });

        Assert.True(result.Success);
        Assert.False(result.Preview);
        var updatedA = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePaths["FileA.cs"]));
        Assert.Contains("public int Run() => 5;", updatedA, StringComparison.Ordinal);
        Assert.DoesNotContain("MaxRetries", updatedA, StringComparison.Ordinal);
        Assert.Contains("private const string Greeting", updatedA, StringComparison.Ordinal);
        Assert.Contains("public string Hello() => Greeting;", updatedA, StringComparison.Ordinal);
        Assert.Equal(beforeB, await File.ReadAllTextAsync(workspace.SourcePaths["FileB.cs"]));
        Assert.Equal(beforeC, await File.ReadAllTextAsync(workspace.SourcePaths["FileC.cs"]));
        Assert.Single(result.Changes!.FilesModified);
        Assert.Contains(result.Changes.FilesModified, p => PathEquals(p, workspace.SourcePaths["FileA.cs"]));
    }

    [SkippableFact]
    public async Task InlineConstant_OmittedAllFiles_KeepsSingleSiteInline()
    {
        await using var workspace = await TempWorkspace.CreateAsync(EligibleFileA);
        var operation = new InlineConstantOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new InlineConstantParams
        {
            SourceFile = workspace.SourcePath,
            ConstantName = "MaxRetries"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("public int Run() => 5;", updated, StringComparison.Ordinal);
        Assert.DoesNotContain("MaxRetries", updated, StringComparison.Ordinal);
        Assert.Contains("private const string Greeting", updated, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task InlineConstant_AllFilesTrue_InlinesEligibleConstantsAcrossFiles()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("FileA.cs", EligibleFileA),
            ("FileB.cs", EligibleFileB),
            ("FileC.cs", IneligibleFileC));
        var operation = new InlineConstantOperation(workspace.Context);
        var beforeC = await File.ReadAllTextAsync(workspace.SourcePaths["FileC.cs"]);

        var result = await operation.ExecuteAsync(new InlineConstantParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        Assert.False(result.Preview);
        var updatedA = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePaths["FileA.cs"]));
        var updatedB = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePaths["FileB.cs"]));
        Assert.Contains("public int Run() => 5;", updatedA, StringComparison.Ordinal);
        Assert.Contains("public string Hello() => \"hi\";", updatedA, StringComparison.Ordinal);
        Assert.DoesNotContain("MaxRetries", updatedA, StringComparison.Ordinal);
        Assert.DoesNotContain("Greeting", updatedA, StringComparison.Ordinal);
        Assert.Contains("const int skipped = 1;", updatedA, StringComparison.Ordinal);
        Assert.Contains("public int Run() => 10;", updatedB, StringComparison.Ordinal);
        Assert.DoesNotContain("Capacity", updatedB, StringComparison.Ordinal);
        Assert.Equal(beforeC, await File.ReadAllTextAsync(workspace.SourcePaths["FileC.cs"]));
        Assert.Equal(2, result.Changes!.FilesModified.Count);
        Assert.Contains(result.Changes.FilesModified, p => PathEquals(p, workspace.SourcePaths["FileA.cs"]));
        Assert.Contains(result.Changes.FilesModified, p => PathEquals(p, workspace.SourcePaths["FileB.cs"]));
        Assert.DoesNotContain(result.Changes.FilesModified, p => PathEquals(p, workspace.SourcePaths["FileC.cs"]));
    }

    [SkippableFact]
    public async Task InlineConstant_AllFilesTrue_WithoutSourceFileOrConstantName_Succeeds()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("FileA.cs", EligibleFileA),
            ("FileB.cs", EligibleFileB));
        var operation = new InlineConstantOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new InlineConstantParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        Assert.Equal(2, result.Changes!.FilesModified.Count);
    }

    [SkippableFact]
    public async Task InlineConstant_AllFilesFalse_WithoutSourceFile_MissingRequiredParam()
    {
        await using var workspace = await TempWorkspace.CreateAsync(EligibleFileA);
        var operation = new InlineConstantOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new InlineConstantParams
            {
                AllFiles = false,
                ConstantName = "MaxRetries"
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("sourceFile", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task InlineConstant_AllFilesFalse_WithoutConstantName_MissingRequiredParam()
    {
        await using var workspace = await TempWorkspace.CreateAsync(EligibleFileA);
        var operation = new InlineConstantOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new InlineConstantParams
            {
                AllFiles = false,
                SourceFile = workspace.SourcePath
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("constantName", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task InlineConstant_AllFilesTrue_WithConstantName_Rejects()
    {
        await using var workspace = await TempWorkspace.CreateAsync(EligibleFileA);
        var operation = new InlineConstantOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new InlineConstantParams
            {
                AllFiles = true,
                ConstantName = "MaxRetries"
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("constantName", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task InlineConstant_AllFilesTrue_WithLine_Rejects()
    {
        await using var workspace = await TempWorkspace.CreateAsync(EligibleFileA);
        var operation = new InlineConstantOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new InlineConstantParams
            {
                AllFiles = true,
                Line = 8
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("line", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task InlineConstant_AllFilesTrue_WithColumn_Rejects()
    {
        await using var workspace = await TempWorkspace.CreateAsync(EligibleFileA);
        var operation = new InlineConstantOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new InlineConstantParams
            {
                AllFiles = true,
                Column = 1
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("column", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task InlineConstant_AllFilesTrue_WithTypeName_Rejects()
    {
        await using var workspace = await TempWorkspace.CreateAsync(EligibleFileA);
        var operation = new InlineConstantOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new InlineConstantParams
            {
                AllFiles = true,
                TypeName = "FileA"
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("typeName", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task InlineConstant_PreviewAllFiles_AggregatesChangedFilesAndWritesNothing()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("FileA.cs", EligibleFileA),
            ("FileB.cs", EligibleFileB),
            ("FileC.cs", IneligibleFileC));
        var operation = new InlineConstantOperation(workspace.Context);
        var beforeA = await File.ReadAllTextAsync(workspace.SourcePaths["FileA.cs"]);
        var beforeB = await File.ReadAllTextAsync(workspace.SourcePaths["FileB.cs"]);
        var beforeC = await File.ReadAllTextAsync(workspace.SourcePaths["FileC.cs"]);

        var result = await operation.ExecuteAsync(new InlineConstantParams
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
            c.Description.Contains("Inline", StringComparison.OrdinalIgnoreCase) &&
            c.AfterSnippet != null &&
            (c.AfterSnippet.Contains("=> 5;", StringComparison.Ordinal) ||
             c.AfterSnippet.Contains("=> 10;", StringComparison.Ordinal)));
        Assert.Equal(beforeA, await File.ReadAllTextAsync(workspace.SourcePaths["FileA.cs"]));
        Assert.Equal(beforeB, await File.ReadAllTextAsync(workspace.SourcePaths["FileB.cs"]));
        Assert.Equal(beforeC, await File.ReadAllTextAsync(workspace.SourcePaths["FileC.cs"]));
    }

    [SkippableFact]
    public async Task InlineConstant_AllFilesTrue_EveryFileIneligible_SucceedsWithEmptyChanges()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("FileC.cs", IneligibleFileC),
            ("FileC2.cs", IneligibleFileC
                .Replace("Limits", "Limits2", StringComparison.Ordinal)
                .Replace("Widget", "Widget2", StringComparison.Ordinal)
                .Replace("FileC", "FileC2", StringComparison.Ordinal)));
        var operation = new InlineConstantOperation(workspace.Context);
        var beforeA = await File.ReadAllTextAsync(workspace.SourcePaths["FileC.cs"]);
        var beforeB = await File.ReadAllTextAsync(workspace.SourcePaths["FileC2.cs"]);

        var result = await operation.ExecuteAsync(new InlineConstantParams
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
    public async Task InlineConstant_AllFilesTrue_SkipsAttributePublicApiAndLocals()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("Mixed.cs", MixedEligibleAndSkipped));
        var operation = new InlineConstantOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new InlineConstantParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePaths["Mixed.cs"]));
        Assert.Contains("public int Run() => 7 + PublicSkip + NotConst;", updated, StringComparison.Ordinal);
        Assert.DoesNotContain("Eligible + PublicSkip", updated, StringComparison.Ordinal);
        Assert.Contains("internal const string Reason = \"old\";", updated, StringComparison.Ordinal);
        Assert.Contains("[System.Obsolete(MixedLimits.Reason)]", updated, StringComparison.Ordinal);
        Assert.Contains("public const int PublicSkip = 2;", updated, StringComparison.Ordinal);
        Assert.Contains("private int NotConst = 3;", updated, StringComparison.Ordinal);
        Assert.Contains("const int skipped = 1;", updated, StringComparison.Ordinal);
        Assert.Single(result.Changes!.FilesModified);
    }

    [SkippableFact]
    public async Task InlineConstant_AllFilesTrue_RemoveConstantFalse_InlinesPublicApiAndKeepsDeclaration()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("FileC.cs", IneligibleFileC));
        var operation = new InlineConstantOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new InlineConstantParams
        {
            AllFiles = true,
            RemoveConstant = false
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePaths["FileC.cs"]));
        Assert.Contains("public const int PublicMax = 5;", updated, StringComparison.Ordinal);
        Assert.Contains("public int Run() => 5 + NotConst;", updated, StringComparison.Ordinal);
        Assert.Contains("internal const string Reason = \"old\";", updated, StringComparison.Ordinal);
        Assert.Contains("[System.Obsolete(Limits.Reason)]", updated, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task InlineConstant_AllFilesTrue_OptionalSourceFile_LimitsWalk()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("FileA.cs", EligibleFileA),
            ("FileB.cs", EligibleFileB));
        var operation = new InlineConstantOperation(workspace.Context);
        var beforeB = await File.ReadAllTextAsync(workspace.SourcePaths["FileB.cs"]);

        var result = await operation.ExecuteAsync(new InlineConstantParams
        {
            AllFiles = true,
            SourceFile = workspace.SourcePaths["FileA.cs"]
        });

        Assert.True(result.Success);
        var updatedA = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePaths["FileA.cs"]));
        Assert.Contains("public int Run() => 5;", updatedA, StringComparison.Ordinal);
        Assert.Contains("public string Hello() => \"hi\";", updatedA, StringComparison.Ordinal);
        Assert.Equal(beforeB, await File.ReadAllTextAsync(workspace.SourcePaths["FileB.cs"]));
        Assert.Single(result.Changes!.FilesModified);
        Assert.Contains(result.Changes.FilesModified, p => PathEquals(p, workspace.SourcePaths["FileA.cs"]));
    }

    [SkippableFact]
    public async Task InlineConstant_AllFilesTrue_OptionalSourceFile_MatchesIgnoreCase()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("FileA.cs", EligibleFileA),
            ("FileB.cs", EligibleFileB));
        var operation = new InlineConstantOperation(workspace.Context);
        var beforeB = await File.ReadAllTextAsync(workspace.SourcePaths["FileB.cs"]);
        var flipped = FlipPathCasing(workspace.SourcePaths["FileA.cs"]);

        var result = await operation.ExecuteAsync(new InlineConstantParams
        {
            AllFiles = true,
            SourceFile = flipped
        });

        Assert.True(result.Success);
        var updatedA = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePaths["FileA.cs"]));
        Assert.Contains("public int Run() => 5;", updatedA, StringComparison.Ordinal);
        Assert.Equal(beforeB, await File.ReadAllTextAsync(workspace.SourcePaths["FileB.cs"]));
        Assert.Single(result.Changes!.FilesModified);
        Assert.Contains(result.Changes.FilesModified, p => PathEquals(p, workspace.SourcePaths["FileA.cs"]));
    }

    [Fact]
    public void CollectConstDeclarators_ExcludesLocalsAndNonConstFields()
    {
        var root = Parse(EligibleFileA);
        var fields = InlineConstantOperation.CollectConstDeclarators(root);
        Assert.Equal(2, fields.Count);
        Assert.All(fields, d => Assert.True(
            d.Parent?.Parent is FieldDeclarationSyntax field &&
            field.Modifiers.Any(Microsoft.CodeAnalysis.CSharp.SyntaxKind.ConstKeyword)));
        Assert.Contains(fields, d => d.Identifier.Text == "MaxRetries");
        Assert.Contains(fields, d => d.Identifier.Text == "Greeting");
        Assert.DoesNotContain(fields, d => d.Identifier.Text == "skipped");
    }

    #endregion

    #region Helpers

    private static string AbsoluteTestPath() =>
        Path.Combine(Path.GetTempPath(), "RoslynMcpInlineConstantMissing.cs");

    private static string NormalizeNewlines(string text) => text.Replace("\r\n", "\n");

    private static bool PathEquals(string left, string right) =>
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

    private static string FlipPathCasing(string path)
    {
        var chars = path.ToCharArray();
        for (var i = chars.Length - 1; i >= 0; i--)
        {
            if (char.IsLetter(chars[i]))
            {
                chars[i] = char.IsUpper(chars[i])
                    ? char.ToLowerInvariant(chars[i])
                    : char.ToUpperInvariant(chars[i]);
                break;
            }
        }

        return new string(chars);
    }

    private static SyntaxNode Parse(string source) =>
        CSharpSyntaxTree.ParseText(NormalizeNewlines(source)).GetRoot();

    private static int FindLine(string source, string snippet)
    {
        source = NormalizeNewlines(source);
        snippet = NormalizeNewlines(snippet);
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
        source = NormalizeNewlines(source);
        snippet = NormalizeNewlines(snippet);
        var index = source.IndexOf(snippet, StringComparison.Ordinal);
        if (index < 0)
            throw new InvalidOperationException($"Snippet not found: {snippet}");

        var lineStart = source.LastIndexOf('\n', index);
        return index - lineStart;
    }

    private static (string Outer, string Nested) SplitOuterAndNested(string source)
    {
        var nestedStart = source.IndexOf("class Nested", StringComparison.Ordinal);
        Assert.True(nestedStart >= 0, "Expected a Nested type in the source.");
        return (source[..nestedStart], source[nestedStart..]);
    }

    private sealed class TempWorkspace : IAsyncDisposable
    {
        public required string DirectoryPath { get; init; }
        public required string ProjectPath { get; init; }
        public required string SourcePath { get; init; }
        public required IReadOnlyDictionary<string, string> SourcePaths { get; init; }
        public required WorkspaceContext Context { get; init; }

        public string GetPath(string relativePath) => Path.Combine(DirectoryPath, relativePath);

        public static Task<TempWorkspace> CreateAsync(string source, string fileName = "Limits.cs") =>
            CreateMultiFileAsync((fileName, source));

        public static Task<TempWorkspace> CreateWithFilesAsync(params (string FileName, string Source)[] files) =>
            CreateMultiFileAsync(files);

        public static async Task<TempWorkspace> CreateMultiFileAsync(params (string FileName, string Source)[] files)
        {
            Skip.IfNot(ModuleInitializer.MsBuildAvailable, ModuleInitializer.MsBuildError ?? "MSBuild not available");

            var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpInlineConstant_" + Guid.NewGuid().ToString("N"));
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

            string? firstSource = null;
            foreach (var (fileName, source) in files)
            {
                var sourcePath = Path.Combine(directory, fileName);
                Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
                await File.WriteAllTextAsync(sourcePath, source);
                sourcePaths[fileName] = sourcePath;
                firstSource ??= sourcePath;
            }

            return await LoadAsync(directory, projectPath, firstSource!, sourcePaths);
        }

        public static async Task<TempWorkspace> CreateCrossProjectAsync()
        {
            Skip.IfNot(ModuleInitializer.MsBuildAvailable, ModuleInitializer.MsBuildError ?? "MSBuild not available");

            var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpInlineConstantXP_" + Guid.NewGuid().ToString("N"));
            var libDir = Path.Combine(directory, "Lib");
            var appDir = Path.Combine(directory, "App");
            Directory.CreateDirectory(libDir);
            Directory.CreateDirectory(appDir);

            var libProject = Path.Combine(libDir, "Lib.csproj");
            var appProject = Path.Combine(appDir, "App.csproj");
            var libSource = Path.Combine(libDir, "Limits.cs");
            var appSource = Path.Combine(appDir, "Worker.cs");

            await File.WriteAllTextAsync(libProject, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net9.0</TargetFramework>
                    <Nullable>enable</Nullable>
                  </PropertyGroup>
                </Project>
                """);
            await File.WriteAllTextAsync(appProject, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net9.0</TargetFramework>
                    <Nullable>enable</Nullable>
                  </PropertyGroup>
                  <ItemGroup>
                    <ProjectReference Include="..\Lib\Lib.csproj" />
                  </ItemGroup>
                </Project>
                """);
            await File.WriteAllTextAsync(libSource, """
                namespace TestLib;

                public static class Limits
                {
                    public const int MaxRetries = 5;
                }
                """);
            await File.WriteAllTextAsync(appSource, """
                namespace TestApp;

                public class Worker
                {
                    public int Run() => TestLib.Limits.MaxRetries;
                }
                """);

            var solutionPath = Path.Combine(directory, "TestApp.sln");
            await File.WriteAllTextAsync(solutionPath, """
                Microsoft Visual Studio Solution File, Format Version 12.00
                # Visual Studio Version 17
                Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Lib", "Lib\Lib.csproj", "{11111111-1111-1111-1111-111111111111}"
                EndProject
                Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "App", "App\App.csproj", "{22222222-2222-2222-2222-222222222222}"
                EndProject
                Global
                	GlobalSection(SolutionConfigurationPlatforms) = preSolution
                		Debug|Any CPU = Debug|Any CPU
                	EndGlobalSection
                	GlobalSection(ProjectConfigurationPlatforms) = postSolution
                		{11111111-1111-1111-1111-111111111111}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
                		{11111111-1111-1111-1111-111111111111}.Debug|Any CPU.Build.0 = Debug|Any CPU
                		{22222222-2222-2222-2222-222222222222}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
                		{22222222-2222-2222-2222-222222222222}.Debug|Any CPU.Build.0 = Debug|Any CPU
                	EndGlobalSection
                EndGlobal
                """);

            return await LoadAsync(
                directory,
                solutionPath,
                libSource,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    [Path.Combine("Lib", "Limits.cs")] = libSource,
                    [Path.Combine("App", "Worker.cs")] = appSource
                });
        }

        private static async Task<TempWorkspace> LoadAsync(
            string directory,
            string projectPath,
            string sourcePath,
            IReadOnlyDictionary<string, string> sourcePaths)
        {
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

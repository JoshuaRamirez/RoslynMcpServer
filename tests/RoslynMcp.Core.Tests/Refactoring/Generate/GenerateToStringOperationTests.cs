using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Generate;
using RoslynMcp.Core.Workspace;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Generate;

/// <summary>
/// Operation-level tests for <see cref="GenerateToStringOperation"/>, including optional
/// <c>line</c> / <c>column</c>, <c>format</c>, <c>includeProperties</c>, <c>includeInheritedMembers</c>,
/// <c>replaceExisting</c>, <c>callSuper</c>, and <c>allFiles</c>.
/// </summary>
public class GenerateToStringOperationTests
{
    private const string PersonSource = """
        namespace TestApp;

        public class Person
        {
            public string Name { get; set; }

            public int Age { get; set; }
        }
        """;

    #region Input Validation

    [Fact]
    public void Validate_MissingSourceFile_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            GenerateToStringOperation.Validate(new GenerateToStringParams
            {
                SourceFile = "",
                TypeName = "Person"
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public void Validate_MissingTypeName_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            GenerateToStringOperation.Validate(new GenerateToStringParams
            {
                SourceFile = AbsoluteTestPath(),
                TypeName = ""
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public void Validate_RelativePath_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            GenerateToStringOperation.Validate(new GenerateToStringParams
            {
                SourceFile = "Types.cs",
                TypeName = "Person"
            }));

        Assert.Equal(ErrorCodes.InvalidSourcePath, ex.ErrorCode);
    }

    [Fact]
    public void Validate_MissingFile_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            GenerateToStringOperation.Validate(new GenerateToStringParams
            {
                SourceFile = AbsoluteTestPath(),
                TypeName = "Person"
            }));

        Assert.Equal(ErrorCodes.SourceFileNotFound, ex.ErrorCode);
    }

    [Theory]
    [InlineData("json")]
    [InlineData("xml")]
    [InlineData("foo")]
    [InlineData("string_builder")]
    public void Validate_UnknownFormat_Throws(string format)
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            GenerateToStringOperation.Validate(new GenerateToStringParams
            {
                SourceFile = AbsoluteTestPath(),
                TypeName = "Person",
                Format = format
            }));

        Assert.Equal(ErrorCodes.InvalidToStringFormat, ex.ErrorCode);
        Assert.Equal("3143", ex.ErrorCode);
        Assert.Contains(format, ex.Message);
        Assert.Contains("interpolated", ex.Message);
        Assert.Contains("stringbuilder", ex.Message);
    }

    [Fact]
    public void CallSuper_DefaultsToFalse()
    {
        var @params = new GenerateToStringParams
        {
            SourceFile = AbsoluteTestPath(),
            TypeName = "Person"
        };

        Assert.False(@params.CallSuper);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("interpolated")]
    [InlineData("INTERPOLATED")]
    [InlineData("Interpolated")]
    [InlineData("stringbuilder")]
    [InlineData("STRINGBUILDER")]
    [InlineData("StringBuilder")]
    public void Validate_KnownOrOmittedFormat_DoesNotRejectFormat(string? format)
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            GenerateToStringOperation.Validate(new GenerateToStringParams
            {
                SourceFile = AbsoluteTestPath(),
                TypeName = "Person",
                Format = format
            }));

        Assert.Equal(ErrorCodes.SourceFileNotFound, ex.ErrorCode);
    }

    [Fact]
    public void Line_DefaultsToNull()
    {
        var @params = new GenerateToStringParams
        {
            SourceFile = AbsoluteTestPath(),
            TypeName = "Person"
        };

        Assert.Null(@params.Line);
        Assert.False(@params.AllFiles);
    }

    [Fact]
    public void Validate_InvalidLine_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            GenerateToStringOperation.Validate(new GenerateToStringParams
            {
                SourceFile = AbsoluteTestPath(),
                TypeName = "Person",
                Line = 0
            }));

        Assert.Equal(ErrorCodes.InvalidLineNumber, ex.ErrorCode);
        Assert.Equal("1006", ex.ErrorCode);
    }

    [Fact]
    public void Validate_NegativeLine_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            GenerateToStringOperation.Validate(new GenerateToStringParams
            {
                SourceFile = AbsoluteTestPath(),
                TypeName = "Person",
                Line = -1
            }));

        Assert.Equal(ErrorCodes.InvalidLineNumber, ex.ErrorCode);
        Assert.Equal("1006", ex.ErrorCode);
    }

    [Fact]
    public void Column_DefaultsToNull()
    {
        var @params = new GenerateToStringParams
        {
            SourceFile = AbsoluteTestPath(),
            TypeName = "Person"
        };

        Assert.Null(@params.Column);
    }

    [Fact]
    public void Validate_InvalidColumn_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            GenerateToStringOperation.Validate(new GenerateToStringParams
            {
                SourceFile = AbsoluteTestPath(),
                TypeName = "Person",
                Column = 0
            }));

        Assert.Equal(ErrorCodes.InvalidColumnNumber, ex.ErrorCode);
        Assert.Equal("1007", ex.ErrorCode);
    }

    [Fact]
    public void Validate_NegativeColumn_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            GenerateToStringOperation.Validate(new GenerateToStringParams
            {
                SourceFile = AbsoluteTestPath(),
                TypeName = "Person",
                Column = -1
            }));

        Assert.Equal(ErrorCodes.InvalidColumnNumber, ex.ErrorCode);
        Assert.Equal("1007", ex.ErrorCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_EmptyTypeName_WithColumnAndLine_ThrowsMissingRequiredParam(string typeName)
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            GenerateToStringOperation.Validate(new GenerateToStringParams
            {
                SourceFile = AbsoluteTestPath(),
                TypeName = typeName,
                Line = 1,
                Column = 1
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    #endregion

    #region P0 optional line disambiguation

    private const string NestedSameNamePersonSource = """
        namespace TestApp;

        public /* outer-person */ class Person
        {
            public string Name { get; set; }

            public /* nested-person */ class Person
            {
                public int Age { get; set; }
            }
        }
        """;

    [SkippableFact]
    public async Task GenerateToString_OmittedLine_KeepsTypeNameFirstOrDefaultPick()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NestedSameNamePersonSource, "Person.cs");
        var operation = new GenerateToStringOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateToStringParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Person"
        });

        Assert.True(result.Success);
        var types = GetTypes(await File.ReadAllTextAsync(workspace.SourcePath), "Person");
        Assert.Equal(2, types.Count);
        Assert.True(TypeHasMethod(types[0], "ToString"));
        Assert.False(TypeHasMethod(types[1], "ToString"));
        var outer = ExtractToStringMethod(NormalizeNewlines(types[0].ToFullString()));
        Assert.Contains("{Name}", outer);
        Assert.DoesNotContain("{Age}", outer);
    }

    [SkippableFact]
    public async Task GenerateToString_LineOnNestedIdentifier_PicksNestedType()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NestedSameNamePersonSource, "Person.cs");
        var operation = new GenerateToStringOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateToStringParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Person",
            Line = FindLine(NestedSameNamePersonSource, "nested-person")
        });

        Assert.True(result.Success);
        var types = GetTypes(await File.ReadAllTextAsync(workspace.SourcePath), "Person");
        Assert.Equal(2, types.Count);
        Assert.False(TypeHasMethod(types[0], "ToString"));
        Assert.True(TypeHasMethod(types[1], "ToString"));
        var nested = ExtractToStringMethod(NormalizeNewlines(types[1].ToFullString()));
        Assert.Contains("{Age}", nested);
        Assert.DoesNotContain("{Name}", nested);
    }

    [SkippableFact]
    public async Task GenerateToString_LineOnOuterIdentifier_PicksOuterType()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NestedSameNamePersonSource, "Person.cs");
        var operation = new GenerateToStringOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateToStringParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Person",
            Line = FindLine(NestedSameNamePersonSource, "outer-person")
        });

        Assert.True(result.Success);
        var types = GetTypes(await File.ReadAllTextAsync(workspace.SourcePath), "Person");
        Assert.Equal(2, types.Count);
        Assert.True(TypeHasMethod(types[0], "ToString"));
        Assert.False(TypeHasMethod(types[1], "ToString"));
        var outer = ExtractToStringMethod(NormalizeNewlines(types[0].ToFullString()));
        Assert.Contains("{Name}", outer);
        Assert.DoesNotContain("{Age}", outer);
    }

    [SkippableFact]
    public async Task GenerateToString_Line_Preview_WritesNothing_AndDescribesGeneration()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NestedSameNamePersonSource, "Person.cs");
        var operation = new GenerateToStringOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var result = await operation.ExecuteAsync(new GenerateToStringParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Person",
            Line = FindLine(NestedSameNamePersonSource, "nested-person"),
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.NotEmpty(result.PendingChanges);
        Assert.Contains("Generate ToString", result.PendingChanges[0].Description);
        Assert.Contains("Person", result.PendingChanges[0].Description);
        Assert.Contains("Age", result.PendingChanges[0].Description);
        Assert.Contains("public override string ToString()", result.PendingChanges[0].AfterSnippet);
        Assert.Contains("{Age}", result.PendingChanges[0].AfterSnippet);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [Fact]
    public void FindTypeDeclaration_OmittedLine_FirstOrDefaultPicksOuter()
    {
        var root = CSharpSyntaxTree.ParseText(NestedSameNamePersonSource).GetRoot();
        var found = GenerateToStringOperation.FindTypeDeclaration(root, "Person", line: null);

        Assert.NotNull(found);
        Assert.False(found.Parent is TypeDeclarationSyntax);
        Assert.IsType<ClassDeclarationSyntax>(found);
    }

    [Fact]
    public void FindTypeDeclaration_LineOnNestedIdentifier_PicksNested()
    {
        var root = CSharpSyntaxTree.ParseText(NestedSameNamePersonSource).GetRoot();
        var found = GenerateToStringOperation.FindTypeDeclaration(
            root, "Person", FindLine(NestedSameNamePersonSource, "nested-person"));

        Assert.NotNull(found);
        Assert.True(found.Parent is TypeDeclarationSyntax outer && outer.Identifier.Text == "Person");
    }

    [Fact]
    public void FindTypeDeclaration_LineOnOuterIdentifier_PicksOuter()
    {
        var root = CSharpSyntaxTree.ParseText(NestedSameNamePersonSource).GetRoot();
        var found = GenerateToStringOperation.FindTypeDeclaration(
            root, "Person", FindLine(NestedSameNamePersonSource, "outer-person"));

        Assert.NotNull(found);
        Assert.False(found.Parent is TypeDeclarationSyntax);
    }

    [Fact]
    public void FindTypeDeclaration_LineOnContinuationIdentifier_PicksType()
    {
        const string source = """
            namespace TestApp;

            public class
                Person // split-person
            {
                public string Name { get; set; }

                public class Person // nested-person
                {
                    public int Age { get; set; }
                }
            }
            """;

        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var startLine = FindLine(source, "public class");
        var identifierLine = FindLine(source, "split-person");
        Assert.NotEqual(startLine, identifierLine);

        var found = GenerateToStringOperation.FindTypeDeclaration(root, "Person", identifierLine);

        Assert.NotNull(found);
        Assert.False(found.Parent is TypeDeclarationSyntax);
    }

    [Fact]
    public void FindTypeDeclaration_LineMiss_KeepsFirstMatch()
    {
        var root = CSharpSyntaxTree.ParseText(NestedSameNamePersonSource).GetRoot();
        var found = GenerateToStringOperation.FindTypeDeclaration(root, "Person", line: 1);

        Assert.NotNull(found);
        Assert.False(found.Parent is TypeDeclarationSyntax);
    }

    private const string EnumFirstThenSameNamedClassSource = """
        namespace Other
        {
            public /* person-enum */ enum Person
            {
                Ready
            }
        }

        namespace TestApp
        {
            public /* person-class */ class Person
            {
                public string Name { get; set; }
            }
        }
        """;

    private const string DelegateFirstThenSameNamedClassSource = """
        namespace Other
        {
            public /* person-delegate */ delegate void Person();
        }

        namespace TestApp
        {
            public /* person-class */ class Person
            {
                public string Name { get; set; }
            }
        }
        """;

    [Fact]
    public void FindTypeDeclaration_OmittedLine_EnumFirstPicksClass()
    {
        var root = CSharpSyntaxTree.ParseText(EnumFirstThenSameNamedClassSource).GetRoot();
        var found = GenerateToStringOperation.FindTypeDeclaration(root, "Person", line: null);

        Assert.NotNull(found);
        Assert.IsType<ClassDeclarationSyntax>(found);
    }

    [Fact]
    public void FindTypeDeclaration_LineOnEnumIdentifier_PicksEnum()
    {
        var root = CSharpSyntaxTree.ParseText(EnumFirstThenSameNamedClassSource).GetRoot();
        var found = GenerateToStringOperation.FindTypeDeclaration(
            root, "Person", FindLine(EnumFirstThenSameNamedClassSource, "person-enum"));

        Assert.NotNull(found);
        Assert.IsType<EnumDeclarationSyntax>(found);
    }

    [Fact]
    public void FindTypeDeclaration_LineOnClassIdentifier_PicksClass()
    {
        var root = CSharpSyntaxTree.ParseText(EnumFirstThenSameNamedClassSource).GetRoot();
        var found = GenerateToStringOperation.FindTypeDeclaration(
            root, "Person", FindLine(EnumFirstThenSameNamedClassSource, "person-class"));

        Assert.NotNull(found);
        Assert.IsType<ClassDeclarationSyntax>(found);
    }

    [Fact]
    public void FindTypeDeclaration_OmittedLine_DelegateFirstPicksClass()
    {
        var root = CSharpSyntaxTree.ParseText(DelegateFirstThenSameNamedClassSource).GetRoot();
        var found = GenerateToStringOperation.FindTypeDeclaration(root, "Person", line: null);

        Assert.NotNull(found);
        Assert.IsType<ClassDeclarationSyntax>(found);
    }

    [Fact]
    public void FindTypeDeclaration_LineOnDelegateIdentifier_PicksDelegate()
    {
        var root = CSharpSyntaxTree.ParseText(DelegateFirstThenSameNamedClassSource).GetRoot();
        var found = GenerateToStringOperation.FindTypeDeclaration(
            root, "Person", FindLine(DelegateFirstThenSameNamedClassSource, "person-delegate"));

        Assert.NotNull(found);
        Assert.IsType<DelegateDeclarationSyntax>(found);
    }

    [SkippableFact]
    public async Task GenerateToString_OmittedLine_EnumFirstThenSameNamedClass_GeneratesOnClass()
    {
        await using var workspace = await TempWorkspace.CreateAsync(EnumFirstThenSameNamedClassSource, "Person.cs");
        var operation = new GenerateToStringOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateToStringParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Person"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var types = GetTypes(updated, "Person");
        Assert.Single(types);
        Assert.True(TypeHasMethod(types[0], "ToString"));
        Assert.Contains("public override string ToString()", updated);
        Assert.Contains("enum Person", updated, StringComparison.Ordinal);
        Assert.DoesNotContain("override string ToString", updated[..updated.IndexOf("class Person", StringComparison.Ordinal)]);
    }

    [SkippableFact]
    public async Task GenerateToString_LineOnEnumIdentifier_SameNamedClass_ThrowsInvalidSymbolKind()
    {
        await using var workspace = await TempWorkspace.CreateAsync(EnumFirstThenSameNamedClassSource, "Person.cs");
        var operation = new GenerateToStringOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GenerateToStringParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Person",
                Line = FindLine(EnumFirstThenSameNamedClassSource, "person-enum")
            }));

        Assert.Equal(ErrorCodes.InvalidSymbolKind, ex.ErrorCode);
        Assert.Equal("2020", ex.ErrorCode);
        Assert.Contains("not a supported target", ex.Message);
        var updated = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Equal(before, updated);
        Assert.DoesNotContain("override string ToString", updated, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task GenerateToString_LineOnDelegateIdentifier_SameNamedClass_ThrowsInvalidSymbolKind()
    {
        await using var workspace = await TempWorkspace.CreateAsync(DelegateFirstThenSameNamedClassSource, "Person.cs");
        var operation = new GenerateToStringOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GenerateToStringParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Person",
                Line = FindLine(DelegateFirstThenSameNamedClassSource, "person-delegate")
            }));

        Assert.Equal(ErrorCodes.InvalidSymbolKind, ex.ErrorCode);
        Assert.Equal("2020", ex.ErrorCode);
        Assert.Contains("not a supported target", ex.Message);
        var updated = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Equal(before, updated);
        Assert.DoesNotContain("override string ToString", updated, StringComparison.Ordinal);
    }

    [Fact]
    public void SpanCoversLine_TreatsEndAsExclusive()
    {
        var span = new FileLinePositionSpan(
            "t.cs",
            new LinePosition(0, 0),
            new LinePosition(2, 0));

        Assert.True(GenerateToStringOperation.SpanCoversLine(span, 1));
        Assert.True(GenerateToStringOperation.SpanCoversLine(span, 2));
        Assert.False(GenerateToStringOperation.SpanCoversLine(span, 3));
        Assert.False(GenerateToStringOperation.SpanCoversLine(span, 0));
    }

    [SkippableFact]
    public async Task GenerateToString_LineOnLaterSameFilePartial_ReplaceExisting_InsertsOnSelectedPartial()
    {
        const string source = """
            namespace Other
            {
                public class Person
                {
                    public string Title { get; set; }
                }
            }

            namespace TestApp
            {
                public partial class Person
                {
                    public string Name { get; set; }

                    public override string ToString() => "old"; /* old-body */
                }

                public /* later-partial */ partial class Person
                {
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Person.cs");
        var operation = new GenerateToStringOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateToStringParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Person",
            Line = FindLine(source, "later-partial"),
            ReplaceExisting = true
        });

        Assert.True(result.Success);
        var types = GetTypes(await File.ReadAllTextAsync(workspace.SourcePath), "Person");
        Assert.Equal(3, types.Count);
        Assert.False(TypeHasMethod(types[0], "ToString"));
        Assert.False(TypeHasMethod(types[1], "ToString"));
        Assert.True(TypeHasMethod(types[2], "ToString"));
        Assert.DoesNotContain("old-body", types[2].ToFullString(), StringComparison.Ordinal);
        Assert.DoesNotContain("=> \"old\"", types[2].ToFullString(), StringComparison.Ordinal);
        Assert.Contains("{Name}", types[2].ToFullString(), StringComparison.Ordinal);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Equal(1, CountOccurrences(updated, "public override string ToString()"));
        Assert.DoesNotContain("old-body", updated, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task GenerateToString_SequentialReplaceExisting_ReusedWorkspace_InsertsOnSecondSelectedType()
    {
        const string source = """
            namespace TestApp;

            public class Alpha
            {
                public string Name { get; set; }

                public override string ToString() => "old-alpha";
            }

            public class Beta
            {
                public string Title { get; set; }

                public override string ToString() => "old-beta";
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Types.cs");
        var operation = new GenerateToStringOperation(workspace.Context);

        var first = await operation.ExecuteAsync(new GenerateToStringParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Alpha",
            ReplaceExisting = true
        });
        Assert.True(first.Success);

        var second = await operation.ExecuteAsync(new GenerateToStringParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Beta",
            ReplaceExisting = true
        });
        Assert.True(second.Success);

        var updated = await File.ReadAllTextAsync(workspace.SourcePath);
        var types = GetTypes(updated, "Alpha").Concat(GetTypes(updated, "Beta")).ToList();
        var alpha = types.Single(t => t.Identifier.Text == "Alpha");
        var beta = types.Single(t => t.Identifier.Text == "Beta");
        Assert.True(TypeHasMethod(alpha, "ToString"));
        Assert.True(TypeHasMethod(beta, "ToString"));
        var alphaToString = ExtractToStringMethod(NormalizeNewlines(alpha.ToFullString()));
        var betaToString = ExtractToStringMethod(NormalizeNewlines(beta.ToFullString()));
        Assert.Contains("{Name}", alphaToString);
        Assert.DoesNotContain("{Title}", alphaToString);
        Assert.Contains("{Title}", betaToString);
        Assert.DoesNotContain("{Name}", betaToString);
        Assert.DoesNotContain("old-alpha", updated, StringComparison.Ordinal);
        Assert.DoesNotContain("old-beta", updated, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(NormalizeNewlines(alpha.ToFullString()), "public override string ToString()"));
        Assert.Equal(1, CountOccurrences(NormalizeNewlines(beta.ToFullString()), "public override string ToString()"));
    }

    #endregion

    #region P0 optional column disambiguation

    private const string SameLineNestedPersonSource = """
        namespace TestApp;

        public class Person { public string Name { get; set; } public class Person { public int Age { get; set; } } }
        """;

    [SkippableFact]
    public async Task GenerateToString_OmittedColumn_KeepsTypeNameFirstOrDefaultPick()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NestedSameNamePersonSource, "Person.cs");
        var operation = new GenerateToStringOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateToStringParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Person"
        });

        Assert.True(result.Success);
        var types = GetTypes(await File.ReadAllTextAsync(workspace.SourcePath), "Person");
        Assert.Equal(2, types.Count);
        Assert.True(TypeHasMethod(types[0], "ToString"));
        Assert.False(TypeHasMethod(types[1], "ToString"));
        var outer = ExtractToStringMethod(NormalizeNewlines(types[0].ToFullString()));
        Assert.Contains("{Name}", outer);
        Assert.DoesNotContain("{Age}", outer);
    }

    [SkippableFact]
    public async Task GenerateToString_OmittedColumn_LineOnNestedIdentifier_PicksNestedType()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NestedSameNamePersonSource, "Person.cs");
        var operation = new GenerateToStringOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateToStringParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Person",
            Line = FindLine(NestedSameNamePersonSource, "nested-person")
        });

        Assert.True(result.Success);
        var types = GetTypes(await File.ReadAllTextAsync(workspace.SourcePath), "Person");
        Assert.Equal(2, types.Count);
        Assert.False(TypeHasMethod(types[0], "ToString"));
        Assert.True(TypeHasMethod(types[1], "ToString"));
        var nested = ExtractToStringMethod(NormalizeNewlines(types[1].ToFullString()));
        Assert.Contains("{Age}", nested);
        Assert.DoesNotContain("{Name}", nested);
    }

    [SkippableFact]
    public async Task GenerateToString_ColumnOnNestedIdentifier_PicksNestedType()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SameLineNestedPersonSource, "Person.cs");
        var operation = new GenerateToStringOperation(workspace.Context);
        var line = FindLine(SameLineNestedPersonSource, "public class Person { public string Name");
        var column = ColumnOf(SameLineNestedPersonSource, "Person { public int Age");

        var result = await operation.ExecuteAsync(new GenerateToStringParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Person",
            Line = line,
            Column = column
        });

        Assert.True(result.Success);
        var types = GetTypes(await File.ReadAllTextAsync(workspace.SourcePath), "Person");
        Assert.Equal(2, types.Count);
        Assert.False(TypeHasMethod(types[0], "ToString"));
        Assert.True(TypeHasMethod(types[1], "ToString"));
        var nested = ExtractToStringMethod(NormalizeNewlines(types[1].ToFullString()));
        Assert.Contains("{Age}", nested);
        Assert.DoesNotContain("{Name}", nested);
    }

    [SkippableFact]
    public async Task GenerateToString_ColumnOnOuterIdentifier_PicksOuterType()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SameLineNestedPersonSource, "Person.cs");
        var operation = new GenerateToStringOperation(workspace.Context);
        var line = FindLine(SameLineNestedPersonSource, "public class Person { public string Name");
        var column = ColumnOf(SameLineNestedPersonSource, "Person { public string Name");

        var result = await operation.ExecuteAsync(new GenerateToStringParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Person",
            Line = line,
            Column = column
        });

        Assert.True(result.Success);
        var types = GetTypes(await File.ReadAllTextAsync(workspace.SourcePath), "Person");
        Assert.Equal(2, types.Count);
        Assert.True(TypeHasMethod(types[0], "ToString"));
        Assert.False(TypeHasMethod(types[1], "ToString"));
        var outer = ExtractToStringMethod(NormalizeNewlines(types[0].ToFullString()));
        Assert.Contains("{Name}", outer);
        Assert.DoesNotContain("{Age}", outer);
    }

    [Fact]
    public void FindTypeDeclaration_OmittedColumn_FirstOrDefaultPicksOuter()
    {
        var root = CSharpSyntaxTree.ParseText(NestedSameNamePersonSource).GetRoot();
        var found = GenerateToStringOperation.FindTypeDeclaration(root, "Person", line: null, column: null);

        Assert.NotNull(found);
        Assert.False(found.Parent is TypeDeclarationSyntax);
        Assert.IsType<ClassDeclarationSyntax>(found);
    }

    [Fact]
    public void FindTypeDeclaration_OmittedColumn_EnumFirstPicksClass()
    {
        var root = CSharpSyntaxTree.ParseText(EnumFirstThenSameNamedClassSource).GetRoot();
        var found = GenerateToStringOperation.FindTypeDeclaration(root, "Person", line: null, column: null);

        Assert.NotNull(found);
        Assert.IsType<ClassDeclarationSyntax>(found);
    }

    [Fact]
    public void FindTypeDeclaration_ColumnOnNestedIdentifier_PicksNested()
    {
        var root = CSharpSyntaxTree.ParseText(SameLineNestedPersonSource).GetRoot();
        var line = FindLine(SameLineNestedPersonSource, "public class Person { public string Name");
        var found = GenerateToStringOperation.FindTypeDeclaration(
            root, "Person", line, ColumnOf(SameLineNestedPersonSource, "Person { public int Age"));

        Assert.NotNull(found);
        Assert.True(found.Parent is TypeDeclarationSyntax outer && outer.Identifier.Text == "Person");
    }

    [Fact]
    public void FindTypeDeclaration_ColumnOnOuterIdentifier_PicksOuter()
    {
        var root = CSharpSyntaxTree.ParseText(SameLineNestedPersonSource).GetRoot();
        var line = FindLine(SameLineNestedPersonSource, "public class Person { public string Name");
        var found = GenerateToStringOperation.FindTypeDeclaration(
            root, "Person", line, ColumnOf(SameLineNestedPersonSource, "Person { public string Name"));

        Assert.NotNull(found);
        Assert.False(found.Parent is TypeDeclarationSyntax);
    }

    [Fact]
    public void FindTypeDeclaration_ColumnWithoutLine_KeepsFirstMatch()
    {
        var root = CSharpSyntaxTree.ParseText(SameLineNestedPersonSource).GetRoot();
        var nestedColumn = ColumnOf(SameLineNestedPersonSource, "Person { public int Age");
        var found = GenerateToStringOperation.FindTypeDeclaration(
            root, "Person", line: null, nestedColumn);

        Assert.NotNull(found);
        Assert.False(found.Parent is TypeDeclarationSyntax);
        Assert.IsType<ClassDeclarationSyntax>(found);
    }

    [Fact]
    public void FindTypeDeclaration_ColumnWithoutLine_EnumFirstPicksClass()
    {
        var root = CSharpSyntaxTree.ParseText(EnumFirstThenSameNamedClassSource).GetRoot();
        var enumColumn = ColumnOf(EnumFirstThenSameNamedClassSource, "Person");
        var found = GenerateToStringOperation.FindTypeDeclaration(
            root, "Person", line: null, enumColumn);

        Assert.NotNull(found);
        Assert.IsType<ClassDeclarationSyntax>(found);
    }

    [Fact]
    public void FindTypeDeclaration_ColumnWithoutLine_DelegateFirstPicksClass()
    {
        var root = CSharpSyntaxTree.ParseText(DelegateFirstThenSameNamedClassSource).GetRoot();
        var delegateColumn = ColumnOf(DelegateFirstThenSameNamedClassSource, "Person()");
        var found = GenerateToStringOperation.FindTypeDeclaration(
            root, "Person", line: null, delegateColumn);

        Assert.NotNull(found);
        Assert.IsType<ClassDeclarationSyntax>(found);
    }

    [Fact]
    public void FindTypeDeclaration_ColumnOnContinuationIdentifier_PicksType()
    {
        const string source = """
            namespace TestApp;

            public class
                Person // split-person
            {
                public string Name { get; set; }

                public class Person // nested-person
                {
                    public int Age { get; set; }
                }
            }
            """;

        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var startLine = FindLine(source, "public class");
        var identifierLine = FindLine(source, "split-person");
        Assert.NotEqual(startLine, identifierLine);

        var found = GenerateToStringOperation.FindTypeDeclaration(
            root, "Person", identifierLine, ColumnOf(source, "Person // split-person"));

        Assert.NotNull(found);
        Assert.False(found.Parent is TypeDeclarationSyntax);
        Assert.IsType<ClassDeclarationSyntax>(found);
    }

    [SkippableFact]
    public async Task GenerateToString_ColumnOnContinuationLine_PicksType()
    {
        const string source = """
            namespace TestApp;

            public class
                Person // split-person
            {
                public string Name { get; set; }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Person.cs");
        var operation = new GenerateToStringOperation(workspace.Context);
        var startLine = FindLine(source, "public class\n    Person");
        var identifierLine = FindLine(source, "split-person");
        Assert.NotEqual(startLine, identifierLine);

        var result = await operation.ExecuteAsync(new GenerateToStringParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Person",
            Line = identifierLine,
            Column = ColumnOf(source, "Person // split-person")
        });

        Assert.True(result.Success);
        var types = GetTypes(await File.ReadAllTextAsync(workspace.SourcePath), "Person");
        Assert.Single(types);
        Assert.True(TypeHasMethod(types[0], "ToString"));
        var toString = ExtractToStringMethod(NormalizeNewlines(types[0].ToFullString()));
        Assert.Contains("{Name}", toString);
    }

    [Fact]
    public void FindTypeDeclaration_ColumnOnEnumIdentifier_PicksEnum()
    {
        const string source = """
            namespace TestApp { public enum Person { Ready } public class Person { public string Name { get; set; } }
            """;

        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var line = FindLine(source, "public enum Person");
        var found = GenerateToStringOperation.FindTypeDeclaration(
            root, "Person", line, ColumnOf(source, "Person { Ready }"));

        Assert.NotNull(found);
        Assert.IsType<EnumDeclarationSyntax>(found);
    }

    [SkippableFact]
    public async Task GenerateToString_ColumnOnEnumIdentifier_SameNamedClass_ThrowsInvalidSymbolKind()
    {
        const string source = """
            namespace TestApp { public enum Person { Ready } public class Person { public string Name { get; set; } }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Person.cs");
        var operation = new GenerateToStringOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GenerateToStringParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Person",
                Line = FindLine(source, "public enum Person"),
                Column = ColumnOf(source, "Person { Ready }")
            }));

        Assert.Equal(ErrorCodes.InvalidSymbolKind, ex.ErrorCode);
        Assert.Equal("2020", ex.ErrorCode);
        Assert.Contains("not a supported target", ex.Message);
        var updated = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Equal(before, updated);
        Assert.DoesNotContain("override string ToString", updated, StringComparison.Ordinal);
    }

    [Fact]
    public void FindTypeDeclaration_ColumnOnDelegateIdentifier_PicksDelegate()
    {
        var root = CSharpSyntaxTree.ParseText(DelegateFirstThenSameNamedClassSource).GetRoot();
        var found = GenerateToStringOperation.FindTypeDeclaration(
            root, "Person",
            FindLine(DelegateFirstThenSameNamedClassSource, "person-delegate"),
            ColumnOf(DelegateFirstThenSameNamedClassSource, "Person()"));

        Assert.NotNull(found);
        Assert.IsType<DelegateDeclarationSyntax>(found);
    }

    [SkippableFact]
    public async Task GenerateToString_ColumnOnDelegateIdentifier_SameNamedClass_ThrowsInvalidSymbolKind()
    {
        await using var workspace = await TempWorkspace.CreateAsync(DelegateFirstThenSameNamedClassSource, "Person.cs");
        var operation = new GenerateToStringOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GenerateToStringParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Person",
                Line = FindLine(DelegateFirstThenSameNamedClassSource, "person-delegate"),
                Column = ColumnOf(DelegateFirstThenSameNamedClassSource, "Person()")
            }));

        Assert.Equal(ErrorCodes.InvalidSymbolKind, ex.ErrorCode);
        Assert.Equal("2020", ex.ErrorCode);
        Assert.Contains("not a supported target", ex.Message);
        var updated = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Equal(before, updated);
        Assert.DoesNotContain("override string ToString", updated, StringComparison.Ordinal);
    }

    [Fact]
    public void FindTypeDeclaration_ColumnAndLineMiss_DoesNotFallBackToFirst()
    {
        var root = CSharpSyntaxTree.ParseText(NestedSameNamePersonSource).GetRoot();
        var found = GenerateToStringOperation.FindTypeDeclaration(root, "Person", line: 1, column: 1);

        Assert.Null(found);
    }

    [SkippableFact]
    public async Task GenerateToString_ColumnAndLineMiss_ThrowsTypeNotFound()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NestedSameNamePersonSource, "Person.cs");
        var before = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new GenerateToStringOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GenerateToStringParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Person",
                Line = 1,
                Column = 1
            }));

        Assert.Equal(ErrorCodes.TypeNotFound, ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task GenerateToString_ColumnAndLine_UnknownTypeName_ThrowsTypeNotFound()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NestedSameNamePersonSource, "Person.cs");
        var before = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new GenerateToStringOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GenerateToStringParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Missing",
                Line = 1,
                Column = 1
            }));

        Assert.Equal(ErrorCodes.TypeNotFound, ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task GenerateToString_Column_Preview_WritesNothing_AndDescribesGeneration()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SameLineNestedPersonSource, "Person.cs");
        var operation = new GenerateToStringOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);
        var line = FindLine(SameLineNestedPersonSource, "public class Person { public string Name");

        var result = await operation.ExecuteAsync(new GenerateToStringParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Person",
            Line = line,
            Column = ColumnOf(SameLineNestedPersonSource, "Person { public int Age"),
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.NotEmpty(result.PendingChanges);
        Assert.Contains("Generate ToString", result.PendingChanges[0].Description);
        Assert.Contains("Person", result.PendingChanges[0].Description);
        Assert.Contains("Age", result.PendingChanges[0].Description);
        Assert.Contains("public override string ToString()", result.PendingChanges[0].AfterSnippet);
        Assert.Contains("{Age}", result.PendingChanges[0].AfterSnippet);
        Assert.DoesNotContain("{Name}", result.PendingChanges[0].AfterSnippet);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [Fact]
    public void SpanCoversColumn_TreatsEndAsExclusive()
    {
        const string source = "class Outer { class Nested { } }";
        var tree = CSharpSyntaxTree.ParseText(source);
        var nested = tree.GetRoot().DescendantNodes().OfType<TypeDeclarationSyntax>()
            .First(t => t.Identifier.Text == "Nested");
        var span = nested.GetLocation().GetLineSpan();
        var line = span.StartLinePosition.Line + 1;
        var startCol = span.StartLinePosition.Character + 1;
        var endCol = span.EndLinePosition.Character + 1;

        Assert.True(GenerateToStringOperation.SpanCoversColumn(span, line, startCol));
        Assert.True(GenerateToStringOperation.SpanCoversColumn(span, line, endCol - 1));
        Assert.False(GenerateToStringOperation.SpanCoversColumn(span, line, endCol));
        Assert.False(GenerateToStringOperation.SpanCoversColumn(span, line, startCol - 1));
    }

    [SkippableFact]
    public async Task GenerateToString_SequentialColumn_ReusedWorkspace_InsertsOnSecondSelectedType()
    {
        const string source = """
            namespace TestApp;

            public class Person { public string Name { get; set; } public override string ToString() => "old-outer"; public class Person { public int Age { get; set; } public override string ToString() => "old-nested"; } }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Person.cs");
        var operation = new GenerateToStringOperation(workspace.Context);
        var line = FindLine(source, "public class Person { public string Name");

        var first = await operation.ExecuteAsync(new GenerateToStringParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Person",
            Line = line,
            Column = ColumnOf(source, "Person { public string Name"),
            ReplaceExisting = true
        });
        Assert.True(first.Success);

        // Recompute from the rewritten file. A per-execution annotation
        // must not leave the first selected type as the only recover-able
        // node in a reused workspace.
        var afterFirst = await File.ReadAllTextAsync(workspace.SourcePath);
        var second = await operation.ExecuteAsync(new GenerateToStringParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Person",
            Line = FindLine(afterFirst, "old-nested"),
            Column = ColumnOf(afterFirst, "Person { public int Age"),
            ReplaceExisting = true
        });
        Assert.True(second.Success);

        var types = GetTypes(await File.ReadAllTextAsync(workspace.SourcePath), "Person");
        Assert.Equal(2, types.Count);
        Assert.True(TypeHasMethod(types[0], "ToString"));
        Assert.True(TypeHasMethod(types[1], "ToString"));
        // Own members only — types[0].ToFullString() also contains the nested
        // type's ToString, so ExtractToStringMethod on the outer dump would
        // see {Age} from the child.
        var outer = ExtractOwnToString(types[0]);
        var nested = ExtractOwnToString(types[1]);
        Assert.Contains("{Name}", outer);
        Assert.DoesNotContain("{Age}", outer);
        Assert.Contains("{Age}", nested);
        Assert.DoesNotContain("{Name}", nested);
        Assert.DoesNotContain("old-outer", outer, StringComparison.Ordinal);
        Assert.DoesNotContain("old-nested", nested, StringComparison.Ordinal);
        Assert.Single(types[0].Members.OfType<MethodDeclarationSyntax>(), m => m.Identifier.Text == "ToString");
        Assert.Single(types[1].Members.OfType<MethodDeclarationSyntax>(), m => m.Identifier.Text == "ToString");
    }

    #endregion

    #region Interpolated (default)

    [SkippableFact]
    public async Task GenerateToString_FormatOmitted_WritesInterpolatedBody()
    {
        await using var workspace = await TempWorkspace.CreateAsync(PersonSource);
        var operation = new GenerateToStringOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateToStringParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Person"
        });

        Assert.True(result.Success);
        AssertInterpolatedToString(NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath)));
    }

    [SkippableFact]
    public async Task GenerateToString_FormatEmpty_WritesInterpolatedBody()
    {
        await using var workspace = await TempWorkspace.CreateAsync(PersonSource);
        var operation = new GenerateToStringOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateToStringParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Person",
            Format = ""
        });

        Assert.True(result.Success);
        AssertInterpolatedToString(NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath)));
    }

    [SkippableTheory]
    [InlineData("interpolated")]
    [InlineData("INTERPOLATED")]
    [InlineData("Interpolated")]
    public async Task GenerateToString_FormatInterpolated_WritesInterpolatedBody(string format)
    {
        await using var workspace = await TempWorkspace.CreateAsync(PersonSource);
        var operation = new GenerateToStringOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateToStringParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Person",
            Format = format
        });

        Assert.True(result.Success);
        AssertInterpolatedToString(NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath)));
    }

    [SkippableFact]
    public async Task GenerateToString_Default_StillIncludesProperties()
    {
        const string source = """
            namespace TestApp;

            public class Widget
            {
                public string _id;

                public string Name { get; set; }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Widget.cs");
        var operation = new GenerateToStringOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateToStringParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var toString = ExtractToStringMethod(updated);
        Assert.Contains("{_id}", toString);
        Assert.Contains("{Name}", toString);
    }

    [SkippableFact]
    public async Task GenerateToString_Default_DoesNotCollectInheritedMembers()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
                public string Species;
            }

            public class Dog : Animal
            {
                public string Name;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Dog.cs");
        var operation = new GenerateToStringOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateToStringParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var toString = ExtractToStringMethod(updated);
        Assert.Contains("{Name}", toString);
        Assert.DoesNotContain("Species", toString);
    }

    #endregion

    #region StringBuilder

    [SkippableTheory]
    [InlineData("stringbuilder")]
    [InlineData("STRINGBUILDER")]
    [InlineData("StringBuilder")]
    public async Task GenerateToString_FormatStringBuilder_WritesStringBuilderBody(string format)
    {
        await using var workspace = await TempWorkspace.CreateAsync(PersonSource);
        var operation = new GenerateToStringOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateToStringParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Person",
            Format = format
        });

        Assert.True(result.Success);
        AssertStringBuilderToString(NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath)), "Name", "Age");
    }

    [SkippableFact]
    public async Task GenerateToString_FormatStringBuilder_HonorsFields()
    {
        await using var workspace = await TempWorkspace.CreateAsync(PersonSource);
        var operation = new GenerateToStringOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateToStringParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Person",
            Fields = new[] { "Name" },
            Format = "stringbuilder"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        AssertStringBuilderToString(updated, "Name");
        Assert.DoesNotContain("Age = ", updated);
    }

    [SkippableFact]
    public async Task GenerateToString_FormatStringBuilder_FieldNamedSb_UsesThisQualification()
    {
        const string source = """
            namespace TestApp;

            public class Holder
            {
                public string sb;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Holder.cs");
        var operation = new GenerateToStringOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateToStringParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Holder",
            Format = "stringbuilder"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var toString = ExtractToStringMethod(updated);
        Assert.Contains("global::System.Text.StringBuilder", toString);
        Assert.Contains("Append(this.sb)", toString);
        Assert.DoesNotContain("Append(sb)", toString);
        Assert.Contains("sb.ToString()", toString);
    }

    [SkippableFact]
    public async Task GenerateToString_FormatStringBuilder_NamespaceNamedSystem_UsesGlobalQualifiedStringBuilder()
    {
        const string source = """
            namespace System;

            public class Widget
            {
                public string Name { get; set; }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Widget.cs");
        var operation = new GenerateToStringOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateToStringParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget",
            Format = "stringbuilder"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var toString = ExtractToStringMethod(updated);
        Assert.Contains("global::System.Text.StringBuilder", toString);
        Assert.Contains("Append(this.Name)", toString);
        Assert.DoesNotContain("new System.Text.StringBuilder", toString);
    }

    #endregion

    #region Preview

    [SkippableFact]
    public async Task GenerateToString_PreviewInterpolated_DoesNotWriteFiles()
    {
        await using var workspace = await TempWorkspace.CreateAsync(PersonSource);
        var operation = new GenerateToStringOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var result = await operation.ExecuteAsync(new GenerateToStringParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Person",
            Format = "interpolated",
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.NotEmpty(result.PendingChanges);
        AssertInterpolatedToString(result.PendingChanges[0].AfterSnippet!);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task GenerateToString_PreviewStringBuilder_DoesNotWriteFiles()
    {
        await using var workspace = await TempWorkspace.CreateAsync(PersonSource);
        var operation = new GenerateToStringOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var result = await operation.ExecuteAsync(new GenerateToStringParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Person",
            Format = "stringbuilder",
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.NotEmpty(result.PendingChanges);
        AssertStringBuilderToString(result.PendingChanges[0].AfterSnippet!, "Name", "Age");
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    #endregion

    #region includeInheritedMembers

    private const string DogOnAnimalSource = """
        namespace TestApp;

        public class Animal
        {
            public string Species;

            protected int Legs;

            private string Secret;

            public string Nickname { get; set; }
        }

        public class Dog : Animal
        {
            public string Name;
        }
        """;

    [SkippableFact]
    public async Task GenerateToString_IncludeInheritedMembersFalse_DoesNotIncludeBaseField()
    {
        await using var workspace = await TempWorkspace.CreateAsync(DogOnAnimalSource, "Dog.cs");
        var operation = new GenerateToStringOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateToStringParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            IncludeInheritedMembers = false
        });

        Assert.True(result.Success);
        var toString = ExtractToStringMethod(NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath)));
        Assert.Contains("{Name}", toString);
        Assert.DoesNotContain("Species", toString);
        Assert.DoesNotContain("Legs", toString);
        Assert.DoesNotContain("Secret", toString);
        Assert.DoesNotContain("Nickname", toString);
    }

    [SkippableFact]
    public async Task GenerateToString_IncludeInheritedMembersTrue_IncludesPublicAndProtectedBaseFields()
    {
        await using var workspace = await TempWorkspace.CreateAsync(DogOnAnimalSource, "Dog.cs");
        var operation = new GenerateToStringOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateToStringParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            IncludeInheritedMembers = true
        });

        Assert.True(result.Success);
        var toString = ExtractToStringMethod(NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath)));
        Assert.Contains("{Name}", toString);
        Assert.Contains("{Species}", toString);
        Assert.Contains("{Legs}", toString);
        Assert.Contains("{Nickname}", toString);
        Assert.DoesNotContain("Secret", toString);
    }

    [SkippableFact]
    public async Task GenerateToString_IncludeInheritedMembersTrue_SkipsPrivateBaseField()
    {
        await using var workspace = await TempWorkspace.CreateAsync(DogOnAnimalSource, "Dog.cs");
        var operation = new GenerateToStringOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateToStringParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            IncludeInheritedMembers = true
        });

        Assert.True(result.Success);
        var toString = ExtractToStringMethod(NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath)));
        Assert.DoesNotContain("Secret", toString);
        Assert.DoesNotContain("{Secret}", toString);
    }

    [SkippableFact]
    public async Task GenerateToString_IncludeInheritedMembersTrue_FieldsNamesInheritedMember_IncludesIt()
    {
        await using var workspace = await TempWorkspace.CreateAsync(DogOnAnimalSource, "Dog.cs");
        var operation = new GenerateToStringOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateToStringParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            IncludeInheritedMembers = true,
            Fields = new[] { "Species" }
        });

        Assert.True(result.Success);
        var toString = ExtractToStringMethod(NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath)));
        Assert.Contains("{Species}", toString);
        Assert.DoesNotContain("{Name}", toString);
        Assert.DoesNotContain("Legs", toString);
        Assert.DoesNotContain("Nickname", toString);
    }

    [SkippableFact]
    public async Task GenerateToString_IncludeInheritedMembersFalse_FieldsNamesInheritedMember_NotFound()
    {
        await using var workspace = await TempWorkspace.CreateAsync(DogOnAnimalSource, "Dog.cs");
        var operation = new GenerateToStringOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GenerateToStringParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Dog",
                IncludeInheritedMembers = false,
                Fields = new[] { "Species" }
            }));

        Assert.Equal(ErrorCodes.NoMembersToGenerate, ex.ErrorCode);
        Assert.Equal("3055", ex.ErrorCode);
        Assert.Equal(DogOnAnimalSource, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task GenerateToString_IncludeInheritedMembersTrue_ObjectOnlyBase_NoExtraMembers()
    {
        await using var workspace = await TempWorkspace.CreateAsync(PersonSource);
        var operation = new GenerateToStringOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateToStringParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Person",
            IncludeInheritedMembers = true
        });

        Assert.True(result.Success);
        var toString = ExtractToStringMethod(NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath)));
        Assert.Contains("{Name}", toString);
        Assert.Contains("{Age}", toString);
        Assert.DoesNotContain("Equals", toString);
        Assert.DoesNotContain("GetHashCode", toString);
        Assert.DoesNotContain("GetType", toString);
    }

    [SkippableFact]
    public async Task GenerateToString_IncludeInheritedMembersTrue_StringBuilder_IncludesInheritedMembers()
    {
        await using var workspace = await TempWorkspace.CreateAsync(DogOnAnimalSource, "Dog.cs");
        var operation = new GenerateToStringOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateToStringParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            IncludeInheritedMembers = true,
            Format = "stringbuilder"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var toString = ExtractToStringMethod(updated);
        Assert.Contains("global::System.Text.StringBuilder", toString);
        Assert.Contains("Append(this.Name)", toString);
        Assert.Contains("Append(this.Species)", toString);
        Assert.Contains("Append(this.Legs)", toString);
        Assert.Contains("Append(this.Nickname)", toString);
        Assert.DoesNotContain("Secret", toString);
        Assert.DoesNotContain("$\"Dog", toString);
    }

    [SkippableFact]
    public async Task GenerateToString_IncludeInheritedMembersTrue_Preview_DoesNotWriteFiles()
    {
        await using var workspace = await TempWorkspace.CreateAsync(DogOnAnimalSource, "Dog.cs");
        var operation = new GenerateToStringOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var result = await operation.ExecuteAsync(new GenerateToStringParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            IncludeInheritedMembers = true,
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.NotEmpty(result.PendingChanges);
        Assert.Contains("inherited members", result.PendingChanges[0].Description);
        Assert.Contains("interpolated", result.PendingChanges[0].Description);
        Assert.Contains("Name", result.PendingChanges[0].Description);
        Assert.Contains("Species", result.PendingChanges[0].Description);
        var snippet = result.PendingChanges[0].AfterSnippet!;
        Assert.Contains("{Name}", snippet);
        Assert.Contains("{Species}", snippet);
        Assert.Contains("{Legs}", snippet);
        Assert.Contains("{Nickname}", snippet);
        Assert.DoesNotContain("Secret", snippet);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task GenerateToString_IncludeInheritedMembersTrue_CloserMethodHidesInheritedField()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
                public string Name;

                public string Species;
            }

            public class Dog : Animal
            {
                public string Extra;

                public string Name() => Extra;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Dog.cs");
        var operation = new GenerateToStringOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateToStringParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            IncludeInheritedMembers = true,
            Format = "stringbuilder"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var toString = ExtractToStringMethod(updated);
        Assert.Contains("Append(this.Extra)", toString);
        Assert.Contains("Append(this.Species)", toString);
        Assert.DoesNotContain("Append(this.Name)", toString);
        Assert.DoesNotContain("Name = ", toString);
        Assert.DoesNotContain("{Name}", toString);
        AssertCompiles(updated);
    }

    [SkippableFact]
    public async Task GenerateToString_IncludeInheritedMembersTrue_InheritedMemberNamedToString_IsOmitted()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
                public string ToString;

                public string Species;
            }

            public class Dog : Animal
            {
                public string Name;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Dog.cs");
        var operation = new GenerateToStringOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateToStringParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            IncludeInheritedMembers = true,
            Format = "stringbuilder"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var toString = ExtractToStringMethod(updated);
        Assert.Contains("Append(this.Name)", toString);
        Assert.Contains("Append(this.Species)", toString);
        Assert.DoesNotContain("Append(this.ToString)", toString);
        Assert.DoesNotContain("ToString = ", toString);
        Assert.DoesNotContain("{ToString}", toString);
        Assert.Contains("sb.ToString()", toString);
    }

    #endregion

    #region includeProperties

    private const string WidgetWithFieldAndPropertySource = """
        namespace TestApp;

        public class Widget
        {
            public string _id;

            public string Name { get; set; }
        }
        """;

    [SkippableFact]
    public async Task GenerateToString_IncludePropertiesOmitted_IncludesFieldAndProperty()
    {
        await using var workspace = await TempWorkspace.CreateAsync(WidgetWithFieldAndPropertySource, "Widget.cs");
        var operation = new GenerateToStringOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateToStringParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget"
        });

        Assert.True(result.Success);
        var toString = ExtractToStringMethod(NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath)));
        Assert.Contains("{_id}", toString);
        Assert.Contains("{Name}", toString);
    }

    [SkippableFact]
    public async Task GenerateToString_IncludePropertiesTrue_IncludesFieldAndProperty()
    {
        await using var workspace = await TempWorkspace.CreateAsync(WidgetWithFieldAndPropertySource, "Widget.cs");
        var operation = new GenerateToStringOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateToStringParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget",
            IncludeProperties = true
        });

        Assert.True(result.Success);
        var toString = ExtractToStringMethod(NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath)));
        Assert.Contains("{_id}", toString);
        Assert.Contains("{Name}", toString);
    }

    [SkippableFact]
    public async Task GenerateToString_IncludePropertiesFalse_IncludesFieldOnly()
    {
        await using var workspace = await TempWorkspace.CreateAsync(WidgetWithFieldAndPropertySource, "Widget.cs");
        var operation = new GenerateToStringOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateToStringParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget",
            IncludeProperties = false
        });

        Assert.True(result.Success);
        var toString = ExtractToStringMethod(NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath)));
        Assert.Contains("{_id}", toString);
        Assert.DoesNotContain("{Name}", toString);
        Assert.DoesNotContain("Name = ", toString);
    }

    [SkippableFact]
    public async Task GenerateToString_IncludePropertiesFalse_EmptyFieldsList_IncludesFieldOnly()
    {
        await using var workspace = await TempWorkspace.CreateAsync(WidgetWithFieldAndPropertySource, "Widget.cs");
        var operation = new GenerateToStringOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateToStringParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget",
            Fields = Array.Empty<string>(),
            IncludeProperties = false
        });

        Assert.True(result.Success);
        var toString = ExtractToStringMethod(NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath)));
        Assert.Contains("{_id}", toString);
        Assert.DoesNotContain("{Name}", toString);
        Assert.DoesNotContain("Name = ", toString);
    }

    [SkippableFact]
    public async Task GenerateToString_IncludePropertiesFalse_PropertiesOnly_FailsWithNoMembersToGenerate()
    {
        await using var workspace = await TempWorkspace.CreateAsync(PersonSource);
        var operation = new GenerateToStringOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GenerateToStringParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Person",
                IncludeProperties = false
            }));

        Assert.Equal(ErrorCodes.NoMembersToGenerate, ex.ErrorCode);
        Assert.Equal("3055", ex.ErrorCode);
        Assert.Contains("No fields or properties", ex.Message);
        Assert.Equal(PersonSource, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task GenerateToString_IncludePropertiesFalse_FieldsNamesProperty_IncludesThatProperty()
    {
        await using var workspace = await TempWorkspace.CreateAsync(WidgetWithFieldAndPropertySource, "Widget.cs");
        var operation = new GenerateToStringOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateToStringParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget",
            Fields = new[] { "Name" },
            IncludeProperties = false
        });

        Assert.True(result.Success);
        var toString = ExtractToStringMethod(NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath)));
        Assert.Contains("{Name}", toString);
        Assert.DoesNotContain("{_id}", toString);
        Assert.DoesNotContain("_id = ", toString);
    }

    [SkippableFact]
    public async Task GenerateToString_IncludePropertiesFalse_IncludeInheritedMembersTrue_SkipsInheritedProperties()
    {
        await using var workspace = await TempWorkspace.CreateAsync(DogOnAnimalSource, "Dog.cs");
        var operation = new GenerateToStringOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateToStringParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            IncludeProperties = false,
            IncludeInheritedMembers = true
        });

        Assert.True(result.Success);
        var toString = ExtractToStringMethod(NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath)));
        Assert.Contains("{Name}", toString);
        Assert.Contains("{Species}", toString);
        Assert.Contains("{Legs}", toString);
        Assert.DoesNotContain("Nickname", toString);
        Assert.DoesNotContain("Secret", toString);
    }

    [SkippableFact]
    public async Task GenerateToString_IncludePropertiesFalse_FieldsNamesInheritedProperty_IncludesIt()
    {
        await using var workspace = await TempWorkspace.CreateAsync(DogOnAnimalSource, "Dog.cs");
        var operation = new GenerateToStringOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateToStringParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            IncludeProperties = false,
            IncludeInheritedMembers = true,
            Fields = new[] { "Nickname" }
        });

        Assert.True(result.Success);
        var toString = ExtractToStringMethod(NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath)));
        Assert.Contains("{Nickname}", toString);
        Assert.DoesNotContain("{Name}", toString);
        Assert.DoesNotContain("Species", toString);
        Assert.DoesNotContain("Legs", toString);
    }

    [SkippableFact]
    public async Task GenerateToString_IncludePropertiesTrue_StringBuilderAndInheritedMembers_StillWorks()
    {
        await using var workspace = await TempWorkspace.CreateAsync(DogOnAnimalSource, "Dog.cs");
        var operation = new GenerateToStringOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateToStringParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            IncludeProperties = true,
            IncludeInheritedMembers = true,
            Format = "stringbuilder"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var toString = ExtractToStringMethod(updated);
        Assert.Contains("global::System.Text.StringBuilder", toString);
        Assert.Contains("Append(this.Name)", toString);
        Assert.Contains("Append(this.Species)", toString);
        Assert.Contains("Append(this.Legs)", toString);
        Assert.Contains("Append(this.Nickname)", toString);
        Assert.DoesNotContain("Secret", toString);
        Assert.DoesNotContain("$\"Dog", toString);
    }

    [SkippableFact]
    public async Task GenerateToString_IncludePropertiesFalse_Preview_DoesNotWriteFiles_AndDescribesFieldOnly()
    {
        await using var workspace = await TempWorkspace.CreateAsync(WidgetWithFieldAndPropertySource, "Widget.cs");
        var operation = new GenerateToStringOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var result = await operation.ExecuteAsync(new GenerateToStringParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget",
            IncludeProperties = false,
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.NotEmpty(result.PendingChanges);
        Assert.Contains("_id", result.PendingChanges[0].Description);
        Assert.DoesNotContain("Name", result.PendingChanges[0].Description);
        var snippet = result.PendingChanges[0].AfterSnippet!;
        Assert.Contains("{_id}", snippet);
        Assert.DoesNotContain("{Name}", snippet);
        Assert.DoesNotContain("Name = ", snippet);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task GenerateToString_IncludePropertiesFalse_ReplaceExisting_StillWorks()
    {
        const string source = """
            namespace TestApp;

            public class Widget
            {
                public string _id;

                public string Name { get; set; }

                public override string ToString() => "old";
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Widget.cs");
        var operation = new GenerateToStringOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateToStringParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget",
            IncludeProperties = false,
            ReplaceExisting = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var toString = ExtractToStringMethod(updated);
        Assert.DoesNotContain("=> \"old\"", updated);
        Assert.Contains("{_id}", toString);
        Assert.DoesNotContain("{Name}", toString);
        Assert.Equal(1, CountOccurrences(updated, "public override string ToString()"));
    }

    [SkippableFact]
    public async Task GenerateToString_IncludePropertiesFalse_MemberNamedToString_IsOmitted()
    {
        const string source = """
            namespace TestApp;

            public class Widget
            {
                public string ToString;

                public string Name;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Widget.cs");
        var operation = new GenerateToStringOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateToStringParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget",
            IncludeProperties = false
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var toString = ExtractToStringMethod(updated);
        Assert.Contains("{Name}", toString);
        Assert.DoesNotContain("{ToString}", toString);
        Assert.DoesNotContain("ToString = ", toString);
    }

    #endregion

    #region replaceExisting

    private const string PersonWithToStringSource = """
        namespace TestApp;

        public class Person
        {
            public string Name { get; set; }

            public int Age { get; set; }

            public override string ToString() => "old";
        }
        """;

    [SkippableFact]
    public async Task GenerateToString_ReplaceExistingOmitted_ExistingToString_FailsWith3056()
    {
        await using var workspace = await TempWorkspace.CreateAsync(PersonWithToStringSource);
        var operation = new GenerateToStringOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GenerateToStringParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Person"
            }));

        Assert.Equal(ErrorCodes.AlreadyHasOverride, ex.ErrorCode);
        Assert.Equal("3056", ex.ErrorCode);
        Assert.Equal("Type already has a ToString override.", ex.Message);
        Assert.Equal(PersonWithToStringSource, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task GenerateToString_ReplaceExistingFalse_ExistingToString_FailsWith3056()
    {
        await using var workspace = await TempWorkspace.CreateAsync(PersonWithToStringSource);
        var operation = new GenerateToStringOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GenerateToStringParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Person",
                ReplaceExisting = false
            }));

        Assert.Equal(ErrorCodes.AlreadyHasOverride, ex.ErrorCode);
        Assert.Equal("3056", ex.ErrorCode);
        Assert.Equal("Type already has a ToString override.", ex.Message);
        Assert.Equal(PersonWithToStringSource, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task GenerateToString_ReplaceExistingTrue_ReplacesParameterlessToString()
    {
        await using var workspace = await TempWorkspace.CreateAsync(PersonWithToStringSource);
        var operation = new GenerateToStringOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateToStringParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Person",
            ReplaceExisting = true,
            Fields = new[] { "Name" }
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var toString = ExtractToStringMethod(updated);
        Assert.DoesNotContain("=> \"old\"", updated);
        Assert.DoesNotContain("old", toString);
        Assert.Contains("{Name}", toString);
        Assert.DoesNotContain("{Age}", toString);
        Assert.Equal(1, CountOccurrences(updated, "public override string ToString()"));
    }

    [SkippableFact]
    public async Task GenerateToString_ReplaceExistingTrue_NoExistingToString_GeneratesAsToday()
    {
        await using var workspace = await TempWorkspace.CreateAsync(PersonSource);
        var operation = new GenerateToStringOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateToStringParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Person",
            ReplaceExisting = true
        });

        Assert.True(result.Success);
        AssertInterpolatedToString(NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath)));
    }

    [SkippableFact]
    public async Task GenerateToString_ReplaceExistingTrue_ExistingToStringStringOverload_LeavesOverloadAndGeneratesParameterless()
    {
        const string source = """
            namespace TestApp;

            public class Person
            {
                public string Name { get; set; }

                public int Age { get; set; }

                public string ToString(string format) => format;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new GenerateToStringOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateToStringParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Person",
            ReplaceExisting = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("public string ToString(string format) => format;", updated);
        AssertInterpolatedToString(updated);
        Assert.Equal(1, CountOccurrences(updated, "public override string ToString()"));
        Assert.Equal(1, CountOccurrences(updated, "ToString(string format)"));
    }

    [SkippableFact]
    public async Task GenerateToString_ReplaceExistingTrue_PartialOtherFile_RemovesOtherPartToString()
    {
        const string fieldsPart = """
            namespace TestApp;

            public partial class Person
            {
                public string Name { get; set; }

                public int Age { get; set; }
            }
            """;

        const string toStringPart = """
            namespace TestApp;

            public partial class Person
            {
                public override string ToString() => "old";
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(
            ("Person.cs", fieldsPart),
            ("Person.ToString.cs", toStringPart));
        var otherPath = workspace.PathFor("Person.ToString.cs");
        var operation = new GenerateToStringOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateToStringParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Person",
            ReplaceExisting = true
        });

        Assert.True(result.Success);
        var selected = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var other = NormalizeNewlines(await File.ReadAllTextAsync(otherPath));
        AssertInterpolatedToString(selected);
        Assert.DoesNotContain("=> \"old\"", selected);
        Assert.DoesNotContain("public override string ToString", other);
        Assert.DoesNotContain("=> \"old\"", other);
        Assert.Equal(1, CountOccurrences(selected, "public override string ToString()"));
    }

    [SkippableFact]
    public async Task GenerateToString_ReplaceExistingTrue_StringBuilderAndInheritedMembers_StillWorks()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
                public string Species;
            }

            public class Dog : Animal
            {
                public string Name;

                public override string ToString() => "old";
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Dog.cs");
        var operation = new GenerateToStringOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateToStringParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            ReplaceExisting = true,
            Format = "stringbuilder",
            IncludeInheritedMembers = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var toString = ExtractToStringMethod(updated);
        Assert.DoesNotContain("=> \"old\"", updated);
        Assert.Contains("global::System.Text.StringBuilder", toString);
        Assert.Contains("Append(this.Name)", toString);
        Assert.Contains("Append(this.Species)", toString);
        Assert.DoesNotContain("$\"Dog", toString);
        Assert.Equal(1, CountOccurrences(updated, "public override string ToString()"));
    }

    [SkippableFact]
    public async Task GenerateToString_ReplaceExistingTrue_Preview_DoesNotWriteFiles_AndDescribesReplacement()
    {
        await using var workspace = await TempWorkspace.CreateAsync(PersonWithToStringSource);
        var operation = new GenerateToStringOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var result = await operation.ExecuteAsync(new GenerateToStringParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Person",
            ReplaceExisting = true,
            Format = "stringbuilder",
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.NotEmpty(result.PendingChanges);
        Assert.Contains("Replace", result.PendingChanges[0].Description);
        Assert.Contains("stringbuilder", result.PendingChanges[0].Description);
        Assert.Contains("Name", result.PendingChanges[0].Description);
        Assert.Contains("Age", result.PendingChanges[0].Description);
        Assert.Contains("replacing existing ToString", result.PendingChanges[0].BeforeSnippet);
        AssertStringBuilderToString(result.PendingChanges[0].AfterSnippet!, "Name", "Age");
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task GenerateToString_ReplaceExistingTrue_PartialOtherFile_Preview_DoesNotWriteFiles()
    {
        const string fieldsPart = """
            namespace TestApp;

            public partial class Person
            {
                public string Name { get; set; }

                public int Age { get; set; }
            }
            """;

        const string toStringPart = """
            namespace TestApp;

            public partial class Person
            {
                public override string ToString() => "old";
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(
            ("Person.cs", fieldsPart),
            ("Person.ToString.cs", toStringPart));
        var otherPath = workspace.PathFor("Person.ToString.cs");
        var operation = new GenerateToStringOperation(workspace.Context);
        var beforeSelected = await File.ReadAllTextAsync(workspace.SourcePath);
        var beforeOther = await File.ReadAllTextAsync(otherPath);

        var result = await operation.ExecuteAsync(new GenerateToStringParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Person",
            ReplaceExisting = true,
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.Equal(beforeSelected, await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Equal(beforeOther, await File.ReadAllTextAsync(otherPath));
    }

    private const string PersonWithGenericToStringSource = """
        namespace TestApp;

        public class Person
        {
            public string Name { get; set; }

            public string ToString<T>() => typeof(T).Name;
        }
        """;

    [SkippableFact]
    public async Task GenerateToString_ReplaceExistingOmitted_GenericToString_LeavesGenericAndGeneratesInstance()
    {
        await using var workspace = await TempWorkspace.CreateAsync(PersonWithGenericToStringSource);
        var operation = new GenerateToStringOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateToStringParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Person"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("public string ToString<T>() => typeof(T).Name;", updated);
        Assert.Contains("public override string ToString()", updated);
        Assert.Contains("{Name}", updated);
        Assert.Equal(1, CountOccurrences(updated, "ToString<T>()"));
        Assert.Equal(1, CountOccurrences(updated, "public override string ToString()"));
    }

    [SkippableFact]
    public async Task GenerateToString_ReplaceExistingFalse_GenericToString_LeavesGenericAndGeneratesInstance()
    {
        await using var workspace = await TempWorkspace.CreateAsync(PersonWithGenericToStringSource);
        var operation = new GenerateToStringOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateToStringParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Person",
            ReplaceExisting = false
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("public string ToString<T>() => typeof(T).Name;", updated);
        Assert.Contains("public override string ToString()", updated);
        Assert.Equal(1, CountOccurrences(updated, "ToString<T>()"));
    }

    [SkippableFact]
    public async Task GenerateToString_ReplaceExistingTrue_GenericToString_LeavesGenericAndGeneratesInstance()
    {
        await using var workspace = await TempWorkspace.CreateAsync(PersonWithGenericToStringSource);
        var operation = new GenerateToStringOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateToStringParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Person",
            ReplaceExisting = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("public string ToString<T>() => typeof(T).Name;", updated);
        Assert.Contains("public override string ToString()", updated);
        Assert.Contains("{Name}", updated);
        Assert.Equal(1, CountOccurrences(updated, "ToString<T>()"));
        Assert.Equal(1, CountOccurrences(updated, "public override string ToString()"));
    }

    [SkippableFact]
    public async Task GenerateToString_ReplaceExistingTrue_GenericPlusInstanceToString_ReplacesOnlyNonGeneric()
    {
        const string source = """
            namespace TestApp;

            public class Person
            {
                public string Name { get; set; }

                public string ToString<T>() => typeof(T).Name;

                public override string ToString() => "old";
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new GenerateToStringOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateToStringParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Person",
            ReplaceExisting = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("public string ToString<T>() => typeof(T).Name;", updated);
        Assert.DoesNotContain("=> \"old\"", updated);
        Assert.Contains("{Name}", updated);
        Assert.Equal(1, CountOccurrences(updated, "ToString<T>()"));
        Assert.Equal(1, CountOccurrences(updated, "public override string ToString()"));
    }

    private const string PersonWithStaticToStringSource = """
        namespace TestApp;

        public class Person
        {
            public string Name { get; set; }

            public static new string ToString() => "x";
        }
        """;

    [SkippableFact]
    public async Task GenerateToString_ReplaceExistingOmitted_StaticToString_FailsWith3056()
    {
        await using var workspace = await TempWorkspace.CreateAsync(PersonWithStaticToStringSource);
        var operation = new GenerateToStringOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GenerateToStringParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Person"
            }));

        Assert.Equal(ErrorCodes.AlreadyHasOverride, ex.ErrorCode);
        Assert.Equal("3056", ex.ErrorCode);
        Assert.Equal("Type already has a ToString override.", ex.Message);
        Assert.Equal(PersonWithStaticToStringSource, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task GenerateToString_ReplaceExistingFalse_StaticToString_FailsWith3056()
    {
        await using var workspace = await TempWorkspace.CreateAsync(PersonWithStaticToStringSource);
        var operation = new GenerateToStringOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GenerateToStringParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Person",
                ReplaceExisting = false
            }));

        Assert.Equal(ErrorCodes.AlreadyHasOverride, ex.ErrorCode);
        Assert.Equal(PersonWithStaticToStringSource, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task GenerateToString_ReplaceExistingTrue_StaticToString_RemovesStaticAndGeneratesInstance()
    {
        await using var workspace = await TempWorkspace.CreateAsync(PersonWithStaticToStringSource);
        var operation = new GenerateToStringOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateToStringParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Person",
            ReplaceExisting = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.DoesNotContain("static new string ToString()", updated);
        Assert.DoesNotContain("=> \"x\"", updated);
        Assert.Contains("public override string ToString()", updated);
        Assert.Contains("{Name}", updated);
        Assert.Equal(1, CountOccurrences(updated, "ToString()"));
        Assert.DoesNotContain("static", ExtractToStringMethod(updated));
    }

    #endregion

    #region callSuper

    private const string EntitySource = """
        namespace TestApp;

        public class Entity
        {
            public int Id { get; set; }

            public override string ToString() => $"Entity {{ Id = {Id} }}";
        }
        """;

    private const string PersonOnEntitySource = """
        namespace TestApp;

        public class Person : Entity
        {
            public string Name { get; set; }

            public int Age { get; set; }
        }
        """;

    private const string EmployeeOnEntitySource = """
        namespace TestApp;

        public class Employee : Entity
        {
        }
        """;

    private const string PointSource = """
        namespace TestApp;

        public struct Point
        {
            public int X { get; set; }

            public int Y { get; set; }
        }
        """;

    private static Task<TempWorkspace> CreateDerivedOnEntityAsync(string derivedSource, string fileName = "Person.cs") =>
        TempWorkspace.CreateAsync((fileName, derivedSource), ("Entity.cs", EntitySource));

    [SkippableFact]
    public async Task GenerateToString_CallSuperOmitted_DoesNotCallBase()
    {
        await using var workspace = await TempWorkspace.CreateAsync(PersonSource);
        var operation = new GenerateToStringOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateToStringParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Person"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        AssertInterpolatedToString(updated);
        Assert.DoesNotContain("base.ToString()", updated);
    }

    [SkippableFact]
    public async Task GenerateToString_CallSuperFalse_DoesNotCallBase()
    {
        await using var workspace = await CreateDerivedOnEntityAsync(PersonOnEntitySource);
        var operation = new GenerateToStringOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateToStringParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Person",
            CallSuper = false
        });

        Assert.True(result.Success);
        var toString = ExtractToStringMethod(NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath)));
        Assert.Contains("{Name}", toString);
        Assert.Contains("{Age}", toString);
        Assert.DoesNotContain("base.ToString()", toString);
    }

    [SkippableFact]
    public async Task GenerateToString_CallSuperTrue_Interpolated_IncludesBaseToStringFirst()
    {
        const string source = """
            namespace TestApp;

            public class Entity
            {
                public int Id { get; set; }

                public override string ToString() => $"Entity {{ Id = {Id} }}";
            }

            public class Person : Entity
            {
                public string Name { get; set; }

                public int Age { get; set; }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new GenerateToStringOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateToStringParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Person",
            CallSuper = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var toString = ExtractToStringMethod(updated);
        Assert.Contains("$\"Person {{ {base.ToString()}", toString);
        Assert.Contains("{base.ToString()}", toString);
        Assert.Contains("{Name}", toString);
        Assert.Contains("{Age}", toString);
        Assert.DoesNotContain("StringBuilder", toString);
        var baseCall = toString.IndexOf("{base.ToString()}", StringComparison.Ordinal);
        var nameInterp = toString.IndexOf("{Name}", StringComparison.Ordinal);
        Assert.True(baseCall >= 0 && nameInterp > baseCall, "base.ToString() should appear before this type's members");
        AssertCompiles(updated);
    }

    [SkippableFact]
    public async Task GenerateToString_CallSuperTrue_StringBuilder_AppendsBaseToStringFirst()
    {
        const string source = """
            namespace TestApp;

            public class Entity
            {
                public int Id { get; set; }

                public override string ToString() => $"Entity {{ Id = {Id} }}";
            }

            public class Person : Entity
            {
                public string Name { get; set; }

                public int Age { get; set; }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new GenerateToStringOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateToStringParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Person",
            CallSuper = true,
            Format = "stringbuilder"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var toString = ExtractToStringMethod(updated);
        Assert.Contains("global::System.Text.StringBuilder", toString);
        Assert.Contains("Append(base.ToString())", toString);
        Assert.Contains("Append(this.Name)", toString);
        Assert.Contains("Append(this.Age)", toString);
        Assert.DoesNotContain("$\"Person", toString);
        var appendBase = toString.IndexOf("Append(base.ToString())", StringComparison.Ordinal);
        var appendName = toString.IndexOf("Append(this.Name)", StringComparison.Ordinal);
        Assert.True(appendBase >= 0 && appendName > appendBase, "base.ToString() should be Append-ed before members");
        AssertCompiles(updated);
    }

    [SkippableFact]
    public async Task GenerateToString_CallSuperTrue_ObjectBase_FailsWith3146()
    {
        await using var workspace = await TempWorkspace.CreateAsync(PersonSource);
        var operation = new GenerateToStringOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GenerateToStringParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Person",
                CallSuper = true
            }));

        Assert.Equal(ErrorCodes.CallSuperOnObjectBase, ex.ErrorCode);
        Assert.Equal("3146", ex.ErrorCode);
        Assert.Contains("Object", ex.Message);
        Assert.Equal(PersonSource, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task GenerateToString_CallSuperTrue_Struct_FailsWith3146()
    {
        await using var workspace = await TempWorkspace.CreateAsync(PointSource, "Point.cs");
        var operation = new GenerateToStringOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GenerateToStringParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Point",
                CallSuper = true
            }));

        Assert.Equal(ErrorCodes.CallSuperOnObjectBase, ex.ErrorCode);
        Assert.Equal("3146", ex.ErrorCode);
        Assert.Contains("ValueType", ex.Message);
        Assert.Equal(PointSource, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    private const string AbstractEntityPersonSource = """
        namespace TestApp;

        public abstract class Entity
        {
            public abstract override string ToString();
        }

        public class Person : Entity
        {
            public string Name { get; set; }

            public int Age { get; set; }

            public override string ToString() => "old";
        }
        """;

    [SkippableFact]
    public async Task GenerateToString_CallSuperTrue_AbstractBaseToString_FailsWith3147()
    {
        await using var workspace = await TempWorkspace.CreateAsync(AbstractEntityPersonSource);
        var operation = new GenerateToStringOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GenerateToStringParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Person",
                CallSuper = true
            }));

        Assert.Equal(ErrorCodes.CallSuperOnAbstractBase, ex.ErrorCode);
        Assert.Equal("3147", ex.ErrorCode);
        Assert.Contains("abstract", ex.Message);
        Assert.Equal(AbstractEntityPersonSource, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task GenerateToString_CallSuperTrue_ReplaceExisting_AbstractBase_FailsWith3147_DoesNotRewrite()
    {
        await using var workspace = await TempWorkspace.CreateAsync(AbstractEntityPersonSource);
        var operation = new GenerateToStringOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GenerateToStringParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Person",
                CallSuper = true,
                ReplaceExisting = true
            }));

        Assert.Equal(ErrorCodes.CallSuperOnAbstractBase, ex.ErrorCode);
        Assert.Equal("3147", ex.ErrorCode);
        Assert.Contains("abstract", ex.Message);
        Assert.Equal(AbstractEntityPersonSource, await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("=> \"old\"", await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.DoesNotContain("base.ToString()", await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task GenerateToString_CallSuperTrue_NoMembers_GeneratesBaseOnly()
    {
        await using var workspace = await CreateDerivedOnEntityAsync(EmployeeOnEntitySource, "Employee.cs");
        var operation = new GenerateToStringOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateToStringParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Employee",
            CallSuper = true
        });

        Assert.True(result.Success);
        var toString = ExtractToStringMethod(NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath)));
        Assert.Contains("{base.ToString()}", toString);
        Assert.DoesNotContain("Id = ", toString);
        Assert.DoesNotContain("{Id}", toString);
    }

    [SkippableFact]
    public async Task GenerateToString_CallSuperTrue_IncludeInheritedMembers_StillCollectsAsToday()
    {
        const string derived = """
            namespace TestApp;

            public class Person : Entity
            {
                public string Name;
            }
            """;

        await using var workspace = await CreateDerivedOnEntityAsync(derived);
        var operation = new GenerateToStringOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateToStringParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Person",
            CallSuper = true,
            IncludeInheritedMembers = true
        });

        Assert.True(result.Success);
        var toString = ExtractToStringMethod(NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath)));
        Assert.Contains("{base.ToString()}", toString);
        Assert.Contains("{Name}", toString);
        Assert.Contains("{Id}", toString);
        Assert.DoesNotContain("{Age}", toString);
    }

    [SkippableFact]
    public async Task GenerateToString_CallSuperTrue_IncludePropertiesFalse_StillCollectsFieldsOnly()
    {
        const string derived = """
            namespace TestApp;

            public class Widget : Entity
            {
                public string _id;

                public string Label { get; set; }
            }
            """;

        await using var workspace = await CreateDerivedOnEntityAsync(derived, "Widget.cs");
        var operation = new GenerateToStringOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateToStringParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget",
            CallSuper = true,
            IncludeProperties = false
        });

        Assert.True(result.Success);
        var toString = ExtractToStringMethod(NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath)));
        Assert.Contains("{base.ToString()}", toString);
        Assert.Contains("{_id}", toString);
        Assert.DoesNotContain("{Label}", toString);
        Assert.DoesNotContain("Label = ", toString);
        Assert.DoesNotContain("{Id}", toString);
    }

    [SkippableFact]
    public async Task GenerateToString_CallSuperTrue_NamedFields_StillCollectsOnlyNamed()
    {
        await using var workspace = await CreateDerivedOnEntityAsync(PersonOnEntitySource);
        var operation = new GenerateToStringOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateToStringParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Person",
            CallSuper = true,
            Fields = new[] { "Name" }
        });

        Assert.True(result.Success);
        var toString = ExtractToStringMethod(NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath)));
        Assert.Contains("{base.ToString()}", toString);
        Assert.Contains("{Name}", toString);
        Assert.DoesNotContain("{Age}", toString);
        Assert.DoesNotContain("Age = ", toString);
        Assert.DoesNotContain("{Id}", toString);
    }

    [SkippableFact]
    public async Task GenerateToString_CallSuperTrue_ReplaceExisting_ReplacesAndIncludesBaseCall()
    {
        const string source = """
            namespace TestApp;

            public class Person : Entity
            {
                public string Name { get; set; }

                public int Age { get; set; }

                public override string ToString() => "old";
            }
            """;

        await using var workspace = await CreateDerivedOnEntityAsync(source);
        var operation = new GenerateToStringOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateToStringParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Person",
            CallSuper = true,
            ReplaceExisting = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var toString = ExtractToStringMethod(updated);
        Assert.DoesNotContain("=> \"old\"", updated);
        Assert.Contains("{base.ToString()}", toString);
        Assert.Contains("{Name}", toString);
        Assert.Contains("{Age}", toString);
        Assert.Equal(1, CountOccurrences(updated, "public override string ToString()"));
    }

    [SkippableFact]
    public async Task GenerateToString_CallSuperTrue_Preview_DoesNotWriteFiles_AndDescribesBase()
    {
        await using var workspace = await CreateDerivedOnEntityAsync(PersonOnEntitySource);
        var operation = new GenerateToStringOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var result = await operation.ExecuteAsync(new GenerateToStringParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Person",
            CallSuper = true,
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.NotEmpty(result.PendingChanges);
        Assert.Contains("base ToString", result.PendingChanges[0].Description);
        var snippet = result.PendingChanges[0].AfterSnippet!;
        Assert.Contains("{base.ToString()}", snippet);
        Assert.Contains("{Name}", snippet);
        Assert.Contains("{Age}", snippet);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    #endregion

    #region AllFiles

    private const string EligibleFileA = """
        namespace TestApp;

        public class FileA
        {
            public string Name { get; set; }
        }

        public interface ISkip
        {
            string Title { get; }
        }
        """;

    private const string EligibleFileB = """
        namespace TestApp;

        public class FileB
        {
            public int Age { get; set; }
        }
        """;

    private const string IneligibleFileC = """
        namespace TestApp;

        public interface IWidget
        {
            string Name { get; }
        }

        public class Empty
        {
        }

        public class AlreadyHas
        {
            public string Name { get; set; }

            public override string ToString() => "existing";
        }
        """;

    private const string MixedEligibleAndSkipped = """
        namespace TestApp;

        public class Eligible
        {
            public string Name { get; set; }
        }

        public interface ISkip
        {
            string Title { get; }
        }

        public class NoMembers
        {
        }

        public class AlreadyHas
        {
            public int Age { get; set; }

            public override string ToString() => "existing";
        }

        public class Outer
        {
            public string Title { get; set; }

            public class Nested
            {
                public int Count { get; set; }
            }
        }
        """;

    [Fact]
    public void Validate_AllFilesFalse_WithoutSourceFile_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            GenerateToStringOperation.Validate(new GenerateToStringParams
            {
                AllFiles = false,
                TypeName = "Person"
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("sourceFile", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_AllFilesFalse_WithoutTypeName_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            GenerateToStringOperation.Validate(new GenerateToStringParams
            {
                AllFiles = false,
                SourceFile = AbsoluteTestPath()
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("typeName", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_AllFilesTrue_WithoutSourceFileOrTypeName_DoesNotThrow()
    {
        GenerateToStringOperation.Validate(new GenerateToStringParams
        {
            AllFiles = true
        });
    }

    [Fact]
    public void Validate_AllFilesTrue_WithTypeName_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            GenerateToStringOperation.Validate(new GenerateToStringParams
            {
                AllFiles = true,
                TypeName = "Person"
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("typeName", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_AllFilesTrue_WithFields_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            GenerateToStringOperation.Validate(new GenerateToStringParams
            {
                AllFiles = true,
                Fields = new[] { "Name" }
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("fields", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_AllFilesTrue_WithEmptyFields_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            GenerateToStringOperation.Validate(new GenerateToStringParams
            {
                AllFiles = true,
                Fields = Array.Empty<string>()
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("fields", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TypeWalkKey_IncludesProjectIdentity()
    {
        var projectA = ProjectId.CreateNewId();
        var projectB = ProjectId.CreateNewId();
        const string fqn = "global::TestApp.Widget";

        var keyA = GenerateToStringOperation.TypeWalkKey(projectA, fqn);
        var keyB = GenerateToStringOperation.TypeWalkKey(projectB, fqn);

        Assert.NotEqual(keyA, keyB);
        Assert.Equal(keyA, GenerateToStringOperation.TypeWalkKey(projectA, fqn));
        Assert.NotEqual(keyA, GenerateToStringOperation.TypeWalkKey(projectA, "global::TestApp.Other"));
    }

    [Fact]
    public void TypeWalkKey_FileLocalIdentity_DistinguishesSameFqn()
    {
        var project = ProjectId.CreateNewId();
        const string fqn = "global::TestApp.Worker";

        var ordinary = GenerateToStringOperation.TypeWalkKey(project, fqn);
        var fileA = GenerateToStringOperation.TypeWalkKey(project, fqn, "/tmp/FileA.cs");
        var fileB = GenerateToStringOperation.TypeWalkKey(project, fqn, "/tmp/FileB.cs");

        Assert.NotEqual(ordinary, fileA);
        Assert.NotEqual(ordinary, fileB);
        Assert.NotEqual(fileA, fileB);
        Assert.Equal(fileA, GenerateToStringOperation.TypeWalkKey(project, fqn, "/tmp/FileA.cs"));
        Assert.Equal(ordinary, GenerateToStringOperation.TypeWalkKey(project, fqn));
    }

    [Fact]
    public void Validate_AllFilesTrue_WithLine_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            GenerateToStringOperation.Validate(new GenerateToStringParams
            {
                AllFiles = true,
                Line = 8
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("line", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_AllFilesTrue_WithColumn_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            GenerateToStringOperation.Validate(new GenerateToStringParams
            {
                AllFiles = true,
                Column = 1
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("column", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_AllFilesTrue_UnknownFormat_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            GenerateToStringOperation.Validate(new GenerateToStringParams
            {
                AllFiles = true,
                Format = "json"
            }));

        Assert.Equal(ErrorCodes.InvalidToStringFormat, ex.ErrorCode);
        Assert.Contains("json", ex.Message);
    }

    [Fact]
    public void BuildAllFilesDescription_SingularAndPlural()
    {
        Assert.Equal("Generate ToString", GenerateToStringOperation.BuildAllFilesDescription(1));
        Assert.Equal("Generate ToString on 2 types", GenerateToStringOperation.BuildAllFilesDescription(2));
    }

    [SkippableFact]
    public async Task GenerateToString_AllFilesFalse_GeneratesOnlySpecifiedType()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("FileA.cs", EligibleFileA),
            ("FileB.cs", EligibleFileB),
            ("FileC.cs", IneligibleFileC));
        var operation = new GenerateToStringOperation(workspace.Context);
        var beforeB = await File.ReadAllTextAsync(workspace.SourcePaths["FileB.cs"]);
        var beforeC = await File.ReadAllTextAsync(workspace.SourcePaths["FileC.cs"]);

        var result = await operation.ExecuteAsync(new GenerateToStringParams
        {
            SourceFile = workspace.SourcePaths["FileA.cs"],
            AllFiles = false,
            TypeName = "FileA"
        });

        Assert.True(result.Success);
        Assert.False(result.Preview);
        var updatedA = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePaths["FileA.cs"]));
        Assert.Contains("public override string ToString()", updatedA, StringComparison.Ordinal);
        Assert.Contains("{Name}", updatedA, StringComparison.Ordinal);
        Assert.DoesNotContain("public override string ToString()", await File.ReadAllTextAsync(workspace.SourcePaths["FileB.cs"]), StringComparison.Ordinal);
        Assert.Equal(beforeB, await File.ReadAllTextAsync(workspace.SourcePaths["FileB.cs"]));
        Assert.Equal(beforeC, await File.ReadAllTextAsync(workspace.SourcePaths["FileC.cs"]));
        Assert.Single(result.Changes!.FilesModified);
        Assert.Contains(result.Changes.FilesModified, p => PathEquals(p, workspace.SourcePaths["FileA.cs"]));
    }

    [SkippableFact]
    public async Task GenerateToString_OmittedAllFiles_KeepsSingleSiteGenerate()
    {
        await using var workspace = await TempWorkspace.CreateAsync(EligibleFileA, "FileA.cs");
        var operation = new GenerateToStringOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateToStringParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "FileA"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var fileA = GetTypes(updated, "FileA").Single();
        var iSkip = GetTypes(updated, "ISkip").Single();
        Assert.True(TypeHasMethod(fileA, "ToString"));
        Assert.Contains("{Name}", ExtractOwnToString(fileA), StringComparison.Ordinal);
        Assert.False(TypeHasMethod(iSkip, "ToString"));
    }

    [SkippableFact]
    public async Task GenerateToString_AllFilesTrue_GeneratesEligibleTypesAcrossFiles()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("FileA.cs", EligibleFileA),
            ("FileB.cs", EligibleFileB),
            ("FileC.cs", IneligibleFileC));
        var operation = new GenerateToStringOperation(workspace.Context);
        var beforeC = await File.ReadAllTextAsync(workspace.SourcePaths["FileC.cs"]);

        var result = await operation.ExecuteAsync(new GenerateToStringParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        Assert.False(result.Preview);
        var updatedA = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePaths["FileA.cs"]));
        var updatedB = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePaths["FileB.cs"]));
        Assert.Contains("public override string ToString()", updatedA, StringComparison.Ordinal);
        Assert.Contains("{Name}", updatedA, StringComparison.Ordinal);
        Assert.Contains("public override string ToString()", updatedB, StringComparison.Ordinal);
        Assert.Contains("{Age}", updatedB, StringComparison.Ordinal);
        Assert.Equal(beforeC, await File.ReadAllTextAsync(workspace.SourcePaths["FileC.cs"]));
        Assert.Equal(2, result.Changes!.FilesModified.Count);
        Assert.Contains(result.Changes.FilesModified, p => PathEquals(p, workspace.SourcePaths["FileA.cs"]));
        Assert.Contains(result.Changes.FilesModified, p => PathEquals(p, workspace.SourcePaths["FileB.cs"]));
        Assert.DoesNotContain(result.Changes.FilesModified, p => PathEquals(p, workspace.SourcePaths["FileC.cs"]));
    }

    [SkippableFact]
    public async Task GenerateToString_AllFilesTrue_WithoutSourceFileOrTypeName_Succeeds()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("FileA.cs", EligibleFileA),
            ("FileB.cs", EligibleFileB));
        var operation = new GenerateToStringOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateToStringParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        Assert.Equal(2, result.Changes!.FilesModified.Count);
    }

    [SkippableFact]
    public async Task GenerateToString_AllFilesFalse_WithoutSourceFile_MissingRequiredParam()
    {
        await using var workspace = await TempWorkspace.CreateAsync(EligibleFileA, "FileA.cs");
        var operation = new GenerateToStringOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GenerateToStringParams
            {
                AllFiles = false,
                TypeName = "FileA"
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("sourceFile", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task GenerateToString_AllFilesFalse_WithoutTypeName_MissingRequiredParam()
    {
        await using var workspace = await TempWorkspace.CreateAsync(EligibleFileA, "FileA.cs");
        var operation = new GenerateToStringOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GenerateToStringParams
            {
                AllFiles = false,
                SourceFile = workspace.SourcePath
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("typeName", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task GenerateToString_AllFilesTrue_WithTypeName_Rejects()
    {
        await using var workspace = await TempWorkspace.CreateAsync(EligibleFileA, "FileA.cs");
        var operation = new GenerateToStringOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GenerateToStringParams
            {
                AllFiles = true,
                TypeName = "FileA"
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("typeName", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task GenerateToString_AllFilesTrue_WithFields_Rejects()
    {
        await using var workspace = await TempWorkspace.CreateAsync(EligibleFileA, "FileA.cs");
        var operation = new GenerateToStringOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GenerateToStringParams
            {
                AllFiles = true,
                Fields = new[] { "Name" }
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("fields", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task GenerateToString_AllFilesTrue_WithLine_Rejects()
    {
        await using var workspace = await TempWorkspace.CreateAsync(EligibleFileA, "FileA.cs");
        var operation = new GenerateToStringOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GenerateToStringParams
            {
                AllFiles = true,
                Line = 8
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("line", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task GenerateToString_AllFilesTrue_WithColumn_Rejects()
    {
        await using var workspace = await TempWorkspace.CreateAsync(EligibleFileA, "FileA.cs");
        var operation = new GenerateToStringOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GenerateToStringParams
            {
                AllFiles = true,
                Column = 1
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("column", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task GenerateToString_AllFilesTrue_InvalidFormat_Rejected()
    {
        await using var workspace = await TempWorkspace.CreateAsync(EligibleFileA, "FileA.cs");
        var operation = new GenerateToStringOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GenerateToStringParams
            {
                AllFiles = true,
                Format = "xml"
            }));

        Assert.Equal(ErrorCodes.InvalidToStringFormat, ex.ErrorCode);
        Assert.Contains("xml", ex.Message);
    }

    [SkippableFact]
    public async Task GenerateToString_PreviewAllFiles_AggregatesChangedFilesAndWritesNothing()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("FileA.cs", EligibleFileA),
            ("FileB.cs", EligibleFileB),
            ("FileC.cs", IneligibleFileC));
        var operation = new GenerateToStringOperation(workspace.Context);
        var beforeA = await File.ReadAllTextAsync(workspace.SourcePaths["FileA.cs"]);
        var beforeB = await File.ReadAllTextAsync(workspace.SourcePaths["FileB.cs"]);
        var beforeC = await File.ReadAllTextAsync(workspace.SourcePaths["FileC.cs"]);

        var result = await operation.ExecuteAsync(new GenerateToStringParams
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
            c.Description.Contains("ToString", StringComparison.OrdinalIgnoreCase) &&
            c.AfterSnippet != null &&
            (c.AfterSnippet.Contains("Name", StringComparison.Ordinal) ||
             c.AfterSnippet.Contains("Age", StringComparison.Ordinal)));
        Assert.Equal(beforeA, await File.ReadAllTextAsync(workspace.SourcePaths["FileA.cs"]));
        Assert.Equal(beforeB, await File.ReadAllTextAsync(workspace.SourcePaths["FileB.cs"]));
        Assert.Equal(beforeC, await File.ReadAllTextAsync(workspace.SourcePaths["FileC.cs"]));
    }

    [SkippableFact]
    public async Task GenerateToString_AllFilesTrue_EveryFileIneligible_SucceedsWithEmptyChanges()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("FileC.cs", IneligibleFileC),
            ("FileC2.cs", IneligibleFileC
                .Replace("IWidget", "IWidget2", StringComparison.Ordinal)
                .Replace("Empty", "Empty2", StringComparison.Ordinal)
                .Replace("AlreadyHas", "AlreadyHas2", StringComparison.Ordinal)));
        var operation = new GenerateToStringOperation(workspace.Context);
        var beforeA = await File.ReadAllTextAsync(workspace.SourcePaths["FileC.cs"]);
        var beforeB = await File.ReadAllTextAsync(workspace.SourcePaths["FileC2.cs"]);

        var result = await operation.ExecuteAsync(new GenerateToStringParams
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
    public async Task GenerateToString_AllFilesTrue_SkipsInterfaceNoMembersAndAlreadyHasOverride()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("Mixed.cs", MixedEligibleAndSkipped));
        var operation = new GenerateToStringOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateToStringParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePaths["Mixed.cs"]));
        var eligible = GetTypes(updated, "Eligible").Single();
        var alreadyHas = GetTypes(updated, "AlreadyHas").Single();
        var noMembers = GetTypes(updated, "NoMembers").Single();
        var outer = GetTypes(updated, "Outer").Single();
        var nested = GetTypes(updated, "Nested").Single();
        Assert.True(TypeHasMethod(eligible, "ToString"));
        Assert.True(TypeHasMethod(outer, "ToString"));
        Assert.True(TypeHasMethod(nested, "ToString"));
        Assert.Equal(1, CountOccurrences(NormalizeNewlines(alreadyHas.ToFullString()), "public override string ToString()"));
        Assert.False(TypeHasMethod(noMembers, "ToString"));
        Assert.DoesNotContain("public override string ToString()", updated[updated.IndexOf("public interface ISkip", StringComparison.Ordinal)..updated.IndexOf("public class NoMembers", StringComparison.Ordinal)], StringComparison.Ordinal);
        Assert.Single(result.Changes!.FilesModified);
    }

    [SkippableFact]
    public async Task GenerateToString_AllFilesTrue_OptionalSourceFile_LimitsWalk()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("FileA.cs", EligibleFileA),
            ("FileB.cs", EligibleFileB));
        var operation = new GenerateToStringOperation(workspace.Context);
        var beforeB = await File.ReadAllTextAsync(workspace.SourcePaths["FileB.cs"]);

        var result = await operation.ExecuteAsync(new GenerateToStringParams
        {
            AllFiles = true,
            SourceFile = workspace.SourcePaths["FileA.cs"]
        });

        Assert.True(result.Success);
        var updatedA = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePaths["FileA.cs"]));
        Assert.Contains("public override string ToString()", updatedA, StringComparison.Ordinal);
        Assert.Equal(beforeB, await File.ReadAllTextAsync(workspace.SourcePaths["FileB.cs"]));
        Assert.Single(result.Changes!.FilesModified);
        Assert.Contains(result.Changes.FilesModified, p => PathEquals(p, workspace.SourcePaths["FileA.cs"]));
    }

    [SkippableFact]
    public async Task GenerateToString_AllFilesTrue_OptionalSourceFile_MatchesIgnoreCase()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("FileA.cs", EligibleFileA),
            ("FileB.cs", EligibleFileB));
        var operation = new GenerateToStringOperation(workspace.Context);
        var beforeB = await File.ReadAllTextAsync(workspace.SourcePaths["FileB.cs"]);
        var flipped = FlipPathCasing(workspace.SourcePaths["FileA.cs"]);

        var result = await operation.ExecuteAsync(new GenerateToStringParams
        {
            AllFiles = true,
            SourceFile = flipped
        });

        Assert.True(result.Success);
        var updatedA = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePaths["FileA.cs"]));
        Assert.Contains("public override string ToString()", updatedA, StringComparison.Ordinal);
        Assert.Equal(beforeB, await File.ReadAllTextAsync(workspace.SourcePaths["FileB.cs"]));
        Assert.Single(result.Changes!.FilesModified);
        Assert.Contains(result.Changes.FilesModified, p => PathEquals(p, workspace.SourcePaths["FileA.cs"]));
    }

    [SkippableFact]
    public async Task GenerateToString_AllFilesTrue_ReplaceExisting_ReplacesMatchingToString()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("Widget.cs", IneligibleFileC));
        var operation = new GenerateToStringOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateToStringParams
        {
            AllFiles = true,
            ReplaceExisting = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePaths["Widget.cs"]));
        Assert.Equal(1, CountOccurrences(updated, "public override string ToString()"));
        Assert.Contains("{Name}", updated, StringComparison.Ordinal);
        Assert.DoesNotContain("=> \"existing\"", updated, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task GenerateToString_AllFilesTrue_SameNamedFileLocalTypes_BothGetStubs()
    {
        const string fileA = """
            namespace TestApp;

            file class Worker
            {
                public string Name { get; set; }
            }
            """;

        const string fileB = """
            namespace TestApp;

            file class Worker
            {
                public int Age { get; set; }
            }
            """;

        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("FileA.cs", fileA),
            ("FileB.cs", fileB));
        var operation = new GenerateToStringOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateToStringParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        var updatedA = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePaths["FileA.cs"]));
        var updatedB = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePaths["FileB.cs"]));
        Assert.Contains("public override string ToString()", updatedA, StringComparison.Ordinal);
        Assert.Contains("{Name}", updatedA, StringComparison.Ordinal);
        Assert.Contains("public override string ToString()", updatedB, StringComparison.Ordinal);
        Assert.Contains("{Age}", updatedB, StringComparison.Ordinal);
        Assert.Equal(2, result.Changes!.FilesModified.Count);
        Assert.Contains(result.Changes.FilesModified, p => PathEquals(p, workspace.SourcePaths["FileA.cs"]));
        Assert.Contains(result.Changes.FilesModified, p => PathEquals(p, workspace.SourcePaths["FileB.cs"]));
    }

    [SkippableFact]
    public async Task GenerateToString_AllFilesTrue_GenuinePartial_ImplementedOnce()
    {
        const string partA = """
            namespace TestApp;

            public partial class Widget
            {
                public string Name { get; set; }
            }
            """;

        const string partB = """
            namespace TestApp;

            public partial class Widget
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("Widget.PartA.cs", partA),
            ("Widget.PartB.cs", partB));
        var operation = new GenerateToStringOperation(workspace.Context);
        var beforeB = await File.ReadAllTextAsync(workspace.SourcePaths["Widget.PartB.cs"]);

        var result = await operation.ExecuteAsync(new GenerateToStringParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        var updatedA = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePaths["Widget.PartA.cs"]));
        Assert.Contains("public override string ToString()", updatedA, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(updatedA, "public override string ToString()"));
        Assert.Equal(beforeB, await File.ReadAllTextAsync(workspace.SourcePaths["Widget.PartB.cs"]));
        Assert.Single(result.Changes!.FilesModified);
        Assert.Contains(result.Changes.FilesModified, p => PathEquals(p, workspace.SourcePaths["Widget.PartA.cs"]));
    }

    [Fact]
    public void CollectTypeDeclarations_IncludesNestedAndInterface()
    {
        var root = CSharpSyntaxTree.ParseText(NormalizeNewlines(MixedEligibleAndSkipped)).GetRoot();
        var types = GenerateToStringOperation.CollectTypeDeclarations(root);
        var names = types.Select(t => t.Identifier.Text).ToList();
        Assert.Contains("Eligible", names);
        Assert.Contains("ISkip", names);
        Assert.Contains("NoMembers", names);
        Assert.Contains("AlreadyHas", names);
        Assert.Contains("Outer", names);
        Assert.Contains("Nested", names);
        Assert.True(names.IndexOf("Outer") < names.IndexOf("Nested"));
    }

    #endregion

    #region Helpers

    private static void AssertInterpolatedToString(string text)
    {
        Assert.Contains("public override string ToString()", text);
        Assert.DoesNotContain("StringBuilder", text);
        Assert.Contains("{Name}", text);
        Assert.Contains("{Age}", text);

        var toString = ExtractToStringMethod(text);
        Assert.Contains("$\"Person {{", toString);
        Assert.DoesNotContain("new System.Text.StringBuilder", toString);
    }

    private static string ExtractToStringMethod(string text)
    {
        var start = text.IndexOf("public override string ToString()", StringComparison.Ordinal);
        Assert.True(start >= 0, $"Generated source did not contain ToString:\n{text}");
        return text[start..];
    }

    private static string ExtractOwnToString(TypeDeclarationSyntax type)
    {
        var method = type.Members.OfType<MethodDeclarationSyntax>()
            .Single(m => m.Identifier.Text == "ToString");
        return NormalizeNewlines(method.ToFullString());
    }

    private static void AssertCompiles(string source)
    {
        var compilation = CSharpCompilation.Create(
                "ToStringCompileTest",
                new[] { CSharpSyntaxTree.ParseText(source) },
                new[]
                {
                    MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                    MetadataReference.CreateFromFile(typeof(System.Text.StringBuilder).Assembly.Location)
                },
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Select(d => d.ToString())
            .ToList();
        Assert.True(errors.Count == 0, "Generated ToString did not compile:\n" + string.Join("\n", errors) + "\n\n" + source);
    }

    private static void AssertStringBuilderToString(string text, params string[] members)
    {
        Assert.Contains("public override string ToString()", text);
        Assert.Contains("global::System.Text.StringBuilder", text);
        Assert.Contains("sb.ToString()", text);
        Assert.Contains("Person { ", text);
        foreach (var member in members)
        {
            Assert.Contains($"{member} = ", text);
            Assert.Contains($"Append(this.{member})", text);
        }

        Assert.DoesNotContain("$\"Person", text);
    }

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

    private static string AbsoluteTestPath() =>
        Path.Combine(Path.GetTempPath(), "RoslynMcpGenerateToStringMissing.cs");

    private static IReadOnlyList<TypeDeclarationSyntax> GetTypes(string source, string name) =>
        CSharpSyntaxTree.ParseText(source).GetRoot()
            .DescendantNodes()
            .OfType<TypeDeclarationSyntax>()
            .Where(t => t.Identifier.Text == name)
            .ToList();

    private static bool TypeHasMethod(TypeDeclarationSyntax type, string methodName) =>
        type.Members.OfType<MethodDeclarationSyntax>()
            .Any(m => m.Identifier.Text == methodName);

    // Single-line snippets only — IndexOf of an LF-only snippet missed
    // CRLF checkouts (FindMethod_ColumnOnContinuationLine on #200 / #214).
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

    private static string NormalizeNewlines(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\r", "\n", StringComparison.Ordinal);

    private sealed class TempWorkspace : IAsyncDisposable
    {
        public required string DirectoryPath { get; init; }
        public required string ProjectPath { get; init; }
        public required string SourcePath { get; init; }
        public required IReadOnlyDictionary<string, string> SourcePaths { get; init; }
        public required WorkspaceContext Context { get; init; }

        public string PathFor(string fileName) => Path.Combine(DirectoryPath, fileName);

        public static Task<TempWorkspace> CreateWithFilesAsync(params (string FileName, string Source)[] files) =>
            CreateAsync(files);

        public static Task<TempWorkspace> CreateAsync(string source, string fileName = "Person.cs") =>
            CreateAsync((fileName, source));

        public static async Task<TempWorkspace> CreateAsync(params (string FileName, string Source)[] files)
        {
            Skip.IfNot(ModuleInitializer.MsBuildAvailable, ModuleInitializer.MsBuildError ?? "MSBuild not available");

            var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpGenerateToString_" + Guid.NewGuid().ToString("N"));
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
            var sourcePaths = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (fileName, source) in files)
            {
                var path = Path.Combine(directory, fileName);
                await File.WriteAllTextAsync(path, source);
                sourcePaths[fileName] = path;
                sourcePath ??= path;
            }

            sourcePath ??= Path.Combine(directory, "Person.cs");

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

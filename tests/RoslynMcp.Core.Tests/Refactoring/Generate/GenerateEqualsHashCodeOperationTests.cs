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
/// Operation-level tests for <see cref="GenerateEqualsHashCodeOperation"/>, including optional <c>line</c> / <c>column</c>, <c>implementIEquatable</c>, <c>generateOperators</c>, <c>replaceExisting</c>, <c>useHashCodeCombine</c>, <c>includeProperties</c>, <c>callSuper</c>, and <c>includeInheritedMembers</c>.
/// </summary>
public class GenerateEqualsHashCodeOperationTests
{
    private const string PersonSource = """
        namespace TestApp;

        public class Person
        {
            public string Name { get; set; }

            public int Age { get; set; }
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

    #region P0 optional line disambiguation

    [Fact]
    public void Line_DefaultsToNull()
    {
        var @params = new GenerateEqualsHashCodeParams
        {
            SourceFile = AbsoluteTestPath(),
            TypeName = "Person"
        };

        Assert.Null(@params.Line);
    }

    [Fact]
    public void Validate_InvalidLine_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            GenerateEqualsHashCodeOperation.Validate(new GenerateEqualsHashCodeParams
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
            GenerateEqualsHashCodeOperation.Validate(new GenerateEqualsHashCodeParams
            {
                SourceFile = AbsoluteTestPath(),
                TypeName = "Person",
                Line = -1
            }));

        Assert.Equal(ErrorCodes.InvalidLineNumber, ex.ErrorCode);
        Assert.Equal("1006", ex.ErrorCode);
    }

    private const string NestedSameNamePersonSource = """
        namespace TestApp;

        public class Person // outer-person
        {
            public string Name { get; set; }

            public class Person // nested-person
            {
                public int Age { get; set; }
            }
        }
        """;

    [SkippableFact]
    public async Task GenerateEquals_OmittedLine_KeepsTypeNameFirstOrDefaultPick()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NestedSameNamePersonSource, "Person.cs");
        var operation = new GenerateEqualsHashCodeOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateEqualsHashCodeParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Person"
        });

        Assert.True(result.Success);
        var types = GetTypes(await File.ReadAllTextAsync(workspace.SourcePath), "Person");
        Assert.Equal(2, types.Count);
        Assert.True(TypeHasMethod(types[0], "Equals"));
        Assert.True(TypeHasMethod(types[0], "GetHashCode"));
        Assert.False(TypeHasMethod(types[1], "Equals"));
        Assert.False(TypeHasMethod(types[1], "GetHashCode"));
        Assert.Contains("Name", types[0].ToFullString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Age", ExtractMethod(NormalizeNewlines(types[0].ToFullString()), "public override bool Equals(object?"));
    }

    [SkippableFact]
    public async Task GenerateEquals_LineOnNestedIdentifier_PicksNestedType()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NestedSameNamePersonSource, "Person.cs");
        var operation = new GenerateEqualsHashCodeOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateEqualsHashCodeParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Person",
            Line = FindLine(NestedSameNamePersonSource, "nested-person")
        });

        Assert.True(result.Success);
        var types = GetTypes(await File.ReadAllTextAsync(workspace.SourcePath), "Person");
        Assert.Equal(2, types.Count);
        Assert.False(TypeHasMethod(types[0], "Equals"));
        Assert.True(TypeHasMethod(types[1], "Equals"));
        Assert.True(TypeHasMethod(types[1], "GetHashCode"));
        var nestedEquals = ExtractMethod(NormalizeNewlines(types[1].ToFullString()), "public override bool Equals(object?");
        Assert.Contains("Age", nestedEquals);
        Assert.DoesNotContain("Name", nestedEquals);
    }

    [SkippableFact]
    public async Task GenerateEquals_LineOnOuterIdentifier_PicksOuterType()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NestedSameNamePersonSource, "Person.cs");
        var operation = new GenerateEqualsHashCodeOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateEqualsHashCodeParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Person",
            Line = FindLine(NestedSameNamePersonSource, "outer-person")
        });

        Assert.True(result.Success);
        var types = GetTypes(await File.ReadAllTextAsync(workspace.SourcePath), "Person");
        Assert.Equal(2, types.Count);
        Assert.True(TypeHasMethod(types[0], "Equals"));
        Assert.False(TypeHasMethod(types[1], "Equals"));
        var outerEquals = ExtractMethod(NormalizeNewlines(types[0].ToFullString()), "public override bool Equals(object?");
        Assert.Contains("Name", outerEquals);
        Assert.DoesNotContain("Age", outerEquals);
    }

    [SkippableFact]
    public async Task GenerateEquals_Line_Preview_WritesNothing_AndDescribesGeneration()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NestedSameNamePersonSource, "Person.cs");
        var operation = new GenerateEqualsHashCodeOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var result = await operation.ExecuteAsync(new GenerateEqualsHashCodeParams
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
        Assert.Contains("Generate Equals and GetHashCode", result.PendingChanges[0].Description);
        Assert.Contains("Person", result.PendingChanges[0].Description);
        Assert.Contains("public override bool Equals(object?", result.PendingChanges[0].AfterSnippet);
        Assert.Contains("public override int GetHashCode()", result.PendingChanges[0].AfterSnippet);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [Fact]
    public void FindTypeDeclaration_OmittedLine_FirstOrDefaultPicksOuter()
    {
        var root = CSharpSyntaxTree.ParseText(NestedSameNamePersonSource).GetRoot();
        var found = GenerateEqualsHashCodeOperation.FindTypeDeclaration(root, "Person", line: null);

        Assert.NotNull(found);
        Assert.False(found.Parent is TypeDeclarationSyntax);
    }

    [Fact]
    public void FindTypeDeclaration_LineOnNestedIdentifier_PicksNested()
    {
        var root = CSharpSyntaxTree.ParseText(NestedSameNamePersonSource).GetRoot();
        var found = GenerateEqualsHashCodeOperation.FindTypeDeclaration(
            root, "Person", FindLine(NestedSameNamePersonSource, "nested-person"));

        Assert.NotNull(found);
        Assert.True(found.Parent is TypeDeclarationSyntax outer && outer.Identifier.Text == "Person");
    }

    [Fact]
    public void FindTypeDeclaration_LineOnOuterIdentifier_PicksOuter()
    {
        var root = CSharpSyntaxTree.ParseText(NestedSameNamePersonSource).GetRoot();
        var found = GenerateEqualsHashCodeOperation.FindTypeDeclaration(
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

        var found = GenerateEqualsHashCodeOperation.FindTypeDeclaration(root, "Person", identifierLine);

        Assert.NotNull(found);
        Assert.False(found.Parent is TypeDeclarationSyntax);
    }

    [Fact]
    public void FindTypeDeclaration_LineMiss_KeepsFirstMatch()
    {
        var root = CSharpSyntaxTree.ParseText(NestedSameNamePersonSource).GetRoot();
        var found = GenerateEqualsHashCodeOperation.FindTypeDeclaration(root, "Person", line: 1);

        Assert.NotNull(found);
        Assert.False(found.Parent is TypeDeclarationSyntax);
    }

    [Fact]
    public void SpanCoversLine_TreatsEndAsExclusive()
    {
        var span = new FileLinePositionSpan(
            "t.cs",
            new LinePosition(0, 0),
            new LinePosition(2, 0));

        Assert.True(GenerateEqualsHashCodeOperation.SpanCoversLine(span, 1));
        Assert.True(GenerateEqualsHashCodeOperation.SpanCoversLine(span, 2));
        Assert.False(GenerateEqualsHashCodeOperation.SpanCoversLine(span, 3));
        Assert.False(GenerateEqualsHashCodeOperation.SpanCoversLine(span, 0));
    }

    [SkippableFact]
    public async Task GenerateEquals_LineOnLaterSameFilePartial_ReplaceExisting_InsertsOnSelectedPartial()
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

                    public override bool Equals(object? obj) => false; /* old-body */

                    public override int GetHashCode() => 42;
                }

                public partial class Person // later-partial
                {
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Person.cs");
        var operation = new GenerateEqualsHashCodeOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateEqualsHashCodeParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Person",
            Line = FindLine(source, "later-partial"),
            ReplaceExisting = true
        });

        Assert.True(result.Success);
        var types = GetTypes(await File.ReadAllTextAsync(workspace.SourcePath), "Person");
        Assert.Equal(3, types.Count);
        Assert.False(TypeHasMethod(types[0], "Equals"));
        Assert.False(TypeHasMethod(types[1], "Equals"));
        Assert.True(TypeHasMethod(types[2], "Equals"));
        Assert.True(TypeHasMethod(types[2], "GetHashCode"));
        Assert.DoesNotContain("old-body", types[2].ToFullString(), StringComparison.Ordinal);
        Assert.DoesNotContain("=> false", types[2].ToFullString(), StringComparison.Ordinal);
        Assert.DoesNotContain("=> 42", types[2].ToFullString(), StringComparison.Ordinal);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Equal(1, CountOccurrences(updated, "public override bool Equals(object?"));
        Assert.Equal(1, CountOccurrences(updated, "public override int GetHashCode()"));
        Assert.DoesNotContain("old-body", updated, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task GenerateEquals_SequentialReplaceExisting_ReusedWorkspace_InsertsOnSecondSelectedType()
    {
        const string source = """
            namespace TestApp;

            public class Alpha
            {
                public string Name { get; set; }

                public override bool Equals(object? obj) => false; /* old-alpha */

                public override int GetHashCode() => 1;
            }

            public class Beta
            {
                public string Title { get; set; }

                public override bool Equals(object? obj) => false; /* old-beta */

                public override int GetHashCode() => 2;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Types.cs");
        var operation = new GenerateEqualsHashCodeOperation(workspace.Context);

        var first = await operation.ExecuteAsync(new GenerateEqualsHashCodeParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Alpha",
            ReplaceExisting = true
        });
        Assert.True(first.Success);

        var second = await operation.ExecuteAsync(new GenerateEqualsHashCodeParams
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
        Assert.True(TypeHasMethod(alpha, "Equals"));
        Assert.True(TypeHasMethod(alpha, "GetHashCode"));
        Assert.True(TypeHasMethod(beta, "Equals"));
        Assert.True(TypeHasMethod(beta, "GetHashCode"));
        var alphaEquals = ExtractMethod(NormalizeNewlines(alpha.ToFullString()), "public override bool Equals(object?");
        var betaEquals = ExtractMethod(NormalizeNewlines(beta.ToFullString()), "public override bool Equals(object?");
        Assert.Contains("Name", alphaEquals);
        Assert.DoesNotContain("Title", alphaEquals);
        Assert.Contains("Title", betaEquals);
        Assert.DoesNotContain("Name", betaEquals);
        Assert.DoesNotContain("old-alpha", updated, StringComparison.Ordinal);
        Assert.DoesNotContain("old-beta", updated, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(NormalizeNewlines(alpha.ToFullString()), "public override bool Equals(object?"));
        Assert.Equal(1, CountOccurrences(NormalizeNewlines(beta.ToFullString()), "public override bool Equals(object?"));
    }

    #endregion

    #region P0 optional column disambiguation

    private const string SameLineNestedPersonSource = """
        namespace TestApp;

        public class Person { public string Name { get; set; } public class Person { public int Age { get; set; } } }
        """;

    [Fact]
    public void Column_DefaultsToNull()
    {
        var @params = new GenerateEqualsHashCodeParams
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
            GenerateEqualsHashCodeOperation.Validate(new GenerateEqualsHashCodeParams
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
            GenerateEqualsHashCodeOperation.Validate(new GenerateEqualsHashCodeParams
            {
                SourceFile = AbsoluteTestPath(),
                TypeName = "Person",
                Column = -1
            }));

        Assert.Equal(ErrorCodes.InvalidColumnNumber, ex.ErrorCode);
        Assert.Equal("1007", ex.ErrorCode);
    }

    [SkippableFact]
    public async Task GenerateEquals_OmittedColumn_KeepsTypeNameFirstOrDefaultPick()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NestedSameNamePersonSource, "Person.cs");
        var operation = new GenerateEqualsHashCodeOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateEqualsHashCodeParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Person"
        });

        Assert.True(result.Success);
        var types = GetTypes(await File.ReadAllTextAsync(workspace.SourcePath), "Person");
        Assert.Equal(2, types.Count);
        Assert.True(TypeHasMethod(types[0], "Equals"));
        Assert.True(TypeHasMethod(types[0], "GetHashCode"));
        Assert.False(TypeHasMethod(types[1], "Equals"));
        Assert.False(TypeHasMethod(types[1], "GetHashCode"));
        Assert.Contains("Name", types[0].ToFullString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Age", ExtractMethod(NormalizeNewlines(types[0].ToFullString()), "public override bool Equals(object?"));
    }

    [SkippableFact]
    public async Task GenerateEquals_OmittedColumn_LineOnNestedIdentifier_PicksNestedType()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NestedSameNamePersonSource, "Person.cs");
        var operation = new GenerateEqualsHashCodeOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateEqualsHashCodeParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Person",
            Line = FindLine(NestedSameNamePersonSource, "nested-person")
        });

        Assert.True(result.Success);
        var types = GetTypes(await File.ReadAllTextAsync(workspace.SourcePath), "Person");
        Assert.Equal(2, types.Count);
        Assert.False(TypeHasMethod(types[0], "Equals"));
        Assert.True(TypeHasMethod(types[1], "Equals"));
        Assert.True(TypeHasMethod(types[1], "GetHashCode"));
        var nestedEquals = ExtractMethod(NormalizeNewlines(types[1].ToFullString()), "public override bool Equals(object?");
        Assert.Contains("Age", nestedEquals);
        Assert.DoesNotContain("Name", nestedEquals);
    }

    [SkippableFact]
    public async Task GenerateEquals_ColumnOnNestedIdentifier_PicksNestedType()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SameLineNestedPersonSource, "Person.cs");
        var operation = new GenerateEqualsHashCodeOperation(workspace.Context);
        var line = FindLine(SameLineNestedPersonSource, "public class Person { public string Name");
        var column = ColumnOf(SameLineNestedPersonSource, "Person { public int Age");

        var result = await operation.ExecuteAsync(new GenerateEqualsHashCodeParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Person",
            Line = line,
            Column = column
        });

        Assert.True(result.Success);
        var types = GetTypes(await File.ReadAllTextAsync(workspace.SourcePath), "Person");
        Assert.Equal(2, types.Count);
        Assert.False(TypeHasMethod(types[0], "Equals"));
        Assert.True(TypeHasMethod(types[1], "Equals"));
        Assert.True(TypeHasMethod(types[1], "GetHashCode"));
        var nestedEquals = ExtractMethod(NormalizeNewlines(types[1].ToFullString()), "public override bool Equals(object?");
        Assert.Contains("Age", nestedEquals);
        Assert.DoesNotContain("Name", nestedEquals);
    }

    [SkippableFact]
    public async Task GenerateEquals_ColumnOnOuterIdentifier_PicksOuterType()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SameLineNestedPersonSource, "Person.cs");
        var operation = new GenerateEqualsHashCodeOperation(workspace.Context);
        var line = FindLine(SameLineNestedPersonSource, "public class Person { public string Name");
        var column = ColumnOf(SameLineNestedPersonSource, "Person { public string Name");

        var result = await operation.ExecuteAsync(new GenerateEqualsHashCodeParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Person",
            Line = line,
            Column = column
        });

        Assert.True(result.Success);
        var types = GetTypes(await File.ReadAllTextAsync(workspace.SourcePath), "Person");
        Assert.Equal(2, types.Count);
        Assert.True(TypeHasMethod(types[0], "Equals"));
        Assert.False(TypeHasMethod(types[1], "Equals"));
        var outerEquals = ExtractMethod(NormalizeNewlines(types[0].ToFullString()), "public override bool Equals(object?");
        Assert.Contains("Name", outerEquals);
        Assert.DoesNotContain("Age", outerEquals);
    }

    [Fact]
    public void FindTypeDeclaration_OmittedColumn_FirstOrDefaultPicksOuter()
    {
        var root = CSharpSyntaxTree.ParseText(NestedSameNamePersonSource).GetRoot();
        var found = GenerateEqualsHashCodeOperation.FindTypeDeclaration(root, "Person", line: null, column: null);

        Assert.NotNull(found);
        Assert.False(found.Parent is TypeDeclarationSyntax);
    }

    [Fact]
    public void FindTypeDeclaration_ColumnOnNestedIdentifier_PicksNested()
    {
        var root = CSharpSyntaxTree.ParseText(SameLineNestedPersonSource).GetRoot();
        var line = FindLine(SameLineNestedPersonSource, "public class Person { public string Name");
        var found = GenerateEqualsHashCodeOperation.FindTypeDeclaration(
            root, "Person", line, ColumnOf(SameLineNestedPersonSource, "Person { public int Age"));

        Assert.NotNull(found);
        Assert.True(found.Parent is TypeDeclarationSyntax outer && outer.Identifier.Text == "Person");
    }

    [Fact]
    public void FindTypeDeclaration_ColumnOnOuterIdentifier_PicksOuter()
    {
        var root = CSharpSyntaxTree.ParseText(SameLineNestedPersonSource).GetRoot();
        var line = FindLine(SameLineNestedPersonSource, "public class Person { public string Name");
        var found = GenerateEqualsHashCodeOperation.FindTypeDeclaration(
            root, "Person", line, ColumnOf(SameLineNestedPersonSource, "Person { public string Name"));

        Assert.NotNull(found);
        Assert.False(found.Parent is TypeDeclarationSyntax);
    }

    [Fact]
    public void FindTypeDeclaration_ColumnWithoutLine_KeepsFirstMatch()
    {
        var root = CSharpSyntaxTree.ParseText(SameLineNestedPersonSource).GetRoot();
        var nestedColumn = ColumnOf(SameLineNestedPersonSource, "Person { public int Age");
        var found = GenerateEqualsHashCodeOperation.FindTypeDeclaration(
            root, "Person", line: null, nestedColumn);

        Assert.NotNull(found);
        Assert.False(found.Parent is TypeDeclarationSyntax);
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

        var found = GenerateEqualsHashCodeOperation.FindTypeDeclaration(
            root, "Person", identifierLine, ColumnOf(source, "Person // split-person"));

        Assert.NotNull(found);
        Assert.False(found.Parent is TypeDeclarationSyntax);
    }

    [SkippableFact]
    public async Task GenerateEquals_ColumnOnContinuationLine_PicksType()
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
        var operation = new GenerateEqualsHashCodeOperation(workspace.Context);
        var startLine = FindLine(source, "public class\n    Person");
        var identifierLine = FindLine(source, "split-person");
        Assert.NotEqual(startLine, identifierLine);

        var result = await operation.ExecuteAsync(new GenerateEqualsHashCodeParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Person",
            Line = identifierLine,
            Column = ColumnOf(source, "Person // split-person")
        });

        Assert.True(result.Success);
        var types = GetTypes(await File.ReadAllTextAsync(workspace.SourcePath), "Person");
        Assert.Single(types);
        Assert.True(TypeHasMethod(types[0], "Equals"));
        Assert.True(TypeHasMethod(types[0], "GetHashCode"));
    }

    [Fact]
    public void FindTypeDeclaration_ColumnAndLineMiss_DoesNotFallBackToFirst()
    {
        var root = CSharpSyntaxTree.ParseText(NestedSameNamePersonSource).GetRoot();
        var found = GenerateEqualsHashCodeOperation.FindTypeDeclaration(root, "Person", line: 1, column: 1);

        Assert.Null(found);
    }

    [SkippableFact]
    public async Task GenerateEquals_ColumnAndLineMiss_ThrowsTypeNotFound()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NestedSameNamePersonSource, "Person.cs");
        var before = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new GenerateEqualsHashCodeOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GenerateEqualsHashCodeParams
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
    public async Task GenerateEquals_Column_Preview_WritesNothing_AndDescribesGeneration()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SameLineNestedPersonSource, "Person.cs");
        var operation = new GenerateEqualsHashCodeOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);
        var line = FindLine(SameLineNestedPersonSource, "public class Person { public string Name");

        var result = await operation.ExecuteAsync(new GenerateEqualsHashCodeParams
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
        Assert.Contains("Generate Equals and GetHashCode", result.PendingChanges[0].Description);
        Assert.Contains("Person", result.PendingChanges[0].Description);
        Assert.Contains("public override bool Equals(object?", result.PendingChanges[0].AfterSnippet);
        Assert.Contains("public override int GetHashCode()", result.PendingChanges[0].AfterSnippet);
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

        Assert.True(GenerateEqualsHashCodeOperation.SpanCoversColumn(span, line, startCol));
        Assert.True(GenerateEqualsHashCodeOperation.SpanCoversColumn(span, line, endCol - 1));
        Assert.False(GenerateEqualsHashCodeOperation.SpanCoversColumn(span, line, endCol));
        Assert.False(GenerateEqualsHashCodeOperation.SpanCoversColumn(span, line, startCol - 1));
    }

    [SkippableFact]
    public async Task GenerateEquals_SequentialColumn_ReusedWorkspace_InsertsOnSecondSelectedType()
    {
        const string source = """
            namespace TestApp;

            public class Person { public string Name { get; set; } public override bool Equals(object? obj) => false; /* old-outer */ public override int GetHashCode() => 1; public class Person { public int Age { get; set; } public override bool Equals(object? obj) => false; /* old-nested */ public override int GetHashCode() => 2; } }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Person.cs");
        var operation = new GenerateEqualsHashCodeOperation(workspace.Context);
        var line = FindLine(source, "public class Person { public string Name");

        var first = await operation.ExecuteAsync(new GenerateEqualsHashCodeParams
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
        var second = await operation.ExecuteAsync(new GenerateEqualsHashCodeParams
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
        Assert.True(TypeHasMethod(types[0], "Equals"));
        Assert.True(TypeHasMethod(types[0], "GetHashCode"));
        Assert.True(TypeHasMethod(types[1], "Equals"));
        Assert.True(TypeHasMethod(types[1], "GetHashCode"));
        // Own members only — types[0].ToFullString() also contains the nested Equals.
        var outerEquals = OwnEquals(types[0]);
        var nestedEquals = OwnEquals(types[1]);
        Assert.Contains("Name", outerEquals);
        Assert.DoesNotContain("Age", outerEquals);
        Assert.Contains("Age", nestedEquals);
        Assert.DoesNotContain("Name", nestedEquals);
        Assert.DoesNotContain("old-outer", types[0].ToFullString(), StringComparison.Ordinal);
        Assert.DoesNotContain("old-nested", types[1].ToFullString(), StringComparison.Ordinal);
        Assert.Equal(1, types[0].Members.OfType<MethodDeclarationSyntax>().Count(m => m.Identifier.Text == "Equals"));
        Assert.Equal(1, types[1].Members.OfType<MethodDeclarationSyntax>().Count(m => m.Identifier.Text == "Equals"));
    }

    #endregion

    #region Default / false — no IEquatable

    [SkippableFact]
    public async Task GenerateEquals_ImplementIEquatableOmitted_DoesNotAddIEquatable()
    {
        await using var workspace = await TempWorkspace.CreateAsync(PersonSource);
        var operation = new GenerateEqualsHashCodeOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateEqualsHashCodeParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Person"
        });

        Assert.True(result.Success);
        AssertDefaultEqualsShape(NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath)));
    }

    [SkippableFact]
    public async Task GenerateEquals_ImplementIEquatableFalse_DoesNotAddIEquatable()
    {
        await using var workspace = await TempWorkspace.CreateAsync(PersonSource);
        var operation = new GenerateEqualsHashCodeOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateEqualsHashCodeParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Person",
            ImplementIEquatable = false
        });

        Assert.True(result.Success);
        AssertDefaultEqualsShape(NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath)));
    }

    #endregion

    #region true — IEquatable + typed Equals + object delegates

    [SkippableFact]
    public async Task GenerateEquals_ImplementIEquatableTrue_AddsInterfaceTypedEqualsAndDelegatingObjectEquals()
    {
        await using var workspace = await TempWorkspace.CreateAsync(PersonSource);
        var operation = new GenerateEqualsHashCodeOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateEqualsHashCodeParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Person",
            ImplementIEquatable = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        AssertIEquatableClassShape(updated);
        AssertNoOperators(updated);
    }

    [SkippableFact]
    public async Task GenerateEquals_ImplementIEquatableTrue_Struct_UsesNonNullableTypedEquals()
    {
        await using var workspace = await TempWorkspace.CreateAsync(PointSource, "Point.cs");
        var operation = new GenerateEqualsHashCodeOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateEqualsHashCodeParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Point",
            ImplementIEquatable = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("global::System.IEquatable<Point>", updated);
        Assert.Contains("public bool Equals(Point other)", updated);
        Assert.DoesNotContain("Equals(Point?", updated);
        AssertObjectEqualsDelegates(updated, "Point");
        Assert.Contains("public override int GetHashCode()", updated);
    }

    [SkippableFact]
    public async Task GenerateEquals_ImplementIEquatableTrue_UserTypeNamedIEquatable_UsesGlobalQualifiedInterface()
    {
        const string source = """
            namespace TestApp;

            public interface IEquatable { }

            public class Widget
            {
                public string Name { get; set; }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Widget.cs");
        var operation = new GenerateEqualsHashCodeOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateEqualsHashCodeParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget",
            ImplementIEquatable = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("global::System.IEquatable<Widget>", updated);
        Assert.Contains("public bool Equals(Widget? other)", updated);
    }

    [SkippableFact]
    public async Task GenerateEquals_ImplementIEquatableTrue_GenericClass_PreservesTypeArguments()
    {
        const string source = """
            namespace TestApp;

            public class Box<T>
            {
                public T Value { get; set; }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Box.cs");
        var operation = new GenerateEqualsHashCodeOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateEqualsHashCodeParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Box",
            ImplementIEquatable = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("global::System.IEquatable<Box<T>>", updated);
        Assert.Contains("public bool Equals(Box<T>? other)", updated);
        Assert.DoesNotContain("IEquatable<Box>", updated.Replace("IEquatable<Box<T>>", "", StringComparison.Ordinal));
        Assert.DoesNotContain("Equals(Box other)", updated);
        AssertObjectEqualsDelegates(updated, "Box<T>");
    }

    [SkippableFact]
    public async Task GenerateEquals_ImplementIEquatableTrue_GenericStruct_PreservesTypeArguments()
    {
        const string source = """
            namespace TestApp;

            public struct Pair<T, U>
            {
                public T Left { get; set; }

                public U Right { get; set; }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Pair.cs");
        var operation = new GenerateEqualsHashCodeOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateEqualsHashCodeParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Pair",
            ImplementIEquatable = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("global::System.IEquatable<Pair<T, U>>", updated);
        Assert.Contains("public bool Equals(Pair<T, U> other)", updated);
        Assert.DoesNotContain("Equals(Pair<T, U>?", updated);
        AssertObjectEqualsDelegates(updated, "Pair<T, U>");
    }

    [SkippableFact]
    public async Task GenerateEquals_ImplementIEquatableTrue_AlreadyListsUserMarkerIEquatable_Succeeds()
    {
        const string source = """
            namespace TestApp;

            public interface IEquatable { }

            public class Widget : IEquatable
            {
                public string Name { get; set; }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Widget.cs");
        var operation = new GenerateEqualsHashCodeOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateEqualsHashCodeParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget",
            ImplementIEquatable = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("class Widget : IEquatable", updated);
        Assert.Contains("global::System.IEquatable<Widget>", updated);
        Assert.Contains("public bool Equals(Widget? other)", updated);
        AssertObjectEqualsDelegates(updated, "Widget");
        AssertNoOperators(updated);
    }

    #endregion

    #region generateOperators

    [SkippableFact]
    public async Task GenerateEquals_GenerateOperatorsOmitted_DoesNotAddOperators()
    {
        await using var workspace = await TempWorkspace.CreateAsync(PersonSource);
        var operation = new GenerateEqualsHashCodeOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateEqualsHashCodeParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Person"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        AssertDefaultEqualsShape(updated);
        AssertNoOperators(updated);
    }

    [SkippableFact]
    public async Task GenerateEquals_GenerateOperatorsFalse_DoesNotAddOperators()
    {
        await using var workspace = await TempWorkspace.CreateAsync(PersonSource);
        var operation = new GenerateEqualsHashCodeOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateEqualsHashCodeParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Person",
            GenerateOperators = false
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        AssertDefaultEqualsShape(updated);
        AssertNoOperators(updated);
    }

    [SkippableFact]
    public async Task GenerateEquals_GenerateOperatorsTrue_Class_AddsNullSafeOperatorsConsistentWithEquals()
    {
        await using var workspace = await TempWorkspace.CreateAsync(PersonSource);
        var operation = new GenerateEqualsHashCodeOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateEqualsHashCodeParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Person",
            GenerateOperators = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        AssertDefaultEqualsShape(updated);
        AssertEqualityOperators(updated, "Person?", nullableParameters: true);
    }

    [SkippableFact]
    public async Task GenerateEquals_GenerateOperatorsTrue_Struct_UsesNonNullableOperatorParameters()
    {
        await using var workspace = await TempWorkspace.CreateAsync(PointSource, "Point.cs");
        var operation = new GenerateEqualsHashCodeOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateEqualsHashCodeParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Point",
            GenerateOperators = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("public override bool Equals(object?", updated);
        Assert.Contains("public override int GetHashCode()", updated);
        AssertEqualityOperators(updated, "Point", nullableParameters: false);
    }

    [SkippableFact]
    public async Task GenerateEquals_GenerateOperatorsTrue_GenericClass_PreservesTypeArguments()
    {
        const string source = """
            namespace TestApp;

            public class Box<T>
            {
                public T Value { get; set; }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Box.cs");
        var operation = new GenerateEqualsHashCodeOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateEqualsHashCodeParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Box",
            GenerateOperators = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        AssertEqualityOperators(updated, "Box<T>?", nullableParameters: true);
        Assert.DoesNotContain("operator ==(Box ", updated);
        Assert.DoesNotContain("operator ==(Box left", updated);
        Assert.DoesNotContain("operator !=(Box left", updated);
    }

    [SkippableFact]
    public async Task GenerateEquals_GenerateOperatorsTrue_ExistingTwoArgEquals_UsesObjectEquals()
    {
        const string source = """
            namespace TestApp;

            public class Person
            {
                public string Name { get; set; }

                public int Age { get; set; }

                public static bool Equals(Person? a, Person? b) => false;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new GenerateEqualsHashCodeOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateEqualsHashCodeParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Person",
            GenerateOperators = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("public static bool Equals(Person? a, Person? b)", updated);
        AssertEqualityOperators(updated, "Person?", nullableParameters: true);
        var equality = ExtractMethod(updated, "public static bool operator ==");
        Assert.Contains("global::System.Object.Equals(left, right)", equality);
        Assert.DoesNotContain("return Equals(left, right)", equality);
    }

    [SkippableFact]
    public async Task GenerateEquals_ImplementIEquatableAndGenerateOperators_EmitsBoth()
    {
        await using var workspace = await TempWorkspace.CreateAsync(PersonSource);
        var operation = new GenerateEqualsHashCodeOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateEqualsHashCodeParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Person",
            ImplementIEquatable = true,
            GenerateOperators = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        AssertIEquatableClassShape(updated);
        AssertEqualityOperators(updated, "Person?", nullableParameters: true);
    }

    [SkippableFact]
    public async Task GenerateEquals_GenerateOperatorsFalse_TypeAlreadyHasOperators_DoesNotFailOrDuplicate()
    {
        const string source = """
            namespace TestApp;

            public class Person
            {
                public string Name { get; set; }

                public int Age { get; set; }

                public static bool operator ==(Person? left, Person? right) => false;

                public static bool operator !=(Person? left, Person? right) => true;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new GenerateEqualsHashCodeOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateEqualsHashCodeParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Person",
            GenerateOperators = false
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Equal(1, CountOccurrences(updated, "operator =="));
        Assert.Equal(1, CountOccurrences(updated, "operator !="));
        Assert.Contains("=> false", updated);
        Assert.Contains("=> true", updated);
        Assert.DoesNotContain("Object.Equals(left, right)", updated);
    }

    #endregion

    #region Preview

    [SkippableFact]
    public async Task GenerateEquals_PreviewDefault_DoesNotWriteFiles()
    {
        await using var workspace = await TempWorkspace.CreateAsync(PersonSource);
        var operation = new GenerateEqualsHashCodeOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var result = await operation.ExecuteAsync(new GenerateEqualsHashCodeParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Person",
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.NotEmpty(result.PendingChanges);
        var snippet = result.PendingChanges[0].AfterSnippet!;
        Assert.Contains("public override bool Equals(object?", snippet);
        Assert.DoesNotContain("IEquatable", snippet);
        Assert.DoesNotContain("Equals(Person", snippet);
        AssertNoOperators(snippet);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task GenerateEquals_PreviewImplementIEquatableTrue_DoesNotWriteFiles_AndDescribesShape()
    {
        await using var workspace = await TempWorkspace.CreateAsync(PersonSource);
        var operation = new GenerateEqualsHashCodeOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var result = await operation.ExecuteAsync(new GenerateEqualsHashCodeParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Person",
            ImplementIEquatable = true,
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.NotEmpty(result.PendingChanges);
        Assert.Contains("IEquatable", result.PendingChanges[0].Description);
        var snippet = result.PendingChanges[0].AfterSnippet!;
        Assert.Contains("global::System.IEquatable<Person>", snippet);
        Assert.Contains("public bool Equals(Person? other)", snippet);
        Assert.Contains("Equals(other)", snippet);
        AssertNoOperators(snippet);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task GenerateEquals_PreviewGenerateOperatorsTrue_DoesNotWriteFiles_AndDescribesOperators()
    {
        await using var workspace = await TempWorkspace.CreateAsync(PersonSource);
        var operation = new GenerateEqualsHashCodeOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var result = await operation.ExecuteAsync(new GenerateEqualsHashCodeParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Person",
            GenerateOperators = true,
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.NotEmpty(result.PendingChanges);
        Assert.Contains("equality operators", result.PendingChanges[0].Description);
        var snippet = result.PendingChanges[0].AfterSnippet!;
        Assert.Contains("operator ==", snippet);
        Assert.Contains("operator !=", snippet);
        Assert.Contains("global::System.Object.Equals(left, right)", snippet);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    #endregion

    #region Already implements / typed Equals

    [SkippableFact]
    public async Task GenerateEquals_AlreadyImplementsIEquatable_FailsWith3144()
    {
        const string source = """
            namespace TestApp;

            public class Person : System.IEquatable<Person>
            {
                public string Name { get; set; }

                public bool Equals(Person? other) => other is not null && Name == other.Name;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new GenerateEqualsHashCodeOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GenerateEqualsHashCodeParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Person",
                ImplementIEquatable = true
            }));

        Assert.Equal(ErrorCodes.AlreadyImplementsIEquatable, ex.ErrorCode);
        Assert.Equal("3144", ex.ErrorCode);
        Assert.Contains("IEquatable", ex.Message);
        Assert.Equal(source, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task GenerateEquals_AlreadyHasTypedEquals_FailsWith3144()
    {
        const string source = """
            namespace TestApp;

            public class Person
            {
                public string Name { get; set; }

                public bool Equals(Person? other) => other is not null && Name == other.Name;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new GenerateEqualsHashCodeOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GenerateEqualsHashCodeParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Person",
                ImplementIEquatable = true
            }));

        Assert.Equal(ErrorCodes.AlreadyImplementsIEquatable, ex.ErrorCode);
        Assert.Equal("3144", ex.ErrorCode);
        Assert.Equal(source, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task GenerateEquals_AlreadyHasOperators_FailsWith3145()
    {
        const string source = """
            namespace TestApp;

            public class Person
            {
                public string Name { get; set; }

                public int Age { get; set; }

                public static bool operator ==(Person? left, Person? right) => false;

                public static bool operator !=(Person? left, Person? right) => true;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new GenerateEqualsHashCodeOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GenerateEqualsHashCodeParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Person",
                GenerateOperators = true
            }));

        Assert.Equal(ErrorCodes.AlreadyHasEqualityOperators, ex.ErrorCode);
        Assert.Equal("3145", ex.ErrorCode);
        Assert.Contains("operator", ex.Message);
        Assert.Equal(source, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task GenerateEquals_AlreadyHasEqualityOperatorOnly_FailsWith3145()
    {
        const string source = """
            namespace TestApp;

            public class Person
            {
                public string Name { get; set; }

                public int Age { get; set; }

                public static bool operator ==(Person? left, Person? right) => false;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new GenerateEqualsHashCodeOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GenerateEqualsHashCodeParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Person",
                GenerateOperators = true
            }));

        Assert.Equal(ErrorCodes.AlreadyHasEqualityOperators, ex.ErrorCode);
        Assert.Equal("3145", ex.ErrorCode);
        Assert.Equal(source, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    #endregion

    #region replaceExisting

    private const string PersonWithEqualsSource = """
        namespace TestApp;

        public class Person
        {
            public string Name { get; set; }

            public int Age { get; set; }

            public override bool Equals(object? obj) => false;

            public override int GetHashCode() => 42;
        }
        """;

    [SkippableFact]
    public async Task GenerateEquals_ReplaceExistingOmitted_ExistingEquals_FailsWith3056()
    {
        await using var workspace = await TempWorkspace.CreateAsync(PersonWithEqualsSource);
        var operation = new GenerateEqualsHashCodeOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GenerateEqualsHashCodeParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Person"
            }));

        Assert.Equal(ErrorCodes.AlreadyHasOverride, ex.ErrorCode);
        Assert.Equal("3056", ex.ErrorCode);
        Assert.Contains("Equals", ex.Message);
        Assert.Equal(PersonWithEqualsSource, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task GenerateEquals_ReplaceExistingFalse_ExistingEquals_FailsWith3056()
    {
        await using var workspace = await TempWorkspace.CreateAsync(PersonWithEqualsSource);
        var operation = new GenerateEqualsHashCodeOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GenerateEqualsHashCodeParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Person",
                ReplaceExisting = false
            }));

        Assert.Equal(ErrorCodes.AlreadyHasOverride, ex.ErrorCode);
        Assert.Equal("3056", ex.ErrorCode);
        Assert.Equal(PersonWithEqualsSource, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task GenerateEquals_ReplaceExistingTrue_ReplacesEqualsAndGetHashCode()
    {
        await using var workspace = await TempWorkspace.CreateAsync(PersonWithEqualsSource);
        var operation = new GenerateEqualsHashCodeOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateEqualsHashCodeParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Person",
            ReplaceExisting = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        AssertDefaultEqualsShape(updated);
        Assert.DoesNotContain("=> false", updated);
        Assert.DoesNotContain("=> 42", updated);
        Assert.Contains("HashCode.Combine", updated);
        Assert.Equal(1, CountOccurrences(updated, "public override bool Equals(object?"));
        Assert.Equal(1, CountOccurrences(updated, "public override int GetHashCode()"));
        AssertNoOperators(updated);
        Assert.DoesNotContain("IEquatable", updated);
    }

    [SkippableFact]
    public async Task GenerateEquals_ReplaceExistingTrue_ImplementIEquatable_ReplacesTypedEqualsAndInterface()
    {
        const string source = """
            namespace TestApp;

            public class Person : System.IEquatable<Person>
            {
                public string Name { get; set; }

                public int Age { get; set; }

                public bool Equals(Person? other) => false;

                public override bool Equals(object? obj) => false;

                public override int GetHashCode() => 42;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new GenerateEqualsHashCodeOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateEqualsHashCodeParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Person",
            ImplementIEquatable = true,
            ReplaceExisting = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        AssertIEquatableClassShape(updated);
        Assert.DoesNotContain("=> false", updated);
        Assert.DoesNotContain("=> 42", updated);
        Assert.Equal(1, CountOccurrences(updated, "IEquatable<Person>"));
        Assert.Equal(1, CountOccurrences(updated, "public bool Equals(Person? other)"));
        Assert.DoesNotContain(": System.IEquatable<Person>", updated);
        Assert.Contains("global::System.IEquatable<Person>", updated);
        AssertNoOperators(updated);
    }

    [SkippableFact]
    public async Task GenerateEquals_ReplaceExistingTrue_GenerateOperators_ReplacesOperators()
    {
        const string source = """
            namespace TestApp;

            public class Person
            {
                public string Name { get; set; }

                public int Age { get; set; }

                public override bool Equals(object? obj) => false;

                public override int GetHashCode() => 42;

                public static bool operator ==(Person? left, Person? right) => false;

                public static bool operator !=(Person? left, Person? right) => true;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new GenerateEqualsHashCodeOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateEqualsHashCodeParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Person",
            GenerateOperators = true,
            ReplaceExisting = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        AssertDefaultEqualsShape(updated);
        AssertEqualityOperators(updated, "Person?", nullableParameters: true);
        Assert.Equal(1, CountOccurrences(updated, "operator =="));
        Assert.Equal(1, CountOccurrences(updated, "operator !="));
        Assert.DoesNotContain("=> false", updated);
        Assert.DoesNotContain("=> true", updated);
        Assert.DoesNotContain("=> 42", updated);
    }

    [SkippableFact]
    public async Task GenerateEquals_ReplaceExistingTrue_ExistingOperators_GenerateOperatorsFalse_LeavesOperators()
    {
        const string source = """
            namespace TestApp;

            public class Person
            {
                public string Name { get; set; }

                public int Age { get; set; }

                public override bool Equals(object? obj) => false;

                public override int GetHashCode() => 42;

                public static bool operator ==(Person? left, Person? right) => false;

                public static bool operator !=(Person? left, Person? right) => true;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new GenerateEqualsHashCodeOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateEqualsHashCodeParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Person",
            GenerateOperators = false,
            ReplaceExisting = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        AssertDefaultEqualsShape(updated);
        Assert.Equal(1, CountOccurrences(updated, "operator =="));
        Assert.Equal(1, CountOccurrences(updated, "operator !="));
        Assert.Contains("=> false", updated);
        Assert.Contains("=> true", updated);
        Assert.DoesNotContain("Object.Equals(left, right)", updated);
        Assert.DoesNotContain("=> 42", updated);
    }

    [SkippableFact]
    public async Task GenerateEquals_ReplaceExistingTrue_Preview_DoesNotWriteFiles_AndDescribesReplacement()
    {
        await using var workspace = await TempWorkspace.CreateAsync(PersonWithEqualsSource);
        var operation = new GenerateEqualsHashCodeOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var result = await operation.ExecuteAsync(new GenerateEqualsHashCodeParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Person",
            ImplementIEquatable = true,
            GenerateOperators = true,
            ReplaceExisting = true,
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.NotEmpty(result.PendingChanges);
        Assert.Contains("Replace", result.PendingChanges[0].Description);
        Assert.Contains("IEquatable", result.PendingChanges[0].Description);
        Assert.Contains("equality operators", result.PendingChanges[0].Description);
        var snippet = result.PendingChanges[0].AfterSnippet!;
        Assert.Contains("global::System.IEquatable<Person>", snippet);
        Assert.Contains("public bool Equals(Person? other)", snippet);
        Assert.Contains("operator ==", snippet);
        Assert.Contains("operator !=", snippet);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task GenerateEquals_ReplaceExistingTrue_GenericBox_PreservesTypeArguments()
    {
        const string source = """
            namespace TestApp;

            public class Box<T> : System.IEquatable<Box<T>>
            {
                public T Value { get; set; }

                public bool Equals(Box<T>? other) => false;

                public override bool Equals(object? obj) => false;

                public override int GetHashCode() => 42;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Box.cs");
        var operation = new GenerateEqualsHashCodeOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateEqualsHashCodeParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Box",
            ImplementIEquatable = true,
            ReplaceExisting = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("global::System.IEquatable<Box<T>>", updated);
        Assert.Contains("public bool Equals(Box<T>? other)", updated);
        Assert.DoesNotContain("IEquatable<Box>", updated.Replace("IEquatable<Box<T>>", "", StringComparison.Ordinal));
        Assert.DoesNotContain("Equals(Box other)", updated);
        AssertObjectEqualsDelegates(updated, "Box<T>");
        Assert.DoesNotContain("=> false", updated);
        Assert.DoesNotContain("=> 42", updated);
        Assert.Equal(1, CountOccurrences(updated, "IEquatable<Box<T>>"));
        Assert.Equal(1, CountOccurrences(updated, "public bool Equals(Box<T>? other)"));
    }

    [SkippableFact]
    public async Task GenerateEquals_ReplaceExistingTrue_PartialOtherFile_RemovesOtherPartMembers()
    {
        const string fieldsPart = """
            namespace TestApp;

            public partial class Person
            {
                public string Name { get; set; }

                public int Age { get; set; }
            }
            """;

        const string equalsPart = """
            namespace TestApp;

            public partial class Person
            {
                public override bool Equals(object? obj) => false;

                public override int GetHashCode() => 42;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(
            ("Person.cs", fieldsPart),
            ("Person.Equals.cs", equalsPart));
        var otherPath = workspace.PathFor("Person.Equals.cs");
        var operation = new GenerateEqualsHashCodeOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateEqualsHashCodeParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Person",
            ReplaceExisting = true
        });

        Assert.True(result.Success);
        var selected = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var other = NormalizeNewlines(await File.ReadAllTextAsync(otherPath));
        AssertDefaultEqualsShape(selected);
        Assert.DoesNotContain("=> false", selected);
        Assert.DoesNotContain("=> 42", selected);
        Assert.Equal(1, CountOccurrences(selected, "public override bool Equals(object?"));
        Assert.Equal(1, CountOccurrences(selected, "public override int GetHashCode()"));
        Assert.DoesNotContain("public override bool Equals", other);
        Assert.DoesNotContain("public override int GetHashCode", other);
        Assert.DoesNotContain("=> false", other);
        Assert.DoesNotContain("=> 42", other);
    }

    [SkippableFact]
    public async Task GenerateEquals_ReplaceExistingTrue_PartialOtherFile_Preview_DoesNotWriteFiles()
    {
        const string fieldsPart = """
            namespace TestApp;

            public partial class Person
            {
                public string Name { get; set; }

                public int Age { get; set; }
            }
            """;

        const string equalsPart = """
            namespace TestApp;

            public partial class Person
            {
                public override bool Equals(object? obj) => false;

                public override int GetHashCode() => 42;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(
            ("Person.cs", fieldsPart),
            ("Person.Equals.cs", equalsPart));
        var otherPath = workspace.PathFor("Person.Equals.cs");
        var operation = new GenerateEqualsHashCodeOperation(workspace.Context);
        var beforeSelected = await File.ReadAllTextAsync(workspace.SourcePath);
        var beforeOther = await File.ReadAllTextAsync(otherPath);

        var result = await operation.ExecuteAsync(new GenerateEqualsHashCodeParams
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

    #endregion

    #region useHashCodeCombine

    [SkippableFact]
    public async Task GenerateEquals_UseHashCodeCombineOmitted_UsesCombineForFewMembers()
    {
        await using var workspace = await TempWorkspace.CreateAsync(PersonSource);
        var operation = new GenerateEqualsHashCodeOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateEqualsHashCodeParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Person"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        AssertHashCodeCombine(updated, "Name", "Age");
        AssertNoPrimeMultiply(updated);
    }

    [SkippableFact]
    public async Task GenerateEquals_UseHashCodeCombineTrue_UsesCombineForFewMembers()
    {
        await using var workspace = await TempWorkspace.CreateAsync(PersonSource);
        var operation = new GenerateEqualsHashCodeOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateEqualsHashCodeParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Person",
            UseHashCodeCombine = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        AssertHashCodeCombine(updated, "Name", "Age");
        AssertNoPrimeMultiply(updated);
    }

    [SkippableFact]
    public async Task GenerateEquals_UseHashCodeCombineTrue_MoreThanEightMembers_UsesHashCodeBuilder()
    {
        const string source = """
            namespace TestApp;

            public class Wide
            {
                public int A { get; set; }
                public int B { get; set; }
                public int C { get; set; }
                public int D { get; set; }
                public int E { get; set; }
                public int F { get; set; }
                public int G { get; set; }
                public int H { get; set; }
                public int I { get; set; }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Wide.cs");
        var operation = new GenerateEqualsHashCodeOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateEqualsHashCodeParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Wide",
            UseHashCodeCombine = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var getHashCode = ExtractMethod(updated, "public override int GetHashCode()");
        Assert.Contains("new global::System.HashCode()", getHashCode);
        Assert.Contains("hash.Add(this.A)", getHashCode);
        Assert.Contains("hash.Add(this.I)", getHashCode);
        Assert.Contains("hash.ToHashCode()", getHashCode);
        Assert.DoesNotContain("HashCode.Combine", getHashCode);
        AssertNoPrimeMultiply(getHashCode);
    }

    [SkippableFact]
    public async Task GenerateEquals_UseHashCodeCombineFalse_UsesUncheckedPrimeMultiply()
    {
        await using var workspace = await TempWorkspace.CreateAsync(PersonSource);
        var operation = new GenerateEqualsHashCodeOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateEqualsHashCodeParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Person",
            UseHashCodeCombine = false
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var getHashCode = ExtractMethod(updated, "public override int GetHashCode()");
        AssertPrimeMultiply(getHashCode);
        Assert.Contains("this.Name.GetHashCode()", getHashCode);
        Assert.DoesNotContain("this.Name?.GetHashCode()", getHashCode);
        Assert.Contains("this.Age.GetHashCode()", getHashCode);
        AssertNoHashCodeCombineOrBuilder(updated);
        AssertDefaultEqualsShape(updated);
    }

    [SkippableFact]
    public async Task GenerateEquals_UseHashCodeCombineFalse_NullableReferenceMember_UsesNullSafeHash()
    {
        const string source = """
            namespace TestApp;

            public class Person
            {
                public string? Name { get; set; }

                public int Age { get; set; }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new GenerateEqualsHashCodeOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateEqualsHashCodeParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Person",
            UseHashCodeCombine = false
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var getHashCode = ExtractMethod(updated, "public override int GetHashCode()");
        AssertPrimeMultiply(getHashCode);
        Assert.Contains("(this.Name?.GetHashCode() ?? 0)", getHashCode);
        Assert.Contains("this.Age.GetHashCode()", getHashCode);
        AssertNoHashCodeCombineOrBuilder(getHashCode);
    }

    [SkippableFact]
    public async Task GenerateEquals_UseHashCodeCombineFalse_ImplementIEquatableAndGenerateOperators_OnlyChangesGetHashCodeShape()
    {
        await using var workspace = await TempWorkspace.CreateAsync(PersonSource);
        var operation = new GenerateEqualsHashCodeOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateEqualsHashCodeParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Person",
            ImplementIEquatable = true,
            GenerateOperators = true,
            UseHashCodeCombine = false
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        AssertIEquatableClassShape(updated);
        AssertEqualityOperators(updated, "Person?", nullableParameters: true);
        AssertPrimeMultiply(updated);
        AssertNoHashCodeCombineOrBuilder(updated);
    }

    [SkippableFact]
    public async Task GenerateEquals_UseHashCodeCombineFalse_Preview_DoesNotWriteFiles_AndDescribesPrimeMultiply()
    {
        await using var workspace = await TempWorkspace.CreateAsync(PersonSource);
        var operation = new GenerateEqualsHashCodeOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var result = await operation.ExecuteAsync(new GenerateEqualsHashCodeParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Person",
            UseHashCodeCombine = false,
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.NotEmpty(result.PendingChanges);
        Assert.Contains("unchecked prime-multiply", result.PendingChanges[0].Description);
        var snippet = result.PendingChanges[0].AfterSnippet!;
        AssertPrimeMultiply(snippet);
        AssertNoHashCodeCombineOrBuilder(snippet);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task GenerateEquals_UseHashCodeCombineTrue_Preview_DoesNotWriteFiles_AndDescribesCombine()
    {
        await using var workspace = await TempWorkspace.CreateAsync(PersonSource);
        var operation = new GenerateEqualsHashCodeOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var result = await operation.ExecuteAsync(new GenerateEqualsHashCodeParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Person",
            UseHashCodeCombine = true,
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.NotEmpty(result.PendingChanges);
        Assert.Contains("HashCode.Combine", result.PendingChanges[0].Description);
        var snippet = result.PendingChanges[0].AfterSnippet!;
        AssertHashCodeCombine(snippet, "Name", "Age");
        AssertNoPrimeMultiply(snippet);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task GenerateEquals_UseHashCodeCombineTrue_TypeLocalHashCode_QualifiesSystemHashCode()
    {
        const string source = """
            namespace TestApp;

            public class HashCode
            {
                public int Value { get; set; }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "HashCode.cs");
        var operation = new GenerateEqualsHashCodeOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateEqualsHashCodeParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "HashCode",
            UseHashCodeCombine = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var getHashCode = ExtractMethod(updated, "public override int GetHashCode()");
        Assert.Contains("global::System.HashCode.Combine(this.Value)", getHashCode);
        Assert.DoesNotContain("return HashCode.Combine", getHashCode);
    }

    [SkippableFact]
    public async Task GenerateEquals_UseHashCodeCombineFalse_MemberNamedHash_QualifiesWithThis()
    {
        const string source = """
            namespace TestApp;

            public class Bucket
            {
                public int hash;

                public int Age { get; set; }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Bucket.cs");
        var operation = new GenerateEqualsHashCodeOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateEqualsHashCodeParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Bucket",
            UseHashCodeCombine = false
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var getHashCode = ExtractMethod(updated, "public override int GetHashCode()");
        AssertPrimeMultiply(getHashCode);
        Assert.Contains("this.hash.GetHashCode()", getHashCode);
        Assert.Contains("this.Age.GetHashCode()", getHashCode);
        Assert.DoesNotContain("(hash * 31) + hash.GetHashCode()", getHashCode);
        Assert.DoesNotContain("hash?.GetHashCode()", getHashCode);
        AssertNoHashCodeCombineOrBuilder(getHashCode);
    }

    [SkippableFact]
    public async Task GenerateEquals_UseHashCodeCombineTrue_MoreThanEightMembers_MemberNamedHash_QualifiesWithThis()
    {
        const string source = """
            namespace TestApp;

            public class Wide
            {
                public int A { get; set; }
                public int B { get; set; }
                public int C { get; set; }
                public int D { get; set; }
                public int E { get; set; }
                public int F { get; set; }
                public int G { get; set; }
                public int H { get; set; }
                public int hash { get; set; }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Wide.cs");
        var operation = new GenerateEqualsHashCodeOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateEqualsHashCodeParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Wide",
            UseHashCodeCombine = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var getHashCode = ExtractMethod(updated, "public override int GetHashCode()");
        Assert.Contains("new global::System.HashCode()", getHashCode);
        Assert.Contains("hash.Add(this.hash)", getHashCode);
        Assert.Contains("hash.Add(this.A)", getHashCode);
        Assert.Contains("hash.ToHashCode()", getHashCode);
        Assert.DoesNotContain("hash.Add(hash)", getHashCode);
        Assert.DoesNotContain("HashCode.Combine", getHashCode);
        AssertNoPrimeMultiply(getHashCode);
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
    public async Task GenerateEquals_IncludePropertiesOmitted_IncludesFieldAndProperty()
    {
        await using var workspace = await TempWorkspace.CreateAsync(WidgetWithFieldAndPropertySource, "Widget.cs");
        var operation = new GenerateEqualsHashCodeOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateEqualsHashCodeParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        AssertEqualityMembers(updated, "_id", "Name");
    }

    [SkippableFact]
    public async Task GenerateEquals_IncludePropertiesTrue_IncludesFieldAndProperty()
    {
        await using var workspace = await TempWorkspace.CreateAsync(WidgetWithFieldAndPropertySource, "Widget.cs");
        var operation = new GenerateEqualsHashCodeOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateEqualsHashCodeParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget",
            IncludeProperties = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        AssertEqualityMembers(updated, "_id", "Name");
    }

    [SkippableFact]
    public async Task GenerateEquals_IncludePropertiesFalse_IncludesFieldOnly()
    {
        await using var workspace = await TempWorkspace.CreateAsync(WidgetWithFieldAndPropertySource, "Widget.cs");
        var operation = new GenerateEqualsHashCodeOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateEqualsHashCodeParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget",
            IncludeProperties = false
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        AssertEqualityMembers(updated, "_id");
        AssertMemberNotCompared(updated, "Name");
    }

    [SkippableFact]
    public async Task GenerateEquals_IncludePropertiesFalse_EmptyFieldsList_IncludesFieldOnly()
    {
        await using var workspace = await TempWorkspace.CreateAsync(WidgetWithFieldAndPropertySource, "Widget.cs");
        var operation = new GenerateEqualsHashCodeOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateEqualsHashCodeParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget",
            Fields = Array.Empty<string>(),
            IncludeProperties = false
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        AssertEqualityMembers(updated, "_id");
        AssertMemberNotCompared(updated, "Name");
    }

    [SkippableFact]
    public async Task GenerateEquals_IncludePropertiesFalse_PropertiesOnly_FailsWithNoMembersToGenerate()
    {
        await using var workspace = await TempWorkspace.CreateAsync(PersonSource);
        var operation = new GenerateEqualsHashCodeOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GenerateEqualsHashCodeParams
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
    public async Task GenerateEquals_IncludePropertiesFalse_FieldsNamesProperty_IncludesThatProperty()
    {
        await using var workspace = await TempWorkspace.CreateAsync(WidgetWithFieldAndPropertySource, "Widget.cs");
        var operation = new GenerateEqualsHashCodeOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateEqualsHashCodeParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget",
            Fields = new[] { "Name" },
            IncludeProperties = false
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        AssertEqualityMembers(updated, "Name");
        AssertMemberNotCompared(updated, "_id");
    }

    [SkippableFact]
    public async Task GenerateEquals_IncludePropertiesFalse_ImplementIEquatableGenerateOperatorsAndCombine_OnlyChangesCollectedSet()
    {
        await using var workspace = await TempWorkspace.CreateAsync(WidgetWithFieldAndPropertySource, "Widget.cs");
        var operation = new GenerateEqualsHashCodeOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateEqualsHashCodeParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget",
            IncludeProperties = false,
            ImplementIEquatable = true,
            GenerateOperators = true,
            UseHashCodeCombine = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("global::System.IEquatable<Widget>", updated);
        Assert.Contains("public bool Equals(Widget? other)", updated);
        AssertObjectEqualsDelegates(updated, "Widget");
        AssertEqualityOperators(updated, "Widget?", nullableParameters: true);
        AssertHashCodeCombine(updated, "_id");
        var typedEquals = ExtractMethod(updated, "public bool Equals(Widget? other)");
        Assert.Contains("other._id", typedEquals);
        Assert.DoesNotContain("other.Name", typedEquals);
        Assert.DoesNotContain("this.Name", ExtractMethod(updated, "public override int GetHashCode()"));
        AssertNoPrimeMultiply(updated);
    }

    [SkippableFact]
    public async Task GenerateEquals_IncludePropertiesFalse_Preview_DoesNotWriteFiles_AndDescribesFieldOnly()
    {
        await using var workspace = await TempWorkspace.CreateAsync(WidgetWithFieldAndPropertySource, "Widget.cs");
        var operation = new GenerateEqualsHashCodeOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var result = await operation.ExecuteAsync(new GenerateEqualsHashCodeParams
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
        var snippet = result.PendingChanges[0].AfterSnippet!;
        Assert.Contains("this._id", snippet);
        Assert.DoesNotContain("this.Name", snippet);
        Assert.DoesNotContain("other.Name", snippet);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    #endregion

    #region callSuper

    private const string EntitySource = """
        namespace TestApp;

        public class Entity
        {
            public int Id { get; set; }

            public override bool Equals(object? obj) => obj is Entity other && Id == other.Id;

            public override int GetHashCode() => Id.GetHashCode();
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

    private static Task<TempWorkspace> CreateDerivedOnEntityAsync(string derivedSource, string fileName = "Person.cs") =>
        TempWorkspace.CreateAsync((fileName, derivedSource), ("Entity.cs", EntitySource));

    [SkippableFact]
    public async Task GenerateEquals_CallSuperOmitted_DoesNotCallBase()
    {
        await using var workspace = await TempWorkspace.CreateAsync(PersonSource);
        var operation = new GenerateEqualsHashCodeOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateEqualsHashCodeParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Person"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        AssertDefaultEqualsShape(updated);
        AssertNoBaseEqualityCalls(updated);
    }

    [SkippableFact]
    public async Task GenerateEquals_CallSuperFalse_DoesNotCallBase()
    {
        await using var workspace = await CreateDerivedOnEntityAsync(PersonOnEntitySource);
        var operation = new GenerateEqualsHashCodeOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateEqualsHashCodeParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Person",
            CallSuper = false
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        AssertDefaultEqualsShape(updated);
        AssertNoBaseEqualityCalls(updated);
        AssertEqualityMembers(updated, "Name", "Age");
    }

    [SkippableFact]
    public async Task GenerateEquals_CallSuperTrue_FoldsBaseEqualsAndGetHashCode()
    {
        await using var workspace = await CreateDerivedOnEntityAsync(PersonOnEntitySource);
        var operation = new GenerateEqualsHashCodeOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateEqualsHashCodeParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Person",
            CallSuper = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var objectEquals = ExtractMethod(updated, "public override bool Equals(object?");
        Assert.Contains("obj is Person other", objectEquals);
        Assert.Contains("base.Equals(other)", objectEquals);
        Assert.Contains("other.Name", objectEquals);
        Assert.Contains("other.Age", objectEquals);
        var getHashCode = ExtractMethod(updated, "public override int GetHashCode()");
        Assert.Contains("base.GetHashCode()", getHashCode);
        Assert.Contains("this.Name", getHashCode);
        Assert.Contains("this.Age", getHashCode);
        Assert.Contains("global::System.HashCode.Combine(base.GetHashCode()", getHashCode);
    }

    [SkippableFact]
    public async Task GenerateEquals_CallSuperTrue_UseHashCodeCombine_IncludesBaseHashFirst()
    {
        await using var workspace = await CreateDerivedOnEntityAsync(PersonOnEntitySource);
        var operation = new GenerateEqualsHashCodeOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateEqualsHashCodeParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Person",
            CallSuper = true,
            UseHashCodeCombine = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var getHashCode = ExtractMethod(updated, "public override int GetHashCode()");
        Assert.Contains("global::System.HashCode.Combine(base.GetHashCode(), this.Name, this.Age)", getHashCode);
        Assert.DoesNotContain("new global::System.HashCode()", getHashCode);
        AssertNoPrimeMultiply(getHashCode);
    }

    [SkippableFact]
    public async Task GenerateEquals_CallSuperTrue_UseHashCodeCombine_MoreThanEightMembers_AddsBaseHashFirst()
    {
        const string source = """
            namespace TestApp;

            public class Wide : Entity
            {
                public int A { get; set; }
                public int B { get; set; }
                public int C { get; set; }
                public int D { get; set; }
                public int E { get; set; }
                public int F { get; set; }
                public int G { get; set; }
                public int H { get; set; }
                public int I { get; set; }
            }
            """;

        await using var workspace = await CreateDerivedOnEntityAsync(source, "Wide.cs");
        var operation = new GenerateEqualsHashCodeOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateEqualsHashCodeParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Wide",
            CallSuper = true,
            UseHashCodeCombine = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var getHashCode = ExtractMethod(updated, "public override int GetHashCode()");
        Assert.Contains("new global::System.HashCode()", getHashCode);
        Assert.Contains("hash.Add(base.GetHashCode())", getHashCode);
        Assert.Contains("hash.Add(this.A)", getHashCode);
        Assert.Contains("hash.Add(this.I)", getHashCode);
        Assert.Contains("hash.ToHashCode()", getHashCode);
        Assert.DoesNotContain("HashCode.Combine", getHashCode);
        AssertNoPrimeMultiply(getHashCode);
        var addBase = getHashCode.IndexOf("hash.Add(base.GetHashCode())", StringComparison.Ordinal);
        var addA = getHashCode.IndexOf("hash.Add(this.A)", StringComparison.Ordinal);
        Assert.True(addBase >= 0 && addA > addBase, "base.GetHashCode() should be Add-ed before members");
    }

    [SkippableFact]
    public async Task GenerateEquals_CallSuperTrue_UseHashCodeCombineFalse_SeedsFromBaseGetHashCode()
    {
        await using var workspace = await CreateDerivedOnEntityAsync(PersonOnEntitySource);
        var operation = new GenerateEqualsHashCodeOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateEqualsHashCodeParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Person",
            CallSuper = true,
            UseHashCodeCombine = false
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var getHashCode = ExtractMethod(updated, "public override int GetHashCode()");
        Assert.Contains("unchecked", getHashCode);
        Assert.Contains("int hash = base.GetHashCode()", getHashCode);
        Assert.DoesNotContain("int hash = 17", getHashCode);
        Assert.Contains("(hash * 31)", getHashCode);
        Assert.Contains("this.Name.GetHashCode()", getHashCode);
        Assert.Contains("this.Age.GetHashCode()", getHashCode);
        AssertNoHashCodeCombineOrBuilder(getHashCode);
        var objectEquals = ExtractMethod(updated, "public override bool Equals(object?");
        Assert.Contains("base.Equals(other)", objectEquals);
    }

    [SkippableFact]
    public async Task GenerateEquals_CallSuperTrue_ImplementIEquatable_FoldsBaseIntoTypedEquals()
    {
        await using var workspace = await CreateDerivedOnEntityAsync(PersonOnEntitySource);
        var operation = new GenerateEqualsHashCodeOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateEqualsHashCodeParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Person",
            CallSuper = true,
            ImplementIEquatable = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("global::System.IEquatable<Person>", updated);
        var typedEquals = ExtractMethod(updated, "public bool Equals(Person? other)");
        Assert.Contains("base.Equals(other)", typedEquals);
        Assert.Contains("Name", typedEquals);
        Assert.Contains("Age", typedEquals);
        var notNull = typedEquals.IndexOf("other is not null", StringComparison.Ordinal);
        var baseEquals = typedEquals.IndexOf("base.Equals(other)", StringComparison.Ordinal);
        var nameCompare = typedEquals.IndexOf("Name", StringComparison.Ordinal);
        Assert.True(notNull >= 0 && baseEquals > notNull && nameCompare > baseEquals,
            "base.Equals should come after the null check and before member comparisons");
        AssertObjectEqualsDelegates(updated, "Person");
        var objectEquals = ExtractMethod(updated, "public override bool Equals(object?");
        Assert.DoesNotContain("base.Equals", objectEquals);
        var getHashCode = ExtractMethod(updated, "public override int GetHashCode()");
        Assert.Contains("base.GetHashCode()", getHashCode);
    }

    [SkippableFact]
    public async Task GenerateEquals_CallSuperTrue_ObjectBase_FailsWith3146()
    {
        await using var workspace = await TempWorkspace.CreateAsync(PersonSource);
        var operation = new GenerateEqualsHashCodeOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GenerateEqualsHashCodeParams
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
    public async Task GenerateEquals_CallSuperTrue_Struct_FailsWith3146()
    {
        await using var workspace = await TempWorkspace.CreateAsync(PointSource, "Point.cs");
        var operation = new GenerateEqualsHashCodeOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GenerateEqualsHashCodeParams
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
            public abstract override bool Equals(object? obj);

            public abstract override int GetHashCode();
        }

        public class Person : Entity
        {
            public string Name { get; set; }

            public int Age { get; set; }

            public override bool Equals(object? obj) => false;

            public override int GetHashCode() => 42;
        }
        """;

    [SkippableFact]
    public async Task GenerateEquals_CallSuperTrue_AbstractBaseEqualsAndGetHashCode_FailsWith3147()
    {
        await using var workspace = await TempWorkspace.CreateAsync(AbstractEntityPersonSource);
        var operation = new GenerateEqualsHashCodeOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GenerateEqualsHashCodeParams
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
    public async Task GenerateEquals_CallSuperTrue_ReplaceExisting_AbstractBase_FailsWith3147_DoesNotRewrite()
    {
        await using var workspace = await TempWorkspace.CreateAsync(AbstractEntityPersonSource);
        var operation = new GenerateEqualsHashCodeOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GenerateEqualsHashCodeParams
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
        var unchanged = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("=> false", unchanged);
        Assert.Contains("=> 42", unchanged);
        Assert.DoesNotContain("base.Equals", unchanged);
        Assert.DoesNotContain("base.GetHashCode", unchanged);
    }

    [SkippableFact]
    public async Task GenerateEquals_CallSuperTrue_NoMembers_GeneratesBaseOnly()
    {
        await using var workspace = await CreateDerivedOnEntityAsync(EmployeeOnEntitySource, "Employee.cs");
        var operation = new GenerateEqualsHashCodeOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateEqualsHashCodeParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Employee",
            CallSuper = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var objectEquals = ExtractMethod(updated, "public override bool Equals(object?");
        Assert.Contains("obj is Employee other", objectEquals);
        Assert.Contains("base.Equals(other)", objectEquals);
        Assert.DoesNotContain("other.Id", objectEquals);
        var getHashCode = ExtractMethod(updated, "public override int GetHashCode()");
        Assert.Contains("global::System.HashCode.Combine(base.GetHashCode())", getHashCode);
        Assert.DoesNotContain("this.Id", getHashCode);
    }

    [SkippableFact]
    public async Task GenerateEquals_CallSuperTrue_Preview_DoesNotWriteFiles_AndDescribesBase()
    {
        await using var workspace = await CreateDerivedOnEntityAsync(PersonOnEntitySource);
        var operation = new GenerateEqualsHashCodeOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var result = await operation.ExecuteAsync(new GenerateEqualsHashCodeParams
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
        Assert.Contains("base equality", result.PendingChanges[0].Description);
        var snippet = result.PendingChanges[0].AfterSnippet!;
        Assert.Contains("base.Equals(other)", snippet);
        Assert.Contains("base.GetHashCode()", snippet);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    #endregion

    #region includeInheritedMembers

    private const string AnimalSource = """
        namespace TestApp;

        public class Animal
        {
            public string Species;

            protected int Legs;

            private string Secret;

            public string Nickname { get; set; }
        }
        """;

    private const string DogSource = """
        namespace TestApp;

        public class Dog : Animal
        {
            public string Name;
        }
        """;

    private static Task<TempWorkspace> CreateDogOnAnimalAsync() =>
        TempWorkspace.CreateAsync(("Dog.cs", DogSource), ("Animal.cs", AnimalSource));

    [SkippableFact]
    public async Task GenerateEquals_IncludeInheritedMembersOmitted_DoesNotCompareBaseField()
    {
        await using var workspace = await CreateDogOnAnimalAsync();
        var operation = new GenerateEqualsHashCodeOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateEqualsHashCodeParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        AssertEqualityMembers(updated, "Name");
        AssertMemberNotCompared(updated, "Species");
        AssertMemberNotCompared(updated, "Legs");
        AssertMemberNotCompared(updated, "Secret");
        AssertMemberNotCompared(updated, "Nickname");
    }

    [SkippableFact]
    public async Task GenerateEquals_IncludeInheritedMembersFalse_DoesNotCompareBaseField()
    {
        await using var workspace = await CreateDogOnAnimalAsync();
        var operation = new GenerateEqualsHashCodeOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateEqualsHashCodeParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            IncludeInheritedMembers = false
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        AssertEqualityMembers(updated, "Name");
        AssertMemberNotCompared(updated, "Species");
        AssertMemberNotCompared(updated, "Legs");
    }

    [SkippableFact]
    public async Task GenerateEquals_IncludeInheritedMembersTrue_IncludesPublicAndProtectedBaseFields()
    {
        await using var workspace = await CreateDogOnAnimalAsync();
        var operation = new GenerateEqualsHashCodeOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateEqualsHashCodeParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            IncludeInheritedMembers = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        AssertEqualityMembers(updated, "Name", "Species", "Legs", "Nickname");
        AssertMemberNotCompared(updated, "Secret");
    }

    [SkippableFact]
    public async Task GenerateEquals_IncludeInheritedMembersTrue_SkipsPrivateBaseField()
    {
        await using var workspace = await CreateDogOnAnimalAsync();
        var operation = new GenerateEqualsHashCodeOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateEqualsHashCodeParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            IncludeInheritedMembers = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        AssertMemberNotCompared(updated, "Secret");
        Assert.DoesNotContain("other.Secret", updated);
        Assert.DoesNotContain("this.Secret", ExtractMethod(updated, "public override int GetHashCode()"));
    }

    [SkippableFact]
    public async Task GenerateEquals_IncludeInheritedMembersTrue_IncludePropertiesFalse_SkipsInheritedProperties()
    {
        await using var workspace = await CreateDogOnAnimalAsync();
        var operation = new GenerateEqualsHashCodeOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateEqualsHashCodeParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            IncludeInheritedMembers = true,
            IncludeProperties = false
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        AssertEqualityMembers(updated, "Name", "Species", "Legs");
        AssertMemberNotCompared(updated, "Nickname");
        AssertMemberNotCompared(updated, "Secret");
    }

    [SkippableFact]
    public async Task GenerateEquals_IncludeInheritedMembersTrue_FieldsNamesInheritedMember_IncludesIt()
    {
        await using var workspace = await CreateDogOnAnimalAsync();
        var operation = new GenerateEqualsHashCodeOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateEqualsHashCodeParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            IncludeInheritedMembers = true,
            IncludeProperties = false,
            Fields = new[] { "Nickname" }
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        AssertEqualityMembers(updated, "Nickname");
        AssertMemberNotCompared(updated, "Name");
        AssertMemberNotCompared(updated, "Species");
    }

    [SkippableFact]
    public async Task GenerateEquals_IncludeInheritedMembersFalse_FieldsNamesInheritedMember_NotFound()
    {
        await using var workspace = await CreateDogOnAnimalAsync();
        var operation = new GenerateEqualsHashCodeOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GenerateEqualsHashCodeParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Dog",
                IncludeInheritedMembers = false,
                Fields = new[] { "Species" }
            }));

        Assert.Equal(ErrorCodes.NoMembersToGenerate, ex.ErrorCode);
        Assert.Equal("3055", ex.ErrorCode);
        Assert.Equal(DogSource, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task GenerateEquals_IncludeInheritedMembersTrue_ObjectOnlyBase_NoExtraMembers()
    {
        await using var workspace = await TempWorkspace.CreateAsync(PersonSource);
        var operation = new GenerateEqualsHashCodeOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateEqualsHashCodeParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Person",
            IncludeInheritedMembers = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        AssertEqualityMembers(updated, "Name", "Age");
        AssertNoBaseEqualityCalls(updated);
    }

    [SkippableFact]
    public async Task GenerateEquals_IncludeInheritedMembersTrue_Preview_DoesNotWriteFiles_AndDescribesInherited()
    {
        await using var workspace = await CreateDogOnAnimalAsync();
        var operation = new GenerateEqualsHashCodeOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var result = await operation.ExecuteAsync(new GenerateEqualsHashCodeParams
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
        var snippet = result.PendingChanges[0].AfterSnippet!;
        Assert.Contains("other.Name", snippet);
        Assert.Contains("other.Species", snippet);
        Assert.Contains("other.Legs", snippet);
        Assert.Contains("other.Nickname", snippet);
        Assert.DoesNotContain("other.Secret", snippet);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task GenerateEquals_IncludeInheritedMembersTrue_Override_ComparesDerivedPropertyOnce()
    {
        const string source = """
            namespace TestApp;

            public class NamedBase
            {
                public virtual string Title { get; set; }
            }

            public class NamedOverride : NamedBase
            {
                public override string Title { get; set; }

                public string Extra;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "NamedOverride.cs");
        var operation = new GenerateEqualsHashCodeOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateEqualsHashCodeParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "NamedOverride",
            IncludeInheritedMembers = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        AssertEqualityMembers(updated, "Extra", "Title");
        Assert.Equal(1, CountOccurrences(ExtractMethod(updated, "public override bool Equals(object?"), "other.Title"));
        Assert.Equal(1, CountOccurrences(ExtractMethod(updated, "public override int GetHashCode()"), "this.Title"));
    }

    [SkippableFact]
    public async Task GenerateEquals_IncludeInheritedMembersTrue_CallSuperTrue_CombinesBoth()
    {
        const string entity = """
            namespace TestApp;

            public class Entity
            {
                public int Id;

                public override bool Equals(object? obj) => obj is Entity other && Id == other.Id;

                public override int GetHashCode() => Id.GetHashCode();
            }
            """;
        const string derived = """
            namespace TestApp;

            public class Person : Entity
            {
                public string Name;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(("Person.cs", derived), ("Entity.cs", entity));
        var operation = new GenerateEqualsHashCodeOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateEqualsHashCodeParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Person",
            IncludeInheritedMembers = true,
            CallSuper = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var objectEquals = ExtractMethod(updated, "public override bool Equals(object?");
        Assert.Contains("base.Equals(other)", objectEquals);
        Assert.Contains("other.Name", objectEquals);
        Assert.Contains("other.Id", objectEquals);
        var getHashCode = ExtractMethod(updated, "public override int GetHashCode()");
        Assert.Contains("base.GetHashCode()", getHashCode);
        Assert.Contains("this.Name", getHashCode);
        Assert.Contains("this.Id", getHashCode);
    }

    #endregion

    #region Helpers

    private static void AssertNoBaseEqualityCalls(string text)
    {
        Assert.DoesNotContain("base.Equals", text);
        Assert.DoesNotContain("base.GetHashCode", text);
    }

    private static void AssertEqualityMembers(string text, params string[] members)
    {
        var objectEquals = ExtractMethod(text, "public override bool Equals(object?");
        var getHashCode = ExtractMethod(text, "public override int GetHashCode()");
        foreach (var member in members)
        {
            Assert.Contains($"other.{member}", objectEquals);
            Assert.Contains($"this.{member}", getHashCode);
        }
    }

    private static void AssertMemberNotCompared(string text, string member)
    {
        var objectEquals = ExtractMethod(text, "public override bool Equals(object?");
        var getHashCode = ExtractMethod(text, "public override int GetHashCode()");
        Assert.DoesNotContain($"other.{member}", objectEquals);
        Assert.DoesNotContain($"this.{member}", getHashCode);
    }

    private static void AssertHashCodeCombine(string text, params string[] members)
    {
        var getHashCode = ExtractMethod(text, "public override int GetHashCode()");
        Assert.Contains("global::System.HashCode.Combine(", getHashCode);
        foreach (var member in members)
            Assert.Contains($"this.{member}", getHashCode);
        Assert.DoesNotContain("new global::System.HashCode()", getHashCode);
        Assert.DoesNotContain("ToHashCode()", getHashCode);
    }

    private static void AssertPrimeMultiply(string text)
    {
        var getHashCode = text.Contains("public override int GetHashCode()", StringComparison.Ordinal)
            ? ExtractMethod(text, "public override int GetHashCode()")
            : text;
        Assert.Contains("unchecked", getHashCode);
        Assert.Contains("int hash = 17", getHashCode);
        Assert.Contains("(hash * 31)", getHashCode);
    }

    private static void AssertNoPrimeMultiply(string text)
    {
        Assert.DoesNotContain("unchecked", text);
        Assert.DoesNotContain("int hash = 17", text);
        Assert.DoesNotContain("(hash * 31)", text);
    }

    private static void AssertNoHashCodeCombineOrBuilder(string text)
    {
        Assert.DoesNotContain("HashCode.Combine", text);
        Assert.DoesNotContain("new HashCode()", text);
        Assert.DoesNotContain("new global::System.HashCode()", text);
        Assert.DoesNotContain("ToHashCode()", text);
        Assert.DoesNotContain("hash.Add(", text);
    }

    private static void AssertDefaultEqualsShape(string text)
    {
        Assert.DoesNotContain("IEquatable", text);
        Assert.DoesNotContain("public bool Equals(Person", text);
        var objectEquals = ExtractMethod(text, "public override bool Equals(object?");
        Assert.Contains("obj is Person other", objectEquals);
        Assert.DoesNotContain("Equals(other)", objectEquals);
        Assert.DoesNotContain("base.Equals", objectEquals);
        Assert.Contains("public override int GetHashCode()", text);
        Assert.DoesNotContain("base.GetHashCode", text);
    }

    private static void AssertIEquatableClassShape(string text)
    {
        Assert.Contains("global::System.IEquatable<Person>", text);
        Assert.Contains("public bool Equals(Person? other)", text);
        AssertObjectEqualsDelegates(text, "Person");
        Assert.Contains("public override int GetHashCode()", text);

        var typedEquals = ExtractMethod(text, "public bool Equals(Person? other)");
        Assert.Contains("other", typedEquals);
        Assert.Contains("Name", typedEquals);
        Assert.Contains("Age", typedEquals);
    }

    private static void AssertNoOperators(string text)
    {
        Assert.DoesNotContain("operator ==", text);
        Assert.DoesNotContain("operator !=", text);
    }

    private static void AssertEqualityOperators(string text, string parameterType, bool nullableParameters)
    {
        var equalitySignature = $"public static bool operator ==({parameterType} left, {parameterType} right)";
        var inequalitySignature = $"public static bool operator !=({parameterType} left, {parameterType} right)";
        Assert.Contains(equalitySignature, text);
        Assert.Contains(inequalitySignature, text);

        if (!nullableParameters)
        {
            Assert.DoesNotContain($"operator ==({parameterType}?", text);
            Assert.DoesNotContain($"operator !=({parameterType}?", text);
        }

        var equality = ExtractMethod(text, "public static bool operator ==");
        Assert.Contains("global::System.Object.Equals(left, right)", equality);

        var inequality = ExtractMethod(text, "public static bool operator !=");
        Assert.Contains("!(left == right)", inequality);
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

    private static void AssertObjectEqualsDelegates(string text, string typeName)
    {
        var objectEquals = ExtractMethod(text, "public override bool Equals(object?");
        Assert.Contains($"obj is {typeName} other", objectEquals);
        Assert.Contains("Equals(other)", objectEquals);
        Assert.DoesNotContain("other.Name", objectEquals);
        Assert.DoesNotContain("other.Age", objectEquals);
        Assert.DoesNotContain("other.X", objectEquals);
    }

    private static string ExtractMethod(string text, string signature)
    {
        var start = text.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Generated source did not contain '{signature}':\n{text}");
        var open = text.IndexOf('{', start);
        Assert.True(open >= 0, $"Generated method '{signature}' had no body:\n{text}");
        var depth = 0;
        for (var i = open; i < text.Length; i++)
        {
            if (text[i] == '{')
                depth++;
            else if (text[i] == '}')
            {
                depth--;
                if (depth == 0)
                    return text[start..(i + 1)];
            }
        }

        return text[start..];
    }

    private static string AbsoluteTestPath() =>
        Path.Combine(Path.GetTempPath(), "RoslynMcpGenerateEqualsMissing.cs");

    private static IReadOnlyList<ClassDeclarationSyntax> GetTypes(string source, string name) =>
        CSharpSyntaxTree.ParseText(source).GetRoot()
            .DescendantNodes()
            .OfType<ClassDeclarationSyntax>()
            .Where(t => t.Identifier.Text == name)
            .ToList();

    private static bool TypeHasMethod(ClassDeclarationSyntax type, string methodName) =>
        type.Members.OfType<MethodDeclarationSyntax>()
            .Any(m => m.Identifier.Text == methodName);

    private static string OwnEquals(ClassDeclarationSyntax type) =>
        NormalizeNewlines(type.Members.OfType<MethodDeclarationSyntax>()
            .Single(m => m.Identifier.Text == "Equals")
            .ToFullString());

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
        public required WorkspaceContext Context { get; init; }

        public string PathFor(string fileName) => Path.Combine(DirectoryPath, fileName);

        public static Task<TempWorkspace> CreateAsync(string source, string fileName = "Person.cs") =>
            CreateAsync((fileName, source));

        public static async Task<TempWorkspace> CreateAsync(params (string FileName, string Source)[] files)
        {
            Skip.IfNot(ModuleInitializer.MsBuildAvailable, ModuleInitializer.MsBuildError ?? "MSBuild not available");

            var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpGenerateEquals_" + Guid.NewGuid().ToString("N"));
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

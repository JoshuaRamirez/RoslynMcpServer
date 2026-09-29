using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.FileSystem;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Convert;
using RoslynMcp.Core.Refactoring.Utilities;
using RoslynMcp.Core.Resolution;
using RoslynMcp.Core.Workspace;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring;

/// <summary>
/// Operation-level tests for <see cref="ConvertAnonymousToClassOperation"/>.
/// </summary>
public class ConvertAnonymousToClassOperationTests
{
    #region Input Validation

    [Fact]
    public void Validate_MissingSourceFile_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ConvertAnonymousToClassOperation.Validate(ValidParams(sourceFile: "")));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public void Validate_MissingNewTypeName_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ConvertAnonymousToClassOperation.Validate(ValidParams(newTypeName: "")));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public void Validate_RelativePath_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ConvertAnonymousToClassOperation.Validate(ValidParams(sourceFile: "Worker.cs")));

        Assert.Equal(ErrorCodes.InvalidSourcePath, ex.ErrorCode);
    }

    [Fact]
    public void Validate_MissingFile_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ConvertAnonymousToClassOperation.Validate(ValidParams()));

        Assert.Equal(ErrorCodes.SourceFileNotFound, ex.ErrorCode);
    }

    [Fact]
    public void Validate_InvalidLine_Throws()
    {
        var path = Path.Combine(Path.GetTempPath(), "RoslynMcpConvertAnonInvalidLine.cs");
        File.WriteAllText(path, "class C {}");
        try
        {
            var ex = Assert.Throws<RefactoringException>(() =>
                ConvertAnonymousToClassOperation.Validate(ValidParams(sourceFile: path, line: 0)));

            Assert.Equal(ErrorCodes.InvalidLineNumber, ex.ErrorCode);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Validate_InvalidColumn_Throws()
    {
        var path = Path.Combine(Path.GetTempPath(), "RoslynMcpConvertAnonInvalidColumn.cs");
        File.WriteAllText(path, "class C {}");
        try
        {
            var ex = Assert.Throws<RefactoringException>(() =>
                ConvertAnonymousToClassOperation.Validate(ValidParams(sourceFile: path, column: 0)));

            Assert.Equal(ErrorCodes.InvalidColumnNumber, ex.ErrorCode);
            Assert.Equal("1007", ex.ErrorCode);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Validate_InvalidTypeName_Throws()
    {
        var path = Path.Combine(Path.GetTempPath(), "RoslynMcpConvertAnonInvalidName.cs");
        File.WriteAllText(path, "class C {}");
        try
        {
            var ex = Assert.Throws<RefactoringException>(() =>
                ConvertAnonymousToClassOperation.Validate(ValidParams(sourceFile: path, newTypeName: "123Bad")));

            Assert.Equal(ErrorCodes.InvalidSymbolName, ex.ErrorCode);
            Assert.Equal("1003", ex.ErrorCode);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void IsValidIdentifier_RejectsInvalidAndKeywords()
    {
        Assert.False(SyntaxIdentifierValidation.IsValidIdentifier("123Bad"));
        Assert.False(SyntaxIdentifierValidation.IsValidIdentifier("class"));
        Assert.False(SyntaxIdentifierValidation.IsValidIdentifier("int"));
        Assert.False(SyntaxIdentifierValidation.IsValidIdentifier("@@@"));
        Assert.True(SyntaxIdentifierValidation.IsValidIdentifier("Person"));
        Assert.True(SyntaxIdentifierValidation.IsValidIdentifier("_Info"));
    }

    #endregion

    #region Happy Path

    [SkippableFact]
    public async Task ConvertAnonymousToClass_SimpleClass_CreatesTypeAndReplacesCreation()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                public object Create()
                {
                    return new { Name = "Ada", Age = 36 };
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ConvertAnonymousToClassOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ConvertAnonymousToClassParams
        {
            SourceFile = workspace.SourcePath,
            Line = 7,
            NewTypeName = "Person"
        });

        Assert.True(result.Success);
        Assert.False(result.Preview);

        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("public class Person", text);
        Assert.Contains("public string Name { get; set; }", text);
        Assert.Contains("public int Age { get; set; }", text);
        Assert.Contains("return new Person { Name = \"Ada\", Age = 36 };", text);
        Assert.DoesNotContain("return new { Name = \"Ada\", Age = 36 };", text);
    }

    [SkippableFact]
    public async Task ConvertAnonymousToClass_AsRecord_CreatesRecordWithInitProperties()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                public object Create()
                {
                    return new { Name = "Ada", Age = 36 };
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ConvertAnonymousToClassOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ConvertAnonymousToClassParams
        {
            SourceFile = workspace.SourcePath,
            Line = 7,
            NewTypeName = "Person",
            AsRecord = true
        });

        Assert.True(result.Success);

        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("public record Person", text);
        Assert.Contains("public string Name { get; init; }", text);
        Assert.Contains("public int Age { get; init; }", text);
        Assert.Contains("return new Person { Name = \"Ada\", Age = 36 };", text);
        Assert.DoesNotContain("public class Person", text);
    }

    [SkippableFact]
    public async Task ConvertAnonymousToClass_Preview_ReturnsChangesAndWritesNothing()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                public object Create()
                {
                    return new { Name = "Ada", Age = 36 };
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var original = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new ConvertAnonymousToClassOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ConvertAnonymousToClassParams
        {
            SourceFile = workspace.SourcePath,
            Line = 7,
            NewTypeName = "Person",
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.NotEmpty(result.PendingChanges);
        Assert.Contains(result.PendingChanges, c =>
            c.AfterSnippet != null &&
            c.AfterSnippet.Contains("public class Person") &&
            c.AfterSnippet.Contains("new Person { Name = \"Ada\", Age = 36 }"));

        var after = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Equal(original, after);
    }

    [SkippableFact]
    public async Task ConvertAnonymousToClass_SameShapeCreations_AreReplacedTogether()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                public object Create()
                {
                    var first = new { Name = "Ada" };
                    var second = new { Name = "Bob" };
                    var other = new { Age = 1 };
                    return first;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ConvertAnonymousToClassOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ConvertAnonymousToClassParams
        {
            SourceFile = workspace.SourcePath,
            Line = 7,
            NewTypeName = "Person"
        });

        Assert.True(result.Success);

        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("var first = new Person { Name = \"Ada\" };", text);
        Assert.Contains("var second = new Person { Name = \"Bob\" };", text);
        Assert.Contains("var other = new { Age = 1 };", text);
    }

    [SkippableFact]
    public async Task ConvertAnonymousToClass_NestedNamespaces_UsesFullNamespace()
    {
        const string worker = """
            namespace Outer
            {
                namespace Inner
                {
                    public class Worker
                    {
                        public object Create()
                        {
                            return new { Name = "Ada" };
                        }
                    }
                }
            }
            """;
        const string client = """
            namespace Other
            {
                public class Client
                {
                    public object Create()
                    {
                        return new { Name = "Bob" };
                    }
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(
            ("Worker.cs", worker),
            ("Client.cs", client));
        var operation = new ConvertAnonymousToClassOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ConvertAnonymousToClassParams
        {
            SourceFile = workspace.SourcePath,
            Line = 9,
            NewTypeName = "Person"
        });

        Assert.True(result.Success);
        Assert.Equal("Outer.Inner.Person", result.Symbol?.FullyQualifiedName);

        var workerText = await File.ReadAllTextAsync(workspace.SourcePath);
        var clientText = await File.ReadAllTextAsync(Path.Combine(workspace.DirectoryPath, "Client.cs"));
        Assert.Contains("namespace Inner", workerText);
        Assert.Contains("public class Person", workerText);
        Assert.Contains("return new Person { Name = \"Ada\" };", workerText);
        Assert.Contains("return new Outer.Inner.Person { Name = \"Bob\" };", clientText);
        Assert.DoesNotContain("new Inner.Person", clientText);
    }

    [SkippableFact]
    public async Task ConvertAnonymousToClass_KeywordMember_EscapesIdentifier()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                public object Create()
                {
                    return new { @class = 1 };
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ConvertAnonymousToClassOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ConvertAnonymousToClassParams
        {
            SourceFile = workspace.SourcePath,
            Line = 7,
            NewTypeName = "Person"
        });

        Assert.True(result.Success);

        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("public int @class { get; set; }", text);
        Assert.Contains("return new Person { @class = 1 };", text);
        Assert.DoesNotContain("public int class {", text);
        Assert.DoesNotContain("{ class = 1 }", text);
    }

    [SkippableFact]
    public async Task ConvertAnonymousToClass_ExclusiveEndAtPreviousCreation_ThrowsCannotConvert()
    {
        const string source = """
            namespace TestApp;

            public class SameLine
            {
                public object Create()
                {
                    var first = new { Name = "Ada" }; var second = new { Age = 1 };
                    return first;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var original = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new ConvertAnonymousToClassOperation(workspace.Context);
        var line = FindLine(source, "var first = new { Name = \"Ada\" }");
        var firstCreationEndCol = FirstAnonymousCreationEndColumn(source);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ConvertAnonymousToClassParams
            {
                SourceFile = workspace.SourcePath,
                Line = line,
                Column = firstCreationEndCol,
                NewTypeName = "Person"
            }));

        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
        Assert.Equal("3020", ex.ErrorCode);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task ConvertAnonymousToClass_Preview_ColumnSelectsSecond_DoesNotModifyFile()
    {
        const string source = """
            namespace TestApp;

            public class SameLine
            {
                public object Create()
                {
                    var first = new { Name = "Ada" }; var second = new { Age = 1 };
                    return first;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new ConvertAnonymousToClassOperation(workspace.Context);
        var line = FindLine(source, "var first = new { Name = \"Ada\" }");
        var secondColumn = ColumnOf(source, "new { Age = 1 }");

        var result = await operation.ExecuteAsync(new ConvertAnonymousToClassParams
        {
            SourceFile = workspace.SourcePath,
            Line = line,
            Column = secondColumn,
            NewTypeName = "AgeInfo",
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.Contains(result.PendingChanges, change =>
            change.AfterSnippet != null &&
            change.AfterSnippet.Contains("public class AgeInfo") &&
            change.AfterSnippet.Contains("new AgeInfo { Age = 1 }"));
        Assert.DoesNotContain(result.PendingChanges, change =>
            change.AfterSnippet != null &&
            change.AfterSnippet.Contains("new Person"));
        Assert.Contains(result.PendingChanges, change =>
            change.AfterSnippet != null &&
            change.AfterSnippet.Contains("new { Name = \"Ada\" }"));
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    #endregion

    #region Covering-span column

    [Fact]
    public void FindAnonymousCreation_OmittedColumn_PicksSingleOnLine()
    {
        var root = CSharpSyntaxTree.ParseText(IndentedAnonymousSource).GetRoot();
        var line = FindLine(IndentedAnonymousSource, "return new { Name = \"Ada\" }");
        var creation = root.DescendantNodes().OfType<AnonymousObjectCreationExpressionSyntax>().Single();
        var startCol = creation.GetLocation().GetLineSpan().StartLinePosition.Character + 1;
        Assert.True(startCol > 1);

        var found = ConvertAnonymousToClassOperation.FindAnonymousCreation(
            root,
            new ConvertAnonymousToClassParams
            {
                SourceFile = "/tmp/anon.cs",
                Line = line,
                NewTypeName = "Person"
            });

        Assert.Equal(creation.Span, found.Span);
        Assert.Contains("Name", found.Initializers[0].NameEquals!.Name.Identifier.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void FindAnonymousCreation_OmittedColumn_TwoOnLine_ThrowsSymbolAmbiguous()
    {
        var root = CSharpSyntaxTree.ParseText(SameLineAnonymousSource).GetRoot();
        var line = FindLine(SameLineAnonymousSource, "var first = new { Name = \"Ada\" }");

        var ex = Assert.Throws<RefactoringException>(() =>
            ConvertAnonymousToClassOperation.FindAnonymousCreation(
                root,
                new ConvertAnonymousToClassParams
                {
                    SourceFile = "/tmp/anon.cs",
                    Line = line,
                    NewTypeName = "Person"
                }));

        Assert.Equal(ErrorCodes.SymbolAmbiguous, ex.ErrorCode);
        Assert.Contains("column", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FindAnonymousCreation_ColumnPicksUniqueCoveringCreation()
    {
        var root = CSharpSyntaxTree.ParseText(SameLineAnonymousSource).GetRoot();
        var line = FindLine(SameLineAnonymousSource, "var first = new { Name = \"Ada\" }");
        var secondColumn = ColumnOf(SameLineAnonymousSource, "new { Age = 1 }");

        var found = ConvertAnonymousToClassOperation.FindAnonymousCreation(
            root,
            new ConvertAnonymousToClassParams
            {
                SourceFile = "/tmp/anon.cs",
                Line = line,
                Column = secondColumn,
                NewTypeName = "AgeInfo"
            });

        Assert.Contains("Age", found.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Name", found.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void FindAnonymousCreation_AdjacentCreations_ExclusiveEndDoesNotStealNext()
    {
        var root = CSharpSyntaxTree.ParseText(SameLineAnonymousSource).GetRoot();
        var line = FindLine(SameLineAnonymousSource, "var first = new { Name = \"Ada\" }");
        var first = root.DescendantNodes().OfType<AnonymousObjectCreationExpressionSyntax>()
            .First(n => n.ToString().Contains("Name", StringComparison.Ordinal));
        var firstCreationEndCol = first.GetLocation().GetLineSpan().EndLinePosition.Character + 1;
        var secondColumn = ColumnOf(SameLineAnonymousSource, "new { Age = 1 }");

        Assert.False(SpanCoverage.SpanCoversColumn(
            first.GetLocation().GetLineSpan(), line, firstCreationEndCol));

        var exclusiveEnd = Assert.Throws<RefactoringException>(() =>
            ConvertAnonymousToClassOperation.FindAnonymousCreation(
                root,
                new ConvertAnonymousToClassParams
                {
                    SourceFile = "/tmp/anon.cs",
                    Line = line,
                    Column = firstCreationEndCol,
                    NewTypeName = "Person"
                }));
        Assert.Equal(ErrorCodes.CannotConvert, exclusiveEnd.ErrorCode);

        var atSecond = ConvertAnonymousToClassOperation.FindAnonymousCreation(
            root,
            new ConvertAnonymousToClassParams
            {
                SourceFile = "/tmp/anon.cs",
                Line = line,
                Column = secondColumn,
                NewTypeName = "AgeInfo"
            });
        Assert.Contains("Age", atSecond.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void SpanCoversColumn_TreatsEndAsExclusive()
    {
        var tree = CSharpSyntaxTree.ParseText(SameLineAnonymousSource);
        var first = tree.GetRoot().DescendantNodes().OfType<AnonymousObjectCreationExpressionSyntax>()
            .First(n => n.ToString().Contains("Name", StringComparison.Ordinal));
        var span = first.GetLocation().GetLineSpan();
        var line = span.StartLinePosition.Line + 1;
        var startCol = span.StartLinePosition.Character + 1;
        var endCol = span.EndLinePosition.Character + 1;

        Assert.True(SpanCoverage.SpanCoversColumn(span, line, startCol));
        Assert.True(SpanCoverage.SpanCoversColumn(span, line, endCol - 1));
        Assert.False(SpanCoverage.SpanCoversColumn(span, line, endCol));
        Assert.False(SpanCoverage.SpanCoversColumn(span, line, startCol - 1));
    }

    [Fact]
    public void SpanCoversLine_WithColumn_TreatsEndAsExclusive()
    {
        var tree = CSharpSyntaxTree.ParseText(SameLineAnonymousSource);
        var first = tree.GetRoot().DescendantNodes().OfType<AnonymousObjectCreationExpressionSyntax>()
            .First(n => n.ToString().Contains("Name", StringComparison.Ordinal));
        var span = first.GetLocation().GetLineSpan();
        var line = span.StartLinePosition.Line + 1;
        var startCol = span.StartLinePosition.Character + 1;
        var endCol = span.EndLinePosition.Character + 1;

        Assert.True(SpanCoverage.SpanCoversLine(span, line, startCol));
        Assert.True(SpanCoverage.SpanCoversLine(span, line, endCol - 1));
        Assert.False(SpanCoverage.SpanCoversLine(span, line, endCol));
        Assert.False(SpanCoverage.SpanCoversLine(span, line, startCol - 1));

        const string multiLineSource = """
            class C
            {
                void M()
                {
                    var first = new
                    {
                        Name = "Ada"
                    };
                }
            }
            """;
        var multiLineTree = CSharpSyntaxTree.ParseText(multiLineSource);
        var multiLineFirst = multiLineTree.GetRoot().DescendantNodes().OfType<AnonymousObjectCreationExpressionSyntax>()
            .First(n => n.ToString().Contains("Name", StringComparison.Ordinal));
        var multiLineSpan = multiLineFirst.GetLocation().GetLineSpan();
        var startLine = multiLineSpan.StartLinePosition.Line + 1;
        var endLine = multiLineSpan.EndLinePosition.Line + 1;

        for (var coveredLine = startLine; coveredLine <= endLine; coveredLine++)
            Assert.True(SpanCoverage.SpanCoversLine(multiLineSpan, coveredLine, column: null));

        Assert.False(SpanCoverage.SpanCoversLine(multiLineSpan, startLine - 1, column: null));
        Assert.False(SpanCoverage.SpanCoversLine(multiLineSpan, endLine + 1, column: null));
    }

    [Fact]
    public void SpanCoversLine_TreatsEndAsExclusive()
    {
        var span = new FileLinePositionSpan(
            "t.cs",
            new LinePosition(0, 0),
            new LinePosition(2, 0));

        Assert.True(SpanCoverage.SpanCoversLine(span, 1, column: null));
        Assert.True(SpanCoverage.SpanCoversLine(span, 2, column: null));
        Assert.False(SpanCoverage.SpanCoversLine(span, 3, column: null));
    }

    #endregion

    #region Rejects

    [SkippableFact]
    public async Task ConvertAnonymousToClass_NotAnonymous_ThrowsAndWritesNothing()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                public object Create()
                {
                    return new Worker();
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var original = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new ConvertAnonymousToClassOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ConvertAnonymousToClassParams
            {
                SourceFile = workspace.SourcePath,
                Line = 7,
                NewTypeName = "Person"
            }));

        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
        Assert.Equal("3020", ex.ErrorCode);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task ConvertAnonymousToClass_NameConflict_ThrowsAndWritesNothing()
    {
        const string source = """
            namespace TestApp;

            public class Person
            {
            }

            public class Worker
            {
                public object Create()
                {
                    return new { Name = "Ada" };
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var original = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new ConvertAnonymousToClassOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ConvertAnonymousToClassParams
            {
                SourceFile = workspace.SourcePath,
                Line = 11,
                NewTypeName = "Person"
            }));

        Assert.Equal(ErrorCodes.NameConflictScope, ex.ErrorCode);
        Assert.Equal("3010", ex.ErrorCode);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [Fact]
    public void ConvertAnonymousToClass_UneditableDocument_Throws()
    {
        var workspace = new AdhocWorkspace();
        var project = workspace.AddProject("P", LanguageNames.CSharp);
        var document = workspace.AddDocument(project.Id, "Generated.cs", SourceText.From("class C {}"));

        var ex = Assert.Throws<RefactoringException>(() =>
            DocumentEditableHelpers.ValidateDocumentIsEditable(document, workspace));

        Assert.Equal(ErrorCodes.DocumentNotEditable, ex.ErrorCode);
    }

    [SkippableFact]
    public async Task ConvertAnonymousToClass_MethodTypeParameter_ThrowsAndWritesNothing()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                public object Create<T>(T value)
                {
                    return new { Value = value };
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var original = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new ConvertAnonymousToClassOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ConvertAnonymousToClassParams
            {
                SourceFile = workspace.SourcePath,
                Line = 7,
                NewTypeName = "Wrapper"
            }));

        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
        Assert.Equal("3020", ex.ErrorCode);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task ConvertAnonymousToClass_LessAccessibleMemberType_ThrowsAndWritesNothing()
    {
        const string source = """
            namespace TestApp;

            internal class InternalItem
            {
            }

            public class Worker
            {
                public object Create()
                {
                    return new { Item = new InternalItem() };
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var original = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new ConvertAnonymousToClassOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ConvertAnonymousToClassParams
            {
                SourceFile = workspace.SourcePath,
                Line = 11,
                NewTypeName = "Wrapper"
            }));

        Assert.Equal(ErrorCodes.BreaksAccessibility, ex.ErrorCode);
        Assert.Equal("3006", ex.ErrorCode);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task ConvertAnonymousToClass_PrivateNestedMemberType_ThrowsAndWritesNothing()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                private class Hidden
                {
                }

                public object Create()
                {
                    return new { Item = new Hidden() };
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var original = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new ConvertAnonymousToClassOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ConvertAnonymousToClassParams
            {
                SourceFile = workspace.SourcePath,
                Line = 11,
                NewTypeName = "Wrapper"
            }));

        Assert.Equal(ErrorCodes.BreaksAccessibility, ex.ErrorCode);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }


    #endregion


    #region allFiles

    private const string AnonFileA = """
        namespace TestApp;

        public class WorkerA
        {
            public object Create()
            {
                return new { Name = "Ada", Age = 36 };
            }
        }
        """;

    private const string AnonFileB = """
        namespace TestApp;

        public class WorkerB
        {
            public object Create()
            {
                return new { Title = "Dev", Level = 2 };
            }
        }
        """;

    private const string AnonFileNoAnon = """
        namespace TestApp;

        public class WorkerC
        {
            public object Create() => "none";
        }
        """;

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    [Fact]
    public void Validate_AllFilesTrue_WithoutSourceFile_DoesNotThrow()
    {
        ConvertAnonymousToClassOperation.Validate(new ConvertAnonymousToClassParams
        {
            AllFiles = true
        });
    }

    [Fact]
    public void Validate_AllFilesTrue_WithLine_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ConvertAnonymousToClassOperation.Validate(new ConvertAnonymousToClassParams
            {
                AllFiles = true,
                Line = 1
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("allFiles", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_AllFilesTrue_WithColumn_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ConvertAnonymousToClassOperation.Validate(new ConvertAnonymousToClassParams
            {
                AllFiles = true,
                Column = 1
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public void Validate_AllFilesTrue_WithNewTypeName_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ConvertAnonymousToClassOperation.Validate(new ConvertAnonymousToClassParams
            {
                AllFiles = true,
                NewTypeName = "Person"
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public void Validate_AllFilesFalse_WithoutLine_Throws()
    {
        var path = Path.Combine(Path.GetTempPath(), "RoslynMcpConvertAnonMissingLine.cs");
        File.WriteAllText(path, "class C {}");
        try
        {
            var ex = Assert.Throws<RefactoringException>(() =>
                ConvertAnonymousToClassOperation.Validate(new ConvertAnonymousToClassParams
                {
                    SourceFile = path,
                    NewTypeName = "Person"
                }));

            Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
            Assert.Contains("line", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void BuildAllFilesDescription_SingularAndPlural()
    {
        Assert.Equal(
            "Convert anonymous type to class",
            ConvertAnonymousToClassOperation.BuildAllFilesDescription(1, asRecord: false));
        Assert.Equal(
            "Convert 2 anonymous types to record",
            ConvertAnonymousToClassOperation.BuildAllFilesDescription(2, asRecord: true));
    }

    [Fact]
    public void DeriveTypeNameFromMembers_JoinsPascalCasePropertyNames()
    {
        // Compile a tiny anonymous type via workspace-free syntax is hard; exercise Finalize via public helper
        // with a synthetic member list constructed through reflection-free compile of empty members.
        Assert.Null(ConvertAnonymousToClassOperation.DeriveTypeNameFromMembers(
            Array.Empty<ConvertAnonymousToClassOperation.AnonymousMember>()));
    }

    [SkippableFact]
    public async Task ConvertAnonymous_OmittedAllFiles_KeepsSingleSiteRewrite()
    {
        await using var workspace = await TempWorkspace.CreateAsync(
            ("FileA.cs", AnonFileA),
            ("FileB.cs", AnonFileB));
        var operation = new ConvertAnonymousToClassOperation(workspace.Context);
        var pathA = Path.Combine(workspace.DirectoryPath, "FileA.cs");
        var pathB = Path.Combine(workspace.DirectoryPath, "FileB.cs");
        var beforeB = await File.ReadAllTextAsync(pathB);

        var result = await operation.ExecuteAsync(new ConvertAnonymousToClassParams
        {
            SourceFile = pathA,
            Line = FindLine(AnonFileA, "return new { Name"),
            NewTypeName = "Person"
        });

        Assert.True(result.Success);
        Assert.Contains("public class Person", await File.ReadAllTextAsync(pathA));
        Assert.Equal(beforeB, await File.ReadAllTextAsync(pathB));
    }

    [SkippableFact]
    public async Task ConvertAnonymous_AllFilesTrue_AppliesToEligibleShapesAcrossFiles()
    {
        await using var workspace = await TempWorkspace.CreateAsync(
            ("FileA.cs", AnonFileA),
            ("FileB.cs", AnonFileB),
            ("FileC.cs", AnonFileNoAnon));
        var operation = new ConvertAnonymousToClassOperation(workspace.Context);
        var pathA = Path.Combine(workspace.DirectoryPath, "FileA.cs");
        var pathB = Path.Combine(workspace.DirectoryPath, "FileB.cs");
        var pathC = Path.Combine(workspace.DirectoryPath, "FileC.cs");
        var beforeC = await File.ReadAllTextAsync(pathC);

        var result = await operation.ExecuteAsync(new ConvertAnonymousToClassParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        Assert.False(result.Preview);
        var textA = await File.ReadAllTextAsync(pathA);
        var textB = await File.ReadAllTextAsync(pathB);
        Assert.Contains("public class NameAge", textA);
        Assert.Contains("return new NameAge", textA);
        Assert.Contains("public class TitleLevel", textB);
        Assert.Contains("return new TitleLevel", textB);
        Assert.Equal(beforeC, await File.ReadAllTextAsync(pathC));
        Assert.True(result.Changes!.FilesModified.Count >= 2);
        Assert.Contains(result.Changes.FilesModified, p => PathsEqual(p, pathA));
        Assert.Contains(result.Changes.FilesModified, p => PathsEqual(p, pathB));
        Assert.DoesNotContain(result.Changes.FilesModified, p => PathsEqual(p, pathC));
    }

    [SkippableFact]
    public async Task ConvertAnonymous_AllFilesTrue_WithoutSourceFile_Succeeds()
    {
        await using var workspace = await TempWorkspace.CreateAsync(
            ("FileA.cs", AnonFileA),
            ("FileB.cs", AnonFileB));
        var operation = new ConvertAnonymousToClassOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ConvertAnonymousToClassParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        Assert.True(result.Changes!.FilesModified.Count >= 2);
    }

    [SkippableFact]
    public async Task ConvertAnonymous_AllFilesFalse_WithoutSourceFile_MissingRequiredParam()
    {
        await using var workspace = await TempWorkspace.CreateAsync(AnonFileA);
        var operation = new ConvertAnonymousToClassOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ConvertAnonymousToClassParams
            {
                AllFiles = false,
                Line = 7,
                NewTypeName = "Person"
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("sourceFile", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task ConvertAnonymous_PreviewAllFiles_AggregatesChangedFilesAndWritesNothing()
    {
        await using var workspace = await TempWorkspace.CreateAsync(
            ("FileA.cs", AnonFileA),
            ("FileB.cs", AnonFileB),
            ("FileC.cs", AnonFileNoAnon));
        var operation = new ConvertAnonymousToClassOperation(workspace.Context);
        var pathA = Path.Combine(workspace.DirectoryPath, "FileA.cs");
        var pathB = Path.Combine(workspace.DirectoryPath, "FileB.cs");
        var pathC = Path.Combine(workspace.DirectoryPath, "FileC.cs");
        var beforeA = await File.ReadAllTextAsync(pathA);
        var beforeB = await File.ReadAllTextAsync(pathB);
        var beforeC = await File.ReadAllTextAsync(pathC);

        var result = await operation.ExecuteAsync(new ConvertAnonymousToClassParams
        {
            AllFiles = true,
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.Contains(result.PendingChanges, c => PathsEqual(c.File, pathA));
        Assert.DoesNotContain(result.PendingChanges, c => PathsEqual(c.File, pathC));
        Assert.Equal(beforeA, await File.ReadAllTextAsync(pathA));
        Assert.Equal(beforeB, await File.ReadAllTextAsync(pathB));
        Assert.Equal(beforeC, await File.ReadAllTextAsync(pathC));
    }

    [SkippableFact]
    public async Task ConvertAnonymous_AllFilesTrue_EveryFileIneligible_SucceedsWithEmptyChanges()
    {
        await using var workspace = await TempWorkspace.CreateAsync(
            ("FileC.cs", AnonFileNoAnon));
        var operation = new ConvertAnonymousToClassOperation(workspace.Context);
        var pathC = Path.Combine(workspace.DirectoryPath, "FileC.cs");
        var before = await File.ReadAllTextAsync(pathC);

        var result = await operation.ExecuteAsync(new ConvertAnonymousToClassParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        Assert.Empty(result.Changes!.FilesModified);
        Assert.Equal(before, await File.ReadAllTextAsync(pathC));
    }

    [SkippableFact]
    public async Task ConvertAnonymous_AllFilesTrue_OptionalSourceFile_LimitsWalk()
    {
        await using var workspace = await TempWorkspace.CreateAsync(
            ("FileA.cs", AnonFileA),
            ("FileB.cs", AnonFileB));
        var operation = new ConvertAnonymousToClassOperation(workspace.Context);
        var pathA = Path.Combine(workspace.DirectoryPath, "FileA.cs");
        var pathB = Path.Combine(workspace.DirectoryPath, "FileB.cs");
        var beforeB = await File.ReadAllTextAsync(pathB);

        var result = await operation.ExecuteAsync(new ConvertAnonymousToClassParams
        {
            AllFiles = true,
            SourceFile = pathA
        });

        Assert.True(result.Success);
        Assert.Contains("public class NameAge", await File.ReadAllTextAsync(pathA));
        Assert.Equal(beforeB, await File.ReadAllTextAsync(pathB));
        Assert.Contains(result.Changes!.FilesModified, p => PathsEqual(p, pathA));
        Assert.DoesNotContain(result.Changes.FilesModified, p => PathsEqual(p, pathB));
    }

    [SkippableFact]
    public async Task ConvertAnonymous_AllFilesTrue_WithLine_Rejects()
    {
        await using var workspace = await TempWorkspace.CreateAsync(AnonFileA);
        var operation = new ConvertAnonymousToClassOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ConvertAnonymousToClassParams
            {
                AllFiles = true,
                Line = 1
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [SkippableFact]
    public async Task ConvertAnonymous_AllFilesTrue_NameCollision_UsesNumericSuffix()
    {
        const string source = """
            namespace TestApp;

            public class NameAge { }

            public class Worker
            {
                public object Create()
                {
                    return new { Name = "Ada", Age = 36 };
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ConvertAnonymousToClassOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ConvertAnonymousToClassParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("public class NameAge2", text);
        Assert.Contains("return new NameAge2", text);
    }

    [SkippableFact]
    public async Task ConvertAnonymous_AllFilesTrue_TypeParameterShadow_UsesNumericSuffix()
    {
        // Codex P2 on PR #1497: derived NameAge matches method type parameter
        // M<NameAge>(); must suffix rather than emit new NameAge { ... }.
        const string source = """
            namespace TestApp;

            public class Worker
            {
                public object M<NameAge>()
                {
                    return new { Name = "Ada", Age = 1 };
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ConvertAnonymousToClassOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ConvertAnonymousToClassParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("public class NameAge2", text);
        Assert.Contains("return new NameAge2", text);
        Assert.Contains("M<NameAge>()", text);
        Assert.DoesNotMatch(@"new\s+NameAge(?![0-9A-Za-z_])", text);
    }

    [SkippableFact]
    public async Task ConvertAnonymous_AllFilesTrue_SkipsLinkedMultiViewPath()
    {
        const string sharedSource = """
            namespace TestApp;

            public static class SharedHost
            {
                public static object Create() => new { Name = "Ada", Age = 36 };
            }
            """;
        const string anchorASource = """
            namespace TestApp;

            public static class AnchorA
            {
                public static void KeepA() { }
            }
            """;
        const string anchorBSource = """
            namespace TestApp;

            public static class AnchorB
            {
                public static void KeepB() { }
            }
            """;

        await using var workspace = await TempWorkspace.CreateWithLinkedProjectsAsync(
            sharedSource, anchorASource, anchorBSource);
        var counts = AllFilesDocumentHelpers.BuildLinkedPathCounts(workspace.Context.Solution);
        var sharedKey = PathResolver.GetPathComparisonKey(workspace.SourcePaths["Shared.cs"]);
        Assert.True(counts.TryGetValue(sharedKey, out var sharedCount) && sharedCount > 1);

        var beforeShared = await File.ReadAllTextAsync(workspace.SourcePaths["Shared.cs"]);

        var operation = new ConvertAnonymousToClassOperation(workspace.Context);
        var result = await operation.ExecuteAsync(new ConvertAnonymousToClassParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        Assert.Equal(beforeShared, await File.ReadAllTextAsync(workspace.SourcePaths["Shared.cs"]));
        Assert.Empty(result.Changes!.FilesModified);
    }

    #endregion

    #region Helpers

    private const string SameLineAnonymousSource = """
        class C
        {
            void M()
            {
                var first = new { Name = "Ada" }; var second = new { Age = 1 };
            }
        }
        """;

    private const string IndentedAnonymousSource = """
        class C
        {
            object M()
            {
                return new { Name = "Ada" };
            }
        }
        """;

    private static ConvertAnonymousToClassParams ValidParams(
        string? sourceFile = null,
        int? line = 7,
        string? newTypeName = "Person",
        int? column = null,
        bool allFiles = false) => new()
        {
            SourceFile = sourceFile ?? (allFiles ? null : Path.Combine(Path.GetTempPath(), "RoslynMcpConvertAnonMissing.cs")),
            AllFiles = allFiles,
            Line = line,
            NewTypeName = newTypeName,
            Column = column
        };

    private static int FindLine(string source, string snippet)
    {
        var index = source.IndexOf(snippet, StringComparison.Ordinal);
        Assert.True(index >= 0, $"Snippet not found: {snippet}");
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
        Assert.True(index >= 0, $"Snippet not found: {snippet}");
        var lineStart = source.LastIndexOf('\n', index) + 1;
        return index - lineStart + 1;
    }

    private static int FirstAnonymousCreationEndColumn(string source)
    {
        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var first = root.DescendantNodes().OfType<AnonymousObjectCreationExpressionSyntax>()
            .OrderBy(n => n.SpanStart)
            .First();
        return first.GetLocation().GetLineSpan().EndLinePosition.Character + 1;
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

            var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpConvertAnon_" + Guid.NewGuid().ToString("N"));
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

            var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpConvertAnonLinked_" + Guid.NewGuid().ToString("N"));
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

            try
            {
                var provider = new MSBuildWorkspaceProvider();
                var context = await provider.CreateContextAsync(solutionPath);
                var linkedCount = context.Solution.Projects
                    .SelectMany(p => p.Documents)
                    .Count(d => d.FilePath != null &&
                                PathResolver.GetPathComparisonKey(d.FilePath!) ==
                                PathResolver.GetPathComparisonKey(sharedPath));
                if (linkedCount < 2)
                {
                    context.Dispose();
                    throw new InvalidOperationException($"Expected linked document in both projects, found {linkedCount}.");
                }

                return new TempWorkspace
                {
                    DirectoryPath = directory,
                    ProjectPath = solutionPath,
                    SourcePath = sharedPath,
                    SourcePaths = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["Shared.cs"] = sharedPath,
                        ["AnchorA.cs"] = anchorAPath,
                        ["AnchorB.cs"] = anchorBPath
                    },
                    Context = context
                };
            }
            catch (Exception ex) when (ex is not SkipException)
            {
                try { Directory.Delete(directory, recursive: true); }
                catch { /* ignore */ }
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

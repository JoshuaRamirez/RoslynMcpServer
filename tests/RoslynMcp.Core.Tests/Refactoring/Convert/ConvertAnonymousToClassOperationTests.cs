using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Convert;
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
    public void IsValidTypeName_RejectsInvalidAndKeywords()
    {
        Assert.False(ConvertAnonymousToClassOperation.IsValidTypeName("123Bad"));
        Assert.False(ConvertAnonymousToClassOperation.IsValidTypeName("class"));
        Assert.False(ConvertAnonymousToClassOperation.IsValidTypeName("int"));
        Assert.False(ConvertAnonymousToClassOperation.IsValidTypeName("@@@"));
        Assert.True(ConvertAnonymousToClassOperation.IsValidTypeName("Person"));
        Assert.True(ConvertAnonymousToClassOperation.IsValidTypeName("_Info"));
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

        Assert.False(ConvertAnonymousToClassOperation.SpanCoversColumn(
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

        Assert.True(ConvertAnonymousToClassOperation.SpanCoversColumn(span, line, startCol));
        Assert.True(ConvertAnonymousToClassOperation.SpanCoversColumn(span, line, endCol - 1));
        Assert.False(ConvertAnonymousToClassOperation.SpanCoversColumn(span, line, endCol));
        Assert.False(ConvertAnonymousToClassOperation.SpanCoversColumn(span, line, startCol - 1));
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

        Assert.True(ConvertAnonymousToClassOperation.SpanCoversLine(span, line, startCol));
        Assert.True(ConvertAnonymousToClassOperation.SpanCoversLine(span, line, endCol - 1));
        Assert.False(ConvertAnonymousToClassOperation.SpanCoversLine(span, line, endCol));
        Assert.False(ConvertAnonymousToClassOperation.SpanCoversLine(span, line, startCol - 1));

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
            Assert.True(ConvertAnonymousToClassOperation.SpanCoversLine(multiLineSpan, coveredLine, column: null));

        Assert.False(ConvertAnonymousToClassOperation.SpanCoversLine(multiLineSpan, startLine - 1, column: null));
        Assert.False(ConvertAnonymousToClassOperation.SpanCoversLine(multiLineSpan, endLine + 1, column: null));
    }

    [Fact]
    public void SpanCoversLine_TreatsEndAsExclusive()
    {
        var span = new FileLinePositionSpan(
            "t.cs",
            new LinePosition(0, 0),
            new LinePosition(2, 0));

        Assert.True(ConvertAnonymousToClassOperation.SpanCoversLine(span, 1, column: null));
        Assert.True(ConvertAnonymousToClassOperation.SpanCoversLine(span, 2, column: null));
        Assert.False(ConvertAnonymousToClassOperation.SpanCoversLine(span, 3, column: null));
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
            ConvertAnonymousToClassOperation.ValidateDocumentIsEditable(document, workspace));

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

    [Fact]
    public void GetFullNamespaceName_NestedDeclarations_JoinsEnclosingNames()
    {
        var tree = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText("""
            namespace Outer
            {
                namespace Inner
                {
                    class Worker { }
                }
            }
            """);
        var inner = tree.GetRoot().DescendantNodes()
            .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.NamespaceDeclarationSyntax>()
            .Last();

        Assert.Equal("Inner", inner.Name.ToString());
        Assert.Equal("Outer.Inner", ConvertAnonymousToClassOperation.GetFullNamespaceName(inner));
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
        int line = 7,
        string newTypeName = "Person",
        int? column = null) => new()
        {
            SourceFile = sourceFile ?? Path.Combine(Path.GetTempPath(), "RoslynMcpConvertAnonMissing.cs"),
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

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
/// Operation-level tests for <see cref="ConvertTupleToStructOperation"/>.
/// </summary>
public class ConvertTupleToStructOperationTests
{
    #region Input Validation

    [Fact]
    public void Validate_MissingSourceFile_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ConvertTupleToStructOperation.Validate(ValidParams(sourceFile: "")));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public void Validate_MissingNewTypeName_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ConvertTupleToStructOperation.Validate(ValidParams(newTypeName: "")));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public void Validate_RelativePath_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ConvertTupleToStructOperation.Validate(ValidParams(sourceFile: "Worker.cs")));

        Assert.Equal(ErrorCodes.InvalidSourcePath, ex.ErrorCode);
    }

    [Fact]
    public void Validate_MissingFile_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ConvertTupleToStructOperation.Validate(ValidParams()));

        Assert.Equal(ErrorCodes.SourceFileNotFound, ex.ErrorCode);
    }

    [Fact]
    public void Validate_InvalidLine_Throws()
    {
        var path = Path.Combine(Path.GetTempPath(), "RoslynMcpConvertTupleInvalidLine.cs");
        File.WriteAllText(path, "class C {}");
        try
        {
            var ex = Assert.Throws<RefactoringException>(() =>
                ConvertTupleToStructOperation.Validate(ValidParams(sourceFile: path, line: 0)));

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
        var path = Path.Combine(Path.GetTempPath(), "RoslynMcpConvertTupleInvalidColumn.cs");
        File.WriteAllText(path, "class C {}");
        try
        {
            var ex = Assert.Throws<RefactoringException>(() =>
                ConvertTupleToStructOperation.Validate(ValidParams(sourceFile: path, column: 0)));

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
        var path = Path.Combine(Path.GetTempPath(), "RoslynMcpConvertTupleInvalidName.cs");
        File.WriteAllText(path, "class C {}");
        try
        {
            var ex = Assert.Throws<RefactoringException>(() =>
                ConvertTupleToStructOperation.Validate(ValidParams(sourceFile: path, newTypeName: "123Bad")));

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
        Assert.False(ConvertTupleToStructOperation.IsValidTypeName("123Bad"));
        Assert.False(ConvertTupleToStructOperation.IsValidTypeName("class"));
        Assert.False(ConvertTupleToStructOperation.IsValidTypeName("int"));
        Assert.False(ConvertTupleToStructOperation.IsValidTypeName("@@@"));
        Assert.True(ConvertTupleToStructOperation.IsValidTypeName("Point"));
        Assert.True(ConvertTupleToStructOperation.IsValidTypeName("_Info"));
    }

    #endregion

    #region Happy Path

    [SkippableFact]
    public async Task ConvertTupleToStruct_SimpleNamedTuple_CreatesTypeAndReplacesCreation()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                public object Create()
                {
                    return (X: 1, Y: 2);
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ConvertTupleToStructOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ConvertTupleToStructParams
        {
            SourceFile = workspace.SourcePath,
            Line = 7,
            NewTypeName = "Point"
        });

        Assert.True(result.Success);
        Assert.False(result.Preview);

        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("public struct Point", text);
        Assert.Contains("public int X { get; set; }", text);
        Assert.Contains("public int Y { get; set; }", text);
        Assert.Contains("return new Point { X = 1, Y = 2 };", text);
        Assert.DoesNotContain("return (X: 1, Y: 2);", text);
    }

    [SkippableFact]
    public async Task ConvertTupleToStruct_UnnamedTuple_UsesItemNames()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                public object Create()
                {
                    return (1, 2);
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ConvertTupleToStructOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ConvertTupleToStructParams
        {
            SourceFile = workspace.SourcePath,
            Line = 7,
            NewTypeName = "Pair"
        });

        Assert.True(result.Success);

        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("public struct Pair", text);
        Assert.Contains("public int Item1 { get; set; }", text);
        Assert.Contains("public int Item2 { get; set; }", text);
        Assert.Contains("return new Pair { Item1 = 1, Item2 = 2 };", text);
        Assert.DoesNotContain("return (1, 2);", text);
    }

    [SkippableFact]
    public async Task ConvertTupleToStruct_Preview_ReturnsChangesAndWritesNothing()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                public object Create()
                {
                    return (X: 1, Y: 2);
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var original = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new ConvertTupleToStructOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ConvertTupleToStructParams
        {
            SourceFile = workspace.SourcePath,
            Line = 7,
            NewTypeName = "Point",
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.NotEmpty(result.PendingChanges);
        Assert.Contains(result.PendingChanges, c =>
            c.AfterSnippet != null &&
            c.AfterSnippet.Contains("public struct Point") &&
            c.AfterSnippet.Contains("new Point { X = 1, Y = 2 }"));

        var after = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Equal(original, after);
    }

    [SkippableFact]
    public async Task ConvertTupleToStruct_SameShapeCreations_AreReplacedTogether()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                public object Create()
                {
                    var first = (X: 1, Y: 2);
                    var second = (X: 3, Y: 4);
                    var other = (A: 1, B: 2);
                    return first;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ConvertTupleToStructOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ConvertTupleToStructParams
        {
            SourceFile = workspace.SourcePath,
            Line = 7,
            NewTypeName = "Point"
        });

        Assert.True(result.Success);

        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("var first = new Point { X = 1, Y = 2 };", text);
        Assert.Contains("var second = new Point { X = 3, Y = 4 };", text);
        Assert.Contains("var other = (A: 1, B: 2);", text);
    }

    [SkippableFact]
    public async Task ConvertTupleToStruct_NestedNamespaces_UsesFullNamespace()
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
                            return (X: 1, Y: 2);
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
                        return (X: 3, Y: 4);
                    }
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(
            ("Worker.cs", worker),
            ("Client.cs", client));
        var operation = new ConvertTupleToStructOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ConvertTupleToStructParams
        {
            SourceFile = workspace.SourcePath,
            Line = 9,
            NewTypeName = "Point"
        });

        Assert.True(result.Success);
        Assert.Equal("Outer.Inner.Point", result.Symbol?.FullyQualifiedName);

        var workerText = await File.ReadAllTextAsync(workspace.SourcePath);
        var clientText = await File.ReadAllTextAsync(Path.Combine(workspace.DirectoryPath, "Client.cs"));
        Assert.Contains("namespace Inner", workerText);
        Assert.Contains("public struct Point", workerText);
        Assert.Contains("return new Point { X = 1, Y = 2 };", workerText);
        Assert.Contains("return new Outer.Inner.Point { X = 3, Y = 4 };", clientText);
        Assert.DoesNotContain("new Inner.Point", clientText);
    }

    [SkippableFact]
    public async Task ConvertTupleToStruct_KeywordMember_EscapesIdentifier()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                public object Create()
                {
                    return (@class: 1, value: 2);
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ConvertTupleToStructOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ConvertTupleToStructParams
        {
            SourceFile = workspace.SourcePath,
            Line = 7,
            NewTypeName = "Wrapper"
        });

        Assert.True(result.Success);

        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("public int @class { get; set; }", text);
        Assert.Contains("public int value { get; set; }", text);
        Assert.Contains("return new Wrapper { @class = 1, value = 2 };", text);
        Assert.DoesNotContain("public int class {", text);
        Assert.DoesNotContain("{ class = 1", text);
    }

    [SkippableFact]
    public async Task ConvertTupleToStruct_ExclusiveEndAtPreviousCreation_ThrowsCannotConvert()
    {
        const string source = """
            namespace TestApp;

            public class SameLine
            {
                public object Create()
                {
                    var first = (1, 2); var second = (A: 3, B: 4);
                    return first;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var original = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new ConvertTupleToStructOperation(workspace.Context);
        var line = FindLine(source, "var first = (1, 2)");
        var firstCreationEndCol = FirstTupleCreationEndColumn(source);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ConvertTupleToStructParams
            {
                SourceFile = workspace.SourcePath,
                Line = line,
                Column = firstCreationEndCol,
                NewTypeName = "Pair"
            }));

        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
        Assert.Equal("3020", ex.ErrorCode);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task ConvertTupleToStruct_Preview_ColumnSelectsSecond_DoesNotModifyFile()
    {
        const string source = """
            namespace TestApp;

            public class SameLine
            {
                public object Create()
                {
                    var first = (1, 2); var second = (A: 3, B: 4);
                    return first;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new ConvertTupleToStructOperation(workspace.Context);
        var line = FindLine(source, "var first = (1, 2)");
        var secondColumn = ColumnOf(source, "(A: 3, B: 4)");

        var result = await operation.ExecuteAsync(new ConvertTupleToStructParams
        {
            SourceFile = workspace.SourcePath,
            Line = line,
            Column = secondColumn,
            NewTypeName = "NamedPair",
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.Contains(result.PendingChanges, change =>
            change.AfterSnippet != null &&
            change.AfterSnippet.Contains("public struct NamedPair") &&
            change.AfterSnippet.Contains("new NamedPair { A = 3, B = 4 }"));
        Assert.DoesNotContain(result.PendingChanges, change =>
            change.AfterSnippet != null &&
            change.AfterSnippet.Contains("new Pair"));
        Assert.Contains(result.PendingChanges, change =>
            change.AfterSnippet != null &&
            change.AfterSnippet.Contains("(1, 2)"));
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    #endregion

    #region Covering-span column

    [Fact]
    public void FindTupleCreation_OmittedColumn_PicksSingleOnLine()
    {
        var (root, model) = ParseWithModel(IndentedTupleSource);
        var line = FindLine(IndentedTupleSource, "return (1, 2)");
        var creation = root.DescendantNodes().OfType<TupleExpressionSyntax>().Single();
        var startCol = creation.GetLocation().GetLineSpan().StartLinePosition.Character + 1;
        Assert.True(startCol > 1);

        var found = ConvertTupleToStructOperation.FindTupleCreation(
            root,
            model,
            new ConvertTupleToStructParams
            {
                SourceFile = "/tmp/tuple.cs",
                Line = line,
                NewTypeName = "Pair"
            });

        Assert.Equal(creation.Span, found.Span);
        Assert.Contains("(1, 2)", found.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void FindTupleCreation_OmittedColumn_TwoOnLine_ThrowsSymbolAmbiguous()
    {
        var (root, model) = ParseWithModel(SameLineTupleSource);
        var line = FindLine(SameLineTupleSource, "var first = (1, 2)");

        var ex = Assert.Throws<RefactoringException>(() =>
            ConvertTupleToStructOperation.FindTupleCreation(
                root,
                model,
                new ConvertTupleToStructParams
                {
                    SourceFile = "/tmp/tuple.cs",
                    Line = line,
                    NewTypeName = "Pair"
                }));

        Assert.Equal(ErrorCodes.SymbolAmbiguous, ex.ErrorCode);
        Assert.Contains("column", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FindTupleCreation_ColumnPicksUniqueCoveringCreation()
    {
        var (root, model) = ParseWithModel(SameLineTupleSource);
        var line = FindLine(SameLineTupleSource, "var first = (1, 2)");
        var secondColumn = ColumnOf(SameLineTupleSource, "(A: 3, B: 4)");

        var found = ConvertTupleToStructOperation.FindTupleCreation(
            root,
            model,
            new ConvertTupleToStructParams
            {
                SourceFile = "/tmp/tuple.cs",
                Line = line,
                Column = secondColumn,
                NewTypeName = "NamedPair"
            });

        Assert.Contains("A: 3", found.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("1, 2", found.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void FindTupleCreation_AdjacentCreations_ExclusiveEndDoesNotStealNext()
    {
        var (root, model) = ParseWithModel(SameLineTupleSource);
        var line = FindLine(SameLineTupleSource, "var first = (1, 2)");
        var first = root.DescendantNodes().OfType<TupleExpressionSyntax>()
            .First(n => n.ToString().Contains("1, 2", StringComparison.Ordinal));
        var firstCreationEndCol = first.GetLocation().GetLineSpan().EndLinePosition.Character + 1;
        var secondColumn = ColumnOf(SameLineTupleSource, "(A: 3, B: 4)");

        Assert.False(ConvertTupleToStructOperation.SpanCoversColumn(
            first.GetLocation().GetLineSpan(), line, firstCreationEndCol));

        var exclusiveEnd = Assert.Throws<RefactoringException>(() =>
            ConvertTupleToStructOperation.FindTupleCreation(
                root,
                model,
                new ConvertTupleToStructParams
                {
                    SourceFile = "/tmp/tuple.cs",
                    Line = line,
                    Column = firstCreationEndCol,
                    NewTypeName = "Pair"
                }));
        Assert.Equal(ErrorCodes.CannotConvert, exclusiveEnd.ErrorCode);

        var atSecond = ConvertTupleToStructOperation.FindTupleCreation(
            root,
            model,
            new ConvertTupleToStructParams
            {
                SourceFile = "/tmp/tuple.cs",
                Line = line,
                Column = secondColumn,
                NewTypeName = "NamedPair"
            });
        Assert.Contains("A: 3", atSecond.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void SpanCoversColumn_TreatsEndAsExclusive()
    {
        var tree = CSharpSyntaxTree.ParseText(SameLineTupleSource);
        var first = tree.GetRoot().DescendantNodes().OfType<TupleExpressionSyntax>()
            .First(n => n.ToString().Contains("1, 2", StringComparison.Ordinal));
        var span = first.GetLocation().GetLineSpan();
        var line = span.StartLinePosition.Line + 1;
        var startCol = span.StartLinePosition.Character + 1;
        var endCol = span.EndLinePosition.Character + 1;

        Assert.True(ConvertTupleToStructOperation.SpanCoversColumn(span, line, startCol));
        Assert.True(ConvertTupleToStructOperation.SpanCoversColumn(span, line, endCol - 1));
        Assert.False(ConvertTupleToStructOperation.SpanCoversColumn(span, line, endCol));
        Assert.False(ConvertTupleToStructOperation.SpanCoversColumn(span, line, startCol - 1));
    }

    #endregion

    #region Rejects

    [SkippableFact]
    public async Task ConvertTupleToStruct_NotTuple_ThrowsAndWritesNothing()
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
        var operation = new ConvertTupleToStructOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ConvertTupleToStructParams
            {
                SourceFile = workspace.SourcePath,
                Line = 7,
                NewTypeName = "Point"
            }));

        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
        Assert.Equal("3020", ex.ErrorCode);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task ConvertTupleToStruct_NameConflict_ThrowsAndWritesNothing()
    {
        const string source = """
            namespace TestApp;

            public struct Point
            {
            }

            public class Worker
            {
                public object Create()
                {
                    return (X: 1, Y: 2);
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var original = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new ConvertTupleToStructOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ConvertTupleToStructParams
            {
                SourceFile = workspace.SourcePath,
                Line = 11,
                NewTypeName = "Point"
            }));

        Assert.Equal(ErrorCodes.NameConflictScope, ex.ErrorCode);
        Assert.Equal("3010", ex.ErrorCode);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [Fact]
    public void ConvertTupleToStruct_UneditableDocument_Throws()
    {
        var workspace = new AdhocWorkspace();
        var project = workspace.AddProject("P", LanguageNames.CSharp);
        var document = workspace.AddDocument(project.Id, "Generated.cs", SourceText.From("class C {}"));

        var ex = Assert.Throws<RefactoringException>(() =>
            ConvertTupleToStructOperation.ValidateDocumentIsEditable(document, workspace));

        Assert.Equal(ErrorCodes.DocumentNotEditable, ex.ErrorCode);
    }

    [SkippableFact]
    public async Task ConvertTupleToStruct_MethodTypeParameter_ThrowsAndWritesNothing()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                public object Create<T>(T value)
                {
                    return (Value: value, Count: 1);
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var original = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new ConvertTupleToStructOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ConvertTupleToStructParams
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
    public async Task ConvertTupleToStruct_LessAccessibleMemberType_ThrowsAndWritesNothing()
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
                    return (Item: new InternalItem(), Count: 1);
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var original = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new ConvertTupleToStructOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ConvertTupleToStructParams
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
    public async Task ConvertTupleToStruct_PrivateNestedMemberType_ThrowsAndWritesNothing()
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
                    return (Item: new Hidden(), Count: 1);
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var original = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new ConvertTupleToStructOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ConvertTupleToStructParams
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
        Assert.Equal("Outer.Inner", ConvertTupleToStructOperation.GetFullNamespaceName(inner));
    }

    [SkippableFact]
    public async Task ConvertTupleToStruct_TupleTypedReturn_ThrowsAndWritesNothing()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                public (int X, int Y) Make() => (X: 1, Y: 2);
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var original = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new ConvertTupleToStructOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ConvertTupleToStructParams
            {
                SourceFile = workspace.SourcePath,
                Line = 5,
                NewTypeName = "Point"
            }));

        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
        Assert.Equal("3020", ex.ErrorCode);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task ConvertTupleToStruct_TupleTypedArgument_ThrowsAndWritesNothing()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                public object Create()
                {
                    return Take((X: 1, Y: 2));
                }

                public static object Take((int X, int Y) point) => point;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var original = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new ConvertTupleToStructOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ConvertTupleToStructParams
            {
                SourceFile = workspace.SourcePath,
                Line = 7,
                NewTypeName = "Point"
            }));

        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task ConvertTupleToStruct_TupleTypedSiblingCreation_IsLeftAlone()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                public object Create()
                {
                    var first = (X: 1, Y: 2);
                    (int X, int Y) second = (X: 3, Y: 4);
                    return first;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ConvertTupleToStructOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ConvertTupleToStructParams
        {
            SourceFile = workspace.SourcePath,
            Line = 7,
            NewTypeName = "Point"
        });

        Assert.True(result.Success);
        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("var first = new Point { X = 1, Y = 2 };", text);
        Assert.Contains("(int X, int Y) second = (X: 3, Y: 4);", text);
        Assert.DoesNotContain("second = new Point", text);
    }

    [SkippableFact]
    public async Task ConvertTupleToStruct_SiblingProjectWithoutReference_IsNotRewritten()
    {
        await using var workspace = await TempWorkspace.CreateSiblingProjectsAsync();
        var originalOther = await File.ReadAllTextAsync(workspace.SecondarySourcePath);
        var operation = new ConvertTupleToStructOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ConvertTupleToStructParams
        {
            SourceFile = workspace.SourcePath,
            Line = 7,
            NewTypeName = "Point"
        });

        Assert.True(result.Success);
        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("public struct Point", text);
        Assert.Contains("new Point { X = 1, Y = 2 }", text);
        Assert.Equal(originalOther, await File.ReadAllTextAsync(workspace.SecondarySourcePath));
    }

    [SkippableFact]
    public async Task ConvertTupleToStruct_NamedConstructorArgs_MapToTupleElements()
    {
        const string source = """
            using System;

            namespace TestApp;

            public class Worker
            {
                public object Create()
                {
                    return new ValueTuple<int, int>(item2: 2, item1: 1);
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ConvertTupleToStructOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ConvertTupleToStructParams
        {
            SourceFile = workspace.SourcePath,
            Line = 9,
            NewTypeName = "Pair"
        });

        Assert.True(result.Success);
        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("return new Pair { Item1 = 1, Item2 = 2 };", text);
        Assert.DoesNotContain("Item1 = 2", text);
        Assert.DoesNotContain("Item2 = 1", text);
    }

    [SkippableFact]
    public async Task ConvertTupleToStruct_UsingInUnrelatedNamespace_DoesNotUnqualify()
    {
        const string worker = """
            namespace TestApp
            {
                public class Worker
                {
                    public object Create()
                    {
                        return (X: 1, Y: 2);
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
                        return (X: 3, Y: 4);
                    }
                }
            }
            namespace Unrelated
            {
                using TestApp;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(
            ("Worker.cs", worker),
            ("Client.cs", client));
        var operation = new ConvertTupleToStructOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ConvertTupleToStructParams
        {
            SourceFile = workspace.SourcePath,
            Line = 7,
            NewTypeName = "Point"
        });

        Assert.True(result.Success);
        var clientText = await File.ReadAllTextAsync(Path.Combine(workspace.DirectoryPath, "Client.cs"));
        Assert.Contains("return new TestApp.Point { X = 3, Y = 4 };", clientText);
        Assert.DoesNotContain("return new Point {", clientText);
    }

    [SkippableFact]
    public async Task ConvertTupleToStruct_UsingAlias_DoesNotCountAsImport()
    {
        const string worker = """
            namespace TestApp
            {
                public class Worker
                {
                    public object Create()
                    {
                        return (X: 1, Y: 2);
                    }
                }
            }
            """;
        const string client = """
            namespace Other
            {
                using Alias = TestApp;

                public class Client
                {
                    public object Create()
                    {
                        return (X: 3, Y: 4);
                    }
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(
            ("Worker.cs", worker),
            ("Client.cs", client));
        var operation = new ConvertTupleToStructOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ConvertTupleToStructParams
        {
            SourceFile = workspace.SourcePath,
            Line = 7,
            NewTypeName = "Point"
        });

        Assert.True(result.Success);
        var clientText = await File.ReadAllTextAsync(Path.Combine(workspace.DirectoryPath, "Client.cs"));
        Assert.Contains("return new TestApp.Point { X = 3, Y = 4 };", clientText);
        Assert.DoesNotContain("return new Point {", clientText);
        Assert.DoesNotContain("return new Alias.Point {", clientText);
    }

    [SkippableFact]
    public async Task ConvertTupleToStruct_EscapedNameConflict_ThrowsAndWritesNothing()
    {
        const string source = """
            namespace TestApp;

            public struct Point
            {
            }

            public class Worker
            {
                public object Create()
                {
                    return (X: 1, Y: 2);
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var original = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new ConvertTupleToStructOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ConvertTupleToStructParams
            {
                SourceFile = workspace.SourcePath,
                Line = 11,
                NewTypeName = "@Point"
            }));

        Assert.Equal(ErrorCodes.NameConflictScope, ex.ErrorCode);
        Assert.Equal("3010", ex.ErrorCode);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [Fact]
    public void StripVerbatimPrefix_RemovesAtSignForLookup()
    {
        Assert.Equal("Point", ConvertTupleToStructOperation.StripVerbatimPrefix("@Point"));
        Assert.Equal("Point", ConvertTupleToStructOperation.StripVerbatimPrefix("Point"));
        Assert.Equal("class", ConvertTupleToStructOperation.StripVerbatimPrefix("@class"));
    }

    [Fact]
    public void HasUsingInScope_IgnoresUnrelatedNamespaceAndAlias()
    {
        var tree = CSharpSyntaxTree.ParseText("""
            namespace Other
            {
                public class Client
                {
                    public object Create() => (X: 1, Y: 2);
                }
            }
            namespace Unrelated
            {
                using TestApp;
            }
            """);
        var creation = tree.GetRoot().DescendantNodes().OfType<TupleExpressionSyntax>().Single();
        Assert.False(ConvertTupleToStructOperation.HasUsingInScope(creation, "TestApp"));

        var aliasTree = CSharpSyntaxTree.ParseText("""
            namespace Other
            {
                using Alias = TestApp;
                public class Client
                {
                    public object Create() => (X: 1, Y: 2);
                }
            }
            """);
        var aliasCreation = aliasTree.GetRoot().DescendantNodes().OfType<TupleExpressionSyntax>().Single();
        Assert.False(ConvertTupleToStructOperation.HasUsingInScope(aliasCreation, "TestApp"));

        var inScopeTree = CSharpSyntaxTree.ParseText("""
            using TestApp;
            namespace Other
            {
                public class Client
                {
                    public object Create() => (X: 1, Y: 2);
                }
            }
            """);
        var inScopeCreation = inScopeTree.GetRoot().DescendantNodes().OfType<TupleExpressionSyntax>().Single();
        Assert.True(ConvertTupleToStructOperation.HasUsingInScope(inScopeCreation, "TestApp"));
    }

    [Fact]
    public void MapConstructorArguments_NamedOutOfOrder_UsesParameterNames()
    {
        var tree = CSharpSyntaxTree.ParseText("""
            using System;
            class C
            {
                object M() => new ValueTuple<int, int>(item2: 2, item1: 1);
            }
            """);
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Where(path => path.EndsWith("System.Private.CoreLib.dll", StringComparison.OrdinalIgnoreCase) ||
                           path.EndsWith("System.Runtime.dll", StringComparison.OrdinalIgnoreCase) ||
                           path.EndsWith("System.Console.dll", StringComparison.OrdinalIgnoreCase))
            .Select(path => MetadataReference.CreateFromFile(path))
            .ToArray();
        var compilation = CSharpCompilation.Create(
            "MapCtor",
            [tree],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var model = compilation.GetSemanticModel(tree);
        var creation = tree.GetRoot().DescendantNodes().OfType<ObjectCreationExpressionSyntax>().Single();
        var tupleType = (INamedTypeSymbol)model.GetTypeInfo(creation).Type!;
        var members = ConvertTupleToStructOperation.GetTupleMembers(tupleType);

        var values = ConvertTupleToStructOperation.GetCreationValues(creation, members, model);

        Assert.Equal("1", values[0].ToString());
        Assert.Equal("2", values[1].ToString());
    }

    #endregion

    #region Helpers

    private const string SameLineTupleSource = """
        class C
        {
            void M()
            {
                var first = (1, 2); var second = (A: 3, B: 4);
            }
        }
        """;

    private const string IndentedTupleSource = """
        class C
        {
            object M()
            {
                return (1, 2);
            }
        }
        """;

    private static ConvertTupleToStructParams ValidParams(
        string? sourceFile = null,
        int line = 7,
        string newTypeName = "Point",
        int? column = null) => new()
        {
            SourceFile = sourceFile ?? Path.Combine(Path.GetTempPath(), "RoslynMcpConvertTupleMissing.cs"),
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

    private static int FirstTupleCreationEndColumn(string source)
    {
        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var first = root.DescendantNodes().OfType<TupleExpressionSyntax>()
            .OrderBy(n => n.SpanStart)
            .First();
        return first.GetLocation().GetLineSpan().EndLinePosition.Character + 1;
    }

    private static (SyntaxNode Root, SemanticModel Model) ParseWithModel(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var compilation = CSharpCompilation.Create(
            "TupleFind",
            [tree],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        return (tree.GetRoot(), compilation.GetSemanticModel(tree));
    }

    private sealed class TempWorkspace : IAsyncDisposable
    {
        public required string DirectoryPath { get; init; }
        public required string ProjectPath { get; init; }
        public required string SourcePath { get; init; }
        public required WorkspaceContext Context { get; init; }
        public string SecondarySourcePath { get; init; } = "";

        public static Task<TempWorkspace> CreateAsync(string source, string fileName = "Worker.cs") =>
            CreateAsync((fileName, source));

        public static async Task<TempWorkspace> CreateAsync(params (string FileName, string Source)[] files)
        {
            Skip.IfNot(ModuleInitializer.MsBuildAvailable, ModuleInitializer.MsBuildError ?? "MSBuild not available");

            var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpConvertTuple_" + Guid.NewGuid().ToString("N"));
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
            return await LoadAsync(directory, projectPath, sourcePath);
        }

        public static async Task<TempWorkspace> CreateSiblingProjectsAsync()
        {
            Skip.IfNot(ModuleInitializer.MsBuildAvailable, ModuleInitializer.MsBuildError ?? "MSBuild not available");

            var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpConvertTupleXP_" + Guid.NewGuid().ToString("N"));
            var appDir = Path.Combine(directory, "App");
            var otherDir = Path.Combine(directory, "Other");
            Directory.CreateDirectory(appDir);
            Directory.CreateDirectory(otherDir);

            var appProject = Path.Combine(appDir, "App.csproj");
            var otherProject = Path.Combine(otherDir, "Other.csproj");
            var appSource = Path.Combine(appDir, "Worker.cs");
            var otherSource = Path.Combine(otherDir, "Client.cs");

            const string csproj = """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net9.0</TargetFramework>
                    <Nullable>enable</Nullable>
                  </PropertyGroup>
                </Project>
                """;
            await File.WriteAllTextAsync(appProject, csproj);
            await File.WriteAllTextAsync(otherProject, csproj);
            await File.WriteAllTextAsync(appSource, """
                namespace TestApp;

                public class Worker
                {
                    public object Create()
                    {
                        return (X: 1, Y: 2);
                    }
                }
                """);
            await File.WriteAllTextAsync(otherSource, """
                namespace OtherApp;

                public class Client
                {
                    public object Create()
                    {
                        return (X: 3, Y: 4);
                    }
                }
                """);

            var solutionPath = Path.Combine(directory, "TestApp.sln");
            await File.WriteAllTextAsync(solutionPath, """
                Microsoft Visual Studio Solution File, Format Version 12.00
                # Visual Studio Version 17
                Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "App", "App\App.csproj", "{11111111-1111-1111-1111-111111111111}"
                EndProject
                Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Other", "Other\Other.csproj", "{22222222-2222-2222-2222-222222222222}"
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

            var workspace = await LoadAsync(directory, solutionPath, appSource);
            return new TempWorkspace
            {
                DirectoryPath = workspace.DirectoryPath,
                ProjectPath = workspace.ProjectPath,
                SourcePath = workspace.SourcePath,
                Context = workspace.Context,
                SecondarySourcePath = otherSource
            };
        }

        private static async Task<TempWorkspace> LoadAsync(string directory, string projectPath, string sourcePath)
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

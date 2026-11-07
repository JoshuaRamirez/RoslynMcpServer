using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Convert;
using RoslynMcp.Core.Refactoring.Utilities;
using RoslynMcp.Core.Resolution;
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
    public void IsValidIdentifier_RejectsInvalidAndKeywords()
    {
        Assert.False(SyntaxIdentifierValidation.IsValidIdentifier("123Bad"));
        Assert.False(SyntaxIdentifierValidation.IsValidIdentifier("class"));
        Assert.False(SyntaxIdentifierValidation.IsValidIdentifier("int"));
        Assert.False(SyntaxIdentifierValidation.IsValidIdentifier("@@@"));
        Assert.True(SyntaxIdentifierValidation.IsValidIdentifier("Point"));
        Assert.True(SyntaxIdentifierValidation.IsValidIdentifier("_Info"));
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
    public async Task ConvertTupleToStruct_AsRecord_CreatesRecordStructWithInitProperties()
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
            NewTypeName = "Point",
            AsRecord = true
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Symbol);
        Assert.Equal(RoslynMcp.Contracts.Enums.SymbolKind.Record, result.Symbol!.Kind);

        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("public record struct Point", text);
        Assert.Contains("public int X { get; init; }", text);
        Assert.Contains("public int Y { get; init; }", text);
        Assert.Contains("return new Point { X = 1, Y = 2 };", text);
        Assert.DoesNotContain("public struct Point", text);
        Assert.DoesNotContain("{ get; set; }", text);
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

        Assert.False(SpanCoverage.SpanCoversColumn(
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

        Assert.True(SpanCoverage.SpanCoversColumn(span, line, startCol));
        Assert.True(SpanCoverage.SpanCoversColumn(span, line, endCol - 1));
        Assert.False(SpanCoverage.SpanCoversColumn(span, line, endCol));
        Assert.False(SpanCoverage.SpanCoversColumn(span, line, startCol - 1));
    }

    [Fact]
    public void SpanCoversLine_WithColumn_TreatsEndAsExclusive()
    {
        var tree = CSharpSyntaxTree.ParseText(SameLineTupleSource);
        var first = tree.GetRoot().DescendantNodes().OfType<TupleExpressionSyntax>()
            .First(n => n.ToString().Contains("1, 2", StringComparison.Ordinal));
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
                    var first = (
                        1,
                        2
                    );
                }
            }
            """;
        var multiLineTree = CSharpSyntaxTree.ParseText(multiLineSource);
        var multiLineFirst = multiLineTree.GetRoot().DescendantNodes().OfType<TupleExpressionSyntax>()
            .First(n => n.ToString().Contains("1", StringComparison.Ordinal));
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
    public async Task ConvertTupleToStruct_AsRecordCloneMember_ThrowsAndWritesNothing()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                public object Create()
                {
                    return (Clone: 1, X: 2);
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
                NewTypeName = "Pair",
                AsRecord = true
            }));

        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
        Assert.Contains("Clone", ex.Message, StringComparison.Ordinal);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task ConvertTupleToStruct_AsRecordEqualsMember_ThrowsAndWritesNothing()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                public object Create()
                {
                    return (Equals: 1, X: 2);
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
                NewTypeName = "Pair",
                AsRecord = true
            }));

        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
        Assert.Contains("Equals", ex.Message, StringComparison.Ordinal);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task ConvertTupleToStruct_AsRecordGetHashCodeMember_ThrowsAndWritesNothing()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                public object Create()
                {
                    return (GetHashCode: 1, X: 2);
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
                NewTypeName = "Pair",
                AsRecord = true
            }));

        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
        Assert.Contains("GetHashCode", ex.Message, StringComparison.Ordinal);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task ConvertTupleToStruct_AsRecordPrintMembersMember_ThrowsAndWritesNothing()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                public object Create()
                {
                    return (PrintMembers: 1, X: 2);
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
                NewTypeName = "Pair",
                AsRecord = true
            }));

        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
        Assert.Contains("PrintMembers", ex.Message, StringComparison.Ordinal);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }


    [SkippableFact]
    public async Task ConvertTupleToStruct_AsRecordOpEqualityMember_ThrowsAndWritesNothing()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                public object Create()
                {
                    return (op_Equality: 1, X: 2);
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
                NewTypeName = "Pair",
                AsRecord = true
            }));

        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
        Assert.Contains("op_Equality", ex.Message, StringComparison.Ordinal);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task ConvertTupleToStruct_AsRecordOpInequalityMember_ThrowsAndWritesNothing()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                public object Create()
                {
                    return (op_Inequality: 1, X: 2);
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
                NewTypeName = "Pair",
                AsRecord = true
            }));

        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
        Assert.Contains("op_Inequality", ex.Message, StringComparison.Ordinal);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task ConvertTupleToStruct_AsRecordFalse_AllowsCloneMember()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                public object Create()
                {
                    return (Clone: 1, X: 2);
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ConvertTupleToStructOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ConvertTupleToStructParams
        {
            SourceFile = workspace.SourcePath,
            Line = 7,
            NewTypeName = "Pair",
            AsRecord = false
        });

        Assert.True(result.Success);
        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("public struct Pair", text);
        Assert.Contains("public int Clone { get; set; }", text);
    }

    [SkippableFact]
    public async Task ConvertTupleToStruct_AsRecordElementAssignment_ThrowsAndWritesNothing()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                public object Create()
                {
                    var point = (X: 1, Y: 2);
                    point.X = 3;
                    return point;
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
                NewTypeName = "Point",
                AsRecord = true
            }));

        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
        Assert.Contains("element assignments", ex.Message, StringComparison.Ordinal);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task ConvertTupleToStruct_AsRecordFalse_AllowsElementAssignment()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                public object Create()
                {
                    var point = (X: 1, Y: 2);
                    point.X = 3;
                    return point;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ConvertTupleToStructOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ConvertTupleToStructParams
        {
            SourceFile = workspace.SourcePath,
            Line = 7,
            NewTypeName = "Point",
            AsRecord = false
        });

        Assert.True(result.Success);
        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("public struct Point", text, StringComparison.Ordinal);
        Assert.Contains("public int X { get; set; }", text, StringComparison.Ordinal);
        Assert.Contains("point.X = 3", text, StringComparison.Ordinal);
    }

    // #3347: asRecord:false must refuse by-ref element uses (properties cannot be ref/out).
    [SkippableFact]
    public async Task ConvertTupleToStruct_AsRecordFalse_RefOutAndRefLocalElement_ThrowsAndWritesNothing()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                static void Bump(ref int v) => v++;

                public object Create()
                {
                    var point = (X: 1, Y: 2);
                    Bump(ref point.X);
                    ref int ry = ref point.Y;
                    ry = 40;
                    int.TryParse("7", out point.Y);
                    return point;
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
                Line = 9,
                NewTypeName = "Point",
                AsRecord = false
            }));

        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
        Assert.Contains("passed by reference", ex.Message, StringComparison.Ordinal);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task ConvertTupleToStruct_AsRecordFalse_OutElementAlone_ThrowsAndWritesNothing()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                public object Create()
                {
                    var point = (X: 1, Y: 2);
                    int.TryParse("5", out point.X);
                    return point;
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
                NewTypeName = "Point",
                AsRecord = false
            }));

        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
        Assert.Contains("passed by reference", ex.Message, StringComparison.Ordinal);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task ConvertTupleToStruct_AsRecordFalse_RefArgumentAlone_ThrowsAndWritesNothing()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                static void Bump(ref int v) => v++;

                public object Create()
                {
                    var point = (X: 1, Y: 2);
                    Bump(ref point.X);
                    return point;
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
                Line = 9,
                NewTypeName = "Point",
                AsRecord = false
            }));

        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
        Assert.Contains("passed by reference", ex.Message, StringComparison.Ordinal);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task ConvertTupleToStruct_AsRecordFalse_RefLocalAlone_ThrowsAndWritesNothing()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                public object Create()
                {
                    var point = (X: 1, Y: 2);
                    ref int value = ref point.X;
                    value = 3;
                    return point;
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
                NewTypeName = "Point",
                AsRecord = false
            }));

        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
        Assert.Contains("passed by reference", ex.Message, StringComparison.Ordinal);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    // Codex P1 on #3441: this-ref extension receivers have no explicit ref keyword.
    [SkippableFact]
    public async Task ConvertTupleToStruct_AsRecordFalse_RefExtensionReceiver_ThrowsAndWritesNothing()
    {
        const string source = """
            namespace TestApp;

            public static class IntExtensions
            {
                public static void Increment(this ref int value) => value++;
            }

            public class Worker
            {
                public object Create()
                {
                    var point = (X: 1, Y: 2);
                    point.X.Increment();
                    return point;
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
                Line = 12,
                NewTypeName = "Point",
                AsRecord = false
            }));

        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
        Assert.Contains("passed by reference", ex.Message, StringComparison.Ordinal);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    // Codex P1 on #3441: pointer aliases of converted storage participate in the by-ref gate.
    [SkippableFact]
    public async Task ConvertTupleToStruct_AsRecordFalse_PointerAliasRefElement_ThrowsAndWritesNothing()
    {
        const string source = """
            namespace TestApp;

            public unsafe class Worker
            {
                static void Bump(ref int v) => v++;

                public object Create()
                {
                    var point = (X: 1, Y: 2);
                    var p = &point;
                    Bump(ref p->X);
                    return point;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, allowUnsafe: true);
        var original = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new ConvertTupleToStructOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ConvertTupleToStructParams
            {
                SourceFile = workspace.SourcePath,
                Line = 9,
                NewTypeName = "Point",
                AsRecord = false
            }));

        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
        Assert.Contains("passed by reference", ex.Message, StringComparison.Ordinal);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    // Codex P1 on #3441: (*p).X pointer-indirection form (not only p->X).
    [SkippableFact]
    public async Task ConvertTupleToStruct_AsRecordFalse_PointerIndirectionRefElement_ThrowsAndWritesNothing()
    {
        const string source = """
            namespace TestApp;

            public unsafe class Worker
            {
                static void Bump(ref int v) => v++;

                public object Create()
                {
                    var point = (X: 1, Y: 2);
                    var p = &point;
                    Bump(ref (*p).X);
                    return point;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, allowUnsafe: true);
        var original = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new ConvertTupleToStructOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ConvertTupleToStructParams
            {
                SourceFile = workspace.SourcePath,
                Line = 9,
                NewTypeName = "Point",
                AsRecord = false
            }));

        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
        Assert.Contains("passed by reference", ex.Message, StringComparison.Ordinal);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    // Codex P2 on #3441: __makeref(point.X) requires a variable; properties are not.
    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConvertTupleToStruct_MakeRefElement_ThrowsAndWritesNothing(bool asRecord)
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                public object Create()
                {
                    var point = (X: 1, Y: 2);
                    var handle = __makeref(point.X);
                    return point;
                }
            }
            """;

        AssertTupleContainerCompiles(source);
        await using var workspace = await TempWorkspace.CreateAsync(source);
        var original = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new ConvertTupleToStructOperation(workspace.Context);

        var error = await Record.ExceptionAsync(() =>
            operation.ExecuteAsync(new ConvertTupleToStructParams
            {
                SourceFile = workspace.SourcePath,
                Line = 7,
                NewTypeName = "Point",
                AsRecord = asRecord
            }));

        var after = await File.ReadAllTextAsync(workspace.SourcePath);
        AssertTupleContainerCompiles(after);
        var ex = Assert.IsType<RefactoringException>(error);
        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
        Assert.Equal("3020", ex.ErrorCode);
        Assert.Contains("passed by reference", ex.Message, StringComparison.Ordinal);
        Assert.Equal(original, after);
    }

    // Codex P1 on #3441: generic wrapper fields (Box<T>.Value) yield converted storage.
    [SkippableFact]
    public async Task ConvertTupleToStruct_AsRecordFalse_GenericWrapperFieldRefElement_ThrowsAndWritesNothing()
    {
        const string source = """
            namespace TestApp;

            public struct Box<T>
            {
                public T Value;
            }

            public class Worker
            {
                static Box<T> Wrap<T>(T value) => new Box<T> { Value = value };
                static void Bump(ref int v) => v++;

                public object Create()
                {
                    var point = (X: 1, Y: 2);
                    var box = Wrap(point);
                    Bump(ref box.Value.X);
                    return point;
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
                Line = 15,
                NewTypeName = "Point",
                AsRecord = false
            }));

        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
        Assert.Contains("passed by reference", ex.Message, StringComparison.Ordinal);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    // Codex P1 on #3441: ordinary foreach iteration variables are tracked as aliases.
    [SkippableFact]
    public async Task ConvertTupleToStruct_AsRecordFalse_OrdinaryForeachAliasRefElement_ThrowsAndWritesNothing()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                static void Bump(ref int v) => v++;

                public object Create()
                {
                    var point = (X: 1, Y: 2);
                    var points = new[] { point };
                    foreach (var copy in points)
                        Bump(ref copy.X);
                    return points;
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
                Line = 9,
                NewTypeName = "Point",
                AsRecord = false
            }));

        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
        Assert.Contains("passed by reference", ex.Message, StringComparison.Ordinal);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    // Codex P1 on #3441: deconstructing foreach designations are tracked as aliases.
    [SkippableFact]
    public async Task ConvertTupleToStruct_AsRecordFalse_DeconstructingForeachAliasRefElement_ThrowsAndWritesNothing()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                static void Bump(ref int v) => v++;

                public object Create()
                {
                    var point = (X: 1, Y: 2);
                    foreach (var (copy, _) in new[] { (point, 0) })
                        Bump(ref copy.X);
                    return point;
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
                Line = 9,
                NewTypeName = "Point",
                AsRecord = false
            }));

        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
        Assert.Contains("passed by reference", ex.Message, StringComparison.Ordinal);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    // Codex P1 on #3441: generic wrapper members whose type nests T (T[]) forward storage.
    [SkippableFact]
    public async Task ConvertTupleToStruct_AsRecordFalse_GenericWrapperNestedArrayRefElement_ThrowsAndWritesNothing()
    {
        const string source = """
            namespace TestApp;

            public struct Box<T>
            {
                public T[] Values;
            }

            public class Worker
            {
                static Box<T> Wrap<T>(T value) => new Box<T> { Values = new[] { value } };
                static void Bump(ref int v) => v++;

                public object Create()
                {
                    var point = (X: 1, Y: 2);
                    var box = Wrap(point);
                    Bump(ref box.Values[0].X);
                    return point;
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
                Line = 15,
                NewTypeName = "Point",
                AsRecord = false
            }));

        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
        Assert.Contains("passed by reference", ex.Message, StringComparison.Ordinal);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    // Codex P1 on #3441: multi-arg generic returns that embed T (not Arity:1) forward storage.
    [SkippableFact]
    public async Task ConvertTupleToStruct_AsRecordFalse_MultiArgGenericReturnRefElement_ThrowsAndWritesNothing()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                static (T Value, int Other) Pair<T>(T value) => (value, 0);
                static void Bump(ref int v) => v++;

                public object Create()
                {
                    var point = (X: 1, Y: 2);
                    var wrapped = Pair(point);
                    Bump(ref wrapped.Value.X);
                    return point;
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
                Line = 10,
                NewTypeName = "Point",
                AsRecord = false
            }));

        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
        Assert.Contains("passed by reference", ex.Message, StringComparison.Ordinal);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    // Codex P1 on #3441: query expression results propagate converted storage to aliases.
    [SkippableFact]
    public async Task ConvertTupleToStruct_AsRecordFalse_QueryResultAliasRefElement_ThrowsAndWritesNothing()
    {
        const string source = """
            using System.Linq;

            namespace TestApp;

            public class Worker
            {
                static void Bump(ref int v) => v++;

                public object Create()
                {
                    var point = (X: 1, Y: 2);
                    var query = from x in new[] { point } select x;
                    foreach (var copy in query)
                        Bump(ref copy.X);
                    return point;
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
                NewTypeName = "Point",
                AsRecord = false
            }));

        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
        Assert.Contains("passed by reference", ex.Message, StringComparison.Ordinal);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    // Codex P1 on #3441: inferred delegate returns propagate converted storage to invocation aliases.
    [SkippableFact]
    public async Task ConvertTupleToStruct_AsRecordFalse_InferredDelegateReturnAliasRefElement_ThrowsAndWritesNothing()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                static void Bump(ref int v) => v++;

                public object Create()
                {
                    var point = (X: 1, Y: 2);
                    var get = () => point;
                    var copy = get();
                    Bump(ref copy.X);
                    return point;
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
                Line = 9,
                NewTypeName = "Point",
                AsRecord = false
            }));

        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
        Assert.Contains("passed by reference", ex.Message, StringComparison.Ordinal);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    // Codex P2 on #3441: readonly this-in extension receivers remain valid on generated properties.
    [SkippableFact]
    public async Task ConvertTupleToStruct_AsRecordFalse_InExtensionReceiverOnElement_Succeeds()
    {
        const string source = """
            namespace TestApp;

            public static class IntExtensions
            {
                public static int Inspect(this in int value) => value;
            }

            public class Worker
            {
                public object Create()
                {
                    var point = (X: 1, Y: 2);
                    _ = point.X.Inspect();
                    return point;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ConvertTupleToStructOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ConvertTupleToStructParams
        {
            SourceFile = workspace.SourcePath,
            Line = 12,
            NewTypeName = "Point",
            AsRecord = false
        });

        Assert.True(result.Success);
        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("public struct Point", text, StringComparison.Ordinal);
        Assert.Contains("point.X.Inspect()", text, StringComparison.Ordinal);
    }

    // Codex P2 on #3441: unrelated same-named member on a container aggregate must still convert.
    [SkippableFact]
    public async Task ConvertTupleToStruct_AsRecordFalse_UnrelatedAggregateRefMember_Succeeds()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                static void Bump(ref int v) => v++;

                public object Create()
                {
                    var point = (X: 1, Y: 2);
                    var wrapped = (point, X: 5);
                    Bump(ref wrapped.X);
                    return point;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ConvertTupleToStructOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ConvertTupleToStructParams
        {
            SourceFile = workspace.SourcePath,
            Line = 9,
            NewTypeName = "Point",
            AsRecord = false
        });

        Assert.True(result.Success);
        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("public struct Point", text, StringComparison.Ordinal);
        Assert.Contains("Bump(ref wrapped.X)", text, StringComparison.Ordinal);
    }

    // Carrier member still refuses when the by-ref use is on the converted element.
    [SkippableFact]
    public async Task ConvertTupleToStruct_AsRecordFalse_AggregateCarrierRefElement_ThrowsAndWritesNothing()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                static void Bump(ref int v) => v++;

                public object Create()
                {
                    var point = (X: 1, Y: 2);
                    var wrapped = (point, X: 5);
                    Bump(ref wrapped.point.X);
                    return point;
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
                Line = 9,
                NewTypeName = "Point",
                AsRecord = false
            }));

        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
        Assert.Contains("passed by reference", ex.Message, StringComparison.Ordinal);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    // Codex P1 on #3441: named-tuple ItemN aliases are still by-ref element uses.
    [SkippableFact]
    public async Task ConvertTupleToStruct_AsRecordFalse_NamedTupleItemNRefAlias_ThrowsAndWritesNothing()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                static void Bump(ref int v) => v++;

                public object Create()
                {
                    var point = (X: 1, Y: 2);
                    Bump(ref point.Item1);
                    return point;
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
                Line = 9,
                NewTypeName = "Point",
                AsRecord = false
            }));

        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
        Assert.Contains("passed by reference", ex.Message, StringComparison.Ordinal);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    // Codex P1 on #3441: nested field ++ through a value-type element is CS1612 after conversion.
    [SkippableFact]
    public async Task ConvertTupleToStruct_AsRecordFalse_NestedValueTypeElementFieldWrite_ThrowsAndWritesNothing()
    {
        const string source = """
            namespace TestApp;

            public struct Counter
            {
                public int Value;
            }

            public class Worker
            {
                public object Create()
                {
                    var point = (Counter: new Counter(), Y: 2);
                    point.Counter.Value++;
                    return point;
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
                Line = 12,
                NewTypeName = "Point",
                AsRecord = false
            }));

        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
        Assert.Contains("passed by reference", ex.Message, StringComparison.Ordinal);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    // Codex P1 on #3441: nested field assignment through a value-type element is CS1612 after conversion.
    [SkippableFact]
    public async Task ConvertTupleToStruct_AsRecordFalse_NestedValueTypeElementFieldAssignment_ThrowsAndWritesNothing()
    {
        const string source = """
            namespace TestApp;

            public struct Counter
            {
                public int Value;
            }

            public class Worker
            {
                public object Create()
                {
                    var point = (Counter: new Counter(), Y: 2);
                    point.Counter.Value = 1;
                    return point;
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
                Line = 12,
                NewTypeName = "Point",
                AsRecord = false
            }));

        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
        Assert.Contains("passed by reference", ex.Message, StringComparison.Ordinal);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    // Codex P1 on #3441: direct element ++ under asRecord:false still converts (setters work).
    [SkippableFact]
    public async Task ConvertTupleToStruct_AsRecordFalse_DirectElementIncrement_Succeeds()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                public object Create()
                {
                    var point = (X: 1, Y: 2);
                    point.X++;
                    return point;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ConvertTupleToStructOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ConvertTupleToStructParams
        {
            SourceFile = workspace.SourcePath,
            Line = 7,
            NewTypeName = "Point",
            AsRecord = false
        });

        Assert.True(result.Success);
        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("public struct Point", text, StringComparison.Ordinal);
        Assert.Contains("public int X { get; set; }", text, StringComparison.Ordinal);
        Assert.Contains("point.X++", text, StringComparison.Ordinal);
    }

    // Codex P1 on #3441: nested write through value-type element into reference storage still converts.
    [SkippableFact]
    public async Task ConvertTupleToStruct_AsRecordFalse_NestedValueTypeElementReferenceStorageWrite_Succeeds()
    {
        const string source = """
            namespace TestApp;

            public struct Box
            {
                public int[] Values;
            }

            public class Worker
            {
                public object Create()
                {
                    var point = (Box: new Box { Values = new[] { 1 } }, Y: 2);
                    point.Box.Values[0]++;
                    return point;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ConvertTupleToStructOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ConvertTupleToStructParams
        {
            SourceFile = workspace.SourcePath,
            Line = 12,
            NewTypeName = "Point",
            AsRecord = false
        });

        Assert.True(result.Success);
        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("public struct Point", text, StringComparison.Ordinal);
        Assert.Contains("public TestApp.Box Box { get; set; }", text, StringComparison.Ordinal);
        Assert.Contains("point.Box.Values[0]++", text, StringComparison.Ordinal);
    }

    // Codex P1 on #3441: mutating struct instance-method receivers persist via tuple fields.
    [SkippableFact]
    public async Task ConvertTupleToStruct_AsRecordFalse_MutatingStructInstanceMethodReceiver_ThrowsAndWritesNothing()
    {
        const string source = """
            namespace TestApp;

            public struct Counter
            {
                public int Value;
                public void Increment() => Value++;
            }

            public class Worker
            {
                public object Create()
                {
                    var point = (Counter: new Counter(), Y: 2);
                    point.Counter.Increment();
                    return point;
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
                Line = 13,
                NewTypeName = "Point",
                AsRecord = false
            }));

        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
        Assert.Contains("passed by reference", ex.Message, StringComparison.Ordinal);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    // Codex P1 on #3441: constrained T : struct, ICounter — ContainingType is the interface.
    [SkippableFact]
    public async Task ConvertTupleToStruct_AsRecordFalse_ConstrainedValueTypeInterfaceMethodReceiver_ThrowsAndWritesNothing()
    {
        const string source = """
            namespace TestApp;

            public interface ICounter
            {
                void Increment();
            }

            public struct Counter : ICounter
            {
                public int Value;
                public void Increment() => Value++;
            }

            public class Worker<T> where T : struct, ICounter
            {
                public object Create(T counter)
                {
                    var point = (Counter: counter, Y: 2);
                    point.Counter.Increment();
                    return point;
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
                Line = 18,
                NewTypeName = "Point",
                AsRecord = false
            }));

        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
        Assert.Contains("passed by reference", ex.Message, StringComparison.Ordinal);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    // Codex P2 on #3441: by-ref through a reference-type element remains valid after conversion.
    [SkippableFact]
    public async Task ConvertTupleToStruct_AsRecordFalse_RefThroughReferenceTypeElement_Succeeds()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                static void Bump(ref int v) => v++;

                public object Create()
                {
                    var point = (Values: new[] { 1 }, Y: 2);
                    Bump(ref point.Values[0]);
                    return point;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ConvertTupleToStructOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ConvertTupleToStructParams
        {
            SourceFile = workspace.SourcePath,
            Line = 9,
            NewTypeName = "Point",
            AsRecord = false
        });

        Assert.True(result.Success);
        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("public struct Point", text, StringComparison.Ordinal);
        Assert.Contains("public int[] Values { get; set; }", text, StringComparison.Ordinal);
        Assert.Contains("Bump(ref point.Values[0])", text, StringComparison.Ordinal);
    }

    // Codex P2 on #3441: passing the reference-type element property itself by ref still refuses.
    [SkippableFact]
    public async Task ConvertTupleToStruct_AsRecordFalse_RefOnReferenceTypeElementItself_ThrowsAndWritesNothing()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                static void Retarget(ref int[] values) => values = new[] { 9 };

                public object Create()
                {
                    var point = (Values: new[] { 1 }, Y: 2);
                    Retarget(ref point.Values);
                    return point;
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
                Line = 9,
                NewTypeName = "Point",
                AsRecord = false
            }));

        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
        Assert.Contains("passed by reference", ex.Message, StringComparison.Ordinal);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    // Codex P1 on #3441: anonymous-object carriers must track converted storage for by-ref.
    [SkippableFact]
    public async Task ConvertTupleToStruct_AsRecordFalse_AnonymousObjectCarrierRefElement_ThrowsAndWritesNothing()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                static void Bump(ref int v) => v++;

                public object Create()
                {
                    var point = (X: 1, Y: 2);
                    var box = new { Value = point };
                    Bump(ref box.Value.X);
                    return point;
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
                Line = 9,
                NewTypeName = "Point",
                AsRecord = false
            }));

        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
        Assert.Contains("passed by reference", ex.Message, StringComparison.Ordinal);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    // Codex P2 on #3441: by-ref through a value-type element into nested reference storage remains valid.
    [SkippableFact]
    public async Task ConvertTupleToStruct_AsRecordFalse_RefThroughValueTypeElementToReferenceStorage_Succeeds()
    {
        const string source = """
            namespace TestApp;

            public struct Box
            {
                public int[] Values;
            }

            public class Worker
            {
                static void Bump(ref int v) => v++;

                public object Create()
                {
                    var point = (Box: new Box { Values = new[] { 1 } }, Y: 2);
                    Bump(ref point.Box.Values[0]);
                    return point;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ConvertTupleToStructOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ConvertTupleToStructParams
        {
            SourceFile = workspace.SourcePath,
            Line = 14,
            NewTypeName = "Point",
            AsRecord = false
        });

        Assert.True(result.Success);
        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("public struct Point", text, StringComparison.Ordinal);
        Assert.Contains("public TestApp.Box Box { get; set; }", text, StringComparison.Ordinal);
        Assert.Contains("Bump(ref point.Box.Values[0])", text, StringComparison.Ordinal);
    }

    // Codex P2 on #3441: by-ref to a value field of a value-type element still refuses (temporary copy).
    [SkippableFact]
    public async Task ConvertTupleToStruct_AsRecordFalse_RefOnValueTypeElementField_ThrowsAndWritesNothing()
    {
        const string source = """
            namespace TestApp;

            public struct Box
            {
                public int Count;
            }

            public class Worker
            {
                static void Bump(ref int v) => v++;

                public object Create()
                {
                    var point = (Box: new Box { Count = 1 }, Y: 2);
                    Bump(ref point.Box.Count);
                    return point;
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
                Line = 14,
                NewTypeName = "Point",
                AsRecord = false
            }));

        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
        Assert.Contains("passed by reference", ex.Message, StringComparison.Ordinal);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    // Codex P1 on #3441: deconstruction destinations that receive converted storage must be tracked.
    [SkippableFact]
    public async Task ConvertTupleToStruct_AsRecordFalse_DeconstructionAliasRefElement_ThrowsAndWritesNothing()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                static void Bump(ref int v) => v++;

                public object Create()
                {
                    var point = (X: 1, Y: 2);
                    var (copy, _) = (point, 0);
                    Bump(ref copy.X);
                    return point;
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
                Line = 9,
                NewTypeName = "Point",
                AsRecord = false
            }));

        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
        Assert.Contains("passed by reference", ex.Message, StringComparison.Ordinal);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    // Codex P1 on #3441: anonymous carriers reached through container elements must resolve.
    [SkippableFact]
    public async Task ConvertTupleToStruct_AsRecordFalse_ArrayElementAnonymousCarrierRefElement_ThrowsAndWritesNothing()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                static void Bump(ref int v) => v++;

                public object Create()
                {
                    var point = (X: 1, Y: 2);
                    var boxes = new[] { new { Value = point } };
                    Bump(ref boxes[0].Value.X);
                    return point;
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
                Line = 9,
                NewTypeName = "Point",
                AsRecord = false
            }));

        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
        Assert.Contains("passed by reference", ex.Message, StringComparison.Ordinal);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    // Codex P1 on #3441: declaration-pattern copies of converted storage must be tracked.
    [SkippableFact]
    public async Task ConvertTupleToStruct_AsRecordFalse_DeclarationPatternAliasRefElement_ThrowsAndWritesNothing()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                static void Bump(ref int v) => v++;

                public object Create()
                {
                    var point = (X: 1, Y: 2);
                    if (point is var copy)
                        Bump(ref copy.X);
                    return point;
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
                Line = 9,
                NewTypeName = "Point",
                AsRecord = false
            }));

        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
        Assert.Contains("passed by reference", ex.Message, StringComparison.Ordinal);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    // Codex P1 on #3441: nonconstant container indices must resolve carriers conservatively.
    [SkippableFact]
    public async Task ConvertTupleToStruct_AsRecordFalse_NonConstantIndexAnonymousCarrierRefElement_ThrowsAndWritesNothing()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                static void Bump(ref int v) => v++;

                public object Create(int i)
                {
                    var point = (X: 1, Y: 2);
                    var boxes = new[] { new { Value = point } };
                    Bump(ref boxes[i].Value.X);
                    return point;
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
                Line = 9,
                NewTypeName = "Point",
                AsRecord = false
            }));

        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
        Assert.Contains("passed by reference", ex.Message, StringComparison.Ordinal);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }


    // Codex P1 on #3441: deconstruction from a tuple-valued expression (not only literal RHS).
    [SkippableFact]
    public async Task ConvertTupleToStruct_AsRecordFalse_TupleValuedExpressionDeconstructionAliasRefElement_ThrowsAndWritesNothing()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                static void Bump(ref int v) => v++;

                public object Create()
                {
                    var point = (X: 1, Y: 2);
                    var wrapped = (point, 0);
                    var (copy, _) = wrapped;
                    Bump(ref copy.X);
                    return point;
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
                Line = 9,
                NewTypeName = "Point",
                AsRecord = false
            }));

        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
        Assert.Contains("passed by reference", ex.Message, StringComparison.Ordinal);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    // Codex P1 on #3441: nested recursive-pattern designations must be traversed.
    [SkippableFact]
    public async Task ConvertTupleToStruct_AsRecordFalse_NestedRecursivePatternAliasRefElement_ThrowsAndWritesNothing()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                static void Bump(ref int v) => v++;

                public object Create()
                {
                    var point = (X: 1, Y: 2);
                    if (new { Value = point } is { Value: var copy })
                        Bump(ref copy.X);
                    return point;
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
                Line = 9,
                NewTypeName = "Point",
                AsRecord = false
            }));

        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
        Assert.Contains("passed by reference", ex.Message, StringComparison.Ordinal);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    // Codex P1 on #3441: inferred lambda parameters receiving converted element storage.
    [SkippableFact]
    public async Task ConvertTupleToStruct_AsRecordFalse_LambdaParameterAliasRefElement_ThrowsAndWritesNothing()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                static void Bump(ref int v) => v++;

                public object Create()
                {
                    var point = (X: 1, Y: 2);
                    System.Array.ForEach(new[] { point }, copy => Bump(ref copy.X));
                    return point;
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
                Line = 9,
                NewTypeName = "Point",
                AsRecord = false
            }));

        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
        Assert.Contains("passed by reference", ex.Message, StringComparison.Ordinal);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    // Codex P1 on #3441: map Zip-style multi-param callbacks to the storage-carrying input.
    [SkippableFact]
    public async Task ConvertTupleToStruct_AsRecordFalse_CallbackNonFirstParamRefElement_ThrowsAndWritesNothing()
    {
        const string source = """
            using System.Linq;

            namespace TestApp;

            public class Worker
            {
                static void Bump(ref int v) => v++;

                public object Create()
                {
                    var point = (X: 1, Y: 2);
                    var points = new[] { point };
                    var other = new[] { 0 };
                    _ = other.Zip(points, (n, copy) => { Bump(ref copy.X); return n; }).ToArray();
                    return point;
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
                NewTypeName = "Point",
                AsRecord = false
            }));

        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
        Assert.Contains("passed by reference", ex.Message, StringComparison.Ordinal);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    // Codex P2 on #3441: do not alias unrelated sibling callback params that keep the tuple type.
    [SkippableFact]
    public async Task ConvertTupleToStruct_AsRecordFalse_UnrelatedCallbackParam_Succeeds()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                static void Bump(ref int v) => v++;

                static void Run<T>(T value, System.Action<(int X, int Y)> action)
                {
                    _ = value;
                    action(default);
                }

                public object Create()
                {
                    var point = (X: 1, Y: 2);
                    Run(point, other => Bump(ref other.X));
                    return point;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ConvertTupleToStructOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ConvertTupleToStructParams
        {
            SourceFile = workspace.SourcePath,
            Line = 15,
            NewTypeName = "Point",
            AsRecord = false
        });

        Assert.True(result.Success);
        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("public struct Point", text, StringComparison.Ordinal);
        Assert.Contains("Bump(ref other.X)", text, StringComparison.Ordinal);
    }

    // Codex P1 on #3441: List<T>.ForEach instance receiver must map containing-type T.
    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConvertTupleToStruct_GenericInstanceReceiverCallbackRefElement_ThrowsAndWritesNothing(bool asRecord)
    {
        const string source = """
            using System.Collections.Generic;
            using System.Linq;

            namespace TestApp;

            public class Worker
            {
                static void Bump(ref int v) => v++;

                public object Create()
                {
                    var point = (X: 1, Y: 2);
                    var points = new[] { point }.ToList();
                    points.ForEach(copy => Bump(ref copy.X));
                    return point;
                }
            }
            """;

        AssertTupleContainerCompiles(source);
        await using var workspace = await TempWorkspace.CreateAsync(source);
        var original = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new ConvertTupleToStructOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ConvertTupleToStructParams
            {
                SourceFile = workspace.SourcePath,
                Line = 12,
                NewTypeName = "Point",
                AsRecord = asRecord
            }));

        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
        Assert.Contains(asRecord ? "element assignments" : "passed by reference", ex.Message, StringComparison.Ordinal);
        var after = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Equal(original, after);
        AssertTupleContainerCompiles(after);
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConvertTupleToStruct_ExplicitTupleContainerForEachRefElement_ThrowsAndWritesNothing(bool asRecord)
    {
        const string source = """
            using System.Collections.Generic;

            namespace TestApp;

            public class Worker
            {
                static void Bump(ref int v) => v++;

                public object Create()
                {
                    var point = (X: 1, Y: 2);
                    var list = new List<(int X, int Y)> { point };
                    list.ForEach(copy => Bump(ref copy.X));
                    return point;
                }
            }
            """;

        AssertTupleContainerCompiles(source);
        await using var workspace = await TempWorkspace.CreateAsync(source);
        var original = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new ConvertTupleToStructOperation(workspace.Context);

        var error = await Record.ExceptionAsync(() =>
            operation.ExecuteAsync(new ConvertTupleToStructParams
            {
                SourceFile = workspace.SourcePath,
                Line = 11,
                NewTypeName = "Point",
                AsRecord = asRecord
            }));

        var after = await File.ReadAllTextAsync(workspace.SourcePath);
        // Compile the actual file even if the tool incorrectly reported success.
        AssertTupleContainerCompiles(after);
        var ex = Assert.IsType<RefactoringException>(error);
        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
        Assert.Equal("3020", ex.ErrorCode);
        Assert.Equal(original, after);
    }

    [SkippableTheory]
    [InlineData(false, "{ point }")]
    [InlineData(true, "{ point }")]
    [InlineData(false, "new (int X, int Y)[] { point }")]
    [InlineData(true, "new (int X, int Y)[] { point }")]
    [InlineData(false, "[point]")]
    [InlineData(true, "[point]")]
    public async Task ConvertTupleToStruct_ExplicitTupleArrayForEachRefElement_ThrowsAndWritesNothing(bool asRecord, string initializer)
    {
        var source = $$"""
            using System;

            namespace TestApp;

            public class Worker
            {
                static void Bump(ref int v) => v++;

                public object Create()
                {
                    var point = (X: 1, Y: 2);
                    (int X, int Y)[] arr = {{initializer}};
                    Array.ForEach(arr, copy => Bump(ref copy.X));
                    return point;
                }
            }
            """;

        AssertTupleContainerCompiles(source);
        await using var workspace = await TempWorkspace.CreateAsync(source);
        var original = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new ConvertTupleToStructOperation(workspace.Context);

        var error = await Record.ExceptionAsync(() =>
            operation.ExecuteAsync(new ConvertTupleToStructParams
            {
                SourceFile = workspace.SourcePath,
                Line = 11,
                NewTypeName = "Point",
                AsRecord = asRecord
            }));

        var after = await File.ReadAllTextAsync(workspace.SourcePath);
        // Compile the actual file even if the tool incorrectly reported success.
        AssertTupleContainerCompiles(after);
        var ex = Assert.IsType<RefactoringException>(error);
        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
        Assert.Equal("3020", ex.ErrorCode);
        Assert.Equal(original, after);
    }

    private static void AssertTupleContainerCompiles(string source)
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Select(path => MetadataReference.CreateFromFile(path));
        var compilation = CSharpCompilation.Create(
            "TupleContainerRegression",
            [CSharpSyntaxTree.ParseText(source)],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var output = new MemoryStream();
        var result = compilation.Emit(output);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
    }

    // Codex P1 on #3441: query range variables must be tracked as storage aliases.
    [SkippableFact]
    public async Task ConvertTupleToStruct_AsRecordFalse_QueryRangeVariableRefElement_ThrowsAndWritesNothing()
    {
        const string source = """
            using System.Linq;

            namespace TestApp;

            public class Worker
            {
                static int Read(ref int v) => v;

                public object Create()
                {
                    var point = (X: 1, Y: 2);
                    _ = (from copy in new[] { point } select Read(ref copy.X)).ToArray();
                    return point;
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
                NewTypeName = "Point",
                AsRecord = false
            }));

        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
        Assert.Contains("passed by reference", ex.Message, StringComparison.Ordinal);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    // Codex P1 on #3441: inferred out var receiving converted generic type must be tracked.
    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConvertTupleToStruct_OutVarAliasRefElement_ThrowsAndWritesNothing(bool asRecord)
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                static void Bump(ref int v) => v++;

                static void Copy<T>(T value, out T copy) => copy = value;

                public object Create()
                {
                    var point = (X: 1, Y: 2);
                    Copy(point, out var copy);
                    Bump(ref copy.X);
                    return point;
                }
            }
            """;

        AssertTupleContainerCompiles(source);
        await using var workspace = await TempWorkspace.CreateAsync(source);
        var original = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new ConvertTupleToStructOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ConvertTupleToStructParams
            {
                SourceFile = workspace.SourcePath,
                Line = 11,
                NewTypeName = "Point",
                AsRecord = asRecord
            }));

        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
        Assert.Contains(asRecord ? "element assignments" : "passed by reference", ex.Message, StringComparison.Ordinal);
        var after = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Equal(original, after);
        AssertTupleContainerCompiles(after);
    }

    // Codex P1 on #3441: deconstruction sources returned by generic calls must
    // resolve to the forwarded storage (no variable initializer to walk).
    [SkippableTheory]
    [InlineData(false, "var (copy, _) = Wrap(point); Bump(ref copy.X);")]
    [InlineData(true, "var (copy, _) = Wrap(point); Bump(ref copy.X);")]
    [InlineData(false, "(var copy, var other) = Wrap(point); Bump(ref copy.X);")]
    [InlineData(true, "(var copy, var other) = Wrap(point); Bump(ref copy.X);")]
    [InlineData(false, "var wrapped = Wrap(point); var (copy, _) = wrapped; Bump(ref copy.X);")]
    [InlineData(true, "var wrapped = Wrap(point); var (copy, _) = wrapped; Bump(ref copy.X);")]
    [InlineData(false, "if (Wrap(point) is (var copy, _)) Bump(ref copy.X);")]
    [InlineData(true, "if (Wrap(point) is (var copy, _)) Bump(ref copy.X);")]
    public async Task ConvertTupleToStruct_DeconstructionFromGenericCallRefElement_ThrowsAndWritesNothing(bool asRecord, string statements)
    {
        var source = $$"""
            namespace TestApp;

            public class Worker
            {
                static void Bump(ref int v) => v++;
                static (T Value, int Other) Wrap<T>(T value) => (value, 0);

                public object Create()
                {
                    var point = (X: 1, Y: 2);
                    {{statements}}
                    return point;
                }
            }
            """;

        AssertTupleContainerCompiles(source);
        await using var workspace = await TempWorkspace.CreateAsync(source);
        var original = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new ConvertTupleToStructOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ConvertTupleToStructParams
            {
                SourceFile = workspace.SourcePath,
                Line = 10,
                NewTypeName = "Point",
                AsRecord = asRecord
            }));

        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
        Assert.Contains(asRecord ? "element assignments" : "passed by reference", ex.Message, StringComparison.Ordinal);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    // Codex P1 on #3441: iteration variables over query projections / container
    // literals must recover the anonymous-object carrier that supplies them.
    [SkippableTheory]
    [InlineData(false, "var query = from n in new[] { 0 } select new { Value = point }; foreach (var box in query) Bump(ref box.Value.X);")]
    [InlineData(true, "var query = from n in new[] { 0 } select new { Value = point }; foreach (var box in query) Bump(ref box.Value.X);")]
    [InlineData(false, "foreach (var box in from n in new[] { 0 } select new { Value = point }) Bump(ref box.Value.X);")]
    [InlineData(true, "foreach (var box in from n in new[] { 0 } select new { Value = point }) Bump(ref box.Value.X);")]
    [InlineData(false, "var query = from n in new[] { 0 } let b = new { Value = point } select b; foreach (var box in query) Bump(ref box.Value.X);")]
    [InlineData(true, "var query = from n in new[] { 0 } let b = new { Value = point } select b; foreach (var box in query) Bump(ref box.Value.X);")]
    [InlineData(false, "foreach (var box in new[] { new { Value = point } }) Bump(ref box.Value.X);")]
    [InlineData(true, "foreach (var box in new[] { new { Value = point } }) Bump(ref box.Value.X);")]
    public async Task ConvertTupleToStruct_QueryAnonymousProjectionCarrierRefElement_ThrowsAndWritesNothing(bool asRecord, string statements)
    {
        var source = $$"""
            using System.Linq;

            namespace TestApp;

            public class Worker
            {
                static void Bump(ref int v) => v++;

                public object Create()
                {
                    var point = (X: 1, Y: 2);
                    {{statements}}
                    return point;
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
                NewTypeName = "Point",
                AsRecord = asRecord
            }));

        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
        Assert.Contains(asRecord ? "element assignments" : "passed by reference", ex.Message, StringComparison.Ordinal);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    // Codex P1 on #3441: list-pattern element designations must be traversed.
    [SkippableFact]
    public async Task ConvertTupleToStruct_AsRecordFalse_ListPatternAliasRefElement_ThrowsAndWritesNothing()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                static void Bump(ref int v) => v++;

                public object Create()
                {
                    var point = (X: 1, Y: 2);
                    var points = new[] { point };
                    if (points is [var copy])
                        Bump(ref copy.X);
                    return point;
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
                Line = 9,
                NewTypeName = "Point",
                AsRecord = false
            }));

        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
        Assert.Contains("passed by reference", ex.Message, StringComparison.Ordinal);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task ConvertTupleToStruct_AsRecordDeconstructionAssignment_ThrowsAndWritesNothing()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                public object Create()
                {
                    var point = (X: 1, Y: 2);
                    (point.X, point.Y) = (3, 4);
                    return point;
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
                NewTypeName = "Point",
                AsRecord = true
            }));

        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
        Assert.Contains("element assignments", ex.Message, StringComparison.Ordinal);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }


    [SkippableFact]
    public async Task ConvertTupleToStruct_AsRecordCopyAliasElementAssignment_ThrowsAndWritesNothing()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                public object Create()
                {
                    var point = (X: 1, Y: 2);
                    var copy = point;
                    copy.X = 3;
                    return point;
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
                NewTypeName = "Point",
                AsRecord = true
            }));

        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
        Assert.Contains("element assignments", ex.Message, StringComparison.Ordinal);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task ConvertTupleToStruct_AsRecordConditionalCopyAliasElementAssignment_ThrowsAndWritesNothing()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                public object Create(bool condition)
                {
                    var point = (X: 1, Y: 2);
                    var copy = condition ? point : point;
                    copy.X = 3;
                    return point;
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
                NewTypeName = "Point",
                AsRecord = true
            }));

        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
        Assert.Contains("element assignments", ex.Message, StringComparison.Ordinal);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task ConvertTupleToStruct_AsRecordSwitchCopyAliasElementAssignment_ThrowsAndWritesNothing()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                public object Create(bool flag)
                {
                    var point = (X: 1, Y: 2);
                    var copy = flag switch
                    {
                        true => point,
                        _ => point
                    };
                    copy.X = 3;
                    return point;
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
                NewTypeName = "Point",
                AsRecord = true
            }));

        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
        Assert.Contains("element assignments", ex.Message, StringComparison.Ordinal);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task ConvertTupleToStruct_AsRecordAddressOfElement_ThrowsAndWritesNothing()
    {
        const string source = """
            namespace TestApp;

            public unsafe class Worker
            {
                public object Create()
                {
                    var point = (X: 1, Y: 2);
                    int* p = &point.X;
                    return point;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, allowUnsafe: true);
        var original = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new ConvertTupleToStructOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ConvertTupleToStructParams
            {
                SourceFile = workspace.SourcePath,
                Line = 7,
                NewTypeName = "Point",
                AsRecord = true
            }));

        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
        Assert.Contains("element assignments", ex.Message, StringComparison.Ordinal);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task ConvertTupleToStruct_AsRecordChainedMutableValueWrite_ThrowsAndWritesNothing()
    {
        const string source = """
            namespace TestApp;

            public struct Wrapper
            {
                public int Value;
            }

            public class Worker
            {
                public object Create()
                {
                    var point = (X: new Wrapper { Value = 1 }, Y: 2);
                    point.X.Value = 3;
                    return point;
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
                Line = 12,
                NewTypeName = "Point",
                AsRecord = true
            }));

        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
        Assert.Contains("element assignments", ex.Message, StringComparison.Ordinal);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task ConvertTupleToStruct_AsRecordRefLocalElement_ThrowsAndWritesNothing()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                public object Create()
                {
                    var point = (X: 1, Y: 2);
                    ref int value = ref point.X;
                    value = 3;
                    return point;
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
                NewTypeName = "Point",
                AsRecord = true
            }));

        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
        Assert.Contains("element assignments", ex.Message, StringComparison.Ordinal);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task ConvertTupleToStruct_AsRecordGenericEchoAliasElementAssignment_ThrowsAndWritesNothing()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                public object Create()
                {
                    var point = (X: 1, Y: 2);
                    var copy = Echo(point);
                    copy.X = 3;
                    return point;
                }

                static T Echo<T>(T value) => value;
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
                NewTypeName = "Point",
                AsRecord = true
            }));

        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
        Assert.Contains("element assignments", ex.Message, StringComparison.Ordinal);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task ConvertTupleToStruct_AsRecordUnrelatedExplicitContainerWrite_Succeeds()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                public object Create()
                {
                    var point = (X: 1, Y: 2);
                    (int X, int Y)[] other = [(4, 5)];
                    other[0].X = 6;
                    return point;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ConvertTupleToStructOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ConvertTupleToStructParams
        {
            SourceFile = workspace.SourcePath,
            Line = 7,
            NewTypeName = "Point",
            AsRecord = true
        });

        Assert.True(result.Success);
        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("public record struct Point", text);
        Assert.Contains("public int X { get; init; }", text);
        Assert.Contains("other[0].X = 6", text);
    }

    [SkippableFact]
    public async Task ConvertTupleToStruct_AsRecordExtractedFromContainerAliasElementAssignment_ThrowsAndWritesNothing()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                public object Create()
                {
                    var point = (X: 1, Y: 2);
                    var points = new[] { point };
                    var copy = points[0];
                    copy.X = 3;
                    return points;
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
                NewTypeName = "Point",
                AsRecord = true
            }));

        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
        Assert.Contains("element assignments", ex.Message, StringComparison.Ordinal);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task ConvertTupleToStruct_AsRecordNamedEchoAliasElementAssignment_ThrowsAndWritesNothing()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                public object Create()
                {
                    var point = (X: 1, Y: 2);
                    var copy = Pick(value: point, other: 0);
                    copy.X = 3;
                    return point;
                }

                static T Pick<T>(int other, T value) => value;
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
                NewTypeName = "Point",
                AsRecord = true
            }));

        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
        Assert.Contains("element assignments", ex.Message, StringComparison.Ordinal);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task ConvertTupleToStruct_AsRecordFirstFromContainerAlias_ThrowsAndWritesNothing()
    {
        const string source = """
            using System.Linq;

            namespace TestApp;

            public class Worker
            {
                public object Create()
                {
                    var point = (X: 1, Y: 2);
                    var points = new[] { point };
                    var copy = points.First();
                    copy.X = 3;
                    return point;
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
                Line = 9,
                NewTypeName = "Point",
                AsRecord = true
            }));

        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
        Assert.Contains("element assignments", ex.Message, StringComparison.Ordinal);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task ConvertTupleToStruct_AsRecordSelectProjection_DoesNotFalseReject()
    {
        const string source = """
            using System.Linq;

            namespace TestApp;

            public class Worker
            {
                public object Create()
                {
                    var point = (X: 1, Y: 2);
                    var points = new[] { point };
                    var more = points.Select(_ => (X: 3, Y: 4)).ToArray();
                    more[0].X = 5;
                    return point;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ConvertTupleToStructOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ConvertTupleToStructParams
        {
            SourceFile = workspace.SourcePath,
            Line = 9,
            NewTypeName = "Point",
            AsRecord = true
        });

        Assert.True(result.Success);
        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("public record struct Point", text);
        Assert.Contains("more[0].X = 5", text);
    }

    [SkippableFact]
    public async Task ConvertTupleToStruct_AsRecordIdentitySelectAliasElementWrite_ThrowsAndWritesNothing()
    {
        const string source = """
            using System.Linq;

            namespace TestApp;

            public class Worker
            {
                public object Create()
                {
                    var point = (X: 1, Y: 2);
                    var points = new[] { point };
                    var more = points.Select(x => x).ToArray();
                    more[0].X = 5;
                    return point;
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
                Line = 9,
                NewTypeName = "Point",
                AsRecord = true
            }));

        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
        Assert.Contains("element assignments", ex.Message, StringComparison.Ordinal);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task ConvertTupleToStruct_AsRecordRefForeachElementWrite_ThrowsAndWritesNothing()
    {
        const string source = """
            using System;

            namespace TestApp;

            public class Worker
            {
                public object Create()
                {
                    var point = (X: 1, Y: 2);
                    var points = new[] { point };
                    foreach (ref var p in points.AsSpan())
                        p.X = 3;
                    return points;
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
                Line = 9,
                NewTypeName = "Point",
                AsRecord = true
            }));

        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
        Assert.Contains("element assignments", ex.Message, StringComparison.Ordinal);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }


    [SkippableFact]
    public async Task ConvertTupleToStruct_AsRecordTupleEmbeddedAliasElementAssignment_ThrowsAndWritesNothing()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                public object Create()
                {
                    var point = (X: 1, Y: 2);
                    var wrapped = (Value: point, Other: 0);
                    wrapped.Value.X = 3;
                    return point;
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
                NewTypeName = "Point",
                AsRecord = true
            }));

        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
        Assert.Contains("element assignments", ex.Message, StringComparison.Ordinal);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task ConvertTupleToStruct_AsRecordInArgument_ThrowsAndWritesNothing()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                public object Create()
                {
                    var point = (X: 1, Y: 2);
                    Read(in point.X);
                    return point;
                }

                static void Read(in int value) { _ = value; }
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
                NewTypeName = "Point",
                AsRecord = true
            }));

        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
        Assert.Contains("element assignments", ex.Message, StringComparison.Ordinal);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }



    [SkippableFact]
    public async Task ConvertTupleToStruct_AsRecordMemorySpanElementWrite_ThrowsAndWritesNothing()
    {
        const string source = """
            using System;

            namespace TestApp;

            public class Worker
            {
                public object Create()
                {
                    var point = (X: 1, Y: 2);
                    var points = new[] { point };
                    var memory = points.AsMemory();
                    memory.Span[0].X = 3;
                    return points;
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
                Line = 9,
                NewTypeName = "Point",
                AsRecord = true
            }));

        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
        Assert.Contains("element assignments", ex.Message, StringComparison.Ordinal);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task ConvertTupleToStruct_AsRecordIdentityConvertAllAliasElementWrite_ThrowsAndWritesNothing()
    {
        const string source = """
            using System;

            namespace TestApp;

            public class Worker
            {
                public object Create()
                {
                    var point = (X: 1, Y: 2);
                    var points = new[] { point };
                    var more = Array.ConvertAll(points, x => x);
                    more[0].X = 5;
                    return more;
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
                Line = 9,
                NewTypeName = "Point",
                AsRecord = true
            }));

        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
        Assert.Contains("element assignments", ex.Message, StringComparison.Ordinal);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task ConvertTupleToStruct_AsRecordFreshConvertAll_DoesNotFalseReject()
    {
        const string source = """
            using System;

            namespace TestApp;

            public class Worker
            {
                public object Create()
                {
                    var point = (X: 1, Y: 2);
                    var points = new[] { point };
                    var more = Array.ConvertAll(points, _ => (X: 3, Y: 4));
                    more[0].X = 5;
                    return point;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ConvertTupleToStructOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ConvertTupleToStructParams
        {
            SourceFile = workspace.SourcePath,
            Line = 9,
            NewTypeName = "Point",
            AsRecord = true
        });

        Assert.True(result.Success);
        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("public record struct Point", text);
        Assert.Contains("more[0].X = 5", text);
    }

    [SkippableFact]
    public async Task ConvertTupleToStruct_AsRecordInferredContainerElementWrite_ThrowsAndWritesNothing()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                public object Create()
                {
                    var point = (X: 1, Y: 2);
                    var points = new[] { point };
                    points[0].X = 3;
                    return points;
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
                NewTypeName = "Point",
                AsRecord = true
            }));

        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
        Assert.Contains("element assignments", ex.Message, StringComparison.Ordinal);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task ConvertTupleToStruct_AsRecordContainerToArrayAliasElementWrite_ThrowsAndWritesNothing()
    {
        const string source = """
            using System.Linq;

            namespace TestApp;

            public class Worker
            {
                public object Create()
                {
                    var point = (X: 1, Y: 2);
                    var points = new[] { point };
                    var more = points.ToArray();
                    more[0].X = 3;
                    return more;
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
                Line = 9,
                NewTypeName = "Point",
                AsRecord = true
            }));

        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
        Assert.Contains("element assignments", ex.Message, StringComparison.Ordinal);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task ConvertTupleToStruct_AsRecordRefLocalWholeValueAlias_ThrowsAndWritesNothing()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                public object Create()
                {
                    var point = (X: 1, Y: 2);
                    ref var alias = ref point;
                    alias.X = 3;
                    return point;
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
                NewTypeName = "Point",
                AsRecord = true
            }));

        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
        Assert.Contains("element assignments", ex.Message, StringComparison.Ordinal);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task ConvertTupleToStruct_AsRecordEnumerableRepeatAlias_ThrowsAndWritesNothing()
    {
        const string source = """
            using System.Linq;

            namespace TestApp;

            public class Worker
            {
                public object Create()
                {
                    var point = (X: 1, Y: 2);
                    var points = Enumerable.Repeat(point, 1);
                    var copy = points.First();
                    copy.X = 3;
                    return point;
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
                Line = 9,
                NewTypeName = "Point",
                AsRecord = true
            }));

        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
        Assert.Contains("element assignments", ex.Message, StringComparison.Ordinal);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task ConvertTupleToStruct_AsRecordParenthesizedIdentitySelect_ThrowsAndWritesNothing()
    {
        const string source = """
            using System.Linq;

            namespace TestApp;

            public class Worker
            {
                public object Create()
                {
                    var point = (X: 1, Y: 2);
                    var points = new[] { point };
                    var more = points.Select(x => (x)).ToArray();
                    more[0].X = 5;
                    return points;
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
                Line = 9,
                NewTypeName = "Point",
                AsRecord = true
            }));

        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
        Assert.Contains("element assignments", ex.Message, StringComparison.Ordinal);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }


    [SkippableFact]
    public async Task ConvertTupleToStruct_AsRecordBlockBodiedIdentitySelect_ThrowsAndWritesNothing()
    {
        const string source = """
            using System.Linq;

            namespace TestApp;

            public class Worker
            {
                public object Create()
                {
                    var point = (X: 1, Y: 2);
                    var points = new[] { point };
                    var more = points.Select(x => { return x; }).ToArray();
                    more[0].X = 5;
                    return point;
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
                Line = 9,
                NewTypeName = "Point",
                AsRecord = true
            }));

        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
        Assert.Contains("element assignments", ex.Message, StringComparison.Ordinal);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task ConvertTupleToStruct_AsRecordIndexedIdentitySelect_ThrowsAndWritesNothing()
    {
        const string source = """
            using System.Linq;

            namespace TestApp;

            public class Worker
            {
                public object Create()
                {
                    var point = (X: 1, Y: 2);
                    var points = new[] { point };
                    var more = points.Select((x, index) => x).ToArray();
                    more[0].X = 5;
                    return points;
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
                Line = 9,
                NewTypeName = "Point",
                AsRecord = true
            }));

        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
        Assert.Contains("element assignments", ex.Message, StringComparison.Ordinal);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task ConvertTupleToStruct_AsRecordAsSpanElementWrite_ThrowsAndWritesNothing()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                public object Create()
                {
                    var point = (X: 1, Y: 2);
                    var points = new[] { point };
                    points.AsSpan()[0].X = 3;
                    return points;
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
                NewTypeName = "Point",
                AsRecord = true
            }));

        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
        Assert.Contains("element assignments", ex.Message, StringComparison.Ordinal);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task ConvertTupleToStruct_AsRecordSelectManyResultSelector_DoesNotFalseReject()
    {
        const string source = """
            using System.Linq;

            namespace TestApp;

            public class Worker
            {
                public object Create()
                {
                    var point = (X: 1, Y: 2);
                    var nested = new[] { new[] { point } };
                    var fresh = nested.SelectMany(x => x, (row, item) => (X: 3, Y: 4)).ToArray();
                    fresh[0].X = 5;
                    return point;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ConvertTupleToStructOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ConvertTupleToStructParams
        {
            SourceFile = workspace.SourcePath,
            Line = 9,
            NewTypeName = "Point",
            AsRecord = true
        });

        Assert.True(result.Success);
        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("public record struct Point", text);
        Assert.Contains("fresh[0].X = 5", text);
    }

    [SkippableFact]
    public async Task ConvertTupleToStruct_AsRecordTaskFromResultAlias_ThrowsAndWritesNothing()
    {
        const string source = """
            using System.Threading.Tasks;

            namespace TestApp;

            public class Worker
            {
                public async Task<object> Create()
                {
                    var point = (X: 1, Y: 2);
                    var task = Task.FromResult(point);
                    var copy = await task;
                    copy.X = 3;
                    return point;
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
                Line = 9,
                NewTypeName = "Point",
                AsRecord = true
            }));

        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
        Assert.Contains("element assignments", ex.Message, StringComparison.Ordinal);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task ConvertTupleToStruct_AsRecordTaskResultAlias_ThrowsAndWritesNothing()
    {
        const string source = """
            using System.Threading.Tasks;

            namespace TestApp;

            public class Worker
            {
                public object Create()
                {
                    var point = (X: 1, Y: 2);
                    var task = Task.FromResult(point);
                    var copy = task.Result;
                    copy.X = 3;
                    return point;
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
                Line = 9,
                NewTypeName = "Point",
                AsRecord = true
            }));

        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
        Assert.Contains("element assignments", ex.Message, StringComparison.Ordinal);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task ConvertTupleToStruct_AsRecordPositionalTupleAggregateItem1_ThrowsAndWritesNothing()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                public object Create()
                {
                    var point = (X: 1, Y: 2);
                    var wrapped = (point, 0);
                    wrapped.Item1.X = 3;
                    return point;
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
                NewTypeName = "Point",
                AsRecord = true
            }));

        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
        Assert.Contains("element assignments", ex.Message, StringComparison.Ordinal);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task ConvertTupleToStruct_AsRecordInferredTupleAggregateName_ThrowsAndWritesNothing()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                public object Create()
                {
                    var point = (X: 1, Y: 2);
                    var wrapped = (point, 0);
                    wrapped.point.X = 3;
                    return point;
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
                NewTypeName = "Point",
                AsRecord = true
            }));

        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
        Assert.Contains("element assignments", ex.Message, StringComparison.Ordinal);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }


    [SkippableFact]
    public async Task ConvertTupleToStruct_AsRecordUnrelatedAggregateMember_Succeeds()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                public object Create()
                {
                    var point = (X: 1, Y: 2);
                    var wrapped = (Value: point, Fresh: (X: 3, Y: 4));
                    var fresh = wrapped.Fresh;
                    fresh.X = 5;
                    return point;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ConvertTupleToStructOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ConvertTupleToStructParams
        {
            SourceFile = workspace.SourcePath,
            Line = 7,
            NewTypeName = "Point",
            AsRecord = true
        });

        Assert.True(result.Success);
        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("public record struct Point", text);
        Assert.Contains("fresh.X = 5", text);
    }

    [SkippableFact]
    public async Task ConvertTupleToStruct_AsRecordRefArgument_ThrowsAndWritesNothing()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                public object Create()
                {
                    var point = (X: 1, Y: 2);
                    Mutate(ref point.X);
                    return point;
                }

                static void Mutate(ref int value) => value++;
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
                NewTypeName = "Point",
                AsRecord = true
            }));

        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
        Assert.Contains("element assignments", ex.Message, StringComparison.Ordinal);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task ConvertTupleToStruct_AsRecord_LanguageVersionBelowCSharp10_ThrowsAndWritesNothing()
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

        await using var workspace = await TempWorkspace.CreateAsync(source, langVersion: "9.0");
        var original = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new ConvertTupleToStructOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ConvertTupleToStructParams
            {
                SourceFile = workspace.SourcePath,
                Line = 7,
                NewTypeName = "Point",
                AsRecord = true
            }));

        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
        Assert.Contains("C# 10", ex.Message, StringComparison.Ordinal);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }


    [SkippableFact]
    public async Task ConvertTupleToStruct_AsRecord_ReferencingConsumerBelowCSharp10_ThrowsAndWritesNothing()
    {
        await using var workspace = await TempWorkspace.CreateReferencingConsumerAsync(
            originLangVersion: "10.0",
            consumerLangVersion: "8.0");
        var originalOrigin = await File.ReadAllTextAsync(workspace.SourcePath);
        var originalConsumer = await File.ReadAllTextAsync(workspace.SecondarySourcePath);
        var operation = new ConvertTupleToStructOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ConvertTupleToStructParams
            {
                SourceFile = workspace.SourcePath,
                Line = 7,
                NewTypeName = "Point",
                AsRecord = true
            }));

        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
        Assert.Contains("C# 10", ex.Message, StringComparison.Ordinal);
        Assert.Equal(originalOrigin, await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Equal(originalConsumer, await File.ReadAllTextAsync(workspace.SecondarySourcePath));
    }

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
            DocumentEditableHelpers.ValidateDocumentIsEditable(document, workspace));

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

        public static Task<TempWorkspace> CreateAsync(
            string source,
            string fileName = "Worker.cs",
            string? langVersion = null,
            bool allowUnsafe = false) =>
            CreateAsync(langVersion, allowUnsafe, (fileName, source));

        public static Task<TempWorkspace> CreateAsync(params (string FileName, string Source)[] files) =>
            CreateAsync(langVersion: null, allowUnsafe: false, files);

        public static Task<TempWorkspace> CreateAsync(
            string? langVersion,
            params (string FileName, string Source)[] files) =>
            CreateAsync(langVersion, allowUnsafe: false, files);

        public static async Task<TempWorkspace> CreateAsync(
            string? langVersion,
            bool allowUnsafe,
            params (string FileName, string Source)[] files)
        {
            Skip.IfNot(ModuleInitializer.MsBuildAvailable, ModuleInitializer.MsBuildError ?? "MSBuild not available");

            var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpConvertTuple_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);

            var projectPath = Path.Combine(directory, "TestApp.csproj");
            var langVersionProperty = string.IsNullOrWhiteSpace(langVersion)
                ? ""
                : $"\n                    <LangVersion>{langVersion}</LangVersion>";
            var allowUnsafeProperty = allowUnsafe
                ? "\n                    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>"
                : "";
            await File.WriteAllTextAsync(projectPath, $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net9.0</TargetFramework>
                    <ImplicitUsings>enable</ImplicitUsings>
                    <Nullable>enable</Nullable>{langVersionProperty}{allowUnsafeProperty}
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


        public static async Task<TempWorkspace> CreateReferencingConsumerAsync(
            string originLangVersion,
            string consumerLangVersion)
        {
            Skip.IfNot(ModuleInitializer.MsBuildAvailable, ModuleInitializer.MsBuildError ?? "MSBuild not available");

            var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpConvertTupleRef_" + Guid.NewGuid().ToString("N"));
            var appDir = Path.Combine(directory, "App");
            var otherDir = Path.Combine(directory, "Other");
            Directory.CreateDirectory(appDir);
            Directory.CreateDirectory(otherDir);

            var appProject = Path.Combine(appDir, "App.csproj");
            var otherProject = Path.Combine(otherDir, "Other.csproj");
            var appSource = Path.Combine(appDir, "Worker.cs");
            var otherSource = Path.Combine(otherDir, "Client.cs");

            await File.WriteAllTextAsync(appProject, $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net9.0</TargetFramework>
                    <Nullable>enable</Nullable>
                    <LangVersion>{originLangVersion}</LangVersion>
                  </PropertyGroup>
                </Project>
                """);
            await File.WriteAllTextAsync(otherProject, $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net9.0</TargetFramework>
                    <Nullable>enable</Nullable>
                    <LangVersion>{consumerLangVersion}</LangVersion>
                  </PropertyGroup>
                  <ItemGroup>
                    <ProjectReference Include="..\App\App.csproj" />
                  </ItemGroup>
                </Project>
                """);
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

    // Codex P1 on #3441: selectors that conditionally forward the element
    // (?:, casts, switch arms, block returns) still alias converted storage.
    [SkippableTheory]
    [InlineData(false, "var copies = points.Select(x => flag ? x : x).ToArray(); Bump(ref copies[0].X);")]
    [InlineData(true, "var copies = points.Select(x => flag ? x : x).ToArray(); Bump(ref copies[0].X);")]
    [InlineData(false, "var copies = points.Select(x => (flag ? (x) : x)).ToArray(); Bump(ref copies[0].X);")]
    [InlineData(true, "var copies = points.Select(x => (flag ? (x) : x)).ToArray(); Bump(ref copies[0].X);")]
    [InlineData(false, "var copies = points.Select(x => ((int X, int Y))x).ToArray(); Bump(ref copies[0].X);")]
    [InlineData(true, "var copies = points.Select(x => ((int X, int Y))x).ToArray(); Bump(ref copies[0].X);")]
    [InlineData(false, "var copies = points.Select(x => flag switch { true => x, _ => x }).ToArray(); Bump(ref copies[0].X);")]
    [InlineData(true, "var copies = points.Select(x => flag switch { true => x, _ => x }).ToArray(); Bump(ref copies[0].X);")]
    [InlineData(false, "var copies = points.Select(x => { if (flag) return x; return x; }).ToArray(); Bump(ref copies[0].X);")]
    [InlineData(true, "var copies = points.Select(x => { if (flag) return x; return x; }).ToArray(); Bump(ref copies[0].X);")]
    [InlineData(false, "var copies = points.Select((x, i) => i > 0 ? x : x).ToArray(); Bump(ref copies[0].X);")]
    [InlineData(true, "var copies = points.Select((x, i) => i > 0 ? x : x).ToArray(); Bump(ref copies[0].X);")]
    public async Task ConvertTupleToStruct_ConditionalForwardingSelectorRefElement_ThrowsAndWritesNothing(bool asRecord, string statements)
    {
        var source = $$"""
            using System.Linq;

            namespace TestApp;

            public class Worker
            {
                static void Bump(ref int v) => v++;

                public object Create(bool flag)
                {
                    var point = (X: 1, Y: 2);
                    var points = new[] { point };
                    {{statements}}
                    return point;
                }
            }
            """;

        AssertTupleContainerCompiles(source);
        await using var workspace = await TempWorkspace.CreateAsync(source);
        var original = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new ConvertTupleToStructOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ConvertTupleToStructParams
            {
                SourceFile = workspace.SourcePath,
                Line = 11,
                NewTypeName = "Point",
                AsRecord = asRecord
            }));

        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
        Assert.Contains(asRecord ? "element assignments" : "passed by reference", ex.Message, StringComparison.Ordinal);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    // Codex P2 on #3441: element members that are direct deconstruction
    // destinations are setter writes (like point.X = 3) under asRecord:false.
    [SkippableTheory]
    [InlineData("(point.X, point.Y) = (3, 4);")]
    [InlineData("((point.X), point.Y) = (3, 4);")]
    [InlineData("(point.X, (point.Y, other)) = (3, (4, 5));")]
    [InlineData("(point.X, other) = (3, 5);")]
    public async Task ConvertTupleToStruct_AsRecordFalse_DeconstructionAssignmentDirectElementWrite_Succeeds(string statements)
    {
        var source = $$"""
            namespace TestApp;

            public class Worker
            {
                public object Create()
                {
                    var point = (X: 1, Y: 2);
                    var other = 0;
                    {{statements}}
                    return point;
                }
            }
            """;

        AssertTupleContainerCompiles(source);
        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ConvertTupleToStructOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ConvertTupleToStructParams
        {
            SourceFile = workspace.SourcePath,
            Line = 7,
            NewTypeName = "Point",
            AsRecord = false
        });

        Assert.True(result.Success);
        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("public struct Point", text, StringComparison.Ordinal);
        Assert.Contains("public int X { get; set; }", text, StringComparison.Ordinal);
        Assert.Contains(statements, text, StringComparison.Ordinal);
        AssertTupleContainerCompiles(text);
    }

    // Codex P2 on #3441: under asRecord:true deconstruction destinations keep the
    // existing direct element-assignment rejection (init-only properties).
    [SkippableTheory]
    [InlineData("(point.X, point.Y) = (3, 4);")]
    [InlineData("((point.X), point.Y) = (3, 4);")]
    [InlineData("(point.X, (point.Y, other)) = (3, (4, 5));")]
    [InlineData("(point.X, other) = (3, 5);")]
    public async Task ConvertTupleToStruct_AsRecordTrue_DeconstructionAssignmentDirectElementWrite_ThrowsAndWritesNothing(string statements)
    {
        var source = $$"""
            namespace TestApp;

            public class Worker
            {
                public object Create()
                {
                    var point = (X: 1, Y: 2);
                    var other = 0;
                    {{statements}}
                    return point;
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
                NewTypeName = "Point",
                AsRecord = true
            }));

        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
        Assert.Contains("element assignments", ex.Message, StringComparison.Ordinal);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    // Codex P1 on #3441: extended property-pattern paths ({ Inner.Value: var copy }) and
    // nested property/positional/list combos must resolve every member component.
    [SkippableTheory]
    [InlineData(false, "var box = new { Inner = new { Value = point } }; if (box is { Inner.Value: var copy }) Bump(ref copy.X);")]
    [InlineData(true, "var box = new { Inner = new { Value = point } }; if (box is { Inner.Value: var copy }) Bump(ref copy.X);")]
    [InlineData(false, "if (new { Inner = new { Value = point } } is { Inner.Value: var copy }) Bump(ref copy.X);")]
    [InlineData(true, "if (new { Inner = new { Value = point } } is { Inner.Value: var copy }) Bump(ref copy.X);")]
    [InlineData(false, "var box = new { Outer = new { Inner = new { Value = point } } }; if (box is { Outer.Inner.Value: var copy }) Bump(ref copy.X);")]
    [InlineData(true, "var box = new { Outer = new { Inner = new { Value = point } } }; if (box is { Outer.Inner.Value: var copy }) Bump(ref copy.X);")]
    [InlineData(false, "var box = new { Outer = new { Inner = new { Value = point } } }; if (box is { Outer.Inner: { Value: var copy } }) Bump(ref copy.X);")]
    [InlineData(true, "var box = new { Outer = new { Inner = new { Value = point } } }; if (box is { Outer.Inner: { Value: var copy } }) Bump(ref copy.X);")]
    [InlineData(false, "var box = new { Outer = new { Inner = new { Value = point } } }; if (box is { Outer: { Inner.Value: var copy } }) Bump(ref copy.X);")]
    [InlineData(true, "var box = new { Outer = new { Inner = new { Value = point } } }; if (box is { Outer: { Inner.Value: var copy } }) Bump(ref copy.X);")]
    [InlineData(false, "var box = new { Inner = new { Value = point } }; if (box is { Inner.Value: { } copy }) Bump(ref copy.X);")]
    [InlineData(true, "var box = new { Inner = new { Value = point } }; if (box is { Inner.Value: { } copy }) Bump(ref copy.X);")]
    [InlineData(false, "var box = new { Inner = new { Pair = (point, 0) } }; if (box is { Inner.Pair: (var copy, _) }) Bump(ref copy.X);")]
    [InlineData(true, "var box = new { Inner = new { Pair = (point, 0) } }; if (box is { Inner.Pair: (var copy, _) }) Bump(ref copy.X);")]
    [InlineData(false, "var box = new { Pair = (point, 0) }; if (box is { Pair: (var copy, _) }) Bump(ref copy.X);")]
    [InlineData(true, "var box = new { Pair = (point, 0) }; if (box is { Pair: (var copy, _) }) Bump(ref copy.X);")]
    [InlineData(false, "var box = new { Items = new[] { point } }; if (box is { Items: [var copy] }) Bump(ref copy.X);")]
    [InlineData(true, "var box = new { Items = new[] { point } }; if (box is { Items: [var copy] }) Bump(ref copy.X);")]
    [InlineData(false, "var box = new { Inner = new { Items = new[] { point } } }; if (box is { Inner.Items: [var copy] }) Bump(ref copy.X);")]
    [InlineData(true, "var box = new { Inner = new { Items = new[] { point } } }; if (box is { Inner.Items: [var copy] }) Bump(ref copy.X);")]
    [InlineData(false, "var box = new { Inner = new { Value = point } }; switch (box) { case { Inner.Value: var copy }: Bump(ref copy.X); break; }")]
    [InlineData(true, "var box = new { Inner = new { Value = point } }; switch (box) { case { Inner.Value: var copy }: Bump(ref copy.X); break; }")]
    public async Task ConvertTupleToStruct_ExtendedPropertyPatternRefElement_ThrowsAndWritesNothing(bool asRecord, string statements)
    {
        var source = $$"""
            using System.Linq;

            namespace TestApp;

            public class Worker
            {
                static void Bump(ref int v) => v++;

                public object Create(bool flag)
                {
                    var point = (X: 1, Y: 2);
                    var points = new[] { point };
                    {{statements}}
                    return point;
                }
            }
            """;

        AssertTupleContainerCompiles(source);
        await using var workspace = await TempWorkspace.CreateAsync(source);
        var original = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new ConvertTupleToStructOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ConvertTupleToStructParams
            {
                SourceFile = workspace.SourcePath,
                Line = 11,
                NewTypeName = "Point",
                AsRecord = asRecord
            }));

        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
        Assert.Contains(asRecord ? "element assignments" : "passed by reference", ex.Message, StringComparison.Ordinal);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    // Codex P1 on #3441: null-forgiving (and checked/unchecked) wrappers around converted
    // storage are transparent for by-ref element access and aliasing; aggregate member
    // reads (box.Value, (point, 0).Item1) alias the embedded storage.
    [SkippableTheory]
    [InlineData(false, "Bump(ref point!.X);")]
    [InlineData(true, "Bump(ref point!.X);")]
    [InlineData(false, "Bump(ref (point!).X);")]
    [InlineData(true, "Bump(ref (point!).X);")]
    [InlineData(false, "Bump(ref point!.X!);")]
    [InlineData(true, "Bump(ref point!.X!);")]
    [InlineData(false, "var copy = point!; Bump(ref copy.X);")]
    [InlineData(true, "var copy = point!; Bump(ref copy.X);")]
    [InlineData(false, "var copy = (point!); Bump(ref copy.X);")]
    [InlineData(true, "var copy = (point!); Bump(ref copy.X);")]
    [InlineData(false, "var copy = checked(point); Bump(ref copy.X);")]
    [InlineData(true, "var copy = checked(point); Bump(ref copy.X);")]
    [InlineData(false, "var copy = unchecked(point!); Bump(ref copy.X);")]
    [InlineData(true, "var copy = unchecked(point!); Bump(ref copy.X);")]
    [InlineData(false, "var copy = flag ? point! : point; Bump(ref copy.X);")]
    [InlineData(true, "var copy = flag ? point! : point; Bump(ref copy.X);")]
    [InlineData(false, "ref var alias = ref point!; Bump(ref alias.X);")]
    [InlineData(true, "ref var alias = ref point!; Bump(ref alias.X);")]
    [InlineData(false, "var copies = points.Select(x => x!).ToArray(); Bump(ref copies[0].X);")]
    [InlineData(true, "var copies = points.Select(x => x!).ToArray(); Bump(ref copies[0].X);")]
    [InlineData(false, "var copies = points.Select(x => checked(x)).ToArray(); Bump(ref copies[0].X);")]
    [InlineData(true, "var copies = points.Select(x => checked(x)).ToArray(); Bump(ref copies[0].X);")]
    [InlineData(false, "if (point! is var copy) Bump(ref copy.X);")]
    [InlineData(true, "if (point! is var copy) Bump(ref copy.X);")]
    [InlineData(false, "var box = new { Value = point! }; if (box is { Value: var copy }) Bump(ref copy.X);")]
    [InlineData(true, "var box = new { Value = point! }; if (box is { Value: var copy }) Bump(ref copy.X);")]
    [InlineData(false, "var copies = points!; Bump(ref copies[0].X);")]
    [InlineData(true, "var copies = points!; Bump(ref copies[0].X);")]
    [InlineData(false, "Bump(ref points![0].X);")]
    [InlineData(true, "Bump(ref points![0].X);")]
    [InlineData(false, "Bump(ref points[0]!.X);")]
    [InlineData(true, "Bump(ref points[0]!.X);")]
    [InlineData(false, "var copies = new[] { point! }; Bump(ref copies[0].X);")]
    [InlineData(true, "var copies = new[] { point! }; Bump(ref copies[0].X);")]
    [InlineData(false, "ref var r = ref points![0]; Bump(ref r.X);")]
    [InlineData(true, "ref var r = ref points![0]; Bump(ref r.X);")]
    [InlineData(false, "var copies = points!.ToArray(); Bump(ref copies[0].X);")]
    [InlineData(true, "var copies = points!.ToArray(); Bump(ref copies[0].X);")]
    [InlineData(false, "var copies = points.Select(x => x).ToArray()!; Bump(ref copies[0].X);")]
    [InlineData(true, "var copies = points.Select(x => x).ToArray()!; Bump(ref copies[0].X);")]
    [InlineData(false, "var copy = checked(points[0]); Bump(ref copy.X);")]
    [InlineData(true, "var copy = checked(points[0]); Bump(ref copy.X);")]
    [InlineData(false, "var (copy, _) = (point!, 0); Bump(ref copy.X);")]
    [InlineData(true, "var (copy, _) = (point!, 0); Bump(ref copy.X);")]
    [InlineData(false, "var copy = (point!, 0).Item1; Bump(ref copy.X);")]
    [InlineData(true, "var copy = (point!, 0).Item1; Bump(ref copy.X);")]
    [InlineData(false, "var copy = points![0]!; Bump(ref copy.X);")]
    [InlineData(true, "var copy = points![0]!; Bump(ref copy.X);")]
    [InlineData(false, "var box = new { Value = point }; var copy = box!.Value; Bump(ref copy.X);")]
    [InlineData(true, "var box = new { Value = point }; var copy = box!.Value; Bump(ref copy.X);")]
    [InlineData(false, "var box = new { Value = point }; if (box! is { Value: var copy }) Bump(ref copy.X);")]
    [InlineData(true, "var box = new { Value = point }; if (box! is { Value: var copy }) Bump(ref copy.X);")]
    [InlineData(false, "var copy = (point, 0).Item1; Bump(ref copy.X);")]
    [InlineData(true, "var copy = (point, 0).Item1; Bump(ref copy.X);")]
    [InlineData(false, "var box = new { Value = point }; var copy = box.Value; Bump(ref copy.X);")]
    [InlineData(true, "var box = new { Value = point }; var copy = box.Value; Bump(ref copy.X);")]
    [InlineData(false, "var box = new { Inner = new { Value = point } }; var copy = box!.Inner.Value; Bump(ref copy.X);")]
    [InlineData(true, "var box = new { Inner = new { Value = point } }; var copy = box!.Inner.Value; Bump(ref copy.X);")]
    [InlineData(false, "var wrapped = (point, 0); var copy = wrapped.Item1!; Bump(ref copy.X);")]
    [InlineData(true, "var wrapped = (point, 0); var copy = wrapped.Item1!; Bump(ref copy.X);")]
    public async Task ConvertTupleToStruct_NullForgivingStorageRefElement_ThrowsAndWritesNothing(bool asRecord, string statements)
    {
        var source = $$"""
            using System.Linq;

            namespace TestApp;

            public class Worker
            {
                static void Bump(ref int v) => v++;

                public object Create(bool flag)
                {
                    var point = (X: 1, Y: 2);
                    var points = new[] { point };
                    {{statements}}
                    return point;
                }
            }
            """;

        AssertTupleContainerCompiles(source);
        await using var workspace = await TempWorkspace.CreateAsync(source);
        var original = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new ConvertTupleToStructOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ConvertTupleToStructParams
            {
                SourceFile = workspace.SourcePath,
                Line = 11,
                NewTypeName = "Point",
                AsRecord = asRecord
            }));

        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
        Assert.Contains(asRecord ? "element assignments" : "passed by reference", ex.Message, StringComparison.Ordinal);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    // #3347: tuples placed into collections (initializer / Add / collection expression)
    // and read back via ToArray/ToList/indexer/foreach must stay gated.
    [SkippableTheory]
    [InlineData(false, "var list = new List<(int X, int Y)> { point }; var arr = list.ToArray(); Bump(ref arr[0].X);")]
    [InlineData(true, "var list = new List<(int X, int Y)> { point }; var arr = list.ToArray(); Bump(ref arr[0].X);")]
    [InlineData(false, "var list = new List<(int X, int Y)> { point! }; var arr = list.ToArray(); Bump(ref arr[0].X);")]
    [InlineData(true, "var list = new List<(int X, int Y)> { point! }; var arr = list.ToArray(); Bump(ref arr[0].X);")]
    [InlineData(false, "var list = new List<(int X, int Y)> { point }; var arr = list!.ToArray(); Bump(ref arr[0].X);")]
    [InlineData(true, "var list = new List<(int X, int Y)> { point }; var arr = list!.ToArray(); Bump(ref arr[0].X);")]
    [InlineData(false, "var list = new List<(int X, int Y)> { point }; var copy = list[0]; Bump(ref copy.X);")]
    [InlineData(true, "var list = new List<(int X, int Y)> { point }; var copy = list[0]; Bump(ref copy.X);")]
    [InlineData(false, "var list = new List<(int X, int Y)> { point }; foreach (var copy in list) { var c = copy; Bump(ref c.X); }")]
    [InlineData(true, "var list = new List<(int X, int Y)> { point }; foreach (var copy in list) { var c = copy; Bump(ref c.X); }")]
    [InlineData(false, "var list = new List<(int X, int Y)> { point }; var copies = list.ToList(); var c = copies[0]; Bump(ref c.X);")]
    [InlineData(true, "var list = new List<(int X, int Y)> { point }; var copies = list.ToList(); var c = copies[0]; Bump(ref c.X);")]
    [InlineData(false, "var list = new List<(int X, int Y)>(); list.Add(point); var arr = list.ToArray(); Bump(ref arr[0].X);")]
    [InlineData(true, "var list = new List<(int X, int Y)>(); list.Add(point); var arr = list.ToArray(); Bump(ref arr[0].X);")]
    [InlineData(false, "var list = new List<(int X, int Y)>(); list.Add(point!); var copy = list[0]; Bump(ref copy.X);")]
    [InlineData(true, "var list = new List<(int X, int Y)>(); list.Add(point!); var copy = list[0]; Bump(ref copy.X);")]
    [InlineData(false, "List<(int X, int Y)> list = [point]; var arr = list.ToArray(); Bump(ref arr[0].X);")]
    [InlineData(true, "List<(int X, int Y)> list = [point]; var arr = list.ToArray(); Bump(ref arr[0].X);")]
    [InlineData(false, "(int X, int Y)[] arr = [point]; Bump(ref arr[0].X);")]
    [InlineData(true, "(int X, int Y)[] arr = [point]; Bump(ref arr[0].X);")]
    [InlineData(false, "(int X, int Y)[] arr = [point!]; var copy = arr[0]; Bump(ref copy.X);")]
    [InlineData(true, "(int X, int Y)[] arr = [point!]; var copy = arr[0]; Bump(ref copy.X);")]
    [InlineData(false, "var arr = new (int X, int Y)[] { point }; Bump(ref arr[0].X);")]
    [InlineData(true, "var arr = new (int X, int Y)[] { point }; Bump(ref arr[0].X);")]
    [InlineData(false, "var map = new Dictionary<int, (int X, int Y)> { [0] = point }; var copy = map[0]; Bump(ref copy.X);")]
    [InlineData(true, "var map = new Dictionary<int, (int X, int Y)> { [0] = point }; var copy = map[0]; Bump(ref copy.X);")]
    [InlineData(false, "var map = new Dictionary<int, (int X, int Y)> { { 0, point } }; var copy = map[0]; Bump(ref copy.X);")]
    [InlineData(true, "var map = new Dictionary<int, (int X, int Y)> { { 0, point } }; var copy = map[0]; Bump(ref copy.X);")]
    [InlineData(false, "var list = new List<(int X, int Y)>(); list.Insert(0, point); var arr = list.ToArray(); Bump(ref arr[0].X);")]
    [InlineData(true, "var list = new List<(int X, int Y)>(); list.Insert(0, point); var arr = list.ToArray(); Bump(ref arr[0].X);")]
    [InlineData(false, "var list = new List<(int X, int Y)> { (0, 0) }; list[0] = point; var arr = list.ToArray(); Bump(ref arr[0].X);")]
    [InlineData(true, "var list = new List<(int X, int Y)> { (0, 0) }; list[0] = point; var arr = list.ToArray(); Bump(ref arr[0].X);")]
    [InlineData(false, "var queue = new Queue<(int X, int Y)>(); queue.Enqueue(point); var arr = queue.ToArray(); Bump(ref arr[0].X);")]
    [InlineData(true, "var queue = new Queue<(int X, int Y)>(); queue.Enqueue(point); var arr = queue.ToArray(); Bump(ref arr[0].X);")]
    public async Task ConvertTupleToStruct_CollectionInitializerCarrierRefElement_ThrowsAndWritesNothing(bool asRecord, string statements)
    {
        var source = $$"""
            using System.Collections.Generic;
            using System.Linq;

            namespace TestApp;

            public class Worker
            {
                static void Bump(ref int v) => v++;

                public object Create(bool flag)
                {
                    var point = (X: 1, Y: 2);
                    {{statements}}
                    return point;
                }
            }
            """;

        AssertTupleContainerCompiles(source);
        await using var workspace = await TempWorkspace.CreateAsync(source);
        var original = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new ConvertTupleToStructOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ConvertTupleToStructParams
            {
                SourceFile = workspace.SourcePath,
                Line = 12,
                NewTypeName = "Point",
                AsRecord = asRecord
            }));

        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
        Assert.Contains(asRecord ? "element assignments" : "passed by reference", ex.Message, StringComparison.Ordinal);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    // Codex P1 on #3441: Select/ConvertAll selectors that return storage-returning
    // delegates (x => () => x) must alias through the produced sequence so
    // getters[0]() is gated like a directly assigned delegate.
    [SkippableTheory]
    [InlineData(false, "var getters = points.Select(x => (Func<(int X, int Y)>)(() => x)).ToArray(); var copy = getters[0](); Bump(ref copy.X);")]
    [InlineData(true, "var getters = points.Select(x => (Func<(int X, int Y)>)(() => x)).ToArray(); var copy = getters[0](); Bump(ref copy.X);")]
    [InlineData(false, "var getters = points.Select<(int X, int Y), Func<(int X, int Y)>>(x => () => x).ToArray(); var copy = getters[0](); Bump(ref copy.X);")]
    [InlineData(true, "var getters = points.Select<(int X, int Y), Func<(int X, int Y)>>(x => () => x).ToArray(); var copy = getters[0](); Bump(ref copy.X);")]
    [InlineData(false, "var getters = points.Select<(int X, int Y), Func<(int X, int Y)>>(x => { return () => x; }).ToArray(); var copy = getters[0](); Bump(ref copy.X);")]
    [InlineData(true, "var getters = points.Select<(int X, int Y), Func<(int X, int Y)>>(x => { return () => x; }).ToArray(); var copy = getters[0](); Bump(ref copy.X);")]
    [InlineData(false, "var getters = points.Select<(int X, int Y), Func<(int X, int Y)>>(x => flag ? () => x : () => x).ToArray(); var copy = getters[0](); Bump(ref copy.X);")]
    [InlineData(true, "var getters = points.Select<(int X, int Y), Func<(int X, int Y)>>(x => flag ? () => x : () => x).ToArray(); var copy = getters[0](); Bump(ref copy.X);")]
    [InlineData(false, "var getters = points.Select<(int X, int Y), Func<Func<(int X, int Y)>>>(x => () => () => x).ToArray(); var copy = getters[0]()(); Bump(ref copy.X);")]
    [InlineData(true, "var getters = points.Select<(int X, int Y), Func<Func<(int X, int Y)>>>(x => () => () => x).ToArray(); var copy = getters[0]()(); Bump(ref copy.X);")]
    [InlineData(false, "var getters = points.Select<(int X, int Y), Func<(int X, int Y)>>((x, i) => () => x).ToArray(); var copy = getters[0](); Bump(ref copy.X);")]
    [InlineData(true, "var getters = points.Select<(int X, int Y), Func<(int X, int Y)>>((x, i) => () => x).ToArray(); var copy = getters[0](); Bump(ref copy.X);")]
    [InlineData(false, "var list = new System.Collections.Generic.List<(int X, int Y)> { point }; var getters = list.ConvertAll<Func<(int X, int Y)>>(x => () => x); var copy = getters[0](); Bump(ref copy.X);")]
    [InlineData(true, "var list = new System.Collections.Generic.List<(int X, int Y)> { point }; var getters = list.ConvertAll<Func<(int X, int Y)>>(x => () => x); var copy = getters[0](); Bump(ref copy.X);")]
    [InlineData(false, "var getters = System.Linq.Enumerable.Select<(int X, int Y), Func<(int X, int Y)>>(points, x => () => x).ToArray(); var copy = getters[0](); Bump(ref copy.X);")]
    [InlineData(true, "var getters = System.Linq.Enumerable.Select<(int X, int Y), Func<(int X, int Y)>>(points, x => () => x).ToArray(); var copy = getters[0](); Bump(ref copy.X);")]
    [InlineData(false, "var getters = points.Select<(int X, int Y), Func<(int X, int Y)>>(x => () => x).ToArray(); var copy = getters[0].Invoke(); Bump(ref copy.X);")]
    [InlineData(true, "var getters = points.Select<(int X, int Y), Func<(int X, int Y)>>(x => () => x).ToArray(); var copy = getters[0].Invoke(); Bump(ref copy.X);")]
    [InlineData(false, "var getters = points.Select<(int X, int Y), Func<(int X, int Y)>>(x => () => x).ToArray(); foreach (var get in getters) { var copy = get(); Bump(ref copy.X); }")]
    [InlineData(true, "var getters = points.Select<(int X, int Y), Func<(int X, int Y)>>(x => () => x).ToArray(); foreach (var get in getters) { var copy = get(); Bump(ref copy.X); }")]

    public async Task ConvertTupleToStruct_SelectorReturnedDelegateRefElement_ThrowsAndWritesNothing(bool asRecord, string statements)
    {
        var source = $$"""
            using System;
            using System.Linq;

            namespace TestApp;

            public class Worker
            {
                static void Bump(ref int v) => v++;

                public object Create(bool flag)
                {
                    var point = (X: 1, Y: 2);
                    var points = new[] { point };
                    {{statements}}
                    return point;
                }
            }
            """;

        AssertTupleContainerCompiles(source);
        await using var workspace = await TempWorkspace.CreateAsync(source);
        var original = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new ConvertTupleToStructOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ConvertTupleToStructParams
            {
                SourceFile = workspace.SourcePath,
                Line = 12,
                NewTypeName = "Point",
                AsRecord = asRecord
            }));

        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
        Assert.Contains(asRecord ? "element assignments" : "passed by reference", ex.Message, StringComparison.Ordinal);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    // Codex P1 on #3441: selectors whose leaf is an opaque delegate-returning call
    // (x => MakeGetter(x)), not an inline lambda, must still gate the produced sequence.
    [SkippableTheory]
    [InlineData(false, "static Func<T> MakeGetter<T>(T value) => () => value; var getters = points.Select(x => MakeGetter(x)).ToArray(); var copy = getters[0](); Bump(ref copy.X);")]
    [InlineData(true, "static Func<T> MakeGetter<T>(T value) => () => value; var getters = points.Select(x => MakeGetter(x)).ToArray(); var copy = getters[0](); Bump(ref copy.X);")]
    [InlineData(false, "static Func<(int X, int Y)> MakeGetter((int X, int Y) value) => () => value; var getters = points.Select(x => MakeGetter(x)).ToArray(); var copy = getters[0](); Bump(ref copy.X);")]
    [InlineData(true, "static Func<(int X, int Y)> MakeGetter((int X, int Y) value) => () => value; var getters = points.Select(x => MakeGetter(x)).ToArray(); var copy = getters[0](); Bump(ref copy.X);")]
    [InlineData(false, "static Func<T> MakeGetter<T>(T value) => () => value; var getters = points.Select<(int X, int Y), Func<(int X, int Y)>>(x => { return MakeGetter(x); }).ToArray(); var copy = getters[0](); Bump(ref copy.X);")]
    [InlineData(true, "static Func<T> MakeGetter<T>(T value) => () => value; var getters = points.Select<(int X, int Y), Func<(int X, int Y)>>(x => { return MakeGetter(x); }).ToArray(); var copy = getters[0](); Bump(ref copy.X);")]
    [InlineData(false, "static Func<T> MakeGetter<T>(T value) => () => value; var getters = points.Select<(int X, int Y), Func<(int X, int Y)>>(x => flag ? MakeGetter(x) : MakeGetter(x)).ToArray(); var copy = getters[0](); Bump(ref copy.X);")]
    [InlineData(true, "static Func<T> MakeGetter<T>(T value) => () => value; var getters = points.Select<(int X, int Y), Func<(int X, int Y)>>(x => flag ? MakeGetter(x) : MakeGetter(x)).ToArray(); var copy = getters[0](); Bump(ref copy.X);")]
    [InlineData(false, "static Func<Func<T>> MakeNested<T>(T value) => () => () => value; var getters = points.Select<(int X, int Y), Func<Func<(int X, int Y)>>>(x => MakeNested(x)).ToArray(); var copy = getters[0]()(); Bump(ref copy.X);")]
    [InlineData(true, "static Func<Func<T>> MakeNested<T>(T value) => () => () => value; var getters = points.Select<(int X, int Y), Func<Func<(int X, int Y)>>>(x => MakeNested(x)).ToArray(); var copy = getters[0]()(); Bump(ref copy.X);")]
    [InlineData(false, "static Func<T> MakeGetter<T>(T value) => () => value; var list = new System.Collections.Generic.List<(int X, int Y)> { point }; var getters = list.ConvertAll(x => MakeGetter(x)); var copy = getters[0](); Bump(ref copy.X);")]
    [InlineData(true, "static Func<T> MakeGetter<T>(T value) => () => value; var list = new System.Collections.Generic.List<(int X, int Y)> { point }; var getters = list.ConvertAll(x => MakeGetter(x)); var copy = getters[0](); Bump(ref copy.X);")]
    [InlineData(false, "static Func<T> MakeGetter<T>(T value) => () => value; var get = MakeGetter(point); var copy = get(); Bump(ref copy.X);")]
    [InlineData(true, "static Func<T> MakeGetter<T>(T value) => () => value; var get = MakeGetter(point); var copy = get(); Bump(ref copy.X);")]
    [InlineData(false, "static Func<(int X, int Y)> MakeGetter((int X, int Y) value) => () => value; var get = MakeGetter(point); var copy = get(); Bump(ref copy.X);")]
    [InlineData(true, "static Func<(int X, int Y)> MakeGetter((int X, int Y) value) => () => value; var get = MakeGetter(point); var copy = get(); Bump(ref copy.X);")]
    public async Task ConvertTupleToStruct_OpaqueDelegateReturningCallRefElement_ThrowsAndWritesNothing(bool asRecord, string statements)
    {
        var source = $$"""
            using System;
            using System.Linq;

            namespace TestApp;

            public class Worker
            {
                static void Bump(ref int v) => v++;

                public object Create(bool flag)
                {
                    var point = (X: 1, Y: 2);
                    var points = new[] { point };
                    {{statements}}
                    return point;
                }
            }
            """;

        AssertTupleContainerCompiles(source);
        await using var workspace = await TempWorkspace.CreateAsync(source);
        var original = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new ConvertTupleToStructOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ConvertTupleToStructParams
            {
                SourceFile = workspace.SourcePath,
                Line = 12,
                NewTypeName = "Point",
                AsRecord = asRecord
            }));

        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
        Assert.Contains(asRecord ? "element assignments" : "passed by reference", ex.Message, StringComparison.Ordinal);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    // Codex P2 on #3441: nested lambda/local-function returns must not taint the
    // outer delegate's return analysis (false positive on unrelated outer result).
    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConvertTupleToStruct_NestedFunctionReturnDoesNotTaintOuterDelegate_Succeeds(bool asRecord)
    {
        var source = """
            using System;

            namespace TestApp;

            public class Worker
            {
                static void Bump(ref int v) => v++;

                public object Create()
                {
                    var point = (X: 1, Y: 2);
                    Func<(int A, int B, int C)> get = () =>
                    {
                        Func<(int X, int Y)> nested = () =>
                        {
                            return point;
                        };
                        _ = nested;
                        return (A: 9, B: 9, C: 9);
                    };
                    var copy = get();
                    Bump(ref copy.A);
                    return point;
                }
            }
            """;

        AssertTupleContainerCompiles(source);
        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ConvertTupleToStructOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ConvertTupleToStructParams
        {
            SourceFile = workspace.SourcePath,
            Line = 11,
            NewTypeName = "Point",
            AsRecord = asRecord
        });

        Assert.True(result.Success);
        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains(asRecord ? "public record struct Point" : "public struct Point", text, StringComparison.Ordinal);
        Assert.Contains("Bump(ref copy.A)", text, StringComparison.Ordinal);
    }

    // Codex P1 on #3441: converted storage flowing through ?? / ??= must alias
    // both coalesce operands (and related ?: nesting).
    [SkippableTheory]
    [InlineData(false, "static T? Maybe<T>(T value) where T : struct => value; var maybe = Maybe(point); var copy = maybe ?? point; Bump(ref copy.X);")]
    [InlineData(true, "static T? Maybe<T>(T value) where T : struct => value; var maybe = Maybe(point); var copy = maybe ?? point; Bump(ref copy.X);")]
    [InlineData(false, "static T? Maybe<T>(T value) where T : struct => value; var maybe = Maybe(point); var copy = ((int X, int Y)?)point ?? maybe ?? point; Bump(ref copy.X);")]
    [InlineData(true, "static T? Maybe<T>(T value) where T : struct => value; var maybe = Maybe(point); var copy = ((int X, int Y)?)point ?? maybe ?? point; Bump(ref copy.X);")]
    [InlineData(false, "static T? Maybe<T>(T value) where T : struct => value; var maybe = Maybe(point); var copy = maybe ?? (flag ? point : point); Bump(ref copy.X);")]
    [InlineData(true, "static T? Maybe<T>(T value) where T : struct => value; var maybe = Maybe(point); var copy = maybe ?? (flag ? point : point); Bump(ref copy.X);")]
    [InlineData(false, "static T? Maybe<T>(T value) where T : struct => value; var maybe = Maybe(point); var copy = (maybe ?? point)!; Bump(ref copy.X);")]
    [InlineData(true, "static T? Maybe<T>(T value) where T : struct => value; var maybe = Maybe(point); var copy = (maybe ?? point)!; Bump(ref copy.X);")]
    [InlineData(false, "static T? Maybe<T>(T value) where T : struct => value; var maybe = Maybe(point); var copy = maybe ?? point!; Bump(ref copy.X);")]
    [InlineData(true, "static T? Maybe<T>(T value) where T : struct => value; var maybe = Maybe(point); var copy = maybe ?? point!; Bump(ref copy.X);")]
    [InlineData(false, "(int X, int Y)? maybe = null; maybe ??= point; var copy = maybe.Value; Bump(ref copy.X);")]
    [InlineData(true, "(int X, int Y)? maybe = null; maybe ??= point; var copy = maybe.Value; Bump(ref copy.X);")]
    [InlineData(false, "static T? Maybe<T>(T value) where T : struct => value; var maybe = Maybe(point); (int X, int Y)? holder = null; holder ??= maybe ?? point; var copy = holder.Value; Bump(ref copy.X);")]
    [InlineData(true, "static T? Maybe<T>(T value) where T : struct => value; var maybe = Maybe(point); (int X, int Y)? holder = null; holder ??= maybe ?? point; var copy = holder.Value; Bump(ref copy.X);")]
    [InlineData(false, "static T? Maybe<T>(T value) where T : struct => value; var a = Maybe(point); var b = Maybe(point); var copy = a ?? b ?? point; Bump(ref copy.X);")]
    [InlineData(true, "static T? Maybe<T>(T value) where T : struct => value; var a = Maybe(point); var b = Maybe(point); var copy = a ?? b ?? point; Bump(ref copy.X);")]
    [InlineData(false, "static T? Maybe<T>(T value) where T : struct => value; var maybe = Maybe(point); var copy = flag ? (maybe ?? point) : point; Bump(ref copy.X);")]
    [InlineData(true, "static T? Maybe<T>(T value) where T : struct => value; var maybe = Maybe(point); var copy = flag ? (maybe ?? point) : point; Bump(ref copy.X);")]
    public async Task ConvertTupleToStruct_NullCoalescingStorageRefElement_ThrowsAndWritesNothing(bool asRecord, string statements)
    {
        var source = $$"""
            #nullable enable
            using System;

            namespace TestApp;

            public class Worker
            {
                static void Bump(ref int v) => v++;

                public object Create(bool flag)
                {
                    var point = (X: 1, Y: 2);
                    {{statements}}
                    return point;
                }
            }
            """;

        AssertTupleContainerCompiles(source);
        await using var workspace = await TempWorkspace.CreateAsync(source);
        var original = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new ConvertTupleToStructOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ConvertTupleToStructParams
            {
                SourceFile = workspace.SourcePath,
                Line = 12,
                NewTypeName = "Point",
                AsRecord = asRecord
            }));

        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
        Assert.Contains(asRecord ? "element assignments" : "passed by reference", ex.Message, StringComparison.Ordinal);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }


    // Codex P2 on #3441: Dictionary<object, tuple>.Add(point, …) must not taint
    // the receiver via the unrelated object key — value reads stay tuples.
    [SkippableTheory]
    [InlineData(false, "dict.Add(point, (X: 3, Y: 4)); var copy = dict[point]; Bump(ref copy.X);")]
    [InlineData(true, "dict.Add(point, (X: 3, Y: 4)); var copy = dict[point]; Bump(ref copy.X);")]
    [InlineData(false, "dict.Add(point, (X: 3, Y: 4)); var copy = dict.Values.First(); Bump(ref copy.X);")]
    [InlineData(true, "dict.Add(point, (X: 3, Y: 4)); var copy = dict.Values.First(); Bump(ref copy.X);")]
    [InlineData(false, "dict.TryAdd(point, (X: 3, Y: 4)); var copy = dict[point]; Bump(ref copy.X);")]
    [InlineData(true, "dict.TryAdd(point, (X: 3, Y: 4)); var copy = dict[point]; Bump(ref copy.X);")]
    public async Task ConvertTupleToStruct_DictionaryObjectKeyInsertion_DoesNotTaintValues_Succeeds(bool asRecord, string statements)
    {
        var source = $$"""
            using System.Collections.Generic;
            using System.Linq;

            namespace TestApp;

            public class Worker
            {
                static void Bump(ref int v) => v++;

                public object Create()
                {
                    var point = (X: 1, Y: 2);
                    Dictionary<object, (int X, int Y)> dict = new();
                    {{statements}}
                    return point;
                }
            }
            """;

        AssertTupleContainerCompiles(source);
        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ConvertTupleToStructOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ConvertTupleToStructParams
        {
            SourceFile = workspace.SourcePath,
            Line = 12,
            NewTypeName = "Point",
            AsRecord = asRecord
        });

        Assert.True(result.Success);
        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains(asRecord ? "public record struct Point" : "public struct Point", text, StringComparison.Ordinal);
        Assert.Contains("Bump(ref copy.X)", text, StringComparison.Ordinal);
    }

    // Codex P2 on #3441: still refuse when the converted tuple is the dictionary value.
    [SkippableTheory]
    [InlineData(false, "dict.Add(0, point); var copy = dict[0]; Bump(ref copy.X);")]
    [InlineData(true, "dict.Add(0, point); var copy = dict[0]; Bump(ref copy.X);")]
    [InlineData(false, "dict.TryAdd(0, point); var copy = dict[0]; Bump(ref copy.X);")]
    [InlineData(true, "dict.TryAdd(0, point); var copy = dict[0]; Bump(ref copy.X);")]
    public async Task ConvertTupleToStruct_DictionaryValueInsertionRefElement_ThrowsAndWritesNothing(bool asRecord, string statements)
    {
        var source = $$"""
            using System.Collections.Generic;
            using System.Linq;

            namespace TestApp;

            public class Worker
            {
                static void Bump(ref int v) => v++;

                public object Create()
                {
                    var point = (X: 1, Y: 2);
                    Dictionary<object, (int X, int Y)> dict = new();
                    {{statements}}
                    return point;
                }
            }
            """;

        AssertTupleContainerCompiles(source);
        await using var workspace = await TempWorkspace.CreateAsync(source);
        var original = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new ConvertTupleToStructOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ConvertTupleToStructParams
            {
                SourceFile = workspace.SourcePath,
                Line = 12,
                NewTypeName = "Point",
                AsRecord = asRecord
            }));

        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
        Assert.Contains(asRecord ? "element assignments" : "passed by reference", ex.Message, StringComparison.Ordinal);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    // Codex P2 on #3441: Runner<T,(int X,int Y)> Make<T>(T) — callback bound to the
    // second (original tuple) type argument must not be treated as storage.
    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConvertTupleToStruct_ContainingTypeCallbackUnrelatedTypeArg_Succeeds(bool asRecord)
    {
        var source = """
            using System;

            namespace TestApp;

            public class Runner<T, TCallback>
            {
                public void Run(Action<TCallback> action) => action(default!);
            }

            public class Worker
            {
                static void Bump(ref int v) => v++;

                static Runner<T, (int X, int Y)> Make<T>(T value)
                {
                    _ = value;
                    return new Runner<T, (int X, int Y)>();
                }

                public object Create()
                {
                    var point = (X: 1, Y: 2);
                    var runner = Make(point);
                    runner.Run(copy => Bump(ref copy.X));
                    return point;
                }
            }
            """;

        AssertTupleContainerCompiles(source);
        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ConvertTupleToStructOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ConvertTupleToStructParams
        {
            SourceFile = workspace.SourcePath,
            Line = 22,
            NewTypeName = "Point",
            AsRecord = asRecord
        });

        Assert.True(result.Success);
        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains(asRecord ? "public record struct Point" : "public struct Point", text, StringComparison.Ordinal);
        Assert.Contains("Bump(ref copy.X)", text, StringComparison.Ordinal);
    }

    // Codex P2 on #3441: still refuse when the callback binds the storage-sourced type arg.
    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConvertTupleToStruct_ContainingTypeCallbackStorageTypeArg_ThrowsAndWritesNothing(bool asRecord)
    {
        var source = """
            using System;

            namespace TestApp;

            public class Runner<T, TOther>
            {
                public void Run(Action<T> action) => action(default!);
            }

            public class Worker
            {
                static void Bump(ref int v) => v++;

                static Runner<T, (int A, int B)> Make<T>(T value)
                {
                    _ = value;
                    return new Runner<T, (int A, int B)>();
                }

                public object Create()
                {
                    var point = (X: 1, Y: 2);
                    var runner = Make(point);
                    runner.Run(copy => Bump(ref copy.X));
                    return point;
                }
            }
            """;

        AssertTupleContainerCompiles(source);
        await using var workspace = await TempWorkspace.CreateAsync(source);
        var original = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new ConvertTupleToStructOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ConvertTupleToStructParams
            {
                SourceFile = workspace.SourcePath,
                Line = 22,
                NewTypeName = "Point",
                AsRecord = asRecord
            }));

        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
        Assert.Contains(asRecord ? "element assignments" : "passed by reference", ex.Message, StringComparison.Ordinal);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }


}

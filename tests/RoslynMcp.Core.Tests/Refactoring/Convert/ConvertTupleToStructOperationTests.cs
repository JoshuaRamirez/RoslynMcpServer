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

    // Codex P1 on #3441: pointer aliases derived through arithmetic / casts / ++/--.
    [SkippableTheory]
    [InlineData(false, "var p = &point; var q = p + 0; Bump(ref q->X);")]
    [InlineData(true, "var p = &point; var q = p + 0; Bump(ref q->X);")]
    [InlineData(false, "var p = &point; var q = p - 0; Bump(ref q->X);")]
    [InlineData(true, "var p = &point; var q = p - 0; Bump(ref q->X);")]
    [InlineData(false, "var p = &point; var q = ((int X, int Y)*)p; Bump(ref q->X);")]
    [InlineData(true, "var p = &point; var q = ((int X, int Y)*)p; Bump(ref q->X);")]
    [InlineData(false, "var p = &point; var q = ++p; Bump(ref q->X);")]
    [InlineData(true, "var p = &point; var q = ++p; Bump(ref q->X);")]
    [InlineData(false, "var p = &point; var q = p++; Bump(ref q->X);")]
    [InlineData(true, "var p = &point; var q = p++; Bump(ref q->X);")]
    [InlineData(false, "var p = &point; var q = (p += 0); Bump(ref q->X);")]
    [InlineData(true, "var p = &point; var q = (p += 0); Bump(ref q->X);")]
    public async Task ConvertTupleToStruct_PointerArithmeticAliasRefElement_ThrowsAndWritesNothing(
        bool asRecord,
        string statements)
    {
        var source = $$"""
            namespace TestApp;

            public unsafe class Worker
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

        await using var workspace = await TempWorkspace.CreateAsync(source, allowUnsafe: true);
        var original = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new ConvertTupleToStructOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ConvertTupleToStructParams
            {
                SourceFile = workspace.SourcePath,
                Line = 9,
                NewTypeName = "Point",
                AsRecord = asRecord
            }));

        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
        Assert.Contains(asRecord ? "element assignments" : "passed by reference", ex.Message, StringComparison.Ordinal);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    // Codex P1 on #3441: nested convertible creations inside dynamic/array/collection
    // containers must resolve the enclosing storage (TryGetAssignedStorageSymbol used to
    // return null when the tuple's parent was an initializer, skipping validation).
    // e.g. dynamic[] points = { (1, 2) }; Bump(ref points[0].Item1);
    [SkippableTheory]
    [InlineData(false, "dynamic[] points = { (1, 2) }; Bump(ref points[0].Item1);")]
    [InlineData(true, "dynamic[] points = { (1, 2) }; Bump(ref points[0].Item1);")]
    [InlineData(false, "dynamic[] points = [(1, 2)]; Bump(ref points[0].Item1);")]
    [InlineData(true, "dynamic[] points = [(1, 2)]; Bump(ref points[0].Item1);")]
    [InlineData(false, "var bag = new List<dynamic> { (1, 2) }; Bump(ref bag[0].Item1);")]
    [InlineData(true, "var bag = new List<dynamic> { (1, 2) }; Bump(ref bag[0].Item1);")]
    public async Task ConvertTupleToStruct_NestedCreationEnclosingStorageRefElement_ThrowsAndWritesNothing(
        bool asRecord,
        string statements)
    {
        var source = $$"""
            using System.Collections.Generic;

            namespace TestApp;

            public class Worker
            {
                static void Bump(ref int v) => v++;

                public object Create()
                {
                    {{statements}}
                    return 0;
                }
            }
            """;

        // Matches Codex evidence: these sites are accepted by the compiler as dynamic
        // binding targets in the workspace even when ref-dynamic→ref-int is invalid;
        // conversion must still refuse before rewriting (same stance as DynamicAliasRefElement).
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

    // Codex P1 on #3441: dynamic aliases of converted storage must refuse by-ref element uses
    // (type-mismatch must not short-circuit before consulting the tracked symbol).
    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConvertTupleToStruct_DynamicAliasRefElement_ThrowsAndWritesNothing(bool asRecord)
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                static void Bump(ref int v) => v++;

                public object Create()
                {
                    var point = (1, 2);
                    dynamic copy = point;
                    Bump(ref copy.Item1);
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
                AsRecord = asRecord
            }));

        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
        Assert.Contains(asRecord ? "element assignments" : "passed by reference", ex.Message, StringComparison.Ordinal);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    // Codex P1 on #3441: built-in identity/boxing casts must be unwrapped when collecting
    // aliases (dynamic copy = (dynamic)point; also (object)point in a boxing chain) —
    // user-defined conversions are not followed.
    [SkippableTheory]
    [InlineData(false, "dynamic copy = (dynamic)point;")]
    [InlineData(false, "object boxed = (object)point; dynamic copy = boxed;")]
    [InlineData(true, "dynamic copy = (dynamic)point;")]
    [InlineData(true, "object boxed = (object)point; dynamic copy = boxed;")]
    public async Task ConvertTupleToStruct_ValuePreservingCastAliasRefElement_ThrowsAndWritesNothing(
        bool asRecord,
        string aliasStatements)
    {
        var source = $$"""
            namespace TestApp;

            public class Worker
            {
                static void Bump(ref int v) => v++;

                public object Create()
                {
                    var point = (1, 2);
                    {{aliasStatements}}
                    Bump(ref copy.Item1);
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
                AsRecord = asRecord
            }));

        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
        Assert.Contains(asRecord ? "element assignments" : "passed by reference", ex.Message, StringComparison.Ordinal);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    // Codex P1 on #3441 (PRRT_kwDORD0nYs602M2j): built-in identity/boxing `as` expressions
    // must unwrap like parenthesized casts (object boxed = point as object).
    [SkippableTheory]
    [InlineData(false, "object boxed = point as object; dynamic copy = boxed;")]
    [InlineData(true, "object boxed = point as object; dynamic copy = boxed;")]
    [InlineData(false, "dynamic copy = point as dynamic;")]
    [InlineData(true, "dynamic copy = point as dynamic;")]
    public async Task ConvertTupleToStruct_BoxingAsExpressionAliasRefElement_ThrowsAndWritesNothing(
        bool asRecord,
        string aliasStatements)
    {
        var source = $$"""
            namespace TestApp;

            public class Worker
            {
                static void Bump(ref int v) => v++;

                public object Create()
                {
                    var point = (1, 2);
                    {{aliasStatements}}
                    Bump(ref copy.Item1);
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
                AsRecord = asRecord
            }));

        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
        Assert.Contains(asRecord ? "element assignments" : "passed by reference", ex.Message, StringComparison.Ordinal);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    // Codex P1 on #3441 (PRRT_kwDORD0nYs602M2n): null-conditional member/element bindings
    // must follow the same wrapper-member / container provenance as ordinary access
    // (var maybe = box?.Value; var copy = maybe.Value).
    [SkippableTheory]
    [InlineData(false, "var box = Wrap(point); var maybe = box?.Value; var copy = maybe.Value; Bump(ref copy.X);")]
    [InlineData(true, "var box = Wrap(point); var maybe = box?.Value; var copy = maybe.Value; Bump(ref copy.X);")]
    [InlineData(false, "var box = Wrap(point); var copy = (box?.Value).Value; Bump(ref copy.X);")]
    [InlineData(true, "var box = Wrap(point); var copy = (box?.Value).Value; Bump(ref copy.X);")]
    [InlineData(false, "var points = new[] { point }; var maybe = points?[0]; var copy = maybe.Value; Bump(ref copy.X);")]
    [InlineData(true, "var points = new[] { point }; var maybe = points?[0]; var copy = maybe.Value; Bump(ref copy.X);")]
    [InlineData(false, "var box = new { Value = point }; var maybe = box?.Value; var copy = maybe.Value; Bump(ref copy.X);")]
    [InlineData(true, "var box = new { Value = point }; var maybe = box?.Value; var copy = maybe.Value; Bump(ref copy.X);")]
    public async Task ConvertTupleToStruct_ConditionalAccessStorageRefElement_ThrowsAndWritesNothing(
        bool asRecord,
        string statements)
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
                    {{statements}}
                    return point;
                }

                static Box<T> Wrap<T>(T value) => new Box<T>(value);
            }

            public sealed class Box<T>
            {
                public Box(T value) => Value = value;
                public T Value;
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

    // Codex P1 on #3441 (PRRT_kwDORD0nYs602baw): with-expression copies must track the
    // receiver as a storage source (var copy = point with { Y = 3 }; Bump(ref copy.X)).
    [SkippableTheory]
    [InlineData(false, "var copy = point with { Y = 3 }; Bump(ref copy.X);")]
    [InlineData(true, "var copy = point with { Y = 3 }; Bump(ref copy.X);")]
    [InlineData(false, "var copy = (point with { Y = 3 }); Bump(ref copy.X);")]
    [InlineData(true, "var copy = (point with { Y = 3 }); Bump(ref copy.X);")]
    [InlineData(false, "var copy = point! with { Y = 3 }; Bump(ref copy.X);")]
    [InlineData(true, "var copy = point! with { Y = 3 }; Bump(ref copy.X);")]
    [InlineData(false, "var copy = flag ? point with { Y = 3 } : point with { Y = 4 }; Bump(ref copy.X);")]
    [InlineData(true, "var copy = flag ? point with { Y = 3 } : point with { Y = 4 }; Bump(ref copy.X);")]
    [InlineData(false, "var points = new[] { point }; var copies = points.Select(x => x with { Y = 3 }).ToArray(); Bump(ref copies[0].X);")]
    [InlineData(true, "var points = new[] { point }; var copies = points.Select(x => x with { Y = 3 }).ToArray(); Bump(ref copies[0].X);")]
    public async Task ConvertTupleToStruct_WithExpressionCopyRefElement_ThrowsAndWritesNothing(bool asRecord, string statements)
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

    // Proactive gap fold on #3441: assignment-as-value yields the RHS storage
    // (var copy = (tmp = point); Bump(ref copy.X)).
    [SkippableTheory]
    [InlineData(false, "(int X, int Y) tmp = default; var copy = (tmp = point); Bump(ref copy.X);")]
    [InlineData(true, "(int X, int Y) tmp = default; var copy = (tmp = point); Bump(ref copy.X);")]
    [InlineData(false, "(int X, int Y) tmp = default; var copy = flag ? (tmp = point) : point; Bump(ref copy.X);")]
    [InlineData(true, "(int X, int Y) tmp = default; var copy = flag ? (tmp = point) : point; Bump(ref copy.X);")]
    public async Task ConvertTupleToStruct_AssignmentExpressionValueRefElement_ThrowsAndWritesNothing(bool asRecord, string statements)
    {
        var source = $$"""
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
                Line = 9,
                NewTypeName = "Point",
                AsRecord = asRecord
            }));

        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
        Assert.Contains(asRecord ? "element assignments" : "passed by reference", ex.Message, StringComparison.Ordinal);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    // Codex P1 on #3441: non-local field/property storage used from another partial-class
    // document must still refuse by-ref element uses (scan project documents).
    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConvertTupleToStruct_CrossDocumentPartialFieldRefElement_ThrowsAndWritesNothing(bool asRecord)
    {
        const string primary = """
            namespace TestApp;

            public partial class Worker
            {
                public dynamic point;

                public object Create()
                {
                    point = (1, 2);
                    return point;
                }
            }
            """;
        const string secondary = """
            namespace TestApp;

            public partial class Worker
            {
                static void Bump(ref int v) => v++;

                public void Mutate()
                {
                    Bump(ref point.Item1);
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(
            ("Worker.cs", primary),
            ("Worker.Partial.cs", secondary));
        var primaryPath = workspace.SourcePath;
        var secondaryPath = Path.Combine(workspace.DirectoryPath, "Worker.Partial.cs");
        var originalPrimary = await File.ReadAllTextAsync(primaryPath);
        var originalSecondary = await File.ReadAllTextAsync(secondaryPath);
        var operation = new ConvertTupleToStructOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ConvertTupleToStructParams
            {
                SourceFile = primaryPath,
                Line = 9,
                NewTypeName = "Point",
                AsRecord = asRecord
            }));

        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
        Assert.Contains(asRecord ? "element assignments" : "passed by reference", ex.Message, StringComparison.Ordinal);
        Assert.Equal(originalPrimary, await File.ReadAllTextAsync(primaryPath));
        Assert.Equal(originalSecondary, await File.ReadAllTextAsync(secondaryPath));
    }

    // Codex P1 on #3441: collection-expression spread elements must be traversed when
    // collecting collection aliases (var points = new[] { point }; dynamic[] copies = [.. points]).
    // Matches Codex evidence / DynamicAliasRefElement: skip AssertTupleContainerCompiles —
    // ref-dynamic→ref-int is invalid for emit but the workspace still sees a by-ref use.
    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConvertTupleToStruct_CollectionExpressionSpreadCarrierRefElement_ThrowsAndWritesNothing(bool asRecord)
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
                    dynamic[] copies = [.. points];
                    Bump(ref copies[0].X);
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
                AsRecord = asRecord
            }));

        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
        Assert.Contains(asRecord ? "element assignments" : "passed by reference", ex.Message, StringComparison.Ordinal);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    // Codex P1 on #3441: non-local field/property storage used from a referencing/
    // dependent project must be scanned (public dynamic field in A, Bump(ref …) in B).
    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConvertTupleToStruct_DependentProjectFieldRefElement_ThrowsAndWritesNothing(bool asRecord)
    {
        await using var workspace = await TempWorkspace.CreateDependentProjectFieldRefAsync();
        var originalOrigin = await File.ReadAllTextAsync(workspace.SourcePath);
        var originalConsumer = await File.ReadAllTextAsync(workspace.SecondarySourcePath);
        var operation = new ConvertTupleToStructOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ConvertTupleToStructParams
            {
                SourceFile = workspace.SourcePath,
                Line = 9,
                NewTypeName = "Point",
                AsRecord = asRecord
            }));

        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
        Assert.Contains(asRecord ? "element assignments" : "passed by reference", ex.Message, StringComparison.Ordinal);
        Assert.Equal(originalOrigin, await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Equal(originalConsumer, await File.ReadAllTextAsync(workspace.SecondarySourcePath));
    }

    // Codex P1 on #3441: stackalloc initializers must be tracked as storage carriers
    // (implicit stackalloc[] { point } and explicit stackalloc T[n] { point }).
    [SkippableTheory]
    [InlineData(false, "var points = stackalloc[] { point }; Bump(ref points[0].X);")]
    [InlineData(true, "var points = stackalloc[] { point }; Bump(ref points[0].X);")]
    [InlineData(false, "var points = stackalloc (int X, int Y)[1] { point }; Bump(ref points[0].X);")]
    [InlineData(true, "var points = stackalloc (int X, int Y)[1] { point }; Bump(ref points[0].X);")]
    public async Task ConvertTupleToStruct_StackallocInitializerCarrierRefElement_ThrowsAndWritesNothing(
        bool asRecord,
        string statements)
    {
        var source = $$"""
            namespace TestApp;

            public unsafe class Worker
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

        await using var workspace = await TempWorkspace.CreateAsync(source, allowUnsafe: true);
        var original = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new ConvertTupleToStructOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ConvertTupleToStructParams
            {
                SourceFile = workspace.SourcePath,
                Line = 9,
                NewTypeName = "Point",
                AsRecord = asRecord
            }));

        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
        Assert.Contains(asRecord ? "element assignments" : "passed by reference", ex.Message, StringComparison.Ordinal);
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

    // Codex P1 on #3441: non-readonly property/indexer getters on mutable value-type elements
    // receive the element by ref (point.Counter.Next mutates); refuse like mutating methods.
    [SkippableFact]
    public async Task ConvertTupleToStruct_AsRecordFalse_MutatingStructPropertyGetterReceiver_ThrowsAndWritesNothing()
    {
        const string source = """
            namespace TestApp;

            public struct Counter
            {
                public int Value;
                public int Next => Value++;
                public int this[int _] => Value++;
            }

            public class Worker
            {
                public object Create()
                {
                    var point = (Counter: new Counter(), Y: 2);
                    var viaProperty = point.Counter.Next;
                    var viaIndexer = point.Counter[0];
                    return (viaProperty, viaIndexer, point);
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

    // Codex P1 on #3441 (PRRT_kwDORD0nYs606U0c): foreach implicitly calls a mutating
    // GetEnumerator on a mutable value-type element with no InvocationExpressionSyntax.
    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConvertTupleToStruct_ForeachMutatingGetEnumeratorElement_ThrowsAndWritesNothing(bool asRecord)
    {
        const string source = """
            namespace TestApp;

            public struct Sequence
            {
                public int Mutations;
                public Enumerator GetEnumerator()
                {
                    Mutations++;
                    return new Enumerator();
                }

                public struct Enumerator
                {
                    public int Current => 0;
                    public bool MoveNext() => false;
                }
            }

            public class Worker
            {
                public object Create()
                {
                    var point = (Sequence: new Sequence(), Y: 2);
                    foreach (var item in point.Sequence)
                    {
                    }

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
                Line = 23,
                NewTypeName = "Point",
                AsRecord = asRecord
            }));

        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
        // By-ref gate (not the init-write gate) — message is the same for both asRecord settings.
        Assert.Contains("passed by reference", ex.Message, StringComparison.Ordinal);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    // Sibling: using (point.Resource) implicitly Disposes a mutable value-type element.
    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConvertTupleToStruct_UsingMutatingDisposeElement_ThrowsAndWritesNothing(bool asRecord)
    {
        const string source = """
            namespace TestApp;

            public struct Resource
            {
                public int Mutations;
                public void Dispose() => Mutations++;
            }

            public class Worker
            {
                public object Create()
                {
                    var point = (Resource: new Resource(), Y: 2);
                    using (point.Resource)
                    {
                    }

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
                AsRecord = asRecord
            }));

        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
        // By-ref gate (not the init-write gate) — message is the same for both asRecord settings.
        Assert.Contains("passed by reference", ex.Message, StringComparison.Ordinal);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    // Codex P1 on #3441 (PRRT_kwDORD0nYs606mPZ): await implicitly calls GetAwaiter on a
    // mutable value-type element with no InvocationExpressionSyntax.
    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConvertTupleToStruct_AwaitMutatingGetAwaiterElement_ThrowsAndWritesNothing(bool asRecord)
    {
        const string source = """
            namespace TestApp;

            public struct Awaitable
            {
                public int Calls;
                public Awaiter GetAwaiter()
                {
                    Calls++;
                    return new Awaiter();
                }

                public struct Awaiter : System.Runtime.CompilerServices.INotifyCompletion
                {
                    public bool IsCompleted => true;
                    public void OnCompleted(System.Action continuation) { }
                    public void GetResult() { }
                }
            }

            public class Worker
            {
                public async System.Threading.Tasks.Task<object> Create()
                {
                    var point = (Awaitable: new Awaitable(), Y: 2);
                    await point.Awaitable;
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
                Line = 24,
                NewTypeName = "Point",
                AsRecord = asRecord
            }));

        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
        Assert.Contains("passed by reference", ex.Message, StringComparison.Ordinal);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    // Codex P1 on #3441 (PRRT_kwDORD0nYs607PA-): await whose GetAwaiter resolves to a
    // `this ref` extension must refuse — type.GetMembers missed extensions. (Current
    // compilers already report CS1510 here even on the tuple field; the gate refuses
    // conservatively instead of rewriting the element into a property.)
    [SkippableFact]
    public async Task ConvertTupleToStruct_AwaitRefThisExtensionGetAwaiterElement_ThrowsAndWritesNothing()
    {
        const string source = """
            namespace TestApp;

            public struct Awaitable
            {
                public int Calls;

                public struct Awaiter : System.Runtime.CompilerServices.INotifyCompletion
                {
                    public bool IsCompleted => true;
                    public void OnCompleted(System.Action continuation) { }
                    public void GetResult() { }
                }
            }

            public static class AwaitableExtensions
            {
                public static Awaitable.Awaiter GetAwaiter(this ref Awaitable value)
                {
                    value.Calls++;
                    return new Awaitable.Awaiter();
                }
            }

            public class Worker
            {
                public async System.Threading.Tasks.Task<object> Create()
                {
                    var point = (Awaitable: new Awaitable(), Y: 2);
                    await point.Awaitable;
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
                Line = 28,
                NewTypeName = "Point",
                AsRecord = false
            }));

        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
        Assert.Contains("passed by reference", ex.Message, StringComparison.Ordinal);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    // Control: by-value extension GetAwaiter is a value receiver — generated property OK.
    [SkippableFact]
    public async Task ConvertTupleToStruct_AwaitNonRefExtensionGetAwaiterElement_Succeeds()
    {
        const string source = """
            namespace TestApp;

            public struct Awaitable
            {
                public struct Awaiter : System.Runtime.CompilerServices.INotifyCompletion
                {
                    public bool IsCompleted => true;
                    public void OnCompleted(System.Action continuation) { }
                    public void GetResult() { }
                }
            }

            public static class AwaitableExtensions
            {
                public static Awaitable.Awaiter GetAwaiter(this Awaitable value) => new Awaitable.Awaiter();
            }

            public class Worker
            {
                public async System.Threading.Tasks.Task<object> Create()
                {
                    var point = (Awaitable: new Awaitable(), Y: 2);
                    await point.Awaitable;
                    return point;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ConvertTupleToStructOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ConvertTupleToStructParams
        {
            SourceFile = workspace.SourcePath,
            Line = 22,
            NewTypeName = "Point",
            AsRecord = false
        });

        Assert.True(result.Success);
        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("public struct Point", text, StringComparison.Ordinal);
        Assert.Contains("public TestApp.Awaitable Awaitable { get; set; }", text, StringComparison.Ordinal);
        Assert.Contains("await point.Awaitable", text, StringComparison.Ordinal);
    }

    // Codex P2 on #3441 (PRRT_kwDORD0nYs606mPd): plain using resolves Dispose, so a
    // readonly Dispose + mutating DisposeAsync still converts.
    [SkippableFact]
    public async Task ConvertTupleToStruct_UsingReadonlyDisposeWithMutatingDisposeAsync_Succeeds()
    {
        const string source = """
            namespace TestApp;

            public struct Resource
            {
                public int Mutations;
                public readonly void Dispose() { }
                public System.Threading.Tasks.ValueTask DisposeAsync()
                {
                    Mutations++;
                    return default;
                }
            }

            public class Worker
            {
                public object Create()
                {
                    var point = (Resource: new Resource(), Y: 2);
                    using (point.Resource)
                    {
                    }

                    return point;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ConvertTupleToStructOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ConvertTupleToStructParams
        {
            SourceFile = workspace.SourcePath,
            Line = 18,
            NewTypeName = "Point",
            AsRecord = false
        });

        Assert.True(result.Success);
        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("public struct Point", text, StringComparison.Ordinal);
        Assert.Contains("public TestApp.Resource Resource { get; set; }", text, StringComparison.Ordinal);
        Assert.Contains("using (point.Resource)", text, StringComparison.Ordinal);
    }

    // Codex P2 sibling: await using resolves DisposeAsync — mutating DisposeAsync refuses.
    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConvertTupleToStruct_AwaitUsingMutatingDisposeAsyncElement_ThrowsAndWritesNothing(bool asRecord)
    {
        const string source = """
            namespace TestApp;

            public struct Resource
            {
                public int Mutations;
                public System.Threading.Tasks.ValueTask DisposeAsync()
                {
                    Mutations++;
                    return default;
                }
            }

            public class Worker
            {
                public async System.Threading.Tasks.Task<object> Create()
                {
                    var point = (Resource: new Resource(), Y: 2);
                    await using (point.Resource)
                    {
                    }

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
                Line = 17,
                NewTypeName = "Point",
                AsRecord = asRecord
            }));

        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
        Assert.Contains("passed by reference", ex.Message, StringComparison.Ordinal);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    // Codex P2 vice versa: await using with readonly DisposeAsync + mutating Dispose converts.
    [SkippableFact]
    public async Task ConvertTupleToStruct_AwaitUsingReadonlyDisposeAsyncWithMutatingDispose_Succeeds()
    {
        const string source = """
            namespace TestApp;

            public struct Resource
            {
                public int Mutations;
                public void Dispose() => Mutations++;
                public readonly System.Threading.Tasks.ValueTask DisposeAsync() => default;
            }

            public class Worker
            {
                public async System.Threading.Tasks.Task<object> Create()
                {
                    var point = (Resource: new Resource(), Y: 2);
                    await using (point.Resource)
                    {
                    }

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
        Assert.Contains("public TestApp.Resource Resource { get; set; }", text, StringComparison.Ordinal);
        Assert.Contains("await using (point.Resource)", text, StringComparison.Ordinal);
    }

    // Codex P1 on #3441 (PRRT_kwDORD0nYs6063yg): deconstruction assignment implicitly
    // calls a non-readonly Deconstruct on a mutable value-type element.
    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConvertTupleToStruct_DeconstructMutatingElement_ThrowsAndWritesNothing(bool asRecord)
    {
        const string source = """
            namespace TestApp;

            public struct Counter
            {
                public int A;
                public int B;
                public int Mutations;

                public void Deconstruct(out int a, out int b)
                {
                    Mutations++;
                    a = A;
                    b = B;
                }
            }

            public class Worker
            {
                public object Create()
                {
                    var point = (Counter: new Counter { A = 1, B = 2 }, Y: 2);
                    var (x, y) = point.Counter;
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
                Line = 21,
                NewTypeName = "Point",
                AsRecord = asRecord
            }));

        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
        Assert.Contains("passed by reference", ex.Message, StringComparison.Ordinal);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    // Over-refusal: readonly Deconstruct on a mutable value-type element still converts.
    [SkippableFact]
    public async Task ConvertTupleToStruct_AsRecordFalse_ReadonlyDeconstructElement_Succeeds()
    {
        const string source = """
            namespace TestApp;

            public struct Counter
            {
                public int A;
                public int B;

                public readonly void Deconstruct(out int a, out int b)
                {
                    a = A;
                    b = B;
                }
            }

            public class Worker
            {
                public object Create()
                {
                    var point = (Counter: new Counter { A = 1, B = 2 }, Y: 2);
                    var (x, y) = point.Counter;
                    return point;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ConvertTupleToStructOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ConvertTupleToStructParams
        {
            SourceFile = workspace.SourcePath,
            Line = 19,
            NewTypeName = "Point",
            AsRecord = false
        });

        Assert.True(result.Success);
        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("public struct Point", text, StringComparison.Ordinal);
        Assert.Contains("public TestApp.Counter Counter { get; set; }", text, StringComparison.Ordinal);
        Assert.Contains("var (x, y) = point.Counter", text, StringComparison.Ordinal);
    }

    // Positional pattern also implicitly Deconstructs the element.
    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConvertTupleToStruct_PositionalPatternMutatingDeconstructElement_ThrowsAndWritesNothing(bool asRecord)
    {
        const string source = """
            namespace TestApp;

            public struct Counter
            {
                public int A;
                public int B;
                public int Mutations;

                public void Deconstruct(out int a, out int b)
                {
                    Mutations++;
                    a = A;
                    b = B;
                }
            }

            public class Worker
            {
                public object Create()
                {
                    var point = (Counter: new Counter { A = 1, B = 2 }, Y: 2);
                    var matched = point.Counter is (1, _);
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
                Line = 21,
                NewTypeName = "Point",
                AsRecord = asRecord
            }));

        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
        Assert.Contains("passed by reference", ex.Message, StringComparison.Ordinal);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    // fixed implicitly calls GetPinnableReference on a mutable value-type element.
    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConvertTupleToStruct_FixedMutatingGetPinnableReferenceElement_ThrowsAndWritesNothing(bool asRecord)
    {
        const string source = """
            namespace TestApp;

            public struct PinTarget
            {
                public int[] Data;
                public int Mutations;

                public ref int GetPinnableReference()
                {
                    Mutations++;
                    return ref Data[0];
                }
            }

            public unsafe class Worker
            {
                public object Create()
                {
                    var point = (Pin: new PinTarget { Data = new[] { 1 } }, Y: 2);
                    fixed (int* p = point.Pin)
                    {
                        *p = 2;
                    }

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
                Line = 19,
                NewTypeName = "Point",
                AsRecord = asRecord
            }));

        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
        Assert.Contains("passed by reference", ex.Message, StringComparison.Ordinal);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    // Over-refusal: readonly GetPinnableReference still converts.
    [SkippableFact]
    public async Task ConvertTupleToStruct_AsRecordFalse_FixedReadonlyGetPinnableReferenceElement_Succeeds()
    {
        const string source = """
            namespace TestApp;

            public struct PinTarget
            {
                public int[] Data;

                public readonly ref int GetPinnableReference() => ref Data[0];
            }

            public unsafe class Worker
            {
                public object Create()
                {
                    var point = (Pin: new PinTarget { Data = new[] { 1 } }, Y: 2);
                    fixed (int* p = point.Pin)
                    {
                        *p = 2;
                    }

                    return point;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, allowUnsafe: true);
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
        Assert.Contains("public TestApp.PinTarget Pin { get; set; }", text, StringComparison.Ordinal);
        Assert.Contains("fixed (int* p = point.Pin)", text, StringComparison.Ordinal);
    }

    // Query expression instance Where/Select (no InvocationExpressionSyntax) on a
    // mutable value-type element.
    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConvertTupleToStruct_QueryMutatingInstanceMethodElement_ThrowsAndWritesNothing(bool asRecord)
    {
        const string source = """
            namespace TestApp;

            public struct QuerySource
            {
                public int Mutations;

                public QuerySource Where(System.Func<int, bool> predicate)
                {
                    Mutations++;
                    return this;
                }

                public int Select(System.Func<int, int> selector)
                {
                    Mutations++;
                    return 0;
                }

                public Enumerator GetEnumerator() => default;

                public struct Enumerator
                {
                    public int Current => 0;
                    public bool MoveNext() => false;
                }
            }

            public class Worker
            {
                public object Create()
                {
                    var point = (Source: new QuerySource(), Y: 2);
                    var q = from x in point.Source where x > 0 select x;
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
                Line = 32,
                NewTypeName = "Point",
                AsRecord = asRecord
            }));

        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
        Assert.Contains("passed by reference", ex.Message, StringComparison.Ordinal);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    // Codex P1 on #3441 (PRRT_kwDORD0nYs607PA7): query Where in a later field initializer
    // is an IOperation root under EqualsValueClause — missed when only method/accessor
    // owners are walked.
    [SkippableFact]
    public async Task ConvertTupleToStruct_FieldInitializerQueryMutatingWhereRefElement_ThrowsAndWritesNothing()
    {
        // IIFE inside a field initializer (EqualsValueClause IOperation root) so the
        // creation is `var`-accepted and the mutating query Where is not under a
        // method/accessor owner — missed before those roots were enumerated.
        const string source = """
            namespace TestApp;

            public struct QuerySource
            {
                public int Mutations;

                public QuerySource Where(System.Func<int, bool> predicate)
                {
                    Mutations++;
                    return this;
                }

                public int Select(System.Func<int, int> selector)
                {
                    Mutations++;
                    return 0;
                }

                public Enumerator GetEnumerator() => default;

                public struct Enumerator
                {
                    public int Current => 0;
                    public bool MoveNext() => false;
                }
            }

            public class Holder
            {
                public static object Result = ((System.Func<object>)(() =>
                {
                    var point = (Source: new QuerySource(), Y: 2);
                    return from x in point.Source where x > 0 select x;
                }))();
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var original = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new ConvertTupleToStructOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ConvertTupleToStructParams
            {
                SourceFile = workspace.SourcePath,
                Line = 32,
                NewTypeName = "Point",
                AsRecord = false
            }));

        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
        Assert.Contains("passed by reference", ex.Message, StringComparison.Ordinal);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    // Codex P1 sibling: expression-bodied property is an ArrowExpressionClause IOperation
    // root — same mutating query Where hazard.
    [SkippableFact]
    public async Task ConvertTupleToStruct_ExpressionBodiedPropertyMutatingCallRefElement_ThrowsAndWritesNothing()
    {
        // IIFE inside an expression-bodied property (ArrowExpressionClause IOperation
        // root) — same mutating query Where hazard as the field-initializer case.
        const string source = """
            namespace TestApp;

            public struct QuerySource
            {
                public int Mutations;

                public QuerySource Where(System.Func<int, bool> predicate)
                {
                    Mutations++;
                    return this;
                }

                public int Select(System.Func<int, int> selector)
                {
                    Mutations++;
                    return 0;
                }

                public Enumerator GetEnumerator() => default;

                public struct Enumerator
                {
                    public int Current => 0;
                    public bool MoveNext() => false;
                }
            }

            public class Holder
            {
                public object Query => ((System.Func<object>)(() =>
                {
                    var point = (Source: new QuerySource(), Y: 2);
                    return from x in point.Source where x > 0 select x;
                }))();
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var original = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new ConvertTupleToStructOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ConvertTupleToStructParams
            {
                SourceFile = workspace.SourcePath,
                Line = 32,
                NewTypeName = "Point",
                AsRecord = false
            }));

        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
        Assert.Contains("passed by reference", ex.Message, StringComparison.Ordinal);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    // Codex P1 sibling (PRRT_kwDORD0nYs607PA7): auto-property initializer is its own
    // IOperation root (EqualsValueClause on the property).
    [SkippableFact]
    public async Task ConvertTupleToStruct_PropertyInitializerQueryMutatingWhereRefElement_ThrowsAndWritesNothing()
    {
        const string source = """
            namespace TestApp;

            public struct QuerySource
            {
                public int Mutations;

                public QuerySource Where(System.Func<int, bool> predicate)
                {
                    Mutations++;
                    return this;
                }

                public int Select(System.Func<int, int> selector)
                {
                    Mutations++;
                    return 0;
                }
            }

            public class Base
            {
                public Base(object value) { }
            }

            public class Holder
            {
                public object Result { get; } = ((System.Func<object>)(() =>
                    {
                        var point = (Source: new QuerySource(), Y: 2);
                        return from x in point.Source where x > 0 select x;
                    }))();
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var original = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new ConvertTupleToStructOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ConvertTupleToStructParams
            {
                SourceFile = workspace.SourcePath,
                Line = 29,
                NewTypeName = "Point",
                AsRecord = false
            }));

        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
        Assert.Contains("passed by reference", ex.Message, StringComparison.Ordinal);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    // Codex P1 sibling (PRRT_kwDORD0nYs607PA7): expression-bodied indexer root.
    [SkippableFact]
    public async Task ConvertTupleToStruct_ExpressionBodiedIndexerQueryMutatingWhereRefElement_ThrowsAndWritesNothing()
    {
        const string source = """
            namespace TestApp;

            public struct QuerySource
            {
                public int Mutations;

                public QuerySource Where(System.Func<int, bool> predicate)
                {
                    Mutations++;
                    return this;
                }

                public int Select(System.Func<int, int> selector)
                {
                    Mutations++;
                    return 0;
                }
            }

            public class Base
            {
                public Base(object value) { }
            }

            public class Holder
            {
                public object this[int i] => ((System.Func<object>)(() =>
                    {
                        var point = (Source: new QuerySource(), Y: 2);
                        return from x in point.Source where x > 0 select x;
                    }))();
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var original = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new ConvertTupleToStructOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ConvertTupleToStructParams
            {
                SourceFile = workspace.SourcePath,
                Line = 29,
                NewTypeName = "Point",
                AsRecord = false
            }));

        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
        Assert.Contains("passed by reference", ex.Message, StringComparison.Ordinal);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    // Codex P1 sibling (PRRT_kwDORD0nYs607PA7): primary-constructor base argument root.
    [SkippableFact]
    public async Task ConvertTupleToStruct_PrimaryConstructorBaseArgumentQueryMutatingWhereRefElement_ThrowsAndWritesNothing()
    {
        const string source = """
            namespace TestApp;

            public struct QuerySource
            {
                public int Mutations;

                public QuerySource Where(System.Func<int, bool> predicate)
                {
                    Mutations++;
                    return this;
                }

                public int Select(System.Func<int, int> selector)
                {
                    Mutations++;
                    return 0;
                }
            }

            public class Base
            {
                public Base(object value) { }
            }

            public class Holder(int seed) : Base(((System.Func<object>)(() =>
                {
                    var point = (Source: new QuerySource(), Y: 2);
                    return from x in point.Source where x > 0 select x;
                }))())
            {
                public int Seed => seed;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var original = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new ConvertTupleToStructOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ConvertTupleToStructParams
            {
                SourceFile = workspace.SourcePath,
                Line = 27,
                NewTypeName = "Point",
                AsRecord = false
            }));

        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
        Assert.Contains("passed by reference", ex.Message, StringComparison.Ordinal);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    // Codex P1 sibling (PRRT_kwDORD0nYs607PA-): fixed binds a `this ref` extension
    // GetPinnableReference (compiles on the tuple field; CS1510 on a generated property).
    // type.GetMembers missed the extension.
    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConvertTupleToStruct_FixedRefThisExtensionGetPinnableReferenceElement_ThrowsAndWritesNothing(bool asRecord)
    {
        const string source = """
            namespace TestApp;

            public struct PinTarget
            {
                public int[] Data;
                public int Mutations;
            }

            public static class PinTargetExtensions
            {
                public static ref int GetPinnableReference(this ref PinTarget target)
                {
                    target.Mutations++;
                    return ref target.Data[0];
                }
            }

            public unsafe class Worker
            {
                public object Create()
                {
                    var point = (Pin: new PinTarget { Data = new[] { 1 } }, Y: 2);
                    fixed (int* p = point.Pin)
                    {
                        *p = 2;
                    }

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
                Line = 22,
                NewTypeName = "Point",
                AsRecord = asRecord
            }));

        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
        Assert.Contains("passed by reference", ex.Message, StringComparison.Ordinal);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    // Control: a `this in` extension GetPinnableReference is a readonly value receiver —
    // a generated property still pins (same as ReadOnlySpan<T>), so conversion proceeds.
    [SkippableFact]
    public async Task ConvertTupleToStruct_AsRecordFalse_FixedInExtensionGetPinnableReferenceElement_Succeeds()
    {
        const string source = """
            namespace TestApp;

            public struct PinTarget
            {
                public int[] Data;
            }

            public static class PinTargetExtensions
            {
                public static ref readonly int GetPinnableReference(this in PinTarget target) => ref target.Data[0];
            }

            public unsafe class Worker
            {
                public object Create()
                {
                    var point = (Pin: new PinTarget { Data = new[] { 1 } }, Y: 2);
                    fixed (int* p = point.Pin)
                    {
                        *p = 2;
                    }

                    return point;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, allowUnsafe: true);
        var operation = new ConvertTupleToStructOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ConvertTupleToStructParams
        {
            SourceFile = workspace.SourcePath,
            Line = 17,
            NewTypeName = "Point",
            AsRecord = false
        });

        Assert.True(result.Success);
        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("public struct Point", text, StringComparison.Ordinal);
        Assert.Contains("fixed (int* p = point.Pin)", text, StringComparison.Ordinal);
    }

    // Index/Range implicitly reads Length and calls Slice on a mutable value-type element.
    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConvertTupleToStruct_RangeMutatingSliceElement_ThrowsAndWritesNothing(bool asRecord)
    {
        const string source = """
            namespace TestApp;

            public struct Buffer
            {
                public int Mutations;

                public int Length
                {
                    get
                    {
                        Mutations++;
                        return 2;
                    }
                }

                public int this[int index]
                {
                    get
                    {
                        Mutations++;
                        return 0;
                    }
                }

                public Buffer Slice(int start, int length)
                {
                    Mutations++;
                    return this;
                }
            }

            public class Worker
            {
                public object Create()
                {
                    var point = (Buffer: new Buffer(), Y: 2);
                    var slice = point.Buffer[1..2];
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
                Line = 36,
                NewTypeName = "Point",
                AsRecord = asRecord
            }));

        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
        Assert.Contains("passed by reference", ex.Message, StringComparison.Ordinal);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    // Over-refusal: readonly Length + readonly Slice still converts.
    [SkippableFact]
    public async Task ConvertTupleToStruct_AsRecordFalse_RangeReadonlySliceElement_Succeeds()
    {
        const string source = """
            namespace TestApp;

            public struct Buffer
            {
                public readonly int Length => 2;

                public readonly int this[int index] => 0;

                public readonly Buffer Slice(int start, int length) => this;
            }

            public class Worker
            {
                public object Create()
                {
                    var point = (Buffer: new Buffer(), Y: 2);
                    var slice = point.Buffer[1..2];
                    return point;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ConvertTupleToStructOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ConvertTupleToStructParams
        {
            SourceFile = workspace.SourcePath,
            Line = 16,
            NewTypeName = "Point",
            AsRecord = false
        });

        Assert.True(result.Success);
        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("public struct Point", text, StringComparison.Ordinal);
        Assert.Contains("public TestApp.Buffer Buffer { get; set; }", text, StringComparison.Ordinal);
        Assert.Contains("point.Buffer[1..2]", text, StringComparison.Ordinal);
    }

    // Index (^) form also refuses when Length / indexer getters mutate.
    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConvertTupleToStruct_IndexMutatingLengthElement_ThrowsAndWritesNothing(bool asRecord)
    {
        const string source = """
            namespace TestApp;

            public struct Buffer
            {
                public int Mutations;

                public int Length
                {
                    get
                    {
                        Mutations++;
                        return 1;
                    }
                }

                public int this[int index]
                {
                    get
                    {
                        Mutations++;
                        return 0;
                    }
                }
            }

            public class Worker
            {
                public object Create()
                {
                    var point = (Buffer: new Buffer(), Y: 2);
                    var last = point.Buffer[^1];
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
                Line = 30,
                NewTypeName = "Point",
                AsRecord = asRecord
            }));

        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
        // Indexer getter is also caught by the asRecord init-write gate first.
        Assert.Contains(asRecord ? "element assignments" : "passed by reference", ex.Message, StringComparison.Ordinal);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    // Over-refusal guard: readonly GetEnumerator on a mutable value-type element still converts.
    [SkippableFact]
    public async Task ConvertTupleToStruct_AsRecordFalse_ForeachReadonlyGetEnumeratorElement_Succeeds()
    {
        const string source = """
            namespace TestApp;

            public struct Sequence
            {
                public readonly Enumerator GetEnumerator() => new Enumerator();

                public struct Enumerator
                {
                    public int Current => 0;
                    public bool MoveNext() => false;
                }
            }

            public class Worker
            {
                public object Create()
                {
                    var point = (Sequence: new Sequence(), Y: 2);
                    foreach (var item in point.Sequence)
                    {
                    }

                    return point;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ConvertTupleToStructOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ConvertTupleToStructParams
        {
            SourceFile = workspace.SourcePath,
            Line = 18,
            NewTypeName = "Point",
            AsRecord = false
        });

        Assert.True(result.Success);
        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("public struct Point", text, StringComparison.Ordinal);
        Assert.Contains("public TestApp.Sequence Sequence { get; set; }", text, StringComparison.Ordinal);
        Assert.Contains("foreach (var item in point.Sequence)", text, StringComparison.Ordinal);
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


        /// <summary>
        /// Single-project workspace whose project references a pre-compiled
        /// <c>External.dll</c> built from <paramref name="externalSource"/>, so
        /// callees declared there are metadata-only (no declaring syntax).
        /// </summary>
        public static async Task<TempWorkspace> CreateWithMetadataReferenceAsync(
            string source,
            string externalSource)
        {
            Skip.IfNot(ModuleInitializer.MsBuildAvailable, ModuleInitializer.MsBuildError ?? "MSBuild not available");

            var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpConvertTupleMeta_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);

            var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
                .Split(Path.PathSeparator)
                .Select(path => MetadataReference.CreateFromFile(path));
            var compilation = CSharpCompilation.Create(
                "External",
                [CSharpSyntaxTree.ParseText(externalSource)],
                references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            var externalPath = Path.Combine(directory, "External.dll");
            var emit = compilation.Emit(externalPath);
            Assert.True(emit.Success, string.Join(Environment.NewLine, emit.Diagnostics));

            var projectPath = Path.Combine(directory, "TestApp.csproj");
            await File.WriteAllTextAsync(projectPath, $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net9.0</TargetFramework>
                    <ImplicitUsings>enable</ImplicitUsings>
                    <Nullable>enable</Nullable>
                  </PropertyGroup>
                  <ItemGroup>
                    <Reference Include="External">
                      <HintPath>{externalPath}</HintPath>
                    </Reference>
                  </ItemGroup>
                </Project>
                """);

            var sourcePath = Path.Combine(directory, "Worker.cs");
            await File.WriteAllTextAsync(sourcePath, source);
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

        /// <summary>
        /// Project A exposes a public dynamic field initialized from a convertible
        /// tuple; project B references A and passes an element by ref. Used for the
        /// dependent-project storage scan regression (Codex P1 on #3441).
        /// </summary>
        public static async Task<TempWorkspace> CreateDependentProjectFieldRefAsync()
        {
            Skip.IfNot(ModuleInitializer.MsBuildAvailable, ModuleInitializer.MsBuildError ?? "MSBuild not available");

            var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpConvertTupleDep_" + Guid.NewGuid().ToString("N"));
            var appDir = Path.Combine(directory, "App");
            var otherDir = Path.Combine(directory, "Other");
            Directory.CreateDirectory(appDir);
            Directory.CreateDirectory(otherDir);

            var appProject = Path.Combine(appDir, "App.csproj");
            var otherProject = Path.Combine(otherDir, "Other.csproj");
            var appSource = Path.Combine(appDir, "Worker.cs");
            var otherSource = Path.Combine(otherDir, "Client.cs");

            await File.WriteAllTextAsync(appProject, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net9.0</TargetFramework>
                    <Nullable>enable</Nullable>
                  </PropertyGroup>
                </Project>
                """);
            await File.WriteAllTextAsync(otherProject, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net9.0</TargetFramework>
                    <Nullable>enable</Nullable>
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
                    public dynamic point;

                    public object Create()
                    {
                        point = (1, 2);
                        return point;
                    }
                }
                """);
            await File.WriteAllTextAsync(otherSource, """
                namespace OtherApp;

                public class Client
                {
                    static void Bump(ref int v) => v++;

                    public void Mutate(TestApp.Worker worker)
                    {
                        Bump(ref worker.point.Item1);
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

        /// <summary>
        /// Dependent project holds local convertible storage and calls a public
        /// callee declared in a referenced project whose body passes an element by
        /// ref (Codex P1 sibling on #3441, PRRT_kwDORD0nYs604Ela).
        /// </summary>
        public static async Task<TempWorkspace> CreateDependentProjectCalleeRefAsync()
        {
            Skip.IfNot(ModuleInitializer.MsBuildAvailable, ModuleInitializer.MsBuildError ?? "MSBuild not available");

            var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpConvertTupleCallee_" + Guid.NewGuid().ToString("N"));
            var appDir = Path.Combine(directory, "App");
            var otherDir = Path.Combine(directory, "Other");
            Directory.CreateDirectory(appDir);
            Directory.CreateDirectory(otherDir);

            var appProject = Path.Combine(appDir, "App.csproj");
            var otherProject = Path.Combine(otherDir, "Other.csproj");
            var appSource = Path.Combine(appDir, "Helpers.cs");
            var otherSource = Path.Combine(otherDir, "Worker.cs");

            await File.WriteAllTextAsync(appProject, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net9.0</TargetFramework>
                    <Nullable>enable</Nullable>
                  </PropertyGroup>
                </Project>
                """);
            await File.WriteAllTextAsync(otherProject, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net9.0</TargetFramework>
                    <Nullable>enable</Nullable>
                  </PropertyGroup>
                  <ItemGroup>
                    <ProjectReference Include="..\App\App.csproj" />
                  </ItemGroup>
                </Project>
                """);
            await File.WriteAllTextAsync(appSource, """
                namespace TestApp;

                public static class Helpers
                {
                    static void Bump(ref int v) => v++;

                    public static void Use(dynamic copy) => Bump(ref copy.X);
                }
                """);
            await File.WriteAllTextAsync(otherSource, """
                namespace OtherApp;

                public class Worker
                {
                    public object Create()
                    {
                        var point = (X: 1, Y: 2);
                        TestApp.Helpers.Use(point);
                        return point;
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

            var workspace = await LoadAsync(directory, solutionPath, otherSource);
            return new TempWorkspace
            {
                DirectoryPath = workspace.DirectoryPath,
                ProjectPath = workspace.ProjectPath,
                SourcePath = workspace.SourcePath,
                Context = workspace.Context,
                SecondarySourcePath = appSource
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


    // Codex P2 on #3441 originally kept Dictionary<object, tuple>.Add(point, …)
    // converting. Under the sound metadata rule (Codex P1s PRRT_kwDORD0nYs6087Qc /
    // …6087Qf) a converted tuple boxed into the object KEY of a metadata collection
    // is refused: the key is stored, handed to the key comparer, and readable back
    // through dict.Keys without the receiver being a tracked carrier.
    [SkippableTheory]
    [InlineData(false, "dict.Add(point, (X: 3, Y: 4)); var copy = dict[point]; Bump(ref copy.X);")]
    [InlineData(true, "dict.Add(point, (X: 3, Y: 4)); var copy = dict[point]; Bump(ref copy.X);")]
    [InlineData(false, "dict.Add(point, (X: 3, Y: 4)); var copy = dict.Values.First(); Bump(ref copy.X);")]
    [InlineData(true, "dict.Add(point, (X: 3, Y: 4)); var copy = dict.Values.First(); Bump(ref copy.X);")]
    [InlineData(false, "dict.TryAdd(point, (X: 3, Y: 4)); var copy = dict[point]; Bump(ref copy.X);")]
    [InlineData(true, "dict.TryAdd(point, (X: 3, Y: 4)); var copy = dict[point]; Bump(ref copy.X);")]
    public async Task ConvertTupleToStruct_DictionaryObjectKeyInsertion_ThrowsAndWritesNothing(bool asRecord, string statements)
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
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    // Codex P2 on #3441 (PRRT_kwDORD0nYs600gmf) originally kept { { point, value } }
    // converting because point only lands in the object key. Under the sound metadata
    // rule (Codex P1s PRRT_kwDORD0nYs6087Qc / …6087Qf) a converted tuple boxed into a
    // metadata dictionary key is refused (stored + observed by the key comparer).
    [SkippableTheory]
    [InlineData(false, "Dictionary<object, (int X, int Y)> dict = new() { { point, (X: 3, Y: 4) } }; var copy = dict[point]; Bump(ref copy.X);")]
    [InlineData(true, "Dictionary<object, (int X, int Y)> dict = new() { { point, (X: 3, Y: 4) } }; var copy = dict[point]; Bump(ref copy.X);")]
    [InlineData(false, "var dict = new Dictionary<object, (int X, int Y)> { { point, (X: 3, Y: 4) } }; var copy = dict[point]; Bump(ref copy.X);")]
    [InlineData(true, "var dict = new Dictionary<object, (int X, int Y)> { { point, (X: 3, Y: 4) } }; var copy = dict[point]; Bump(ref copy.X);")]
    public async Task ConvertTupleToStruct_DictionaryInitializerObjectKey_ThrowsAndWritesNothing(bool asRecord, string statements)
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
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
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

    // Codex P1 on #3441 (PRRT_kwDORD0nYs600BT9): storage nested in a generic
    // wrapper parameter (Box<T>) and extracted by the return type (T) —
    // Unwrap(Wrap(point)) — must alias the extracted copy.
    [SkippableTheory]
    [InlineData(false, "var box = Wrap(point); var copy = Unwrap(box); Bump(ref copy.X);")]
    [InlineData(true, "var box = Wrap(point); var copy = Unwrap(box); Bump(ref copy.X);")]
    [InlineData(false, "var copy = Unwrap(Wrap(point)); Bump(ref copy.X);")]
    [InlineData(true, "var copy = Unwrap(Wrap(point)); Bump(ref copy.X);")]
    [InlineData(false, "var box = Wrap(point); var all = UnwrapAll(box); Bump(ref all[0].X);")]
    [InlineData(true, "var box = Wrap(point); var all = UnwrapAll(box); Bump(ref all[0].X);")]
    [InlineData(false, "var pair = MakePair(0, point); var copy = Second(pair); Bump(ref copy.X);")]
    [InlineData(true, "var pair = MakePair(0, point); var copy = Second(pair); Bump(ref copy.X);")]
    [InlineData(false, "var box = Wrap(point); var copy = box.Unwrap(); Bump(ref copy.X);")]
    [InlineData(true, "var box = Wrap(point); var copy = box.Unwrap(); Bump(ref copy.X);")]
    [InlineData(false, "var box = 5.With(point); var copy = Unwrap(box); Bump(ref copy.X);")]
    [InlineData(true, "var box = 5.With(point); var copy = Unwrap(box); Bump(ref copy.X);")]
    [InlineData(false, "var box = Wrap(point); var copy = box.Get(); Bump(ref copy.X);")]
    [InlineData(true, "var box = Wrap(point); var copy = box.Get(); Bump(ref copy.X);")]
    public async Task ConvertTupleToStruct_GenericWrapperExtractionRefElement_ThrowsAndWritesNothing(bool asRecord, string statements)
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
                    {{statements}}
                    return point;
                }

                static Box<T> Wrap<T>(T value) => new Box<T>(value);
                static T Unwrap<T>(Box<T> box) => box.Value;
                static T[] UnwrapAll<T>(Box<T> box) => new[] { box.Value };
                static Pair<TKey, TValue> MakePair<TKey, TValue>(TKey key, TValue value) => new Pair<TKey, TValue>(key, value);
                static TValue Second<TKey, TValue>(Pair<TKey, TValue> pair) => pair.Value;
            }

            public sealed class Box<T>
            {
                public Box(T value) => Value = value;
                public T Value;
                public T Get() => Value;
            }

            public sealed class Pair<TKey, TValue>
            {
                public Pair(TKey key, TValue value) { Key = key; Value = value; }
                public TKey Key;
                public TValue Value;
            }

            public static class BoxExtensions
            {
                public static T Unwrap<T>(this Box<T> box) => box.Value;
                public static Box<T> With<T>(this int seed, T value) => new Box<T>(value);
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

    // Codex P1 on #3441 (PRRT_kwDORD0nYs600BT9) over-refusal guard: extracting the
    // unrelated wrapper type argument (TKey bound to a different tuple) must not
    // alias the converted storage.
    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConvertTupleToStruct_GenericWrapperUnrelatedExtraction_Succeeds(bool asRecord)
    {
        var source = """
            using System;
            using System.Linq;

            namespace TestApp;

            public class Worker
            {
                static void Bump(ref int v) => v++;

                public object Create(bool flag)
                {
                    var point = (X: 1, Y: 2);
                    var pair = MakePair((A: 3, B: 4), point); var other = First(pair); Bump(ref other.A);
                    return point;
                }

                static Pair<TKey, TValue> MakePair<TKey, TValue>(TKey key, TValue value) => new Pair<TKey, TValue>(key, value);
                static TKey First<TKey, TValue>(Pair<TKey, TValue> pair) => pair.Key;
            }

            public sealed class Pair<TKey, TValue>
            {
                public Pair(TKey key, TValue value) { Key = key; Value = value; }
                public TKey Key;
                public TValue Value;
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
        Assert.Contains("Bump(ref other.A)", text, StringComparison.Ordinal);
    }

    // Codex P2 on #3441 (PRRT_kwDORD0nYs600gme): only wrapper members whose
    // original declared type mentions the bound type parameter forward storage.
    // Box<T>.Other is a fixed tuple field that merely shares the constructed shape.
    [SkippableTheory]
    [InlineData(false, "var box = Wrap(point); Bump(ref box.Other.X);")]
    [InlineData(true, "var box = Wrap(point); Bump(ref box.Other.X);")]
    [InlineData(false, "var box = Wrap(point); var other = box.Other; Bump(ref other.X);")]
    [InlineData(true, "var box = Wrap(point); var other = box.Other; Bump(ref other.X);")]
    [InlineData(false, "var box = Wrap(point); var others = box.Others; Bump(ref others[0].X);")]
    [InlineData(true, "var box = Wrap(point); var others = box.Others; Bump(ref others[0].X);")]
    public async Task ConvertTupleToStruct_GenericWrapperFixedTupleMember_Succeeds(bool asRecord, string statements)
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
                    {{statements}}
                    return point;
                }

                static Box<T> Wrap<T>(T value) => new Box<T>(value);
            }

            public sealed class Box<T>
            {
                public Box(T value) => Value = value;
                public T Value;
                public (int X, int Y) Other;
                public (int X, int Y)[] Others = new (int X, int Y)[1];
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
        Assert.Contains(".X)", text, StringComparison.Ordinal);
    }

    // Codex P1 on #3441 (PRRT_kwDORD0nYs600BUC): selectors passed as method
    // groups (points.Select(Echo)) must be analyzed like lambda selectors.
    [SkippableTheory]
    [InlineData(false, "var copies = points.Select(Echo).ToArray(); Bump(ref copies[0].X);")]
    [InlineData(true, "var copies = points.Select(Echo).ToArray(); Bump(ref copies[0].X);")]
    [InlineData(false, "var copies = System.Linq.Enumerable.Select(points, Echo).ToArray(); Bump(ref copies[0].X);")]
    [InlineData(true, "var copies = System.Linq.Enumerable.Select(points, Echo).ToArray(); Bump(ref copies[0].X);")]
    [InlineData(false, "var copies = Array.ConvertAll(points, Echo); Bump(ref copies[0].X);")]
    [InlineData(true, "var copies = Array.ConvertAll(points, Echo); Bump(ref copies[0].X);")]
    [InlineData(false, "var list = new System.Collections.Generic.List<(int X, int Y)> { point }; var copies = list.ConvertAll(Echo).ToArray(); Bump(ref copies[0].X);")]
    [InlineData(true, "var list = new System.Collections.Generic.List<(int X, int Y)> { point }; var copies = list.ConvertAll(Echo).ToArray(); Bump(ref copies[0].X);")]
    [InlineData(false, "var copies = points.Select(EchoIndexed).ToArray(); Bump(ref copies[0].X);")]
    [InlineData(true, "var copies = points.Select(EchoIndexed).ToArray(); Bump(ref copies[0].X);")]
    [InlineData(false, "foreach (var copy in points.Select(Echo)) { var c = copy; Bump(ref c.X); }")]
    [InlineData(true, "foreach (var copy in points.Select(Echo)) { var c = copy; Bump(ref c.X); }")]
    [InlineData(false, "var getters = points.Select(MakeGetter).ToArray(); var copy = getters[0](); Bump(ref copy.X);")]
    [InlineData(true, "var getters = points.Select(MakeGetter).ToArray(); var copy = getters[0](); Bump(ref copy.X);")]
    public async Task ConvertTupleToStruct_MethodGroupSelectorRefElement_ThrowsAndWritesNothing(bool asRecord, string statements)
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

                static T Echo<T>(T x) => x;
                static T EchoIndexed<T>(T x, int index) => x;
                static Func<T> MakeGetter<T>(T value) => () => value;
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

    // Codex P1 on #3441 (PRRT_kwDORD0nYs602oDk): ordinary constructors that capture
    // converted storage into object/dynamic/generic parameters are storage carriers —
    // var holder = new Holder(point); Bump(ref holder.Value.Item1) must refuse.
    [SkippableTheory]
    [InlineData(false, "var holder = new Holder(point); Bump(ref holder.Value.Item1);")]
    [InlineData(true, "var holder = new Holder(point); Bump(ref holder.Value.Item1);")]
    [InlineData(false, "var holder = new Holder(value: point); Bump(ref holder.Value.Item1);")]
    [InlineData(true, "var holder = new Holder(value: point); Bump(ref holder.Value.Item1);")]
    [InlineData(false, "Holder holder = new(point); Bump(ref holder.Value.Item1);")]
    [InlineData(true, "Holder holder = new(point); Bump(ref holder.Value.Item1);")]
    [InlineData(false, "var holder = new DynamicHolder(point); Bump(ref holder.Value.Item1);")]
    [InlineData(true, "var holder = new DynamicHolder(point); Bump(ref holder.Value.Item1);")]
    [InlineData(false, "var box = new Box<object>(point); Bump(ref box.Value.Item1);")]
    [InlineData(true, "var box = new Box<object>(point); Bump(ref box.Value.Item1);")]
    [InlineData(false, "var box = new Box<dynamic>(point); Bump(ref box.Value.Item1);")]
    [InlineData(true, "var box = new Box<dynamic>(point); Bump(ref box.Value.Item1);")]
    public async Task ConvertTupleToStruct_OrdinaryConstructorCaptureRefElement_ThrowsAndWritesNothing(bool asRecord, string statements)
    {
        var source = $$"""
            namespace TestApp;

            public class Holder
            {
                public dynamic Value;
                public Holder(object value) => Value = value;
            }

            public class DynamicHolder
            {
                public dynamic Value;
                public DynamicHolder(dynamic value) => Value = value;
            }

            public class Box<T>
            {
                public dynamic Value;
                public Box(T value) => Value = value;
            }

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
                Line = 27,
                NewTypeName = "Point",
                AsRecord = asRecord
            }));

        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
        Assert.Contains(asRecord ? "element assignments" : "passed by reference", ex.Message, StringComparison.Ordinal);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    // Codex P2 on #3441 (PRRT_kwDORD0nYs602oDm): constant container indices must be
    // resolved before container-wide tainting — values[1] of { point, new Other() }
    // must not mark copy as converted storage.
    [SkippableTheory]
    [InlineData(false, "object[] values = { point, new Other() }; dynamic copy = values[1]; Bump(ref copy.X);")]
    [InlineData(true, "object[] values = { point, new Other() }; dynamic copy = values[1]; Bump(ref copy.X);")]
    [InlineData(false, "object[] values = new object[] { point, new Other() }; dynamic copy = values[1]; Bump(ref copy.X);")]
    [InlineData(true, "object[] values = new object[] { point, new Other() }; dynamic copy = values[1]; Bump(ref copy.X);")]
    [InlineData(false, "var values = new object[] { point, new Other() }; dynamic copy = values[1]; Bump(ref copy.X);")]
    [InlineData(true, "var values = new object[] { point, new Other() }; dynamic copy = values[1]; Bump(ref copy.X);")]
    [InlineData(false, "object[] values = [point, new Other()]; dynamic copy = values[1]; Bump(ref copy.X);")]
    [InlineData(true, "object[] values = [point, new Other()]; dynamic copy = values[1]; Bump(ref copy.X);")]
    [InlineData(false, "object[] values = { point, new Other() }; dynamic copy = values[1]!; Bump(ref copy.X);")]
    [InlineData(true, "object[] values = { point, new Other() }; dynamic copy = values[1]!; Bump(ref copy.X);")]
    public async Task ConvertTupleToStruct_ConstantIndexUnrelatedElement_Succeeds(bool asRecord, string statements)
    {
        var source = $$"""
            namespace TestApp;

            public class Other
            {
                public int X;
            }

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

        // Dynamic ref-to-int sites match Codex evidence (same stance as NestedCreation /
        // DynamicAlias): the workspace accepts them as binding targets even when
        // ref-dynamic→ref-int is CS1503 under AssertTupleContainerCompiles.
        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ConvertTupleToStructOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ConvertTupleToStructParams
        {
            SourceFile = workspace.SourcePath,
            Line = 14,
            NewTypeName = "Point",
            AsRecord = asRecord
        });

        Assert.True(result.Success);
        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains(asRecord ? "public record struct Point" : "public struct Point", text, StringComparison.Ordinal);
        Assert.Contains("Bump(ref copy.X)", text, StringComparison.Ordinal);
    }

    // Codex P1 on #3441 (PRRT_kwDORD0nYs600BUC) over-refusal guard: a method-group
    // selector that synthesizes a fresh value of an unrelated type does not alias.
    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConvertTupleToStruct_MethodGroupProjectionSelector_Succeeds(bool asRecord)
    {
        var source = """
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
                    var copies = points.Select(Project).ToArray(); Bump(ref copies[0].A);
                    return point;
                }

                static (int A, int B) Project<T>(T value) => (1, 2);
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
        Assert.Contains("Bump(ref copies[0].A)", text, StringComparison.Ordinal);
    }

    // Codex P1 on #3441 (PRRT_kwDORD0nYs603sqr): a selected tuple passed directly to an
    // opaque constructor must climb to the enclosing assigned carrier before validation
    // is skipped — var holder = new Holder((X: 1, Y: 2)); Bump(ref holder.Value.X).
    [SkippableTheory]
    [InlineData(false, "var holder = new Holder((X: 1, Y: 2)); Bump(ref holder.Value.X);")]
    [InlineData(true, "var holder = new Holder((X: 1, Y: 2)); Bump(ref holder.Value.X);")]
    [InlineData(false, "var holder = new Holder(value: (X: 1, Y: 2)); Bump(ref holder.Value.X);")]
    [InlineData(true, "var holder = new Holder(value: (X: 1, Y: 2)); Bump(ref holder.Value.X);")]
    [InlineData(false, "Holder holder = new((X: 1, Y: 2)); Bump(ref holder.Value.X);")]
    [InlineData(true, "Holder holder = new((X: 1, Y: 2)); Bump(ref holder.Value.X);")]
    [InlineData(false, "var holder = new DynamicHolder((X: 1, Y: 2)); Bump(ref holder.Value.X);")]
    [InlineData(true, "var holder = new DynamicHolder((X: 1, Y: 2)); Bump(ref holder.Value.X);")]
    [InlineData(false, "var box = new Box<object>((X: 1, Y: 2)); Bump(ref box.Value.X);")]
    [InlineData(true, "var box = new Box<object>((X: 1, Y: 2)); Bump(ref box.Value.X);")]
    [InlineData(false, "var box = new Box<dynamic>((X: 1, Y: 2)); Bump(ref box.Value.X);")]
    [InlineData(true, "var box = new Box<dynamic>((X: 1, Y: 2)); Bump(ref box.Value.X);")]
    public async Task ConvertTupleToStruct_DirectConstructorArgumentRefElement_ThrowsAndWritesNothing(bool asRecord, string statements)
    {
        var source = $$"""
            namespace TestApp;

            public class Holder
            {
                public dynamic Value;
                public Holder(object value) => Value = value;
            }

            public class DynamicHolder
            {
                public dynamic Value;
                public DynamicHolder(dynamic value) => Value = value;
            }

            public class Box<T>
            {
                public dynamic Value;
                public Box(T value) => Value = value;
            }

            public class Worker
            {
                static void Bump(ref int v) => v++;

                public object Create()
                {
                    {{statements}}
                    return 0;
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
                Line = 27,
                NewTypeName = "Point",
                AsRecord = asRecord
            }));

        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
        Assert.Contains(asRecord ? "element assignments" : "passed by reference", ex.Message, StringComparison.Ordinal);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    // Codex P1 on #3441 (PRRT_kwDORD0nYs603sqw): converted storage passed to an ordinary
    // method / stored-delegate parameter must track that callee parameter (not only
    // inline lambda params and out vars) — Use(point) with Use(dynamic copy) => Bump(ref copy.X).
    [SkippableTheory]
    [InlineData(false, "Use(point);", "static void Use(dynamic copy) => Bump(ref copy.X);")]
    [InlineData(true, "Use(point);", "static void Use(dynamic copy) => Bump(ref copy.X);")]
    [InlineData(false, "Use(point);", "static void Use(object boxed) { dynamic copy = boxed; Bump(ref copy.X); }")]
    [InlineData(true, "Use(point);", "static void Use(object boxed) { dynamic copy = boxed; Bump(ref copy.X); }")]
    [InlineData(false, "Use(point);", "static void Use((int X, int Y) copy) => Bump(ref copy.X);")]
    [InlineData(true, "Use(point);", "static void Use((int X, int Y) copy) => Bump(ref copy.X);")]
    [InlineData(false, "Use<object>(point);", "static void Use<T>(T copy) { dynamic d = copy; Bump(ref d.X); }")]
    [InlineData(true, "Use<object>(point);", "static void Use<T>(T copy) { dynamic d = copy; Bump(ref d.X); }")]
    [InlineData(false, "Action<dynamic> use = copy => Bump(ref copy.X); use(point);", "")]
    [InlineData(true, "Action<dynamic> use = copy => Bump(ref copy.X); use(point);", "")]
    [InlineData(false, "Action<dynamic> use = copy => Bump(ref copy.X); use.Invoke(point);", "")]
    [InlineData(true, "Action<dynamic> use = copy => Bump(ref copy.X); use.Invoke(point);", "")]
    [InlineData(false, "var use = (Action<dynamic>)(copy => Bump(ref copy.X)); use(point);", "")]
    [InlineData(true, "var use = (Action<dynamic>)(copy => Bump(ref copy.X)); use(point);", "")]
    [InlineData(false, "Use((X: 1, Y: 2));", "static void Use(dynamic copy) => Bump(ref copy.X);")]
    [InlineData(true, "Use((X: 1, Y: 2));", "static void Use(dynamic copy) => Bump(ref copy.X);")]
    public async Task ConvertTupleToStruct_OrdinaryCalleeParameterRefElement_ThrowsAndWritesNothing(
        bool asRecord,
        string statements,
        string helpers)
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
                    {{statements}}
                    return point;
                }

                {{helpers}}
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

    // Over-refusal guard for ordinary callee params: by-value reads through the
    // callee must still convert.
    [SkippableTheory]
    [InlineData(false, "var n = Read(point);", "static int Read(dynamic copy) => copy.X;")]
    [InlineData(true, "var n = Read(point);", "static int Read(dynamic copy) => copy.X;")]
    [InlineData(false, "var n = Read(point);", "static int Read((int X, int Y) copy) => copy.X;")]
    [InlineData(true, "var n = Read(point);", "static int Read((int X, int Y) copy) => copy.X;")]
    [InlineData(false, "Action<dynamic> read = copy => { var _ = copy.X; }; read(point);", "")]
    [InlineData(true, "Action<dynamic> read = copy => { var _ = copy.X; }; read(point);", "")]
    public async Task ConvertTupleToStruct_OrdinaryCalleeParameterByValue_Succeeds(
        bool asRecord,
        string statements,
        string helpers)
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
                    {{statements}}
                    return point;
                }

                {{helpers}}
            }
            """;

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
    }

    // Codex P1 on #3441 (PRRT_kwDORD0nYs604Ela): local storage + Use(point) in one partial
    // document, callee body with by-ref element use in another — propagated parameter
    // aliases must be scanned in the callee's document too.
    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConvertTupleToStruct_CrossDocumentPartialCalleeParameterRefElement_ThrowsAndWritesNothing(bool asRecord)
    {
        const string primary = """
            namespace TestApp;

            public partial class Worker
            {
                public object Create()
                {
                    var point = (X: 1, Y: 2);
                    Use(point);
                    return point;
                }
            }
            """;
        const string secondary = """
            namespace TestApp;

            public partial class Worker
            {
                static void Bump(ref int v) => v++;

                static void Use(dynamic copy) => Bump(ref copy.X);
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(
            ("Worker.cs", primary),
            ("Worker.Partial.cs", secondary));
        var primaryPath = workspace.SourcePath;
        var secondaryPath = Path.Combine(workspace.DirectoryPath, "Worker.Partial.cs");
        var originalPrimary = await File.ReadAllTextAsync(primaryPath);
        var originalSecondary = await File.ReadAllTextAsync(secondaryPath);
        var operation = new ConvertTupleToStructOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ConvertTupleToStructParams
            {
                SourceFile = primaryPath,
                Line = 7,
                NewTypeName = "Point",
                AsRecord = asRecord
            }));

        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
        Assert.Contains(asRecord ? "element assignments" : "passed by reference", ex.Message, StringComparison.Ordinal);
        Assert.Equal(originalPrimary, await File.ReadAllTextAsync(primaryPath));
        Assert.Equal(originalSecondary, await File.ReadAllTextAsync(secondaryPath));
    }

    // Sibling: constructor parameter body in another partial document.
    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConvertTupleToStruct_CrossDocumentPartialConstructorParameterRefElement_ThrowsAndWritesNothing(bool asRecord)
    {
        const string primary = """
            namespace TestApp;

            public partial class Holder
            {
                public object Create()
                {
                    var point = (X: 1, Y: 2);
                    var holder = new Holder(point);
                    return holder;
                }
            }
            """;
        const string secondary = """
            namespace TestApp;

            public partial class Holder
            {
                static void Bump(ref int v) => v++;

                public Holder(dynamic copy) => Bump(ref copy.X);
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(
            ("Holder.cs", primary),
            ("Holder.Partial.cs", secondary));
        var primaryPath = workspace.SourcePath;
        var secondaryPath = Path.Combine(workspace.DirectoryPath, "Holder.Partial.cs");
        var originalPrimary = await File.ReadAllTextAsync(primaryPath);
        var originalSecondary = await File.ReadAllTextAsync(secondaryPath);
        var operation = new ConvertTupleToStructOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ConvertTupleToStructParams
            {
                SourceFile = primaryPath,
                Line = 7,
                NewTypeName = "Point",
                AsRecord = asRecord
            }));

        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
        Assert.Contains(asRecord ? "element assignments" : "passed by reference", ex.Message, StringComparison.Ordinal);
        Assert.Equal(originalPrimary, await File.ReadAllTextAsync(primaryPath));
        Assert.Equal(originalSecondary, await File.ReadAllTextAsync(secondaryPath));
    }

    // Codex P1 on #3441 (PRRT_kwDORD0nYs606U0Z): constructor initializer : this(value, 0)
    // must forward the tracked parameter into the destination ctor.
    [SkippableTheory]
    [InlineData(false, ": this(value, 0)")]
    [InlineData(true, ": this(value, 0)")]
    [InlineData(false, ": this(value: value, other: 0)")]
    [InlineData(true, ": this(value: value, other: 0)")]
    public async Task ConvertTupleToStruct_ConstructorInitializerForwardRefElement_ThrowsAndWritesNothing(
        bool asRecord,
        string initializer)
    {
        var source = $$"""
            namespace TestApp;

            public class Holder
            {
                public dynamic Value;

                public Holder(object value) {{initializer}}
                {
                }

                public Holder(object value, int other)
                {
                    dynamic copy = value;
                    Bump(ref copy.Item1);
                    Value = value;
                }

                static void Bump(ref int v) => v++;
            }

            public class Worker
            {
                public object Create()
                {
                    var point = (1, 2);
                    var holder = new Holder(point);
                    return holder;
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
                Line = 25,
                NewTypeName = "Point",
                AsRecord = asRecord
            }));

        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
        Assert.Contains(asRecord ? "element assignments" : "passed by reference", ex.Message, StringComparison.Ordinal);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    // Over-refusal guard: constructor initializer that only reads the forwarded
    // value still converts.
    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConvertTupleToStruct_ConstructorInitializerForwardByValue_Succeeds(bool asRecord)
    {
        const string source = """
            namespace TestApp;

            public class Holder
            {
                public object Value;

                public Holder(object value) : this(value, 0)
                {
                }

                public Holder(object value, int other)
                {
                    Value = value;
                    System.Console.WriteLine(other);
                }
            }

            public class Worker
            {
                public object Create()
                {
                    var point = (1, 2);
                    var holder = new Holder(point);
                    return holder;
                }
            }
            """;

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
        Assert.Contains("new Holder(point)", text, StringComparison.Ordinal);
    }

    // Sibling: stored method-group / delegate field whose target lives in another
    // partial document.
    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConvertTupleToStruct_CrossDocumentPartialDelegateMethodGroupRefElement_ThrowsAndWritesNothing(bool asRecord)
    {
        const string primary = """
            using System;

            namespace TestApp;

            public partial class Worker
            {
                public Action<dynamic> use = Use;

                public object Create()
                {
                    var point = (X: 1, Y: 2);
                    use(point);
                    return point;
                }
            }
            """;
        const string secondary = """
            namespace TestApp;

            public partial class Worker
            {
                static void Bump(ref int v) => v++;

                static void Use(dynamic copy) => Bump(ref copy.X);
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(
            ("Worker.cs", primary),
            ("Worker.Partial.cs", secondary));
        var primaryPath = workspace.SourcePath;
        var secondaryPath = Path.Combine(workspace.DirectoryPath, "Worker.Partial.cs");
        var originalPrimary = await File.ReadAllTextAsync(primaryPath);
        var originalSecondary = await File.ReadAllTextAsync(secondaryPath);
        var operation = new ConvertTupleToStructOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ConvertTupleToStructParams
            {
                SourceFile = primaryPath,
                Line = 11,
                NewTypeName = "Point",
                AsRecord = asRecord
            }));

        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
        Assert.Contains(asRecord ? "element assignments" : "passed by reference", ex.Message, StringComparison.Ordinal);
        Assert.Equal(originalPrimary, await File.ReadAllTextAsync(primaryPath));
        Assert.Equal(originalSecondary, await File.ReadAllTextAsync(secondaryPath));
    }

    // Sibling: callee lives in a referenced project; local storage + call are in the
    // dependent project.
    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConvertTupleToStruct_DependentProjectCalleeParameterRefElement_ThrowsAndWritesNothing(bool asRecord)
    {
        await using var workspace = await TempWorkspace.CreateDependentProjectCalleeRefAsync();
        var originalOrigin = await File.ReadAllTextAsync(workspace.SourcePath);
        var originalConsumer = await File.ReadAllTextAsync(workspace.SecondarySourcePath);
        var operation = new ConvertTupleToStructOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ConvertTupleToStructParams
            {
                SourceFile = workspace.SourcePath,
                Line = 7,
                NewTypeName = "Point",
                AsRecord = asRecord
            }));

        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
        Assert.Contains(asRecord ? "element assignments" : "passed by reference", ex.Message, StringComparison.Ordinal);
        Assert.Equal(originalOrigin, await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Equal(originalConsumer, await File.ReadAllTextAsync(workspace.SecondarySourcePath));
    }

    // Over-refusal guard: cross-document callee that only reads by value still converts.
    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConvertTupleToStruct_CrossDocumentPartialCalleeParameterByValue_Succeeds(bool asRecord)
    {
        const string primary = """
            namespace TestApp;

            public partial class Worker
            {
                public object Create()
                {
                    var point = (X: 1, Y: 2);
                    var n = Read(point);
                    return point;
                }
            }
            """;
        const string secondary = """
            namespace TestApp;

            public partial class Worker
            {
                static int Read(dynamic copy) => copy.X;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(
            ("Worker.cs", primary),
            ("Worker.Partial.cs", secondary));
        var operation = new ConvertTupleToStructOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ConvertTupleToStructParams
        {
            SourceFile = workspace.SourcePath,
            Line = 7,
            NewTypeName = "Point",
            AsRecord = asRecord
        });

        Assert.True(result.Success);
        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains(asRecord ? "public record struct Point" : "public struct Point", text, StringComparison.Ordinal);
    }

    // Codex P1 on #3441 (PRRT_kwDORD0nYs604ZeC): reduced extension receiver point.Use()
    // never appears in ArgumentList — refuse rather than miss the this parameter.
    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConvertTupleToStruct_ReducedExtensionReceiverRefElement_ThrowsAndWritesNothing(bool asRecord)
    {
        var source = """
            using System;

            namespace TestApp;

            public static class PointExtensions
            {
                public static void Use(this object boxed)
                {
                    dynamic copy = boxed;
                    Bump(ref copy.Item1);
                }

                static void Bump(ref int v) => v++;
            }

            public class Worker
            {
                public object Create()
                {
                    var point = (X: 1, Y: 2);
                    point.Use();
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
                Line = 20,
                NewTypeName = "Point",
                AsRecord = asRecord
            }));

        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
        Assert.Contains(asRecord ? "element assignments" : "passed by reference", ex.Message, StringComparison.Ordinal);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    // Codex P1 on #3441 (PRRT_kwDORD0nYs604ZeE): interface / virtual dispatch maps to
    // the declaration parameter, not the implementing body — refuse.
    [SkippableTheory]
    [InlineData(false, "interface")]
    [InlineData(true, "interface")]
    [InlineData(false, "virtual")]
    [InlineData(true, "virtual")]
    public async Task ConvertTupleToStruct_InterfaceOrVirtualDispatchRefElement_ThrowsAndWritesNothing(
        bool asRecord,
        string dispatch)
    {
        string source;
        int line;
        if (dispatch == "interface")
        {
            source = """
                using System;

                namespace TestApp;

                public interface IConsumer
                {
                    void Use(object boxed);
                }

                public sealed class Consumer : IConsumer
                {
                    public void Use(object boxed)
                    {
                        dynamic copy = boxed;
                        Bump(ref copy.Item1);
                    }

                    static void Bump(ref int v) => v++;
                }

                public class Worker
                {
                    public object Create()
                    {
                        var point = (X: 1, Y: 2);
                        IConsumer consumer = new Consumer();
                        consumer.Use(point);
                        return point;
                    }
                }
                """;
            line = 25;
        }
        else
        {
            source = """
                using System;

                namespace TestApp;

                public class Consumer
                {
                    public virtual void Use(object boxed)
                    {
                        dynamic copy = boxed;
                        Bump(ref copy.Item1);
                    }

                    static void Bump(ref int v) => v++;
                }

                public class Worker
                {
                    public object Create()
                    {
                        var point = (X: 1, Y: 2);
                        Consumer consumer = new Consumer();
                        consumer.Use(point);
                        return point;
                    }
                }
                """;
            line = 20;
        }

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var original = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new ConvertTupleToStructOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ConvertTupleToStructParams
            {
                SourceFile = workspace.SourcePath,
                Line = line,
                NewTypeName = "Point",
                AsRecord = asRecord
            }));

        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
        Assert.Contains(asRecord ? "element assignments" : "passed by reference", ex.Message, StringComparison.Ordinal);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    // Codex P1 on #3441 (PRRT_kwDORD0nYs604ZeH): later delegate reassignment must not
    // be mapped to the discarded initializer — refuse when sources are not unique.
    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConvertTupleToStruct_DelegateReassignmentRefElement_ThrowsAndWritesNothing(bool asRecord)
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
                    Action<dynamic> use = _ => { };
                    use = copy => Bump(ref copy.Item1);
                    use(point);
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

    // Codex P1 on #3441 (PRRT_kwDORD0nYs604ZeL): expanded params arguments must not
    // drop the storage-bearing arg when its index exceeds Parameters.Length — refuse.
    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConvertTupleToStruct_ExpandedParamsArgumentRefElement_ThrowsAndWritesNothing(bool asRecord)
    {
        var source = """
            using System;

            namespace TestApp;

            public class Worker
            {
                static void Bump(ref int v) => v++;

                static void Use(int n, params dynamic[] copies)
                {
                    Bump(ref copies[1].Item1);
                }

                public object Create()
                {
                    var point = (X: 1, Y: 2);
                    Use(0, "skip", point);
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
                Line = 16,
                NewTypeName = "Point",
                AsRecord = asRecord
            }));

        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
        Assert.Contains(asRecord ? "element assignments" : "passed by reference", ex.Message, StringComparison.Ordinal);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    private const string OpaqueReturnTemplate = """
        using System;

        namespace TestApp;

        public static class Worker
        {
            static void Bump(ref dynamic v) => v++;
            DECL
            public static void Run()
            {
                USE
            }

            static void Use(dynamic copy) => Bump(ref copy.Item1);
        }
        """;

    // Codex P1 on #3441 (PRRT_kwDORD0nYs605DwK): a tuple returned directly from an
    // object / dynamic method (or local function / stored lambda) has no assigned
    // storage; follow the function's call sites and refuse when a result reaches a
    // by-ref element use, or when a call site cannot be resolved (method group).
    [SkippableTheory]
    [InlineData(false, 8, "static dynamic Get() => (1, 2);", "dynamic copy = Get(); Bump(ref copy.Item1);")]
    [InlineData(true, 8, "static dynamic Get() => (1, 2);", "dynamic copy = Get(); Bump(ref copy.Item1);")]
    [InlineData(false, 8, "static object Get() { return (1, 2); }", "dynamic copy = Get(); Bump(ref copy.Item1);")]
    [InlineData(true, 8, "static object Get() { return (1, 2); }", "dynamic copy = Get(); Bump(ref copy.Item1);")]
    [InlineData(false, 8, "static dynamic Get() => (1, 2);", "Bump(ref Get().Item1);")]
    [InlineData(true, 8, "static dynamic Get() => (1, 2);", "Bump(ref Get().Item1);")]
    [InlineData(false, 8, "static object Get() => (object)(1, 2);", "Use(Get());")]
    [InlineData(true, 8, "static object Get() => (object)(1, 2);", "Use(Get());")]
    [InlineData(false, 8, "static dynamic Get() => (1, 2); static dynamic Forward() => Get();", "dynamic copy = Forward(); Bump(ref copy.Item1);")]
    [InlineData(true, 8, "static dynamic Get() => (1, 2); static dynamic Forward() => Get();", "dynamic copy = Forward(); Bump(ref copy.Item1);")]
    [InlineData(false, 8, "static dynamic Get() => (1, 2);", "Func<dynamic> get = Get; dynamic copy = get(); Bump(ref copy.Item1);")]
    [InlineData(true, 8, "static dynamic Get() => (1, 2);", "Func<dynamic> get = Get; dynamic copy = get(); Bump(ref copy.Item1);")]
    [InlineData(false, 11, "static void Unused() { }", "dynamic copy = Local(); Bump(ref copy.Item1); static dynamic Local() => (1, 2);")]
    [InlineData(true, 11, "static void Unused() { }", "dynamic copy = Local(); Bump(ref copy.Item1); static dynamic Local() => (1, 2);")]
    [InlineData(false, 11, "static void Unused() { }", "Func<dynamic> get = () => (1, 2); dynamic copy = get(); Bump(ref copy.Item1);")]
    [InlineData(true, 11, "static void Unused() { }", "Func<dynamic> get = () => (1, 2); dynamic copy = get(); Bump(ref copy.Item1);")]
    public async Task ConvertTupleToStruct_DynamicReturnRefElement_ThrowsAndWritesNothing(
        bool asRecord,
        int line,
        string declaration,
        string use)
    {
        var source = OpaqueReturnTemplate.Replace("DECL", declaration).Replace("USE", use);
        AssertTupleContainerCompiles(source);
        await using var workspace = await TempWorkspace.CreateAsync(source);
        var original = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new ConvertTupleToStructOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ConvertTupleToStructParams
            {
                SourceFile = workspace.SourcePath,
                Line = line,
                NewTypeName = "Point",
                AsRecord = asRecord
            }));

        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    // Companion to PRRT_kwDORD0nYs605DwK: object / dynamic returns whose call-site
    // results are only read (no by-ref element use) still convert.
    [SkippableTheory]
    [InlineData(false, "static object Get() => (1, 2);", "object copy = Get(); Console.WriteLine(copy);")]
    [InlineData(true, "static object Get() => (1, 2);", "object copy = Get(); Console.WriteLine(copy);")]
    [InlineData(false, "static object Get() => (1, 2);", "Console.WriteLine(Get());")]
    [InlineData(true, "static object Get() => (1, 2);", "Console.WriteLine(Get());")]
    [InlineData(false, "static dynamic Get() => (1, 2);", "dynamic copy = Get(); int x = copy.Item1; Console.WriteLine(x);")]
    [InlineData(true, "static dynamic Get() => (1, 2);", "dynamic copy = Get(); int x = copy.Item1; Console.WriteLine(x);")]
    public async Task ConvertTupleToStruct_ObjectReturnNoRefUse_Succeeds(
        bool asRecord,
        string declaration,
        string use)
    {
        var source = OpaqueReturnTemplate.Replace("DECL", declaration).Replace("USE", use);
        AssertTupleContainerCompiles(source);
        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ConvertTupleToStructOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ConvertTupleToStructParams
        {
            SourceFile = workspace.SourcePath,
            Line = 8,
            NewTypeName = "Point",
            AsRecord = asRecord
        });

        Assert.True(result.Success);
        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains(asRecord ? "public record struct Point" : "public struct Point", text, StringComparison.Ordinal);
        Assert.Contains("new Point", text, StringComparison.Ordinal);
    }

    private const string CalleeFlowTemplate = """
        using System;
        using System.Collections.Generic;
        using System.Linq;
        using System.Threading.Tasks;

        namespace TestApp;

        public static class Worker
        {
            static void Bump(ref dynamic v) => v++;
            DECL
            public static void Run()
            {
                USE
            }
        }
        """;

    // Codex P1 on #3441 (PRRT_kwDORD0nYs605hQG): a tuple produced by `yield return`
    // in an object / dynamic iterator (method, getter, async iterator) is followed
    // through the iterator's call sites; foreach elements used by ref refuse, and
    // sequence results that cannot be followed element-wise refuse conservatively.
    [SkippableTheory]
    [InlineData(false, 11, "static IEnumerable<dynamic> Get() { yield return (1, 2); }", "var items = Get(); foreach (dynamic copy in items) Bump(ref copy.Item1);")]
    [InlineData(true, 11, "static IEnumerable<dynamic> Get() { yield return (1, 2); }", "var items = Get(); foreach (dynamic copy in items) Bump(ref copy.Item1);")]
    [InlineData(false, 11, "static IEnumerable<object> Get() { yield return (1, 2); }", "foreach (dynamic copy in Get()) Bump(ref copy.Item1);")]
    [InlineData(true, 11, "static IEnumerable<object> Get() { yield return (1, 2); }", "foreach (dynamic copy in Get()) Bump(ref copy.Item1);")]
    [InlineData(false, 11, "static System.Collections.IEnumerable Get() { yield return (1, 2); }", "foreach (dynamic copy in Get()) Bump(ref copy.Item1);")]
    [InlineData(true, 11, "static System.Collections.IEnumerable Get() { yield return (1, 2); }", "foreach (dynamic copy in Get()) Bump(ref copy.Item1);")]
    [InlineData(false, 11, "static IEnumerable<dynamic> Get() { yield return (1, 2); }", "dynamic copy = Get().First(); Bump(ref copy.Item1);")]
    [InlineData(true, 11, "static IEnumerable<dynamic> Get() { yield return (1, 2); }", "dynamic copy = Get().First(); Bump(ref copy.Item1);")]
    [InlineData(false, 11, "static IEnumerable<object> Items { get { yield return (1, 2); } }", "foreach (dynamic copy in Items) Bump(ref copy.Item1);")]
    [InlineData(true, 11, "static IEnumerable<object> Items { get { yield return (1, 2); } }", "foreach (dynamic copy in Items) Bump(ref copy.Item1);")]
    [InlineData(false, 11, "static async IAsyncEnumerable<object> Get() { await Task.Yield(); yield return (1, 2); } static async Task Consume() { await foreach (dynamic copy in Get()) Bump(ref copy.Item1); }", "Consume().Wait();")]
    [InlineData(true, 11, "static async IAsyncEnumerable<object> Get() { await Task.Yield(); yield return (1, 2); } static async Task Consume() { await foreach (dynamic copy in Get()) Bump(ref copy.Item1); }", "Consume().Wait();")]
    public async Task ConvertTupleToStruct_IteratorYieldRefElement_ThrowsAndWritesNothing(
        bool asRecord,
        int line,
        string declaration,
        string use)
    {
        var source = CalleeFlowTemplate.Replace("DECL", declaration).Replace("USE", use);
        AssertTupleContainerCompiles(source);
        await using var workspace = await TempWorkspace.CreateAsync(source);
        var original = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new ConvertTupleToStructOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ConvertTupleToStructParams
            {
                SourceFile = workspace.SourcePath,
                Line = line,
                NewTypeName = "Point",
                AsRecord = asRecord
            }));

        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    // Companion to PRRT_kwDORD0nYs605hQG: iterator elements only read still convert.
    [SkippableTheory]
    [InlineData(false, 11, "static IEnumerable<object> Get() { yield return (1, 2); }", "foreach (object copy in Get()) Console.WriteLine(copy);")]
    [InlineData(true, 11, "static IEnumerable<object> Get() { yield return (1, 2); }", "foreach (object copy in Get()) Console.WriteLine(copy);")]
    [InlineData(false, 11, "static IEnumerable<object> Get() { yield return (1, 2); }", "var items = Get(); foreach (var copy in items) Console.WriteLine(copy);")]
    [InlineData(true, 11, "static IEnumerable<object> Get() { yield return (1, 2); }", "var items = Get(); foreach (var copy in items) Console.WriteLine(copy);")]
    public async Task ConvertTupleToStruct_IteratorYieldNoRefUse_Succeeds(
        bool asRecord,
        int line,
        string declaration,
        string use)
    {
        var source = CalleeFlowTemplate.Replace("DECL", declaration).Replace("USE", use);
        AssertTupleContainerCompiles(source);
        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ConvertTupleToStructOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ConvertTupleToStructParams
        {
            SourceFile = workspace.SourcePath,
            Line = line,
            NewTypeName = "Point",
            AsRecord = asRecord
        });

        Assert.True(result.Success);
        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains(asRecord ? "public record struct Point" : "public struct Point", text, StringComparison.Ordinal);
        Assert.Contains("new Point", text, StringComparison.Ordinal);
    }

    // Codex P1 on #3441 (PRRT_kwDORD0nYs605hQL): assigning converted storage to an
    // object / dynamic property or indexer maps it onto the setter / init `value`
    // (and indexer index) parameter; open-virtual setters refuse conservatively.
    [SkippableTheory]
    [InlineData(false, 14, "sealed class Holder { public object Value { set { dynamic copy = value; Bump(ref copy.Item1); } } }", "var holder = new Holder(); var point = (1, 2); holder.Value = point;")]
    [InlineData(true, 14, "sealed class Holder { public object Value { set { dynamic copy = value; Bump(ref copy.Item1); } } }", "var holder = new Holder(); var point = (1, 2); holder.Value = point;")]
    [InlineData(false, 14, "sealed class Holder { public dynamic Value { set { dynamic copy = value; Bump(ref copy.Item1); } } }", "var holder = new Holder(); var point = (1, 2); holder.Value = point;")]
    [InlineData(true, 14, "sealed class Holder { public dynamic Value { set { dynamic copy = value; Bump(ref copy.Item1); } } }", "var holder = new Holder(); var point = (1, 2); holder.Value = point;")]
    [InlineData(false, 14, "sealed class Holder { public object Value { init { dynamic copy = value; Bump(ref copy.Item1); } } }", "var point = (1, 2); var holder = new Holder { Value = point };")]
    [InlineData(true, 14, "sealed class Holder { public object Value { init { dynamic copy = value; Bump(ref copy.Item1); } } }", "var point = (1, 2); var holder = new Holder { Value = point };")]
    [InlineData(false, 14, "sealed class Holder { public object this[int i] { set { dynamic copy = value; Bump(ref copy.Item1); } } }", "var holder = new Holder(); var point = (1, 2); holder[0] = point;")]
    [InlineData(true, 14, "sealed class Holder { public object this[int i] { set { dynamic copy = value; Bump(ref copy.Item1); } } }", "var holder = new Holder(); var point = (1, 2); holder[0] = point;")]
    [InlineData(false, 14, "sealed class Holder { public int this[object key] { get { dynamic copy = key; Bump(ref copy.Item1); return 0; } } }", "var holder = new Holder(); var point = (1, 2); Console.WriteLine(holder[point]);")]
    [InlineData(true, 14, "sealed class Holder { public int this[object key] { get { dynamic copy = key; Bump(ref copy.Item1); return 0; } } }", "var holder = new Holder(); var point = (1, 2); Console.WriteLine(holder[point]);")]
    [InlineData(false, 14, "class Holder { public virtual object Value { get; set; } }", "Holder holder = new Holder(); var point = (1, 2); holder.Value = point;")]
    [InlineData(true, 14, "class Holder { public virtual object Value { get; set; } }", "Holder holder = new Holder(); var point = (1, 2); holder.Value = point;")]
    public async Task ConvertTupleToStruct_PropertySetterRefElement_ThrowsAndWritesNothing(
        bool asRecord,
        int line,
        string declaration,
        string use)
    {
        var source = CalleeFlowTemplate.Replace("DECL", declaration).Replace("USE", use);
        AssertTupleContainerCompiles(source);
        await using var workspace = await TempWorkspace.CreateAsync(source);
        var original = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new ConvertTupleToStructOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ConvertTupleToStructParams
            {
                SourceFile = workspace.SourcePath,
                Line = line,
                NewTypeName = "Point",
                AsRecord = asRecord
            }));

        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    // Companion to PRRT_kwDORD0nYs605hQL: auto-properties / setters that only read convert.
    [SkippableTheory]
    [InlineData(false, 14, "sealed class Holder { public object Value { get; set; } }", "var holder = new Holder(); var point = (1, 2); holder.Value = point; Console.WriteLine(holder.Value);")]
    [InlineData(true, 14, "sealed class Holder { public object Value { get; set; } }", "var holder = new Holder(); var point = (1, 2); holder.Value = point; Console.WriteLine(holder.Value);")]
    [InlineData(false, 14, "sealed class Holder { public object Value { set { Console.WriteLine(value); } } }", "var holder = new Holder(); var point = (1, 2); holder.Value = point;")]
    [InlineData(true, 14, "sealed class Holder { public object Value { set { Console.WriteLine(value); } } }", "var holder = new Holder(); var point = (1, 2); holder.Value = point;")]
    public async Task ConvertTupleToStruct_PropertySetterNoRefUse_Succeeds(
        bool asRecord,
        int line,
        string declaration,
        string use)
    {
        var source = CalleeFlowTemplate.Replace("DECL", declaration).Replace("USE", use);
        AssertTupleContainerCompiles(source);
        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ConvertTupleToStructOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ConvertTupleToStructParams
        {
            SourceFile = workspace.SourcePath,
            Line = line,
            NewTypeName = "Point",
            AsRecord = asRecord
        });

        Assert.True(result.Success);
        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains(asRecord ? "public record struct Point" : "public struct Point", text, StringComparison.Ordinal);
        Assert.Contains("new Point", text, StringComparison.Ordinal);
    }

    // Codex P1 on #3441 (PRRT_kwDORD0nYs605hQM): user-defined binary / compound
    // operators and implicit / explicit conversions map storage-bearing operands to
    // their parameters; dynamically bound operators against a type declaring
    // user-defined operators refuse conservatively.
    [SkippableTheory]
    [InlineData(false, 14, "sealed class Consumer { public static Consumer operator +(Consumer c, object boxed) { dynamic copy = boxed; Bump(ref copy.Item1); return c; } }", "var consumer = new Consumer(); var point = (1, 2); var result = consumer + point;")]
    [InlineData(true, 14, "sealed class Consumer { public static Consumer operator +(Consumer c, object boxed) { dynamic copy = boxed; Bump(ref copy.Item1); return c; } }", "var consumer = new Consumer(); var point = (1, 2); var result = consumer + point;")]
    [InlineData(false, 14, "sealed class Accumulator { public static Accumulator operator +(Accumulator c, object boxed) { dynamic copy = boxed; Bump(ref copy.Item1); return c; } }", "var consumer = new Accumulator(); var point = (1, 2); consumer += point;")]
    [InlineData(true, 14, "sealed class Accumulator { public static Accumulator operator +(Accumulator c, object boxed) { dynamic copy = boxed; Bump(ref copy.Item1); return c; } }", "var consumer = new Accumulator(); var point = (1, 2); consumer += point;")]
    [InlineData(false, 14, "sealed class LateBound { public static LateBound operator +(LateBound c, object boxed) { dynamic copy = boxed; Bump(ref copy.Item1); return c; } }", "var consumer = new LateBound(); var point = (1, 2); dynamic boxed = point; var result = consumer + boxed;")]
    [InlineData(true, 14, "sealed class LateBound { public static LateBound operator +(LateBound c, object boxed) { dynamic copy = boxed; Bump(ref copy.Item1); return c; } }", "var consumer = new LateBound(); var point = (1, 2); dynamic boxed = point; var result = consumer + boxed;")]
    [InlineData(false, 14, "sealed class Wrapper { public static implicit operator Wrapper((int, int) boxed) { dynamic copy = boxed; Bump(ref copy.Item1); return new Wrapper(); } }", "var point = (1, 2); Wrapper wrapper = point;")]
    [InlineData(true, 14, "sealed class Wrapper { public static implicit operator Wrapper((int, int) boxed) { dynamic copy = boxed; Bump(ref copy.Item1); return new Wrapper(); } }", "var point = (1, 2); Wrapper wrapper = point;")]
    [InlineData(false, 14, "sealed class Wrapper { public static explicit operator Wrapper((int, int) boxed) { dynamic copy = boxed; Bump(ref copy.Item1); return new Wrapper(); } }", "var point = (1, 2); var wrapper = (Wrapper)point;")]
    [InlineData(true, 14, "sealed class Wrapper { public static explicit operator Wrapper((int, int) boxed) { dynamic copy = boxed; Bump(ref copy.Item1); return new Wrapper(); } }", "var point = (1, 2); var wrapper = (Wrapper)point;")]
    public async Task ConvertTupleToStruct_UserDefinedOperatorRefElement_ThrowsAndWritesNothing(
        bool asRecord,
        int line,
        string declaration,
        string use)
    {
        var source = CalleeFlowTemplate.Replace("DECL", declaration).Replace("USE", use);
        AssertTupleContainerCompiles(source);
        await using var workspace = await TempWorkspace.CreateAsync(source);
        var original = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new ConvertTupleToStructOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ConvertTupleToStructParams
            {
                SourceFile = workspace.SourcePath,
                Line = line,
                NewTypeName = "Point",
                AsRecord = asRecord
            }));

        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    // Companion to PRRT_kwDORD0nYs605hQM: operator / conversion bodies that only read convert.
    [SkippableTheory]
    [InlineData(false, 14, "sealed class Consumer { public static Consumer operator +(Consumer c, object boxed) { Console.WriteLine(boxed); return c; } }", "var consumer = new Consumer(); var point = (1, 2); var result = consumer + point;")]
    [InlineData(true, 14, "sealed class Consumer { public static Consumer operator +(Consumer c, object boxed) { Console.WriteLine(boxed); return c; } }", "var consumer = new Consumer(); var point = (1, 2); var result = consumer + point;")]
    [InlineData(false, 14, "sealed class Wrapper { public static implicit operator Wrapper((int, int) boxed) { Console.WriteLine(boxed.Item1); return new Wrapper(); } }", "var point = (1, 2); Wrapper wrapper = point;")]
    [InlineData(true, 14, "sealed class Wrapper { public static implicit operator Wrapper((int, int) boxed) { Console.WriteLine(boxed.Item1); return new Wrapper(); } }", "var point = (1, 2); Wrapper wrapper = point;")]
    public async Task ConvertTupleToStruct_UserDefinedOperatorNoRefUse_Succeeds(
        bool asRecord,
        int line,
        string declaration,
        string use)
    {
        var source = CalleeFlowTemplate.Replace("DECL", declaration).Replace("USE", use);
        AssertTupleContainerCompiles(source);
        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ConvertTupleToStructOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ConvertTupleToStructParams
        {
            SourceFile = workspace.SourcePath,
            Line = line,
            NewTypeName = "Point",
            AsRecord = asRecord
        });

        Assert.True(result.Success);
        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains(asRecord ? "public record struct Point" : "public struct Point", text, StringComparison.Ordinal);
        Assert.Contains("new Point", text, StringComparison.Ordinal);
    }

    // Codex P1s on #3441 (PRRT_kwDORD0nYs605__Q / …605__Y / …605__c): a selected
    // creation used directly (no alias) must go through the same escape /
    // propagation routine as aliases — setter / init / indexer value parameters,
    // unresolved dispatch (interface / virtual / abstract / extension receiver /
    // reassigned delegate / params expansion), user-defined operators and
    // conversions, collection inserts, generic wrappers, method groups, lambdas
    // and fields. The selected creation is on the USE line (14).
    [SkippableTheory]
    [InlineData(false, "sealed class Holder { public object Value { set { dynamic copy = value; Bump(ref copy.Item1); } } }", "var holder = new Holder(); holder.Value = (1, 2);")]
    [InlineData(true, "sealed class Holder { public object Value { set { dynamic copy = value; Bump(ref copy.Item1); } } }", "var holder = new Holder(); holder.Value = (1, 2);")]
    [InlineData(false, "sealed class Holder { public dynamic Value { set { dynamic copy = value; Bump(ref copy.Item1); } } }", "var holder = new Holder(); holder.Value = (1, 2);")]
    [InlineData(true, "sealed class Holder { public dynamic Value { set { dynamic copy = value; Bump(ref copy.Item1); } } }", "var holder = new Holder(); holder.Value = (1, 2);")]
    [InlineData(false, "sealed class Holder { public object Value { init { dynamic copy = value; Bump(ref copy.Item1); } } }", "var holder = new Holder { Value = (1, 2) };")]
    [InlineData(true, "sealed class Holder { public object Value { init { dynamic copy = value; Bump(ref copy.Item1); } } }", "var holder = new Holder { Value = (1, 2) };")]
    [InlineData(false, "sealed class Holder { public object this[int i] { set { dynamic copy = value; Bump(ref copy.Item1); } } }", "var holder = new Holder(); holder[0] = (1, 2);")]
    [InlineData(true, "sealed class Holder { public object this[int i] { set { dynamic copy = value; Bump(ref copy.Item1); } } }", "var holder = new Holder(); holder[0] = (1, 2);")]
    [InlineData(false, "sealed class Holder { public int this[object key] { get { dynamic copy = key; Bump(ref copy.Item1); return 0; } } }", "var holder = new Holder(); Console.WriteLine(holder[(1, 2)]);")]
    [InlineData(true, "sealed class Holder { public int this[object key] { get { dynamic copy = key; Bump(ref copy.Item1); return 0; } } }", "var holder = new Holder(); Console.WriteLine(holder[(1, 2)]);")]
    [InlineData(false, "class Holder { public virtual object Value { get; set; } }", "Holder holder = new Holder(); holder.Value = (1, 2);")]
    [InlineData(true, "class Holder { public virtual object Value { get; set; } }", "Holder holder = new Holder(); holder.Value = (1, 2);")]
    [InlineData(false, "abstract class Holder { public abstract object Value { set; } }", "Holder holder = null!; holder.Value = (1, 2);")]
    [InlineData(true, "abstract class Holder { public abstract object Value { set; } }", "Holder holder = null!; holder.Value = (1, 2);")]
    [InlineData(false, "interface IHolder { object Value { set; } }", "IHolder holder = null!; holder.Value = (1, 2);")]
    [InlineData(true, "interface IHolder { object Value { set; } }", "IHolder holder = null!; holder.Value = (1, 2);")]
    [InlineData(false, "interface IConsumer { void Use(object boxed); } sealed class Consumer : IConsumer { public void Use(object boxed) { dynamic copy = boxed; Bump(ref copy.Item1); } }", "IConsumer consumer = new Consumer(); consumer.Use((1, 2));")]
    [InlineData(true, "interface IConsumer { void Use(object boxed); } sealed class Consumer : IConsumer { public void Use(object boxed) { dynamic copy = boxed; Bump(ref copy.Item1); } }", "IConsumer consumer = new Consumer(); consumer.Use((1, 2));")]
    [InlineData(false, "class Consumer { public virtual void Use(object boxed) { dynamic copy = boxed; Bump(ref copy.Item1); } }", "var consumer = new Consumer(); consumer.Use((1, 2));")]
    [InlineData(true, "class Consumer { public virtual void Use(object boxed) { dynamic copy = boxed; Bump(ref copy.Item1); } }", "var consumer = new Consumer(); consumer.Use((1, 2));")]
    [InlineData(false, "abstract class Consumer { public abstract void Use(object boxed); } sealed class Impl : Consumer { public override void Use(object boxed) { dynamic copy = boxed; Bump(ref copy.Item1); } }", "Consumer consumer = new Impl(); consumer.Use((1, 2));")]
    [InlineData(true, "abstract class Consumer { public abstract void Use(object boxed); } sealed class Impl : Consumer { public override void Use(object boxed) { dynamic copy = boxed; Bump(ref copy.Item1); } }", "Consumer consumer = new Impl(); consumer.Use((1, 2));")]
    [InlineData(false, "static void Use(this object boxed) { dynamic copy = boxed; Bump(ref copy.Item1); }", "(1, 2).Use();")]
    [InlineData(true, "static void Use(this object boxed) { dynamic copy = boxed; Bump(ref copy.Item1); }", "(1, 2).Use();")]
    [InlineData(false, "static void Use(int n, params dynamic[] copies) { Bump(ref copies[1].Item1); }", "Use(0, \"skip\", (1, 2));")]
    [InlineData(true, "static void Use(int n, params dynamic[] copies) { Bump(ref copies[1].Item1); }", "Use(0, \"skip\", (1, 2));")]
    [InlineData(false, "static void Unused() { }", "Action<dynamic> use = _ => { }; use = copy => Bump(ref copy.Item1); use((1, 2));")]
    [InlineData(true, "static void Unused() { }", "Action<dynamic> use = _ => { }; use = copy => Bump(ref copy.Item1); use((1, 2));")]
    [InlineData(false, "static void Use(object boxed) { dynamic copy = boxed; Bump(ref copy.Item1); }", "Action<object> use = Use; use((1, 2));")]
    [InlineData(true, "static void Use(object boxed) { dynamic copy = boxed; Bump(ref copy.Item1); }", "Action<object> use = Use; use((1, 2));")]
    [InlineData(false, "static void Unused() { }", "Action<dynamic> use = copy => Bump(ref copy.Item1); use((1, 2));")]
    [InlineData(true, "static void Unused() { }", "Action<dynamic> use = copy => Bump(ref copy.Item1); use((1, 2));")]
    [InlineData(false, "static void Use(object boxed) { dynamic copy = boxed; Bump(ref copy.Item1); }", "Use((1, 2));")]
    [InlineData(true, "static void Use(object boxed) { dynamic copy = boxed; Bump(ref copy.Item1); }", "Use((1, 2));")]
    [InlineData(false, "sealed class Holder { public Holder(object boxed) { dynamic copy = boxed; Bump(ref copy.Item1); } }", "var holder = new Holder((1, 2));")]
    [InlineData(true, "sealed class Holder { public Holder(object boxed) { dynamic copy = boxed; Bump(ref copy.Item1); } }", "var holder = new Holder((1, 2));")]
    [InlineData(false, "interface IConsumer { void Use(object boxed); }", "Func<IConsumer> make = null!; make().Use((1, 2));")]
    [InlineData(true, "interface IConsumer { void Use(object boxed); }", "Func<IConsumer> make = null!; make().Use((1, 2));")]
    [InlineData(false, "sealed class Consumer { public static Consumer operator +(Consumer c, object boxed) { dynamic copy = boxed; Bump(ref copy.Item1); return c; } }", "var consumer = new Consumer(); var result = consumer + (1, 2);")]
    [InlineData(true, "sealed class Consumer { public static Consumer operator +(Consumer c, object boxed) { dynamic copy = boxed; Bump(ref copy.Item1); return c; } }", "var consumer = new Consumer(); var result = consumer + (1, 2);")]
    [InlineData(false, "sealed class Consumer { public static Consumer operator +(object boxed, Consumer c) { dynamic copy = boxed; Bump(ref copy.Item1); return c; } }", "var consumer = new Consumer(); var result = (1, 2) + consumer;")]
    [InlineData(true, "sealed class Consumer { public static Consumer operator +(object boxed, Consumer c) { dynamic copy = boxed; Bump(ref copy.Item1); return c; } }", "var consumer = new Consumer(); var result = (1, 2) + consumer;")]
    [InlineData(false, "sealed class Accumulator { public static Accumulator operator +(Accumulator c, object boxed) { dynamic copy = boxed; Bump(ref copy.Item1); return c; } }", "var consumer = new Accumulator(); consumer += (1, 2);")]
    [InlineData(true, "sealed class Accumulator { public static Accumulator operator +(Accumulator c, object boxed) { dynamic copy = boxed; Bump(ref copy.Item1); return c; } }", "var consumer = new Accumulator(); consumer += (1, 2);")]
    [InlineData(false, "sealed class Wrapper { public static implicit operator Wrapper((int, int) boxed) { dynamic copy = boxed; Bump(ref copy.Item1); return new Wrapper(); } }", "Wrapper wrapper = (1, 2);")]
    [InlineData(true, "sealed class Wrapper { public static implicit operator Wrapper((int, int) boxed) { dynamic copy = boxed; Bump(ref copy.Item1); return new Wrapper(); } }", "Wrapper wrapper = (1, 2);")]
    [InlineData(false, "sealed class Wrapper { public static explicit operator Wrapper((int, int) boxed) { dynamic copy = boxed; Bump(ref copy.Item1); return new Wrapper(); } }", "var wrapper = (Wrapper)(1, 2);")]
    [InlineData(true, "sealed class Wrapper { public static explicit operator Wrapper((int, int) boxed) { dynamic copy = boxed; Bump(ref copy.Item1); return new Wrapper(); } }", "var wrapper = (Wrapper)(1, 2);")]
    [InlineData(false, "static void Unused() { }", "var list = new List<object>(); list.Add((1, 2)); dynamic copy = list[0]; Bump(ref copy.Item1);")]
    [InlineData(true, "static void Unused() { }", "var list = new List<object>(); list.Add((1, 2)); dynamic copy = list[0]; Bump(ref copy.Item1);")]
    [InlineData(false, "sealed class Box<T> { public T Value; public Box(T value) { Value = value; } }", "var box = new Box<object>((1, 2)); dynamic copy = box.Value; Bump(ref copy.Item1);")]
    [InlineData(true, "sealed class Box<T> { public T Value; public Box(T value) { Value = value; } }", "var box = new Box<object>((1, 2)); dynamic copy = box.Value; Bump(ref copy.Item1);")]
    [InlineData(false, "static T Echo<T>(T value) => value;", "dynamic copy = Echo<object>((1, 2)); Bump(ref copy.Item1);")]
    [InlineData(true, "static T Echo<T>(T value) => value;", "dynamic copy = Echo<object>((1, 2)); Bump(ref copy.Item1);")]
    [InlineData(false, "static object field;", "field = (1, 2); dynamic copy = field; Bump(ref copy.Item1);")]
    [InlineData(true, "static object field;", "field = (1, 2); dynamic copy = field; Bump(ref copy.Item1);")]
    [InlineData(false, "static void Unused() { }", "dynamic[] items = { (1, 2) }; Bump(ref items[0].Item1);")]
    [InlineData(true, "static void Unused() { }", "dynamic[] items = { (1, 2) }; Bump(ref items[0].Item1);")]
    public async Task ConvertTupleToStruct_DirectCreationEscapeRefElement_ThrowsAndWritesNothing(
        bool asRecord,
        string declaration,
        string use)
    {
        var source = CalleeFlowTemplate.Replace("DECL", declaration).Replace("USE", use);
        AssertTupleContainerCompiles(source);
        await using var workspace = await TempWorkspace.CreateAsync(source);
        var original = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new ConvertTupleToStructOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ConvertTupleToStructParams
            {
                SourceFile = workspace.SourcePath,
                Line = 14,
                NewTypeName = "Point",
                AsRecord = asRecord
            }));

        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    // Companion to PRRT_kwDORD0nYs605__Q / …605__Y / …605__c: direct creations
    // flowing into concrete, read-only setters / callees / operators still convert.
    [SkippableTheory]
    [InlineData(false, "sealed class Holder { public object Value { set { Console.WriteLine(value); } } }", "var holder = new Holder(); holder.Value = (1, 2);")]
    [InlineData(true, "sealed class Holder { public object Value { set { Console.WriteLine(value); } } }", "var holder = new Holder(); holder.Value = (1, 2);")]
    [InlineData(false, "sealed class Holder { public object Value { get; set; } }", "var holder = new Holder(); holder.Value = (1, 2); Console.WriteLine(holder.Value);")]
    [InlineData(true, "sealed class Holder { public object Value { get; set; } }", "var holder = new Holder(); holder.Value = (1, 2); Console.WriteLine(holder.Value);")]
    [InlineData(false, "sealed class Consumer { public void Use(object boxed) => Console.WriteLine(boxed); }", "var consumer = new Consumer(); consumer.Use((1, 2));")]
    [InlineData(true, "sealed class Consumer { public void Use(object boxed) => Console.WriteLine(boxed); }", "var consumer = new Consumer(); consumer.Use((1, 2));")]
    [InlineData(false, "static void Use(object boxed) => Console.WriteLine(boxed);", "Use((1, 2));")]
    [InlineData(true, "static void Use(object boxed) => Console.WriteLine(boxed);", "Use((1, 2));")]
    [InlineData(false, "sealed class Consumer { public static Consumer operator +(Consumer c, object boxed) { Console.WriteLine(boxed); return c; } }", "var consumer = new Consumer(); var result = consumer + (1, 2);")]
    [InlineData(true, "sealed class Consumer { public static Consumer operator +(Consumer c, object boxed) { Console.WriteLine(boxed); return c; } }", "var consumer = new Consumer(); var result = consumer + (1, 2);")]
    [InlineData(false, "static void Unused() { }", "var list = new List<object>(); list.Add((1, 2)); Console.WriteLine(list[0]);")]
    [InlineData(true, "static void Unused() { }", "var list = new List<object>(); list.Add((1, 2)); Console.WriteLine(list[0]);")]
    public async Task ConvertTupleToStruct_DirectCreationNoRefUse_Succeeds(
        bool asRecord,
        string declaration,
        string use)
    {
        var source = CalleeFlowTemplate.Replace("DECL", declaration).Replace("USE", use);
        AssertTupleContainerCompiles(source);
        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ConvertTupleToStructOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ConvertTupleToStructParams
        {
            SourceFile = workspace.SourcePath,
            Line = 14,
            NewTypeName = "Point",
            AsRecord = asRecord
        });

        Assert.True(result.Success);
        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains(asRecord ? "public record struct Point" : "public struct Point", text, StringComparison.Ordinal);
        Assert.Contains("new Point", text, StringComparison.Ordinal);
    }
    // Codex P1 on #3441 (PRRT_kwDORD0nYs6071da): a creation assigned to a ref/out
    // parameter must flow back to every corresponding call-site argument.
    [SkippableTheory]
    [InlineData(false, 8, "static void Get(out dynamic p) { p = (1, 2); }", "Get(out dynamic p); Bump(ref p.Item1);")]
    [InlineData(true, 8, "static void Get(out dynamic p) { p = (1, 2); }", "Get(out dynamic p); Bump(ref p.Item1);")]
    [InlineData(false, 8, "static void Get(out dynamic p) { p = (1, 2); }", "dynamic p; Get(out p); Bump(ref p.Item1);")]
    [InlineData(true, 8, "static void Get(out dynamic p) { p = (1, 2); }", "dynamic p; Get(out p); Bump(ref p.Item1);")]
    [InlineData(false, 8, "static void Get(out dynamic p) { p = (1, 2); }", "Get(p: out dynamic p); Bump(ref p.Item1);")]
    [InlineData(true, 8, "static void Get(out dynamic p) { p = (1, 2); }", "Get(p: out dynamic p); Bump(ref p.Item1);")]
    [InlineData(false, 8, "static void Get(ref dynamic p) { p = (1, 2); }", "dynamic p = 0; Get(ref p); Bump(ref p.Item1);")]
    [InlineData(true, 8, "static void Get(ref dynamic p) { p = (1, 2); }", "dynamic p = 0; Get(ref p); Bump(ref p.Item1);")]
    [InlineData(false, 8, "static void Get(out dynamic p) { dynamic tmp = (1, 2); p = tmp; }", "Get(out dynamic p); Bump(ref p.Item1);")]
    [InlineData(true, 8, "static void Get(out dynamic p) { dynamic tmp = (1, 2); p = tmp; }", "Get(out dynamic p); Bump(ref p.Item1);")]
    [InlineData(false, 8, "sealed class Holder { public Holder(out dynamic p) { p = (1, 2); } }", "var holder = new Holder(out dynamic p); Bump(ref p.Item1);")]
    [InlineData(true, 8, "sealed class Holder { public Holder(out dynamic p) { p = (1, 2); } }", "var holder = new Holder(out dynamic p); Bump(ref p.Item1);")]
    [InlineData(false, 8, "delegate void Getter(out dynamic p); static void Get(out dynamic p) { p = (1, 2); }", "Getter g = Get; g(out dynamic p); Bump(ref p.Item1);")]
    [InlineData(true, 8, "delegate void Getter(out dynamic p); static void Get(out dynamic p) { p = (1, 2); }", "Getter g = Get; g(out dynamic p); Bump(ref p.Item1);")]
    [InlineData(false, 8, "public class Base { public virtual void Get(out dynamic p) { p = (1, 2); } }", "var b = new Base(); b.Get(out dynamic p); Bump(ref p.Item1);")]
    [InlineData(true, 8, "public class Base { public virtual void Get(out dynamic p) { p = (1, 2); } }", "var b = new Base(); b.Get(out dynamic p); Bump(ref p.Item1);")]
    public async Task ConvertTupleToStruct_OutParameterAliasRefElement_ThrowsAndWritesNothing(
        bool asRecord,
        int line,
        string declaration,
        string use)
    {
        var source = OpaqueReturnTemplate.Replace("DECL", declaration).Replace("USE", use);
        AssertTupleContainerCompiles(source);
        await using var workspace = await TempWorkspace.CreateAsync(source);
        var original = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new ConvertTupleToStructOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ConvertTupleToStructParams
            {
                SourceFile = workspace.SourcePath,
                Line = line,
                NewTypeName = "Point",
                AsRecord = asRecord
            }));

        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    // Codex P1 on #3441 (PRRT_kwDORD0nYs608Lny): a tracked ref/out parameter that
    // writes into an indexed / ref-returning destination (Get(out values[0]),
    // Get(out span[0]), Get(out Slot())) has no trackable local/parameter/field
    // symbol, so the conversion is conservatively refused.
    [SkippableTheory]
    [InlineData(false, "static void Get(out dynamic p) { p = (1, 2); }", "dynamic[] values = new dynamic[1]; Get(out values[0]); Bump(ref values[0].Item1);")]
    [InlineData(true, "static void Get(out dynamic p) { p = (1, 2); }", "dynamic[] values = new dynamic[1]; Get(out values[0]); Bump(ref values[0].Item1);")]
    [InlineData(false, "static void Get(ref dynamic p) { p = (1, 2); }", "dynamic[] values = new dynamic[1]; Get(ref values[0]); Bump(ref values[0].Item1);")]
    [InlineData(true, "static void Get(ref dynamic p) { p = (1, 2); }", "dynamic[] values = new dynamic[1]; Get(ref values[0]); Bump(ref values[0].Item1);")]
    [InlineData(false, "static void Get(out dynamic p) { p = (1, 2); }", "dynamic[][] values = { new dynamic[1] }; Get(out values[0][0]); Bump(ref values[0][0].Item1);")]
    [InlineData(true, "static void Get(out dynamic p) { p = (1, 2); }", "dynamic[][] values = { new dynamic[1] }; Get(out values[0][0]); Bump(ref values[0][0].Item1);")]
    [InlineData(false, "static void Get(out dynamic p) { p = (1, 2); }", "dynamic[] values = new dynamic[1]; Span<dynamic> span = values; Get(out span[0]); Bump(ref values[0].Item1);")]
    [InlineData(true, "static void Get(out dynamic p) { p = (1, 2); }", "dynamic[] values = new dynamic[1]; Span<dynamic> span = values; Get(out span[0]); Bump(ref values[0].Item1);")]
    [InlineData(false, "static void Get(out dynamic p) { p = (1, 2); } static dynamic slot; static ref dynamic Slot() => ref slot;", "Get(out Slot()); Bump(ref slot.Item1);")]
    [InlineData(true, "static void Get(out dynamic p) { p = (1, 2); } static dynamic slot; static ref dynamic Slot() => ref slot;", "Get(out Slot()); Bump(ref slot.Item1);")]
    [InlineData(false, "sealed class Holder { public Holder(out dynamic p) { p = (1, 2); } }", "dynamic[] values = new dynamic[1]; var holder = new Holder(out values[0]); Bump(ref values[0].Item1);")]
    [InlineData(true, "sealed class Holder { public Holder(out dynamic p) { p = (1, 2); } }", "dynamic[] values = new dynamic[1]; var holder = new Holder(out values[0]); Bump(ref values[0].Item1);")]
    public async Task ConvertTupleToStruct_IndexedOutDestinationRefElement_ThrowsAndWritesNothing(
        bool asRecord,
        string declaration,
        string use)
    {
        var source = OpaqueReturnTemplate.Replace("DECL", declaration).Replace("USE", use);
        AssertTupleContainerCompiles(source);
        await using var workspace = await TempWorkspace.CreateAsync(source);
        var original = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new ConvertTupleToStructOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ConvertTupleToStructParams
            {
                SourceFile = workspace.SourcePath,
                Line = 8,
                NewTypeName = "Point",
                AsRecord = asRecord
            }));

        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    // Companion to PRRT_kwDORD0nYs6071da: ref/out destinations that are only read convert.
    [SkippableTheory]
    [InlineData(false, "static void Get(out dynamic p) { p = (1, 2); }", "Get(out dynamic p); Console.WriteLine(p.Item1);")]
    [InlineData(true, "static void Get(out dynamic p) { p = (1, 2); }", "Get(out dynamic p); Console.WriteLine(p.Item1);")]
    [InlineData(false, "static void Get(ref dynamic p) { p = (1, 2); }", "dynamic p = 0; Get(ref p); Console.WriteLine(p.Item1);")]
    [InlineData(true, "static void Get(ref dynamic p) { p = (1, 2); }", "dynamic p = 0; Get(ref p); Console.WriteLine(p.Item1);")]
    public async Task ConvertTupleToStruct_OutParameterAliasNoRefUse_Succeeds(
        bool asRecord,
        string declaration,
        string use)
    {
        var source = OpaqueReturnTemplate.Replace("DECL", declaration).Replace("USE", use);
        AssertTupleContainerCompiles(source);
        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ConvertTupleToStructOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ConvertTupleToStructParams
        {
            SourceFile = workspace.SourcePath,
            Line = 8,
            NewTypeName = "Point",
            AsRecord = asRecord
        });

        Assert.True(result.Success);
        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains(asRecord ? "public record struct Point" : "public struct Point", text, StringComparison.Ordinal);
        Assert.Contains("new Point", text, StringComparison.Ordinal);
    }

    // Codex P1 on #3441 (PRRT_kwDORD0nYs6071de): ordinary methods / properties /
    // indexers / iterators that return an existing alias (not a fresh creation)
    // must be followed to their call sites.
    [SkippableTheory]
    [InlineData(false, 8, "static dynamic point = (1, 2); static dynamic Get() => point;", "dynamic copy = Get(); Bump(ref copy.Item1);")]
    [InlineData(true, 8, "static dynamic point = (1, 2); static dynamic Get() => point;", "dynamic copy = Get(); Bump(ref copy.Item1);")]
    [InlineData(false, 8, "static dynamic point = (1, 2); static dynamic Get() { return point; }", "dynamic copy = Get(); Bump(ref copy.Item1);")]
    [InlineData(true, 8, "static dynamic point = (1, 2); static dynamic Get() { return point; }", "dynamic copy = Get(); Bump(ref copy.Item1);")]
    [InlineData(false, 8, "static dynamic point = (1, 2); static dynamic Get() => point;", "Bump(ref Get().Item1);")]
    [InlineData(true, 8, "static dynamic point = (1, 2); static dynamic Get() => point;", "Bump(ref Get().Item1);")]
    [InlineData(false, 8, "static dynamic point = (1, 2); static dynamic Get() => point;", "Func<dynamic> get = Get; dynamic copy = get(); Bump(ref copy.Item1);")]
    [InlineData(true, 8, "static dynamic point = (1, 2); static dynamic Get() => point;", "Func<dynamic> get = Get; dynamic copy = get(); Bump(ref copy.Item1);")]
    [InlineData(false, 8, "static dynamic point = (1, 2); static dynamic Value => point;", "dynamic copy = Value; Bump(ref copy.Item1);")]
    [InlineData(true, 8, "static dynamic point = (1, 2); static dynamic Value => point;", "dynamic copy = Value; Bump(ref copy.Item1);")]
    [InlineData(false, 8, "static dynamic point = (1, 2); static dynamic Value { get => point; }", "dynamic copy = Value; Bump(ref copy.Item1);")]
    [InlineData(true, 8, "static dynamic point = (1, 2); static dynamic Value { get => point; }", "dynamic copy = Value; Bump(ref copy.Item1);")]
    [InlineData(false, 8, "static dynamic point = (1, 2); static dynamic Value { get { return point; } }", "dynamic copy = Value; Bump(ref copy.Item1);")]
    [InlineData(true, 8, "static dynamic point = (1, 2); static dynamic Value { get { return point; } }", "dynamic copy = Value; Bump(ref copy.Item1);")]
    [InlineData(false, 8, "sealed class Holder { public dynamic point = (1, 2); public dynamic this[int i] => point; }", "var holder = new Holder(); dynamic copy = holder[0]; Bump(ref copy.Item1);")]
    [InlineData(true, 8, "sealed class Holder { public dynamic point = (1, 2); public dynamic this[int i] => point; }", "var holder = new Holder(); dynamic copy = holder[0]; Bump(ref copy.Item1);")]
    [InlineData(false, 8, "static dynamic point = (1, 2); static ref dynamic Get() => ref point;", "ref dynamic copy = ref Get(); Bump(ref copy.Item1);")]
    [InlineData(true, 8, "static dynamic point = (1, 2); static ref dynamic Get() => ref point;", "ref dynamic copy = ref Get(); Bump(ref copy.Item1);")]
    [InlineData(false, 8, "public class Base { public dynamic point = (1, 2); public virtual dynamic Get() => point; }", "var b = new Base(); dynamic copy = b.Get(); Bump(ref copy.Item1);")]
    [InlineData(true, 8, "public class Base { public dynamic point = (1, 2); public virtual dynamic Get() => point; }", "var b = new Base(); dynamic copy = b.Get(); Bump(ref copy.Item1);")]
    public async Task ConvertTupleToStruct_ReturnedAliasRefElement_ThrowsAndWritesNothing(
        bool asRecord,
        int line,
        string declaration,
        string use)
    {
        var source = OpaqueReturnTemplate.Replace("DECL", declaration).Replace("USE", use);
        AssertTupleContainerCompiles(source);
        await using var workspace = await TempWorkspace.CreateAsync(source);
        var original = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new ConvertTupleToStructOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ConvertTupleToStructParams
            {
                SourceFile = workspace.SourcePath,
                Line = line,
                NewTypeName = "Point",
                AsRecord = asRecord
            }));

        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    // Iterator yield of an existing alias (needs System.Collections.Generic).
    [SkippableTheory]
    [InlineData(false, 11, "static dynamic point = (1, 2); static IEnumerable<dynamic> Get() { yield return point; }", "foreach (dynamic copy in Get()) Bump(ref copy.Item1);")]
    [InlineData(true, 11, "static dynamic point = (1, 2); static IEnumerable<dynamic> Get() { yield return point; }", "foreach (dynamic copy in Get()) Bump(ref copy.Item1);")]
    public async Task ConvertTupleToStruct_IteratorYieldAliasRefElement_ThrowsAndWritesNothing(
        bool asRecord,
        int line,
        string declaration,
        string use)
    {
        var source = CalleeFlowTemplate.Replace("DECL", declaration).Replace("USE", use);
        AssertTupleContainerCompiles(source);
        await using var workspace = await TempWorkspace.CreateAsync(source);
        var original = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new ConvertTupleToStructOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ConvertTupleToStructParams
            {
                SourceFile = workspace.SourcePath,
                Line = line,
                NewTypeName = "Point",
                AsRecord = asRecord
            }));

        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    // Companion to PRRT_kwDORD0nYs6071de: returning an existing alias is fine
    // when the call-site result is only read.
    [SkippableTheory]
    [InlineData(false, "static dynamic point = (1, 2); static dynamic Get() => point;", "dynamic copy = Get(); int x = copy.Item1; Console.WriteLine(x);")]
    [InlineData(true, "static dynamic point = (1, 2); static dynamic Get() => point;", "dynamic copy = Get(); int x = copy.Item1; Console.WriteLine(x);")]
    [InlineData(false, "static dynamic point = (1, 2); static dynamic Value => point;", "dynamic copy = Value; Console.WriteLine(copy.Item1);")]
    [InlineData(true, "static dynamic point = (1, 2); static dynamic Value => point;", "dynamic copy = Value; Console.WriteLine(copy.Item1);")]
    [InlineData(false, "static dynamic point = (1, 2); static dynamic Get() => point;", "Func<dynamic> get = Get; dynamic copy = get(); Console.WriteLine(copy.Item1);")]
    [InlineData(true, "static dynamic point = (1, 2); static dynamic Get() => point;", "Func<dynamic> get = Get; dynamic copy = get(); Console.WriteLine(copy.Item1);")]
    public async Task ConvertTupleToStruct_ReturnedAliasNoRefUse_Succeeds(
        bool asRecord,
        string declaration,
        string use)
    {
        var source = OpaqueReturnTemplate.Replace("DECL", declaration).Replace("USE", use);
        AssertTupleContainerCompiles(source);
        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ConvertTupleToStructOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ConvertTupleToStructParams
        {
            SourceFile = workspace.SourcePath,
            Line = 8,
            NewTypeName = "Point",
            AsRecord = asRecord
        });

        Assert.True(result.Success);
        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains(asRecord ? "public record struct Point" : "public struct Point", text, StringComparison.Ordinal);
        Assert.Contains("new Point", text, StringComparison.Ordinal);
    }

    private const string MetadataCalleeExternalSource = """
        namespace ExternalLib;

        public static class Ext
        {
            static void Bump(ref dynamic v) => v++;
            public static void UseDynamic(dynamic copy) => Bump(ref copy.Item1);
            public static void UseObject(object boxed) { dynamic copy = boxed; Bump(ref copy.Item1); }
            public static void UseGeneric<T>(T value) { dynamic copy = value!; Bump(ref copy.Item1); }
            public static void UseRef(ref object boxed) { dynamic copy = boxed; Bump(ref copy.Item1); }
            public static int Twice(int value) => value * 2;
        }

        public sealed class Holder
        {
            public Holder(dynamic copy) { Bump(ref copy.Item1); }
            static void Bump(ref dynamic v) => v++;
        }
        """;

    private const string MetadataCalleeTemplate = """
        using ExternalLib;

        namespace TestApp;

        public static class Worker
        {
            public static object Run()
            {
                var point = (X: 1, Y: 2);
                USE
                return point;
            }
        }
        """;

    // Codex P1 on #3441 (PRRT_kwDORD0nYs608hph): a non-virtual callee that exists only
    // in metadata has no body for alias propagation to inspect. An external
    // Use(dynamic copy) doing Bump(ref copy.Item1) works on a boxed ValueTuple but
    // fails after conversion, so passing converted storage to an object / dynamic /
    // generic / by-ref metadata parameter is refused.
    [SkippableTheory]
    [InlineData(false, "Ext.UseDynamic(point);")]
    [InlineData(true, "Ext.UseDynamic(point);")]
    [InlineData(false, "Ext.UseObject(point);")]
    [InlineData(true, "Ext.UseObject(point);")]
    [InlineData(false, "Ext.UseGeneric(point);")]
    [InlineData(true, "Ext.UseGeneric(point);")]
    [InlineData(false, "object box = point; Ext.UseRef(ref box);")]
    [InlineData(true, "object box = point; Ext.UseRef(ref box);")]
    [InlineData(false, "var holder = new Holder(point);")]
    [InlineData(true, "var holder = new Holder(point);")]
    public async Task ConvertTupleToStruct_MetadataOnlyCalleeOpaqueParameter_ThrowsAndWritesNothing(
        bool asRecord,
        string use)
    {
        var source = MetadataCalleeTemplate.Replace("USE", use);
        await using var workspace = await TempWorkspace.CreateWithMetadataReferenceAsync(
            source, MetadataCalleeExternalSource);
        var original = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new ConvertTupleToStructOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ConvertTupleToStructParams
            {
                SourceFile = workspace.SourcePath,
                Line = 9,
                NewTypeName = "Point",
                AsRecord = asRecord
            }));

        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    // Companion to PRRT_kwDORD0nYs608hph: metadata-only callees that only receive a
    // scalar element, allowlisted read-only observers such as Console.WriteLine(object),
    // and modeled carrier stores such as List<object>.Add on a tracked local still convert.
    [SkippableTheory]
    [InlineData(false, "var doubled = Ext.Twice(point.X);")]
    [InlineData(true, "var doubled = Ext.Twice(point.X);")]
    [InlineData(false, "System.Console.WriteLine(point.X);")]
    [InlineData(true, "System.Console.WriteLine(point.X);")]
    [InlineData(false, "System.Console.WriteLine(point);")]
    [InlineData(true, "System.Console.WriteLine(point);")]
    [InlineData(false, "var list = new List<object>(); list.Add(point);")]
    [InlineData(true, "var list = new List<object>(); list.Add(point);")]
    public async Task ConvertTupleToStruct_MetadataOnlyCalleeScalarElement_Succeeds(
        bool asRecord,
        string use)
    {
        var source = MetadataCalleeTemplate.Replace("USE", use);
        await using var workspace = await TempWorkspace.CreateWithMetadataReferenceAsync(
            source, MetadataCalleeExternalSource);
        var operation = new ConvertTupleToStructOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ConvertTupleToStructParams
        {
            SourceFile = workspace.SourcePath,
            Line = 9,
            NewTypeName = "Point",
            AsRecord = asRecord
        });

        Assert.True(result.Success);
        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains(asRecord ? "public record struct Point" : "public struct Point", text, StringComparison.Ordinal);
    }

    private const string SequenceExtensionTemplate = """
        using System;
        using System.Collections.Generic;
        using System.Linq;

        namespace TestApp;

        public static class SequenceExtensions
        {
            public static void Bump(ref dynamic v) => v++;

            public static void Use(this IEnumerable<dynamic> values)
            {
                dynamic copy = values.First();
                Bump(ref copy.Item1);
            }

            public static void UseObjects(this IEnumerable<object> values)
            {
                foreach (var boxed in values)
                {
                    dynamic copy = boxed;
                    Bump(ref copy.Item1);
                }
            }

            public static void UseGeneric<T>(this IEnumerable<T> values)
            {
                dynamic copy = values.First()!;
                Bump(ref copy.Item1);
            }

            public static void UseWith(this List<int> values, dynamic copy)
            {
                Bump(ref copy.Item1);
            }

            public static void Read(this IEnumerable<dynamic> values)
            {
                dynamic copy = values.First();
                Console.WriteLine(copy.Item1);
            }
        }

        public static class Worker
        {
            static void Bump(ref dynamic v) => v++;

            public static object Run()
            {
                var point = (X: 1, Y: 2);
                USE
                return point;
            }
        }
        """;

    // Codex P1 on #3441 (PRRT_kwDORD0nYs608u-W): a reduced extension's hidden `this`
    // parameter must be aliased like argument 0. points.Use() on a source extension
    // Use(this IEnumerable<dynamic> values) that does Bump(ref values.First().Item1)
    // works on a boxed ValueTuple but fails after conversion — the sequence-receiver
    // exemption used to skip both the refusal and the body analysis.
    [SkippableTheory]
    [InlineData(false, "dynamic[] points = { point }; points.Use();")]
    [InlineData(true, "dynamic[] points = { point }; points.Use();")]
    [InlineData(false, "var points = new List<object> { point }; points.UseObjects();")]
    [InlineData(true, "var points = new List<object> { point }; points.UseObjects();")]
    [InlineData(false, "var points = new[] { point }; points.UseGeneric();")]
    [InlineData(true, "var points = new[] { point }; points.UseGeneric();")]
    [InlineData(false, "object[] points = { point }; SequenceExtensions.UseObjects(points);")]
    [InlineData(true, "object[] points = { point }; SequenceExtensions.UseObjects(points);")]
    [InlineData(false, "var points = new List<object> { point }; SequenceExtensions.UseObjects(points);")]
    [InlineData(true, "var points = new List<object> { point }; SequenceExtensions.UseObjects(points);")]
    [InlineData(false, "var points = new List<object> { point }; dynamic copy = points[0]; Bump(ref copy.Item1);")]
    [InlineData(true, "var points = new List<object> { point }; dynamic copy = points[0]; Bump(ref copy.Item1);")]
    [InlineData(false, "var points = new List<object>(); points.Add(point); points.UseObjects();")]
    [InlineData(true, "var points = new List<object>(); points.Add(point); points.UseObjects();")]
    [InlineData(false, "var other = new List<int>(); other.UseWith(point);")]
    [InlineData(true, "var other = new List<int>(); other.UseWith(point);")]
    public async Task ConvertTupleToStruct_SequenceExtensionReceiverRefElement_ThrowsAndWritesNothing(
        bool asRecord,
        string use)
    {
        var source = SequenceExtensionTemplate.Replace("USE", use);
        await using var workspace = await TempWorkspace.CreateAsync(source);
        var original = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new ConvertTupleToStructOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ConvertTupleToStructParams
            {
                SourceFile = workspace.SourcePath,
                Line = 50,
                NewTypeName = "Point",
                AsRecord = asRecord
            }));

        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    // Over-refusal guard for PRRT_kwDORD0nYs608u-W: source sequence extensions that
    // only read by value and BCL sequence extensions (LINQ) still convert.
    [SkippableTheory]
    [InlineData(false, "dynamic[] points = { point }; points.Read();")]
    [InlineData(true, "dynamic[] points = { point }; points.Read();")]
    [InlineData(false, "var points = new[] { point }; var count = points.Count();")]
    [InlineData(true, "var points = new[] { point }; var count = points.Count();")]
    public async Task ConvertTupleToStruct_SequenceExtensionReceiverByValue_Succeeds(
        bool asRecord,
        string use)
    {
        var source = SequenceExtensionTemplate.Replace("USE", use);
        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ConvertTupleToStructOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ConvertTupleToStructParams
        {
            SourceFile = workspace.SourcePath,
            Line = 50,
            NewTypeName = "Point",
            AsRecord = asRecord
        });

        Assert.True(result.Success);
        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains(asRecord ? "public record struct Point" : "public struct Point", text, StringComparison.Ordinal);
    }

    private const string MetadataSequenceExtensionExternalSource = """
        using System.Collections.Generic;

        namespace ExternalLib;

        public static class SeqExt
        {
            static void Bump(ref dynamic v) => v++;
            public static void UseAll(this IEnumerable<object> values)
            {
                foreach (var boxed in values) { dynamic copy = boxed; Bump(ref copy.Item1); }
            }
        }
        """;

    // PRRT_kwDORD0nYs608u-W sibling: a metadata-only, non-framework sequence extension
    // has no body to analyze, so a storage-carrying receiver falls under the
    // metadata-only refusal instead of the sequence exemption.
    [SkippableTheory]
    [InlineData(false, "var points = new List<object> { point }; points.UseAll();")]
    [InlineData(true, "var points = new List<object> { point }; points.UseAll();")]
    public async Task ConvertTupleToStruct_MetadataOnlySequenceExtensionReceiver_ThrowsAndWritesNothing(
        bool asRecord,
        string use)
    {
        var source = MetadataCalleeTemplate.Replace("using ExternalLib;", "using System.Collections.Generic;\nusing ExternalLib;").Replace("USE", use);
        await using var workspace = await TempWorkspace.CreateWithMetadataReferenceAsync(
            source, MetadataSequenceExtensionExternalSource);
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
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    private const string FrameworkMetadataTemplate = """
        using System;
        using System.Collections;
        using System.Collections.Concurrent;
        using System.Collections.Generic;
        using System.Diagnostics;
        using System.Text;
        using System.Threading;
        using System.Threading.Tasks;

        namespace TestApp;

        public static class Worker
        {
            static void Bump(ref dynamic v) => v++;

            public static object Run()
            {
                var point = (X: 1, Y: 2);
                USE
                return point;
            }
        }
        """;

    private static async Task AssertFrameworkMetadataUseRefusedAsync(bool asRecord, string use)
    {
        var source = FrameworkMetadataTemplate.Replace("USE", use);
        await using var workspace = await TempWorkspace.CreateAsync(source);
        var original = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new ConvertTupleToStructOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ConvertTupleToStructParams
            {
                SourceFile = workspace.SourcePath,
                Line = 18,
                NewTypeName = "Point",
                AsRecord = asRecord
            }));

        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    // Codex P1 on #3441 (PRRT_kwDORD0nYs6087Qc): Array.SetValue(point, 0) is not a
    // modeled insertion and used to be BCL-exempt, so values never became a carrier;
    // dynamic copy = values.GetValue(0)!; Bump(ref copy.X) then fails after conversion.
    // Framework callees no longer get a blanket exemption.
    [SkippableTheory]
    [InlineData(false, "Array values = new object[1]; values.SetValue(point, 0); dynamic copy = values.GetValue(0)!; Bump(ref copy.X);")]
    [InlineData(true, "Array values = new object[1]; values.SetValue(point, 0); dynamic copy = values.GetValue(0)!; Bump(ref copy.X);")]
    [InlineData(false, "var values = new object[1]; values.SetValue(point, 0); dynamic copy = values.GetValue(0)!; Bump(ref copy.Item1);")]
    [InlineData(true, "var values = new object[1]; values.SetValue(point, 0); dynamic copy = values.GetValue(0)!; Bump(ref copy.Item1);")]
    public async Task ConvertTupleToStruct_ArraySetValueOpaqueStore_ThrowsAndWritesNothing(bool asRecord, string use) =>
        await AssertFrameworkMetadataUseRefusedAsync(asRecord, use);

    // Codex P1 on #3441 (PRRT_kwDORD0nYs6087Qf): framework APIs that forward an opaque
    // object state to a callback (ThreadPool.QueueUserWorkItem, Timer, Task.Factory.StartNew)
    // hand the boxed tuple to code that can Bump(ref copy.Item1).
    [SkippableTheory]
    [InlineData(false, "ThreadPool.QueueUserWorkItem(state => { dynamic copy = state!; Bump(ref copy.Item1); }, point);")]
    [InlineData(true, "ThreadPool.QueueUserWorkItem(state => { dynamic copy = state!; Bump(ref copy.Item1); }, point);")]
    [InlineData(false, "using var timer = new Timer(state => { dynamic copy = state!; Bump(ref copy.Item1); }, point, 0, -1);")]
    [InlineData(true, "using var timer = new Timer(state => { dynamic copy = state!; Bump(ref copy.Item1); }, point, 0, -1);")]
    [InlineData(false, "Task.Factory.StartNew(state => { dynamic copy = state!; Bump(ref copy.Item1); }, point).Wait();")]
    [InlineData(true, "Task.Factory.StartNew(state => { dynamic copy = state!; Bump(ref copy.Item1); }, point).Wait();")]
    public async Task ConvertTupleToStruct_QueueUserWorkItemStateCallback_ThrowsAndWritesNothing(bool asRecord, string use) =>
        await AssertFrameworkMetadataUseRefusedAsync(asRecord, use);

    // Sound metadata rule (PRRT_kwDORD0nYs6087Qc / …6087Qf): any framework API that
    // stores or forwards the boxed tuple through an opaque/generic/by-ref parameter
    // refuses unless it is a modeled carrier store on a tracked receiver.
    [SkippableTheory]
    [InlineData(false, "var bag = new ConcurrentBag<object>(); bag.Add(point);")]
    [InlineData(true, "var bag = new ConcurrentBag<object>(); bag.Add(point);")]
    [InlineData(false, "var bag = new ConcurrentBag<object> { point };")]
    [InlineData(true, "var bag = new ConcurrentBag<object> { point };")]
    [InlineData(false, "var set = new HashSet<object>(); set.Add(point);")]
    [InlineData(true, "var set = new HashSet<object>(); set.Add(point);")]
    [InlineData(false, "var task = Task.FromResult<object>(point);")]
    [InlineData(true, "var task = Task.FromResult<object>(point);")]
    [InlineData(false, "object slot = null!; Interlocked.Exchange(ref slot, point);")]
    [InlineData(true, "object slot = null!; Interlocked.Exchange(ref slot, point);")]
    [InlineData(false, "var boxed = Tuple.Create<object>(point);")]
    [InlineData(true, "var boxed = Tuple.Create<object>(point);")]
    [InlineData(false, "var list = new List<object>(); list.Add(point); ThreadPool.QueueUserWorkItem(s => { }, list);")]
    [InlineData(true, "var list = new List<object>(); list.Add(point); ThreadPool.QueueUserWorkItem(s => { }, list);")]
    [InlineData(false, "var map = new ConcurrentDictionary<int, object>(); map[0] = point;")]
    [InlineData(true, "var map = new ConcurrentDictionary<int, object>(); map[0] = point;")]
    [InlineData(false, "var map = new Dictionary<int, object>(); map.Add(0, point); dynamic copy = map[0]; Bump(ref copy.Item1);")]
    [InlineData(true, "var map = new Dictionary<int, object>(); map.Add(0, point); dynamic copy = map[0]; Bump(ref copy.Item1);")]
    [InlineData(false, "var linked = new LinkedList<object>(); linked.AddFirst(point);")]
    [InlineData(true, "var linked = new LinkedList<object>(); linked.AddFirst(point);")]
    public async Task ConvertTupleToStruct_FrameworkOpaqueStoreOrForward_ThrowsAndWritesNothing(bool asRecord, string use) =>
        await AssertFrameworkMetadataUseRefusedAsync(asRecord, use);

    // Allowlist guard for the sound metadata rule: read-only, non-storing,
    // non-forwarding framework observers and modeled carrier stores still convert.
    [SkippableTheory]
    [InlineData(false, "Console.WriteLine(point);")]
    [InlineData(true, "Console.Write(point);")]
    [InlineData(false, "Console.WriteLine(\"{0} {1}\", point, point);")]
    [InlineData(true, "var text = string.Format(\"{0}\", point);")]
    [InlineData(false, "var text = string.Concat(point, \"!\");")]
    [InlineData(true, "var text = string.Join(\", \", point, point);")]
    [InlineData(false, "var text = $\"{point}\";")]
    [InlineData(true, "Debug.WriteLine(point);")]
    [InlineData(false, "var builder = new StringBuilder().Append(point);")]
    [InlineData(true, "var same = object.Equals(point, point);")]
    [InlineData(false, "var same = point.Equals((object)point);")]
    [InlineData(true, "var hash = point.GetHashCode(); var text = point.ToString();")]
    [InlineData(false, "var list = new List<object>(); list.Add(point);")]
    [InlineData(true, "var queue = new Queue<object>(); queue.Enqueue(point);")]
    [InlineData(false, "var map = new Dictionary<object, object>(); map.Add(0, point);")]
    [InlineData(true, "var map = new Dictionary<object, object>(); map.Add(0, point);")]
    [InlineData(true, "var list = new List<object>(); list.Add(point); dynamic copy = list[0]; Console.WriteLine(copy.Item1);")]
    public async Task ConvertTupleToStruct_FrameworkReadOnlyAllowlist_Succeeds(bool asRecord, string use)
    {
        var source = FrameworkMetadataTemplate.Replace("USE", use);
        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ConvertTupleToStructOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ConvertTupleToStructParams
        {
            SourceFile = workspace.SourcePath,
            Line = 18,
            NewTypeName = "Point",
            AsRecord = asRecord
        });

        Assert.True(result.Success);
        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains(asRecord ? "public record struct Point" : "public struct Point", text, StringComparison.Ordinal);
    }
}

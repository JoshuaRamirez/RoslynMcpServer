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
/// Operation-level tests for <see cref="RemoveBracesOperation"/> (UC-A6).
/// </summary>
public class RemoveBracesOperationTests
{
    #region Input Validation

    [Fact]
    public void Validate_MissingSourceFile_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            RemoveBracesOperation.Validate(new RemoveBracesParams
            {
                SourceFile = ""
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public void Validate_AllFilesFalse_WithoutSourceFile_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            RemoveBracesOperation.Validate(new RemoveBracesParams
            {
                AllFiles = false
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public void Validate_AllFilesTrue_WithoutSourceFile_DoesNotThrow()
    {
        RemoveBracesOperation.Validate(new RemoveBracesParams
        {
            AllFiles = true
        });
    }

    [Fact]
    public void Validate_AllFilesTrue_WithStatementScope_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            RemoveBracesOperation.Validate(new RemoveBracesParams
            {
                AllFiles = true,
                Scope = "statement"
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("allFiles", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("statement", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_AllFilesTrue_WithTypeScope_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            RemoveBracesOperation.Validate(new RemoveBracesParams
            {
                AllFiles = true,
                Scope = "type",
                TypeName = "Gate"
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("allFiles", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("type", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_RelativePath_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            RemoveBracesOperation.Validate(new RemoveBracesParams
            {
                SourceFile = "Types.cs",
                Line = 1
            }));

        Assert.Equal(ErrorCodes.InvalidSourcePath, ex.ErrorCode);
    }

    [Fact]
    public void Validate_StatementScope_MissingLine_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            RemoveBracesOperation.Validate(new RemoveBracesParams
            {
                SourceFile = AbsoluteTestPath(),
                Scope = "statement"
            }));

        Assert.Equal(ErrorCodes.InvalidLineNumber, ex.ErrorCode);
    }

    [Fact]
    public void Validate_InvalidLine_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            RemoveBracesOperation.Validate(new RemoveBracesParams
            {
                SourceFile = AbsoluteTestPath(),
                Line = 0
            }));

        Assert.Equal(ErrorCodes.InvalidLineNumber, ex.ErrorCode);
    }

    [Fact]
    public void Validate_InvalidColumn_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            RemoveBracesOperation.Validate(new RemoveBracesParams
            {
                SourceFile = AbsoluteTestPath(),
                Line = 1,
                Column = 0
            }));

        Assert.Equal(ErrorCodes.InvalidColumnNumber, ex.ErrorCode);
        Assert.Equal("1007", ex.ErrorCode);
    }

    [Fact]
    public void Validate_StatementScope_ColumnWithoutLine_ThrowsInvalidLineNumber()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            RemoveBracesOperation.Validate(new RemoveBracesParams
            {
                SourceFile = AbsoluteTestPath(),
                Scope = "statement",
                Column = 4
            }));

        Assert.Equal(ErrorCodes.InvalidLineNumber, ex.ErrorCode);
        Assert.Contains("line", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_TypeScope_MissingTypeName_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            RemoveBracesOperation.Validate(new RemoveBracesParams
            {
                SourceFile = AbsoluteTestPath(),
                Scope = "type"
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public void Validate_InvalidScope_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            RemoveBracesOperation.Validate(new RemoveBracesParams
            {
                SourceFile = AbsoluteTestPath(),
                Scope = "method"
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public void Validate_MissingFile_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            RemoveBracesOperation.Validate(new RemoveBracesParams
            {
                SourceFile = AbsoluteTestPath(),
                Line = 1
            }));

        Assert.Equal(ErrorCodes.SourceFileNotFound, ex.ErrorCode);
    }

    [Fact]
    public void FindTypeDeclaration_SimpleName_Ambiguous_Throws()
    {
        var root = CSharpSyntaxTree.ParseText("""
            namespace A { class C {} }
            namespace B { class C {} }
            """).GetRoot();

        var ex = Assert.Throws<RefactoringException>(() =>
            RemoveBracesOperation.FindTypeDeclaration(root, "C"));

        Assert.Equal(ErrorCodes.SymbolAmbiguous, ex.ErrorCode);
    }

    [Fact]
    public void FindTypeDeclaration_NamespaceQualified_SelectsA()
    {
        var root = CSharpSyntaxTree.ParseText("""
            namespace A { class C {} }
            namespace B { class C {} }
            """).GetRoot();

        var type = RemoveBracesOperation.FindTypeDeclaration(root, "A.C");

        Assert.Equal("C", type.Identifier.Text);
        Assert.Equal("A.C", RemoveBracesOperation.GetQualifiedTypeName(type));
    }

    [Fact]
    public void WouldHideExternallyReferencedLabel_GotoRetry_IsTrue()
    {
        var method = CSharpSyntaxTree.ParseText("""
            class Loop
            {
                void Run(bool condition)
                {
                    goto retry;
                    if (condition)
                    {
                        retry: Work();
                    }
                }
                static void Work() {}
            }
            """).GetRoot()
            .DescendantNodes()
            .OfType<IfStatementSyntax>()
            .Single();

        var inner = Assert.IsType<BlockSyntax>(method.Statement).Statements[0];
        Assert.True(RemoveBracesOperation.WouldHideExternallyReferencedLabel(inner));
    }

    [Fact]
    public void WouldCreateDanglingElse_NestedIfThenOuterElse_IsTrue()
    {
        var outer = CSharpSyntaxTree.ParseText("""
            class Gate
            {
                void Run(bool outer, bool inner)
                {
                    if (outer)
                    {
                        if (inner) Work();
                    }
                    else Other();
                }
                static void Work() {}
                static void Other() {}
            }
            """).GetRoot()
            .DescendantNodes()
            .OfType<IfStatementSyntax>()
            .First(statement => statement.Condition.ToString() == "outer");

        var inner = Assert.IsType<BlockSyntax>(outer.Statement).Statements[0];
        Assert.True(RemoveBracesOperation.WouldCreateDanglingElse(outer, inner));
    }

    [Fact]
    public void WouldCreateDanglingElse_ElseClauseOwner_IsFalse()
    {
        var elseClause = CSharpSyntaxTree.ParseText("""
            class Gate
            {
                void Run(bool a, bool b)
                {
                    if (a)
                        Work();
                    else
                    {
                        if (b) Other();
                    }
                }
                static void Work() {}
                static void Other() {}
            }
            """).GetRoot()
            .DescendantNodes()
            .OfType<ElseClauseSyntax>()
            .Single();

        var inner = Assert.IsType<BlockSyntax>(elseClause.Statement).Statements[0];
        Assert.False(RemoveBracesOperation.WouldCreateDanglingElse(elseClause, inner));
    }

    [Fact]
    public void CollectTargets_ElseOwner_IsElseClause()
    {
        var root = CSharpSyntaxTree.ParseText("""
            class Gate
            {
                void Run(bool flag)
                {
                    if (flag)
                        Work();
                    else
                    {
                        Other();
                    }
                }
                static void Work() {}
                static void Other() {}
            }
            """).GetRoot();

        var elseTarget = RemoveBracesOperation.CollectTargets(root)
            .Single(target => target.Keyword.IsKind(SyntaxKind.ElseKeyword));

        Assert.IsType<ElseClauseSyntax>(elseTarget.Owner);
    }

    [Fact]
    public void CannotBeEmbeddedStatement_DeclarationLocalFunctionAndLabel_AreTrue()
    {
        var root = CSharpSyntaxTree.ParseText("""
            class Gate
            {
                void Run()
                {
                    { var value = 1; }
                    { void Local() {} }
                    { retry: Work(); }
                    { Work(); }
                }
                static void Work() {}
            }
            """).GetRoot();

        var blocks = root.DescendantNodes().OfType<BlockSyntax>()
            .Where(block => block.Parent is BlockSyntax)
            .ToList();

        Assert.True(RemoveBracesOperation.CannotBeEmbeddedStatement(blocks[0].Statements[0]));
        Assert.True(RemoveBracesOperation.CannotBeEmbeddedStatement(blocks[1].Statements[0]));
        Assert.True(RemoveBracesOperation.CannotBeEmbeddedStatement(blocks[2].Statements[0]));
        Assert.False(RemoveBracesOperation.CannotBeEmbeddedStatement(blocks[3].Statements[0]));
    }

    [Fact]
    public void UnwrapBlock_PreservesCommentOnOpenBrace()
    {
        var block = (BlockSyntax)SyntaxFactory.ParseStatement("""
            { // keep-reason
                return 1;
            }
            """);

        var unwrapped = RemoveBracesOperation.UnwrapBlock(block);

        Assert.Contains("keep-reason", unwrapped.GetLeadingTrivia().ToFullString(), StringComparison.Ordinal);
        Assert.Contains("return 1;", unwrapped.ToString(), StringComparison.Ordinal);
    }

    #endregion

    #region P0 Happy Path

    [SkippableFact]
    public async Task RemoveBraces_If_UnwrapsThenBody()
    {
        const string source = """
            namespace TestApp;

            public class Gate
            {
                public string Classify(int x)
                {
                    if (x > 0)
                    {
                        return "positive";
                    }
                    return "other";
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new RemoveBracesOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new RemoveBracesParams
        {
            SourceFile = workspace.SourcePath,
            Line = FindLine(source, "if (x > 0)")
        });

        Assert.True(result.Success);
        Assert.False(result.Preview);
        Assert.Equal(1, result.StatementsModified);
        Assert.Equal("statement", result.Scope);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        AssertIfBodyIsNotBlock(updated, "x > 0");
        Assert.Contains("return \"positive\";", updated);
        await AssertCompilesAsync(workspace);
    }

    [SkippableFact]
    public async Task RemoveBraces_Else_UnwrapsElseBody()
    {
        const string source = """
            namespace TestApp;

            public class Gate
            {
                public string Classify(bool flag)
                {
                    if (flag)
                    {
                        return "yes";
                    }
                    else
                    {
                        return "no";
                    }
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new RemoveBracesOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new RemoveBracesParams
        {
            SourceFile = workspace.SourcePath,
            Line = FindLine(source, "else")
        });

        Assert.True(result.Success);
        Assert.Equal(1, result.StatementsModified);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var tree = CSharpSyntaxTree.ParseText(updated);
        var ifStatement = tree.GetRoot().DescendantNodes().OfType<IfStatementSyntax>().Single();
        Assert.IsType<BlockSyntax>(ifStatement.Statement);
        Assert.IsNotType<BlockSyntax>(ifStatement.Else?.Statement);
        Assert.Contains("return \"no\";", ifStatement.Else!.Statement.ToString());
        await AssertCompilesAsync(workspace);
    }

    [SkippableFact]
    public async Task RemoveBraces_For_UnwrapsBody()
    {
        const string source = """
            namespace TestApp;

            public class Loop
            {
                public int Sum(int n)
                {
                    var total = 0;
                    for (var i = 0; i < n; i++)
                    {
                        total += i;
                    }
                    return total;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new RemoveBracesOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new RemoveBracesParams
        {
            SourceFile = workspace.SourcePath,
            Line = FindLine(source, "for (var i = 0; i < n; i++)")
        });

        Assert.True(result.Success);
        Assert.Equal(1, result.StatementsModified);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var forStatement = CSharpSyntaxTree.ParseText(updated).GetRoot()
            .DescendantNodes().OfType<ForStatementSyntax>().Single();
        Assert.IsNotType<BlockSyntax>(forStatement.Statement);
        Assert.Contains("total += i;", forStatement.Statement.ToString());
        await AssertCompilesAsync(workspace);
    }

    [SkippableFact]
    public async Task RemoveBraces_Foreach_UnwrapsBody()
    {
        const string source = """
            namespace TestApp;

            public class Loop
            {
                public int Count(int[] items)
                {
                    var n = 0;
                    foreach (var item in items)
                    {
                        n++;
                    }
                    return n;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new RemoveBracesOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new RemoveBracesParams
        {
            SourceFile = workspace.SourcePath,
            Line = FindLine(source, "foreach (var item in items)")
        });

        Assert.True(result.Success);
        Assert.Equal(1, result.StatementsModified);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var forEach = CSharpSyntaxTree.ParseText(updated).GetRoot()
            .DescendantNodes().OfType<ForEachStatementSyntax>().Single();
        Assert.IsNotType<BlockSyntax>(forEach.Statement);
        Assert.Contains("n++;", forEach.Statement.ToString());
        await AssertCompilesAsync(workspace);
    }

    [SkippableFact]
    public async Task RemoveBraces_While_UnwrapsBody()
    {
        const string source = """
            namespace TestApp;

            public class Loop
            {
                public int Drain(int n)
                {
                    while (n > 0)
                    {
                        n--;
                    }
                    return n;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new RemoveBracesOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new RemoveBracesParams
        {
            SourceFile = workspace.SourcePath,
            Line = FindLine(source, "while (n > 0)")
        });

        Assert.True(result.Success);
        Assert.Equal(1, result.StatementsModified);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var whileStatement = CSharpSyntaxTree.ParseText(updated).GetRoot()
            .DescendantNodes().OfType<WhileStatementSyntax>().Single();
        Assert.IsNotType<BlockSyntax>(whileStatement.Statement);
        Assert.Contains("n--;", whileStatement.Statement.ToString());
        await AssertCompilesAsync(workspace);
    }

    [SkippableFact]
    public async Task RemoveBraces_Using_UnwrapsBody()
    {
        const string source = """
            namespace TestApp;

            public class Holder
            {
                public void Use()
                {
                    using (var stream = new System.IO.MemoryStream())
                    {
                        stream.WriteByte(1);
                    }
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new RemoveBracesOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new RemoveBracesParams
        {
            SourceFile = workspace.SourcePath,
            Line = FindLine(source, "using (var stream")
        });

        Assert.True(result.Success);
        Assert.Equal(1, result.StatementsModified);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var usingStatement = CSharpSyntaxTree.ParseText(updated).GetRoot()
            .DescendantNodes().OfType<UsingStatementSyntax>().Single();
        Assert.IsNotType<BlockSyntax>(usingStatement.Statement);
        Assert.Contains("WriteByte(1);", usingStatement.Statement.ToString());
        await AssertCompilesAsync(workspace);
    }

    #endregion

    #region P0 Rejects

    [SkippableFact]
    public async Task RemoveBraces_MultipleStatements_Throws()
    {
        const string source = """
            namespace TestApp;

            public class Gate
            {
                public void Run(bool flag)
                {
                    if (flag)
                    {
                        Work();
                        Other();
                    }
                }

                private static void Work() { }
                private static void Other() { }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new RemoveBracesOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new RemoveBracesParams
            {
                SourceFile = workspace.SourcePath,
                Line = FindLine(source, "if (flag)")
            }));

        Assert.Equal(ErrorCodes.MultipleStatementsInBlock, ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task RemoveBraces_NoBracesPresent_Throws()
    {
        const string source = """
            namespace TestApp;

            public class Gate
            {
                public void Run(bool flag)
                {
                    if (flag)
                        Work();
                }

                private static void Work() { }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new RemoveBracesOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new RemoveBracesParams
            {
                SourceFile = workspace.SourcePath,
                Line = FindLine(source, "if (flag)")
            }));

        Assert.Equal(ErrorCodes.NoBracesToRemove, ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task RemoveBraces_NoControlStatement_Throws()
    {
        const string source = """
            namespace TestApp;

            public class Empty
            {
                public void Work()
                {
                    var x = 1;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new RemoveBracesOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new RemoveBracesParams
            {
                SourceFile = workspace.SourcePath,
                Line = FindLine(source, "var x = 1;")
            }));

        Assert.Equal(ErrorCodes.NoControlStatement, ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [Fact]
    public void RemoveBraces_UneditableDocument_Throws()
    {
        var workspace = new AdhocWorkspace();
        var project = workspace.AddProject("P", LanguageNames.CSharp);
        var document = workspace.AddDocument(project.Id, "Generated.cs", SourceText.From("class C {}"));

        var ex = Assert.Throws<RefactoringException>(() =>
            RemoveBracesOperation.ValidateDocumentIsEditable(document, workspace));

        Assert.Equal(ErrorCodes.DocumentNotEditable, ex.ErrorCode);
    }

    #endregion

    #region P0 Preview

    [SkippableFact]
    public async Task RemoveBraces_Preview_DoesNotModifyFile()
    {
        const string source = """
            namespace TestApp;

            public class Gate
            {
                public string Classify(int x)
                {
                    if (x > 0)
                    {
                        return "positive";
                    }
                    return "other";
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new RemoveBracesOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new RemoveBracesParams
        {
            SourceFile = workspace.SourcePath,
            Line = FindLine(source, "if (x > 0)"),
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.Equal(1, result.StatementsModified);
        Assert.Equal("statement", result.Scope);
        Assert.NotNull(result.PendingChanges);
        Assert.Contains(result.PendingChanges, change =>
            change.Description.Contains("Remove braces", StringComparison.Ordinal) &&
            change.AfterSnippet != null &&
            change.AfterSnippet.Contains("return \"positive\"", StringComparison.Ordinal));
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    #endregion

    #region P1 / P2 Scope

    [SkippableFact]
    public async Task RemoveBraces_FileScope_UnwrapsEveryEligibleBody()
    {
        const string source = """
            namespace TestApp;

            public class One
            {
                public void Run(int[] items)
                {
                    if (items.Length > 0)
                    {
                        Start();
                    }
                    else
                    {
                        Stop();
                    }

                    for (var i = 0; i < items.Length; i++)
                    {
                        Touch(items[i]);
                    }

                    foreach (var item in items)
                    {
                        Touch(item);
                    }

                    while (items.Length == 0)
                    {
                        return;
                    }
                }

                private static void Start() { }
                private static void Stop() { }
                private static void Touch(int value) { }
            }

            public class Two
            {
                public void Use()
                {
                    using (var stream = new System.IO.MemoryStream())
                    {
                        stream.WriteByte(1);
                    }
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new RemoveBracesOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new RemoveBracesParams
        {
            SourceFile = workspace.SourcePath,
            Scope = "file"
        });

        Assert.True(result.Success);
        Assert.Equal(6, result.StatementsModified);
        Assert.Equal("file", result.Scope);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var root = CSharpSyntaxTree.ParseText(updated).GetRoot();
        Assert.All(root.DescendantNodes().OfType<IfStatementSyntax>(), statement =>
        {
            Assert.IsNotType<BlockSyntax>(statement.Statement);
            Assert.IsNotType<BlockSyntax>(statement.Else?.Statement);
        });
        Assert.All(root.DescendantNodes().OfType<ForStatementSyntax>(),
            statement => Assert.IsNotType<BlockSyntax>(statement.Statement));
        Assert.All(root.DescendantNodes().OfType<ForEachStatementSyntax>(),
            statement => Assert.IsNotType<BlockSyntax>(statement.Statement));
        Assert.All(root.DescendantNodes().OfType<WhileStatementSyntax>(),
            statement => Assert.IsNotType<BlockSyntax>(statement.Statement));
        Assert.All(root.DescendantNodes().OfType<UsingStatementSyntax>(),
            statement => Assert.IsNotType<BlockSyntax>(statement.Statement));
        await AssertCompilesAsync(workspace);
    }

    [SkippableFact]
    public async Task RemoveBraces_TypeScope_UnwrapsOnlyThatType()
    {
        const string source = """
            namespace TestApp;

            public class Inside
            {
                public void Run(bool flag)
                {
                    if (flag)
                    {
                        Work();
                    }
                }

                private static void Work() { }
            }

            public class Outside
            {
                public void Run(bool flag)
                {
                    if (flag)
                    {
                        Work();
                    }
                }

                private static void Work() { }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new RemoveBracesOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new RemoveBracesParams
        {
            SourceFile = workspace.SourcePath,
            Scope = "type",
            TypeName = "Inside"
        });

        Assert.True(result.Success);
        Assert.Equal(1, result.StatementsModified);
        Assert.Equal("type", result.Scope);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var root = CSharpSyntaxTree.ParseText(updated).GetRoot();
        var insideIf = root.DescendantNodes().OfType<ClassDeclarationSyntax>()
            .Single(type => type.Identifier.Text == "Inside")
            .DescendantNodes().OfType<IfStatementSyntax>().Single();
        var outsideIf = root.DescendantNodes().OfType<ClassDeclarationSyntax>()
            .Single(type => type.Identifier.Text == "Outside")
            .DescendantNodes().OfType<IfStatementSyntax>().Single();
        Assert.IsNotType<BlockSyntax>(insideIf.Statement);
        Assert.IsType<BlockSyntax>(outsideIf.Statement);
        await AssertCompilesAsync(workspace);
    }

    [SkippableFact]
    public async Task RemoveBraces_TypeScope_MissingType_Throws()
    {
        const string source = """
            namespace TestApp;

            public class Only
            {
                public void Run() { }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new RemoveBracesOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new RemoveBracesParams
            {
                SourceFile = workspace.SourcePath,
                Scope = "type",
                TypeName = "Missing"
            }));

        Assert.Equal(ErrorCodes.TypeNotFound, ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task RemoveBraces_DanglingElse_StatementScope_Throws()
    {
        const string source = """
            namespace TestApp;

            public class Gate
            {
                public void Run(bool outer, bool inner)
                {
                    if (outer)
                    {
                        if (inner) Work();
                    }
                    else Other();
                }

                private static void Work() { }
                private static void Other() { }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new RemoveBracesOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new RemoveBracesParams
            {
                SourceFile = workspace.SourcePath,
                Line = FindLine(source, "if (outer)")
            }));

        Assert.Equal(ErrorCodes.CompilationError, ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task RemoveBraces_DanglingElse_FileScope_SkipsOuterAndUnwrapsSafe()
    {
        const string source = """
            namespace TestApp;

            public class Gate
            {
                public void Run(bool outer, bool inner, bool other)
                {
                    if (outer)
                    {
                        if (inner) Work();
                    }
                    else Other();

                    if (other)
                    {
                        Safe();
                    }
                }

                private static void Work() { }
                private static void Other() { }
                private static void Safe() { }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new RemoveBracesOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new RemoveBracesParams
        {
            SourceFile = workspace.SourcePath,
            Scope = "file"
        });

        Assert.True(result.Success);
        Assert.Equal(1, result.StatementsModified);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var root = CSharpSyntaxTree.ParseText(updated).GetRoot();
        var outerIf = root.DescendantNodes().OfType<IfStatementSyntax>()
            .Single(statement => statement.Condition.ToString() == "outer");
        var safeIf = root.DescendantNodes().OfType<IfStatementSyntax>()
            .Single(statement => statement.Condition.ToString() == "other");
        Assert.IsType<BlockSyntax>(outerIf.Statement);
        Assert.IsNotType<BlockSyntax>(safeIf.Statement);
        await AssertCompilesAsync(workspace);
    }

    [SkippableFact]
    public async Task RemoveBraces_GotoRetry_StatementScope_Throws()
    {
        const string source = """
            namespace TestApp;

            public class Loop
            {
                public void Run(bool condition)
                {
                    goto retry;
                    if (condition)
                    {
                        retry: Work();
                    }
                }

                private static void Work() { }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new RemoveBracesOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new RemoveBracesParams
            {
                SourceFile = workspace.SourcePath,
                Line = FindLine(source, "if (condition)")
            }));

        Assert.Equal(ErrorCodes.CompilationError, ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task RemoveBraces_GotoRetry_FileScope_SkipsLabeledBody()
    {
        const string source = """
            namespace TestApp;

            public class Loop
            {
                public void Run(bool condition, bool other)
                {
                    goto retry;
                    if (condition)
                    {
                        retry: Work();
                    }
                    if (other)
                    {
                        Safe();
                    }
                }

                private static void Work() { }
                private static void Safe() { }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new RemoveBracesOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new RemoveBracesParams
        {
            SourceFile = workspace.SourcePath,
            Scope = "file"
        });

        Assert.True(result.Success);
        Assert.Equal(1, result.StatementsModified);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var root = CSharpSyntaxTree.ParseText(updated).GetRoot();
        var labeledIf = root.DescendantNodes().OfType<IfStatementSyntax>()
            .Single(statement => statement.Condition.ToString() == "condition");
        var safeIf = root.DescendantNodes().OfType<IfStatementSyntax>()
            .Single(statement => statement.Condition.ToString() == "other");
        Assert.IsType<BlockSyntax>(labeledIf.Statement);
        Assert.IsType<LabeledStatementSyntax>(
            Assert.IsType<BlockSyntax>(labeledIf.Statement).Statements[0]);
        Assert.IsNotType<BlockSyntax>(safeIf.Statement);
        Assert.Contains("retry: Work();", updated);
        Assert.DoesNotContain("retry: Work();", safeIf.ToString());
    }

    [SkippableFact]
    public async Task RemoveBraces_LocalDeclaration_StatementScope_Throws()
    {
        const string source = """
            namespace TestApp;

            public class Gate
            {
                public void Run(bool flag)
                {
                    if (flag)
                    {
                        var value = GetValue();
                    }
                }

                private static int GetValue() => 1;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new RemoveBracesOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new RemoveBracesParams
            {
                SourceFile = workspace.SourcePath,
                Line = FindLine(source, "if (flag)")
            }));

        Assert.Equal(ErrorCodes.CompilationError, ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task RemoveBraces_LocalFunction_StatementScope_Throws()
    {
        const string source = """
            namespace TestApp;

            public class Gate
            {
                public void Run(bool flag)
                {
                    if (flag)
                    {
                        void Local() { }
                    }
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new RemoveBracesOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new RemoveBracesParams
            {
                SourceFile = workspace.SourcePath,
                Line = FindLine(source, "if (flag)")
            }));

        Assert.Equal(ErrorCodes.CompilationError, ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task RemoveBraces_LabeledBody_StatementScope_Throws()
    {
        const string source = """
            namespace TestApp;

            public class Gate
            {
                public void Run(bool flag)
                {
                    if (flag)
                    {
                        retry: Work();
                    }
                }

                private static void Work() { }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new RemoveBracesOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new RemoveBracesParams
            {
                SourceFile = workspace.SourcePath,
                Line = FindLine(source, "if (flag)")
            }));

        Assert.Equal(ErrorCodes.CompilationError, ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task RemoveBraces_FileScope_SkipsNonEmbeddableAndUnwrapsNeighbor()
    {
        const string source = """
            namespace TestApp;

            public class Gate
            {
                public void Run(bool declare, bool local, bool labeled, bool other)
                {
                    if (declare)
                    {
                        var value = GetValue();
                    }
                    if (local)
                    {
                        void Local() { }
                    }
                    if (labeled)
                    {
                        retry: Work();
                    }
                    if (other)
                    {
                        Safe();
                    }
                }

                private static int GetValue() => 1;
                private static void Work() { }
                private static void Safe() { }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new RemoveBracesOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new RemoveBracesParams
        {
            SourceFile = workspace.SourcePath,
            Scope = "file"
        });

        Assert.True(result.Success);
        Assert.Equal(1, result.StatementsModified);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var root = CSharpSyntaxTree.ParseText(updated).GetRoot();
        IfStatementSyntax IfWith(string condition) =>
            root.DescendantNodes().OfType<IfStatementSyntax>()
                .Single(statement => statement.Condition.ToString() == condition);

        Assert.IsType<BlockSyntax>(IfWith("declare").Statement);
        Assert.IsType<BlockSyntax>(IfWith("local").Statement);
        Assert.IsType<BlockSyntax>(IfWith("labeled").Statement);
        Assert.IsNotType<BlockSyntax>(IfWith("other").Statement);
        await AssertCompilesAsync(workspace);
    }

    [SkippableFact]
    public async Task RemoveBraces_OpenBraceComment_Survives()
    {
        const string source = """
            namespace TestApp;

            public class Gate
            {
                public string Classify(int x)
                {
                    if (x > 0)
                    { // keep-reason
                        return "positive";
                    }
                    return "other";
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new RemoveBracesOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new RemoveBracesParams
        {
            SourceFile = workspace.SourcePath,
            Line = FindLine(source, "if (x > 0)")
        });

        Assert.True(result.Success);
        Assert.Equal(1, result.StatementsModified);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("keep-reason", updated, StringComparison.Ordinal);
        AssertIfBodyIsNotBlock(updated, "x > 0");
        await AssertCompilesAsync(workspace);
    }

    [SkippableFact]
    public async Task RemoveBraces_TypeScope_AmbiguousSimpleName_Throws()
    {
        const string source = """
            namespace A
            {
                public class C
                {
                    public void Run(bool flag)
                    {
                        if (flag)
                        {
                            Work();
                        }
                    }

                    private static void Work() { }
                }
            }

            namespace B
            {
                public class C
                {
                    public void Run(bool flag)
                    {
                        if (flag)
                        {
                            Work();
                        }
                    }

                    private static void Work() { }
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new RemoveBracesOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new RemoveBracesParams
            {
                SourceFile = workspace.SourcePath,
                Scope = "type",
                TypeName = "C"
            }));

        Assert.Equal(ErrorCodes.SymbolAmbiguous, ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task RemoveBraces_TypeScope_NamespaceQualified_EditsOnlyThatType()
    {
        const string source = """
            namespace A
            {
                public class C
                {
                    public void Run(bool flag)
                    {
                        if (flag)
                        {
                            WorkA();
                        }
                    }

                    private static void WorkA() { }
                }
            }

            namespace B
            {
                public class C
                {
                    public void Run(bool flag)
                    {
                        if (flag)
                        {
                            WorkB();
                        }
                    }

                    private static void WorkB() { }
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new RemoveBracesOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new RemoveBracesParams
        {
            SourceFile = workspace.SourcePath,
            Scope = "type",
            TypeName = "A.C"
        });

        Assert.True(result.Success);
        Assert.Equal(1, result.StatementsModified);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var root = CSharpSyntaxTree.ParseText(updated).GetRoot();
        var typeA = root.DescendantNodes().OfType<ClassDeclarationSyntax>()
            .Single(type => type.Identifier.Text == "C" &&
                type.Ancestors().OfType<BaseNamespaceDeclarationSyntax>()
                    .Any(ns => ns.Name.ToString() == "A"));
        var typeB = root.DescendantNodes().OfType<ClassDeclarationSyntax>()
            .Single(type => type.Identifier.Text == "C" &&
                type.Ancestors().OfType<BaseNamespaceDeclarationSyntax>()
                    .Any(ns => ns.Name.ToString() == "B"));
        Assert.IsNotType<BlockSyntax>(typeA.DescendantNodes().OfType<IfStatementSyntax>().Single().Statement);
        Assert.IsType<BlockSyntax>(typeB.DescendantNodes().OfType<IfStatementSyntax>().Single().Statement);
        await AssertCompilesAsync(workspace);
    }

    [SkippableFact]
    public async Task RemoveBraces_FileScope_LeavesElseIfAsSingleConstruct()
    {
        const string source = """
            namespace TestApp;

            public class Chain
            {
                public string Pick(bool a, bool b)
                {
                    if (a)
                    {
                        return "a";
                    }
                    else if (b)
                    {
                        return "b";
                    }
                    return "none";
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new RemoveBracesOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new RemoveBracesParams
        {
            SourceFile = workspace.SourcePath,
            Scope = "file"
        });

        Assert.True(result.Success);
        Assert.Equal(2, result.StatementsModified);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var outerIf = CSharpSyntaxTree.ParseText(updated).GetRoot()
            .DescendantNodes().OfType<IfStatementSyntax>()
            .First(statement => statement.Condition.ToString().Contains("a", StringComparison.Ordinal));
        Assert.IsNotType<BlockSyntax>(outerIf.Statement);
        Assert.IsType<IfStatementSyntax>(outerIf.Else?.Statement);
        Assert.IsNotType<BlockSyntax>(((IfStatementSyntax)outerIf.Else!.Statement).Statement);
        await AssertCompilesAsync(workspace);
    }

    [SkippableFact]
    public async Task RemoveBraces_FileScope_DoesNotTurnElseBlockIfIntoElseIf()
    {
        const string source = """
            namespace TestApp;

            public class Chain
            {
                public string Pick(bool a, bool b)
                {
                    if (a)
                    {
                        return "a";
                    }
                    else
                    {
                        if (b)
                        {
                            return "b";
                        }
                    }
                    return "none";
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new RemoveBracesOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new RemoveBracesParams
        {
            SourceFile = workspace.SourcePath,
            Scope = "file"
        });

        Assert.True(result.Success);
        Assert.Equal(2, result.StatementsModified);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var outerIf = CSharpSyntaxTree.ParseText(updated).GetRoot()
            .DescendantNodes().OfType<IfStatementSyntax>()
            .First(statement => statement.Condition.ToString().Contains("a", StringComparison.Ordinal));
        Assert.IsNotType<BlockSyntax>(outerIf.Statement);
        Assert.IsType<BlockSyntax>(outerIf.Else?.Statement);
        var innerIf = Assert.IsType<IfStatementSyntax>(
            Assert.IsType<BlockSyntax>(outerIf.Else!.Statement).Statements[0]);
        Assert.IsNotType<BlockSyntax>(innerIf.Statement);
        await AssertCompilesAsync(workspace);
    }

    [SkippableFact]
    public async Task RemoveBraces_StatementScope_ElseBlockIf_BecomesElseIf()
    {
        const string source = """
            namespace TestApp;

            public class Chain
            {
                public string Pick(bool a, bool b)
                {
                    if (a)
                    {
                        return "a";
                    }
                    else
                    {
                        if (b)
                            return "b";
                    }
                    return "none";
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new RemoveBracesOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new RemoveBracesParams
        {
            SourceFile = workspace.SourcePath,
            Line = FindLine(source, "else")
        });

        Assert.True(result.Success);
        Assert.Equal(1, result.StatementsModified);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var outerIf = CSharpSyntaxTree.ParseText(updated).GetRoot()
            .DescendantNodes().OfType<IfStatementSyntax>()
            .First(statement => statement.Condition.ToString().Contains("a", StringComparison.Ordinal));
        Assert.IsType<BlockSyntax>(outerIf.Statement);
        var elseIf = Assert.IsType<IfStatementSyntax>(outerIf.Else?.Statement);
        Assert.IsNotType<BlockSyntax>(elseIf.Statement);
        Assert.Contains("return \"b\";", elseIf.Statement.ToString());
        await AssertCompilesAsync(workspace);
    }

    [SkippableFact]
    public async Task RemoveBraces_ColumnSelectsInnerIfOnSameLine()
    {
        const string source = SameLineIfsSource;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new RemoveBracesOperation(workspace.Context);
        var line = FindLine(source, "if (a) { WorkA(); } if (b)");
        var innerIfColumn = ColumnOf(source, "if (b)");

        var result = await operation.ExecuteAsync(new RemoveBracesParams
        {
            SourceFile = workspace.SourcePath,
            Line = line,
            Column = innerIfColumn
        });

        Assert.True(result.Success);
        Assert.Equal(1, result.StatementsModified);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var root = CSharpSyntaxTree.ParseText(updated).GetRoot();
        var innerIf = root.DescendantNodes().OfType<IfStatementSyntax>()
            .Single(statement => statement.Condition.ToString() == "b");
        var outerIf = root.DescendantNodes().OfType<IfStatementSyntax>()
            .Single(statement => statement.Condition.ToString() == "a");
        Assert.IsNotType<BlockSyntax>(innerIf.Statement);
        Assert.IsType<BlockSyntax>(outerIf.Statement);
        await AssertCompilesAsync(workspace);
    }

    [SkippableFact]
    public async Task RemoveBraces_OmittedColumn_UnwrapsFirstStatementOnTheLine()
    {
        const string source = SameLineIfsSource;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new RemoveBracesOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new RemoveBracesParams
        {
            SourceFile = workspace.SourcePath,
            Line = FindLine(source, "if (a) { WorkA(); } if (b)")
        });

        Assert.True(result.Success);
        Assert.Equal(1, result.StatementsModified);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var root = CSharpSyntaxTree.ParseText(updated).GetRoot();
        var outerIf = root.DescendantNodes().OfType<IfStatementSyntax>()
            .Single(statement => statement.Condition.ToString() == "a");
        var innerIf = root.DescendantNodes().OfType<IfStatementSyntax>()
            .Single(statement => statement.Condition.ToString() == "b");
        Assert.IsNotType<BlockSyntax>(outerIf.Statement);
        Assert.IsType<BlockSyntax>(innerIf.Statement);
        await AssertCompilesAsync(workspace);
    }

    [SkippableFact]
    public async Task RemoveBraces_ExclusiveEndAtPreviousKeyword_ThrowsNoControlStatement()
    {
        const string source = SameLineIfsSource;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var original = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new RemoveBracesOperation(workspace.Context);
        var line = FindLine(source, "if (a) { WorkA(); } if (b)");
        var firstKeywordEndCol = FirstKeywordEndColumn(source);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new RemoveBracesParams
            {
                SourceFile = workspace.SourcePath,
                Line = line,
                Column = firstKeywordEndCol
            }));

        Assert.Equal(ErrorCodes.NoControlStatement, ex.ErrorCode);
        Assert.Equal("3155", ex.ErrorCode);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task RemoveBraces_Preview_ColumnSelectsInnerIf_DoesNotModifyFile()
    {
        const string source = SameLineIfsSource;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new RemoveBracesOperation(workspace.Context);
        var line = FindLine(source, "if (a) { WorkA(); } if (b)");
        var innerIfColumn = ColumnOf(source, "if (b)");

        var result = await operation.ExecuteAsync(new RemoveBracesParams
        {
            SourceFile = workspace.SourcePath,
            Line = line,
            Column = innerIfColumn,
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.Equal(1, result.StatementsModified);
        Assert.Equal("statement", result.Scope);
        Assert.NotNull(result.PendingChanges);
        Assert.Contains(result.PendingChanges, change =>
            change.Description.Contains("Remove braces", StringComparison.Ordinal) &&
            change.AfterSnippet != null &&
            change.AfterSnippet.Contains("WorkB", StringComparison.Ordinal));
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    #endregion

    #region AllFiles

    [SkippableFact]
    public async Task RemoveBraces_AllFilesFalse_UnwrapsOnlySpecifiedFile()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("BracedA.cs", BracedA),
            ("BracedB.cs", BracedB),
            ("AlreadyUnbraced.cs", AlreadyUnbraced));
        var operation = new RemoveBracesOperation(workspace.Context);
        var beforeB = await File.ReadAllTextAsync(workspace.SourcePaths["BracedB.cs"]);
        var beforeUnbraced = await File.ReadAllTextAsync(workspace.SourcePaths["AlreadyUnbraced.cs"]);

        var result = await operation.ExecuteAsync(new RemoveBracesParams
        {
            SourceFile = workspace.SourcePaths["BracedA.cs"],
            AllFiles = false,
            Scope = "file"
        });

        Assert.True(result.Success);
        Assert.False(result.Preview);
        var updatedA = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePaths["BracedA.cs"]));
        AssertIfBodyIsNotBlock(updatedA, "flag");
        Assert.Equal(beforeB, await File.ReadAllTextAsync(workspace.SourcePaths["BracedB.cs"]));
        Assert.Equal(beforeUnbraced, await File.ReadAllTextAsync(workspace.SourcePaths["AlreadyUnbraced.cs"]));
        Assert.Single(result.Changes!.FilesModified);
        Assert.Contains(result.Changes.FilesModified, p => PathEquals(p, workspace.SourcePaths["BracedA.cs"]));
    }

    [SkippableFact]
    public async Task RemoveBraces_AllFilesTrue_WithoutSourceFile_UnwrapsMultipleFiles_LeavesAlreadyUnbracedUntouched()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("BracedA.cs", BracedA),
            ("BracedB.cs", BracedB),
            ("AlreadyUnbraced.cs", AlreadyUnbraced));
        var operation = new RemoveBracesOperation(workspace.Context);
        var beforeUnbraced = await File.ReadAllTextAsync(workspace.SourcePaths["AlreadyUnbraced.cs"]);

        var result = await operation.ExecuteAsync(new RemoveBracesParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        Assert.False(result.Preview);
        var updatedA = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePaths["BracedA.cs"]));
        var updatedB = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePaths["BracedB.cs"]));
        AssertIfBodyIsNotBlock(updatedA, "flag");
        AssertIfBodyIsNotBlock(updatedB, "ready");
        Assert.Equal(beforeUnbraced, await File.ReadAllTextAsync(workspace.SourcePaths["AlreadyUnbraced.cs"]));
        Assert.Equal(2, result.Changes!.FilesModified.Count);
        Assert.Contains(result.Changes.FilesModified, p => PathEquals(p, workspace.SourcePaths["BracedA.cs"]));
        Assert.Contains(result.Changes.FilesModified, p => PathEquals(p, workspace.SourcePaths["BracedB.cs"]));
        Assert.DoesNotContain(result.Changes.FilesModified, p => PathEquals(p, workspace.SourcePaths["AlreadyUnbraced.cs"]));
        Assert.Equal("file", result.Scope);
    }

    [SkippableFact]
    public async Task RemoveBraces_AllFilesTrue_EveryFileAlreadyUnbraced_SucceedsWithEmptyChanges()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("AlreadyUnbraced.cs", AlreadyUnbraced),
            ("AlreadyUnbracedB.cs", AlreadyUnbracedB));
        var operation = new RemoveBracesOperation(workspace.Context);
        var beforeA = await File.ReadAllTextAsync(workspace.SourcePaths["AlreadyUnbraced.cs"]);
        var beforeB = await File.ReadAllTextAsync(workspace.SourcePaths["AlreadyUnbracedB.cs"]);

        var result = await operation.ExecuteAsync(new RemoveBracesParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        Assert.False(result.Preview);
        Assert.NotNull(result.Changes);
        Assert.Empty(result.Changes.FilesModified);
        Assert.Equal(beforeA, await File.ReadAllTextAsync(workspace.SourcePaths["AlreadyUnbraced.cs"]));
        Assert.Equal(beforeB, await File.ReadAllTextAsync(workspace.SourcePaths["AlreadyUnbracedB.cs"]));
    }

    [SkippableFact]
    public async Task RemoveBraces_AllFilesFalse_WithoutSourceFile_MissingRequiredParam()
    {
        await using var workspace = await TempWorkspace.CreateAsync(BracedA);
        var operation = new RemoveBracesOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new RemoveBracesParams
            {
                AllFiles = false
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [SkippableFact]
    public async Task RemoveBraces_AllFilesTrue_WithStatementScope_Rejects()
    {
        await using var workspace = await TempWorkspace.CreateAsync(BracedA);
        var operation = new RemoveBracesOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new RemoveBracesParams
            {
                AllFiles = true,
                Scope = "statement",
                Line = 1
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("statement", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task RemoveBraces_AllFilesTrue_WithTypeScope_Rejects()
    {
        await using var workspace = await TempWorkspace.CreateAsync(BracedA);
        var operation = new RemoveBracesOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new RemoveBracesParams
            {
                AllFiles = true,
                Scope = "type",
                TypeName = "GateA"
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("type", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task RemoveBraces_PreviewAllFiles_DoesNotWriteFiles()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("BracedA.cs", BracedA),
            ("BracedB.cs", BracedB),
            ("AlreadyUnbraced.cs", AlreadyUnbraced));
        var operation = new RemoveBracesOperation(workspace.Context);
        var beforeA = await File.ReadAllTextAsync(workspace.SourcePaths["BracedA.cs"]);
        var beforeB = await File.ReadAllTextAsync(workspace.SourcePaths["BracedB.cs"]);
        var beforeUnbraced = await File.ReadAllTextAsync(workspace.SourcePaths["AlreadyUnbraced.cs"]);

        var result = await operation.ExecuteAsync(new RemoveBracesParams
        {
            AllFiles = true,
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.Equal(2, result.PendingChanges.Count);
        Assert.Contains(result.PendingChanges, c => PathEquals(c.File, workspace.SourcePaths["BracedA.cs"]));
        Assert.Contains(result.PendingChanges, c => PathEquals(c.File, workspace.SourcePaths["BracedB.cs"]));
        Assert.DoesNotContain(result.PendingChanges, c => PathEquals(c.File, workspace.SourcePaths["AlreadyUnbraced.cs"]));
        Assert.Equal(beforeA, await File.ReadAllTextAsync(workspace.SourcePaths["BracedA.cs"]));
        Assert.Equal(beforeB, await File.ReadAllTextAsync(workspace.SourcePaths["BracedB.cs"]));
        Assert.Equal(beforeUnbraced, await File.ReadAllTextAsync(workspace.SourcePaths["AlreadyUnbraced.cs"]));
    }

    #endregion

    #region Column Coverage

    private const string SameLineIfsSource = """
        namespace TestApp;

        public class SameLine
        {
            public string Pick(bool a, bool b)
            {
                if (a) { WorkA(); } if (b) { WorkB(); }
                return "done";
            }

            private static void WorkA() { }
            private static void WorkB() { }
        }
        """;

    private const string IndentedIfSource = """
        class C
        {
            void M(bool flag)
            {
                if (flag)
                {
                    return;
                }
            }
        }
        """;

    [Fact]
    public void FindControlTarget_OmittedColumn_PicksFirstKeywordBySpanStart()
    {
        var root = CSharpSyntaxTree.ParseText(SameLineIfsSource).GetRoot();
        var line = FindLine(SameLineIfsSource, "if (a) { WorkA(); } if (b)");

        var found = RemoveBracesOperation.FindControlTarget(root, line, column: null);

        Assert.NotNull(found);
        Assert.Equal("if", found.Value.Keyword.ValueText);
        var ownerIf = Assert.IsType<IfStatementSyntax>(found.Value.Owner);
        Assert.Equal("a", ownerIf.Condition.ToString());
    }

    [Fact]
    public void FindControlTarget_OmittedColumn_IndentedKeyword_DoesNotForceColumn1()
    {
        var root = CSharpSyntaxTree.ParseText(IndentedIfSource).GetRoot();
        var line = FindLine(IndentedIfSource, "if (flag)");
        var ifStmt = root.DescendantNodes().OfType<IfStatementSyntax>().Single();
        var startCol = ifStmt.IfKeyword.GetLocation().GetLineSpan().StartLinePosition.Character + 1;
        Assert.True(startCol > 1);

        var found = RemoveBracesOperation.FindControlTarget(root, line, column: null);

        Assert.NotNull(found);
        var ownerIf = Assert.IsType<IfStatementSyntax>(found.Value.Owner);
        Assert.Equal("flag", ownerIf.Condition.ToString());
    }

    [Fact]
    public void FindControlTarget_ColumnSelectsInnerIfOnSameLine()
    {
        var root = CSharpSyntaxTree.ParseText(SameLineIfsSource).GetRoot();
        var line = FindLine(SameLineIfsSource, "if (a) { WorkA(); } if (b)");

        var outer = RemoveBracesOperation.FindControlTarget(root, line, ColumnOf(SameLineIfsSource, "if (a)"));
        var inner = RemoveBracesOperation.FindControlTarget(root, line, ColumnOf(SameLineIfsSource, "if (b)"));

        Assert.NotNull(outer);
        var outerIf = Assert.IsType<IfStatementSyntax>(outer.Value.Owner);
        Assert.Equal("a", outerIf.Condition.ToString());
        Assert.NotNull(inner);
        var innerIf = Assert.IsType<IfStatementSyntax>(inner.Value.Owner);
        Assert.Equal("b", innerIf.Condition.ToString());
    }

    [Fact]
    public void FindControlTarget_AdjacentKeywords_ExclusiveEndDoesNotStealNext()
    {
        var root = CSharpSyntaxTree.ParseText(SameLineIfsSource).GetRoot();
        var line = FindLine(SameLineIfsSource, "if (a) { WorkA(); } if (b)");
        var first = root.DescendantNodes().OfType<IfStatementSyntax>()
            .First(statement => statement.Condition.ToString() == "a");
        var firstKeywordEndCol = first.IfKeyword.GetLocation().GetLineSpan().EndLinePosition.Character + 1;
        var secondKeyword = ColumnOf(SameLineIfsSource, "if (b)");

        var atExclusiveEnd = RemoveBracesOperation.FindControlTarget(root, line, firstKeywordEndCol);
        var atSecond = RemoveBracesOperation.FindControlTarget(root, line, secondKeyword);

        Assert.False(RemoveBracesOperation.SpanCoversColumn(
            first.IfKeyword.GetLocation().GetLineSpan(), line, firstKeywordEndCol));
        Assert.True(atExclusiveEnd == null
            || ((IfStatementSyntax)atExclusiveEnd.Value.Owner).Condition.ToString() != "a");
        Assert.NotNull(atSecond);
        var secondIf = Assert.IsType<IfStatementSyntax>(atSecond.Value.Owner);
        Assert.Equal("b", secondIf.Condition.ToString());
    }

    [Fact]
    public void SpanCoversColumn_TreatsEndAsExclusive()
    {
        var tree = CSharpSyntaxTree.ParseText(SameLineIfsSource);
        var first = tree.GetRoot().DescendantNodes().OfType<IfStatementSyntax>()
            .First(statement => statement.Condition.ToString() == "a");
        var span = first.IfKeyword.GetLocation().GetLineSpan();
        var line = span.StartLinePosition.Line + 1;
        var startCol = span.StartLinePosition.Character + 1;
        var endCol = span.EndLinePosition.Character + 1;

        Assert.True(RemoveBracesOperation.SpanCoversColumn(span, line, startCol));
        Assert.True(RemoveBracesOperation.SpanCoversColumn(span, line, endCol - 1));
        Assert.False(RemoveBracesOperation.SpanCoversColumn(span, line, endCol));
        Assert.False(RemoveBracesOperation.SpanCoversColumn(span, line, startCol - 1));
    }

    #endregion

    #region Helpers

    private static void AssertIfBodyIsNotBlock(string updated, string condition)
    {
        var ifStatement = CSharpSyntaxTree.ParseText(updated).GetRoot()
            .DescendantNodes()
            .OfType<IfStatementSyntax>()
            .First(statement => statement.Condition.ToString().Contains(condition, StringComparison.Ordinal));
        Assert.IsNotType<BlockSyntax>(ifStatement.Statement);
    }

    private static async Task AssertCompilesAsync(TempWorkspace workspace)
    {
        var document = workspace.Context.GetDocumentByPath(workspace.SourcePath);
        Assert.NotNull(document);
        var compilation = await document.Project.GetCompilationAsync();
        Assert.NotNull(compilation);
        var errors = compilation.GetDiagnostics()
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Select(diagnostic => diagnostic.ToString())
            .ToList();
        Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors));
    }

    private const string BracedA = """
        namespace TestApp;

        public class GateA
        {
            public void Run(bool flag)
            {
                if (flag)
                {
                    Work();
                }
            }

            private static void Work() { }
        }
        """;

    private const string BracedB = """
        namespace TestApp;

        public class GateB
        {
            public void Run(bool ready)
            {
                if (ready)
                {
                    Work();
                }
            }

            private static void Work() { }
        }
        """;

    private const string AlreadyUnbraced = """
        namespace TestApp;

        public class AlreadyUnbraced
        {
            public void Run(bool flag)
            {
                if (flag)
                    Work();
            }

            private static void Work() { }
        }
        """;

    private const string AlreadyUnbracedB = """
        namespace TestApp;

        public class AlreadyUnbracedB
        {
            public void Run(bool ready)
            {
                if (ready)
                    Work();
            }

            private static void Work() { }
        }
        """;

    private static bool PathEquals(string left, string right) =>
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

    private static string AbsoluteTestPath() =>
        Path.Combine(Path.GetTempPath(), "RoslynMcpRemoveBracesMissing.cs");

    private static string NormalizeNewlines(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal);

    private static int ColumnOf(string source, string snippet)
    {
        var index = source.IndexOf(snippet, StringComparison.Ordinal);
        if (index < 0)
            throw new InvalidOperationException($"Snippet not found: {snippet}");

        var lineStart = source.LastIndexOf('\n', index) + 1;
        return index - lineStart + 1;
    }

    private static int FirstKeywordEndColumn(string source)
    {
        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var first = RemoveBracesOperation.CollectTargets(root)
            .OrderBy(target => target.Keyword.SpanStart)
            .First();
        return first.Keyword.GetLocation().GetLineSpan().EndLinePosition.Character + 1;
    }

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

    private sealed class TempWorkspace : IAsyncDisposable
    {
        public required string DirectoryPath { get; init; }
        public required string ProjectPath { get; init; }
        public required string SourcePath { get; init; }
        public required IReadOnlyDictionary<string, string> SourcePaths { get; init; }
        public required WorkspaceContext Context { get; init; }

        public static Task<TempWorkspace> CreateAsync(string source, string fileName = "Types.cs") =>
            CreateWithFilesAsync((fileName, source));

        public static async Task<TempWorkspace> CreateWithFilesAsync(params (string FileName, string Source)[] files)
        {
            Skip.IfNot(ModuleInitializer.MsBuildAvailable, ModuleInitializer.MsBuildError ?? "MSBuild not available");

            var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpRemoveBraces_" + Guid.NewGuid().ToString("N"));
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

            foreach (var (fileName, source) in files)
            {
                var sourcePath = Path.Combine(directory, fileName);
                await File.WriteAllTextAsync(sourcePath, source);
                sourcePaths[fileName] = sourcePath;
            }

            try
            {
                var provider = new MSBuildWorkspaceProvider();
                var context = await provider.CreateContextAsync(projectPath);
                foreach (var sourcePath in sourcePaths.Values)
                {
                    if (context.GetDocumentByPath(sourcePath) == null)
                    {
                        context.Dispose();
                        throw new InvalidOperationException($"Workspace loaded but did not include {sourcePath}.");
                    }
                }

                return new TempWorkspace
                {
                    DirectoryPath = directory,
                    ProjectPath = projectPath,
                    SourcePath = sourcePaths.Values.First(),
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

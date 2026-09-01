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
/// Operation-level tests for <see cref="SimplifyNameOperation"/> (UC-A8).
/// </summary>
public class SimplifyNameOperationTests
{
    #region Input Validation

    [Fact]
    public void Validate_MissingSourceFile_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            SimplifyNameOperation.Validate(new SimplifyNameParams
            {
                SourceFile = ""
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public void Validate_AllFilesFalse_WithoutSourceFile_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            SimplifyNameOperation.Validate(new SimplifyNameParams
            {
                AllFiles = false
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public void Validate_AllFilesTrue_WithoutSourceFile_DoesNotThrow()
    {
        SimplifyNameOperation.Validate(new SimplifyNameParams
        {
            AllFiles = true
        });
    }

    [Fact]
    public void Validate_AllFilesTrue_WithLocationScope_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            SimplifyNameOperation.Validate(new SimplifyNameParams
            {
                AllFiles = true,
                Scope = "location",
                Line = 1
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("allFiles", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("location", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_RelativePath_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            SimplifyNameOperation.Validate(new SimplifyNameParams
            {
                SourceFile = "Types.cs"
            }));

        Assert.Equal(ErrorCodes.InvalidSourcePath, ex.ErrorCode);
    }

    [Fact]
    public void Validate_LocationScope_MissingLine_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            SimplifyNameOperation.Validate(new SimplifyNameParams
            {
                SourceFile = AbsoluteTestPath(),
                Scope = "location"
            }));

        Assert.Equal(ErrorCodes.InvalidLineNumber, ex.ErrorCode);
    }

    [Fact]
    public void Validate_InvalidLine_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            SimplifyNameOperation.Validate(new SimplifyNameParams
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
            SimplifyNameOperation.Validate(new SimplifyNameParams
            {
                SourceFile = AbsoluteTestPath(),
                Line = 1,
                Column = 0
            }));

        Assert.Equal(ErrorCodes.InvalidColumnNumber, ex.ErrorCode);
    }

    [Fact]
    public void Validate_InvalidScope_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            SimplifyNameOperation.Validate(new SimplifyNameParams
            {
                SourceFile = AbsoluteTestPath(),
                Scope = "type"
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public void Validate_MissingFile_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            SimplifyNameOperation.Validate(new SimplifyNameParams
            {
                SourceFile = AbsoluteTestPath()
            }));

        Assert.Equal(ErrorCodes.SourceFileNotFound, ex.ErrorCode);
    }

    [Fact]
    public void NormalizeScope_Default_IsFile()
    {
        Assert.Equal(SimplifyNameOperation.ScopeFile, SimplifyNameOperation.NormalizeScope(null));
        Assert.Equal(SimplifyNameOperation.ScopeFile, SimplifyNameOperation.NormalizeScope(""));
        Assert.Equal(SimplifyNameOperation.ScopeFile, SimplifyNameOperation.NormalizeScope("FILE"));
        Assert.Equal(SimplifyNameOperation.ScopeLocation, SimplifyNameOperation.NormalizeScope("Location"));
    }

    [Fact]
    public void CollectQualifiedNameNodes_SkipsUsingsAndNamespace()
    {
        var root = CSharpSyntaxTree.ParseText("""
            using System.Collections.Generic;

            namespace TestApp.Services
            {
                class C
                {
                    System.Collections.Generic.List<int> items;
                }
            }
            """).GetRoot();

        var names = SimplifyNameOperation.CollectQualifiedNameNodes(root);

        Assert.Single(names);
        Assert.Contains("List", names[0].ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(names, node => node.ToString() == "System.Collections.Generic" && node.Parent is UsingDirectiveSyntax);
    }

    [Fact]
    public void FindNameAtLocation_PicksLeftmostWithoutColumn()
    {
        var root = CSharpSyntaxTree.ParseText("""
            class C
            {
                System.Text.StringBuilder a; System.Collections.Generic.List<int> b;
            }
            """).GetRoot();

        var names = SimplifyNameOperation.CollectQualifiedNameNodes(root);
        var line = 3;
        var found = SimplifyNameOperation.FindNameAtLocation(names, line, column: null);

        Assert.NotNull(found);
        Assert.Contains("StringBuilder", found.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void FindNameAtLocation_ColumnNarrowsName()
    {
        const string source = """
            class C
            {
                System.Text.StringBuilder a; System.Collections.Generic.List<int> b;
            }
            """;
        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var names = SimplifyNameOperation.CollectQualifiedNameNodes(root);
        var listIndex = source.IndexOf("System.Collections.Generic.List", StringComparison.Ordinal);
        var column = ColumnOnLine(source, listIndex);
        var line = 3;

        var found = SimplifyNameOperation.FindNameAtLocation(names, line, column);

        Assert.NotNull(found);
        Assert.Contains("List", found.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void GetRightmostIdentifier_QualifiedGeneric()
    {
        var name = SyntaxFactory.ParseName("System.Collections.Generic.List<int>");
        Assert.Equal("List", SimplifyNameOperation.GetRightmostIdentifier(name));
    }

    [Fact]
    public void GetShorterForms_DropsLeftQualifiers()
    {
        var name = SyntaxFactory.ParseName("System.Collections.Generic.List<int>");
        var forms = SimplifyNameOperation.GetShorterForms(name).Select(form => form.ToString()).ToList();

        Assert.Contains("List<int>", forms);
        Assert.Contains("Generic.List<int>", forms);
        Assert.DoesNotContain("System.Collections.Generic.List<int>", forms);
    }

    [Fact]
    public void GetShorterForms_KeepsInnerCommentOnRetainedName()
    {
        var root = CSharpSyntaxTree.ParseText("""
            class C
            {
                System.Collections.Generic./* type */List<int> items;
            }
            """).GetRoot();

        var name = SimplifyNameOperation.CollectQualifiedNameNodes(root).Single();
        var forms = SimplifyNameOperation.GetShorterForms(name);
        var shortest = forms[0].ToFullString();

        Assert.Contains("/* type */", shortest, StringComparison.Ordinal);
        Assert.Contains("List<int>", shortest, StringComparison.Ordinal);
    }

    [Fact]
    public void ClassifySkipReason_LocalTypeConflict()
    {
        var tree = CSharpSyntaxTree.ParseText("""
            using System.Collections.Generic;
            class List {}
            class C { System.Collections.Generic.List<int> items; }
            """);
        var compilation = CSharpCompilation.Create(
            "skip",
            [tree],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
             MetadataReference.CreateFromFile(typeof(List<>).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var model = compilation.GetSemanticModel(tree);
        var name = tree.GetRoot().DescendantNodes().OfType<QualifiedNameSyntax>()
            .First(node => node.ToString().Contains("List", StringComparison.Ordinal)
                && node.Parent is not QualifiedNameSyntax);

        var reason = SimplifyNameOperation.ClassifySkipReason(name, model);

        Assert.Contains("conflict", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SimplifyName_UneditableDocument_Throws()
    {
        var workspace = new AdhocWorkspace();
        var project = workspace.AddProject("P", LanguageNames.CSharp);
        var document = workspace.AddDocument(project.Id, "Generated.cs", SourceText.From("class C {}"));

        var ex = Assert.Throws<RefactoringException>(() =>
            SimplifyNameOperation.ValidateDocumentIsEditable(document, workspace));

        Assert.Equal(ErrorCodes.DocumentNotEditable, ex.ErrorCode);
    }

    #endregion

    #region P0 Happy Path

    [SkippableFact]
    public async Task SimplifyName_FullyQualifiedName_Shortens()
    {
        const string source = """
            using System.Collections.Generic;

            namespace TestApp;

            public class Processor
            {
                public System.Collections.Generic.List<int> Items()
                {
                    return new System.Collections.Generic.List<int>();
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new SimplifyNameOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new SimplifyNameParams
        {
            SourceFile = workspace.SourcePath
        });

        Assert.True(result.Success);
        Assert.False(result.Preview);
        Assert.True(result.SimplificationsApplied >= 1);
        Assert.Equal("file", result.Scope);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.DoesNotContain("System.Collections.Generic.List", updated, StringComparison.Ordinal);
        Assert.Contains("List<int>", updated, StringComparison.Ordinal);
        await AssertCompilesAsync(workspace);
    }

    [SkippableFact]
    public async Task SimplifyName_MultipleNames_ShortensEach()
    {
        const string source = """
            using System.Collections.Generic;
            using System.Text;

            namespace TestApp;

            public class Processor
            {
                public System.Collections.Generic.List<int> Items()
                {
                    return new System.Collections.Generic.List<int>();
                }

                public System.Text.StringBuilder Builder()
                {
                    return new System.Text.StringBuilder();
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new SimplifyNameOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new SimplifyNameParams
        {
            SourceFile = workspace.SourcePath
        });

        Assert.True(result.Success);
        Assert.True(result.SimplificationsApplied >= 2);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.DoesNotContain("System.Collections.Generic.List", updated, StringComparison.Ordinal);
        Assert.DoesNotContain("System.Text.StringBuilder", updated, StringComparison.Ordinal);
        Assert.Contains("List<int>", updated, StringComparison.Ordinal);
        Assert.Contains("StringBuilder", updated, StringComparison.Ordinal);
        await AssertCompilesAsync(workspace);
    }

    [SkippableFact]
    public async Task SimplifyName_SkipAmbiguous_ReportsReasonAndKeepsConflict()
    {
        const string source = """
            using System.Collections.Generic;
            using System.Text;

            namespace TestApp;

            public class StringBuilder
            {
            }

            public class Processor
            {
                public System.Collections.Generic.List<int> Items()
                {
                    return new System.Collections.Generic.List<int>();
                }

                public System.Text.StringBuilder Builder()
                {
                    return new System.Text.StringBuilder();
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new SimplifyNameOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new SimplifyNameParams
        {
            SourceFile = workspace.SourcePath
        });

        Assert.True(result.Success);
        Assert.True(result.SimplificationsApplied >= 1);
        Assert.True(result.SimplificationsSkipped >= 1);
        Assert.NotNull(result.SkippedReasons);
        Assert.Contains(result.SkippedReasons, skip =>
            skip.Name.Contains("StringBuilder", StringComparison.Ordinal) &&
            (skip.Reason.Contains("conflict", StringComparison.OrdinalIgnoreCase)
                || skip.Reason.Contains("ambiguous", StringComparison.OrdinalIgnoreCase)));
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("System.Text.StringBuilder", updated, StringComparison.Ordinal);
        Assert.DoesNotContain("System.Collections.Generic.List", updated, StringComparison.Ordinal);
        await AssertCompilesAsync(workspace);
    }

    [SkippableFact]
    public async Task SimplifyName_AllAmbiguous_ThrowsNoSimplifiableNames()
    {
        const string source = """
            namespace Other
            {
                public class Widget
                {
                }
            }

            namespace TestApp
            {
                public class Widget
                {
                }

                public class Processor
                {
                    public Other.Widget Create()
                    {
                        return new Other.Widget();
                    }
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new SimplifyNameOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new SimplifyNameParams
            {
                SourceFile = workspace.SourcePath
            }));

        Assert.Equal(ErrorCodes.NoSimplifiableNames, ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task SimplifyName_FileScope_NothingToSimplify_Throws()
    {
        const string source = """
            using System.Collections.Generic;

            namespace TestApp;

            public class Processor
            {
                public List<int> Items() => new List<int>();
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new SimplifyNameOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new SimplifyNameParams
            {
                SourceFile = workspace.SourcePath
            }));

        Assert.Equal(ErrorCodes.NoSimplifiableNames, ex.ErrorCode);
    }

    #endregion

    #region P0 Preview

    [SkippableFact]
    public async Task SimplifyName_Preview_DoesNotModifyFile()
    {
        const string source = """
            using System.Collections.Generic;

            namespace TestApp;

            public class Processor
            {
                public System.Collections.Generic.List<int> Items()
                {
                    return new System.Collections.Generic.List<int>();
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new SimplifyNameOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new SimplifyNameParams
        {
            SourceFile = workspace.SourcePath,
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.True(result.SimplificationsApplied >= 1);
        Assert.Equal("file", result.Scope);
        Assert.NotNull(result.PendingChanges);
        Assert.Contains(result.PendingChanges, change =>
            change.Description.Contains("Simplify", StringComparison.Ordinal) &&
            change.AfterSnippet != null &&
            change.AfterSnippet.Contains("List", StringComparison.Ordinal));
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    #endregion

    #region P1 Scope and Edge

    [SkippableFact]
    public async Task SimplifyName_Location_SimplifiesOnlyThatName()
    {
        const string source = """
            using System.Collections.Generic;
            using System.Text;

            namespace TestApp;

            public class Processor
            {
                public System.Collections.Generic.List<int> Items()
                {
                    return new System.Collections.Generic.List<int>();
                }

                public System.Text.StringBuilder Builder()
                {
                    return new System.Text.StringBuilder();
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new SimplifyNameOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new SimplifyNameParams
        {
            SourceFile = workspace.SourcePath,
            Scope = "location",
            Line = FindLine(source, "System.Text.StringBuilder Builder")
        });

        Assert.True(result.Success);
        Assert.Equal(1, result.SimplificationsApplied);
        Assert.Equal("location", result.Scope);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("System.Collections.Generic.List", updated, StringComparison.Ordinal);
        Assert.DoesNotContain("System.Text.StringBuilder Builder", updated, StringComparison.Ordinal);
        Assert.Contains("StringBuilder Builder", updated, StringComparison.Ordinal);
        await AssertCompilesAsync(workspace);
    }

    [SkippableFact]
    public async Task SimplifyName_Location_NoName_Throws()
    {
        const string source = """
            using System.Collections.Generic;

            namespace TestApp;

            public class Processor
            {
                public List<int> Items() => new List<int>();
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new SimplifyNameOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new SimplifyNameParams
            {
                SourceFile = workspace.SourcePath,
                Scope = "location",
                Line = FindLine(source, "public class Processor")
            }));

        Assert.Equal(ErrorCodes.NoSimplifiableNames, ex.ErrorCode);
    }

    [SkippableFact]
    public async Task SimplifyName_GenericTypeQualification_Shortens()
    {
        const string source = """
            using System.Collections.Generic;

            namespace TestApp;

            public class Processor
            {
                public System.Collections.Generic.Dictionary<string, int> Map()
                {
                    return new System.Collections.Generic.Dictionary<string, int>();
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new SimplifyNameOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new SimplifyNameParams
        {
            SourceFile = workspace.SourcePath
        });

        Assert.True(result.Success);
        Assert.True(result.SimplificationsApplied >= 1);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.DoesNotContain("System.Collections.Generic.Dictionary", updated, StringComparison.Ordinal);
        Assert.Contains("Dictionary<string, int>", updated, StringComparison.Ordinal);
        await AssertCompilesAsync(workspace);
    }

    [SkippableFact]
    public async Task SimplifyName_NestedTypeQualification_Shortens()
    {
        const string source = """
            namespace TestApp;

            public class Outer
            {
                public class Inner
                {
                    public int Value { get; set; }
                }
            }

            public class Consumer
            {
                public TestApp.Outer.Inner Create()
                {
                    return new TestApp.Outer.Inner();
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new SimplifyNameOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new SimplifyNameParams
        {
            SourceFile = workspace.SourcePath
        });

        Assert.True(result.Success);
        Assert.True(result.SimplificationsApplied >= 1);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.DoesNotContain("TestApp.Outer.Inner", updated, StringComparison.Ordinal);
        Assert.Contains("Inner", updated, StringComparison.Ordinal);
        await AssertCompilesAsync(workspace);
    }

    [SkippableFact]
    public async Task SimplifyName_GlobalAlias_Required_IsLeftAlone()
    {
        const string source = """
            using System.Text;

            namespace TestApp;

            public class StringBuilder
            {
            }

            public class Processor
            {
                public global::System.Text.StringBuilder Builder()
                {
                    return new global::System.Text.StringBuilder();
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new SimplifyNameOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        try
        {
            var result = await operation.ExecuteAsync(new SimplifyNameParams
            {
                SourceFile = workspace.SourcePath
            });

            var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
            Assert.DoesNotContain("public StringBuilder Builder", updated, StringComparison.Ordinal);
            Assert.Contains("System.Text.StringBuilder", updated, StringComparison.Ordinal);
            if (result.Success && result.SimplificationsApplied > 0)
                await AssertCompilesAsync(workspace);
        }
        catch (RefactoringException ex)
        {
            Assert.Equal(ErrorCodes.NoSimplifiableNames, ex.ErrorCode);
            Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
        }
    }

    [SkippableFact]
    public async Task SimplifyName_Nameof_PreservesMeaning()
    {
        const string source = """
            using System;

            namespace TestApp;

            public class Processor
            {
                public string Name() => nameof(System.String);
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new SimplifyNameOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new SimplifyNameParams
        {
            SourceFile = workspace.SourcePath
        });

        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        if (result.Success && result.SimplificationsApplied > 0)
        {
            Assert.DoesNotContain("nameof(System.String)", updated, StringComparison.Ordinal);
            Assert.Contains("nameof(", updated, StringComparison.Ordinal);
        }

        await AssertCompilesAsync(workspace);
    }

    [SkippableFact]
    public async Task SimplifyName_SimplifierOnly_PredefinedTypeWithoutUsing()
    {
        const string source = """
            namespace TestApp;

            public class Processor
            {
                public System.Int32 Count() => 0;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, implicitUsings: false);
        var document = workspace.Context.GetDocumentByPath(workspace.SourcePath);
        Assert.NotNull(document);
        var model = await document.GetSemanticModelAsync();
        Assert.NotNull(model);
        var root = await document.GetSyntaxRootAsync();
        Assert.NotNull(root);
        var qualified = SimplifyNameOperation.CollectQualifiedNameNodes(root).Single();
        var fallbackCan = SimplifyNameOperation.TryGetSafeReplacement(qualified, model, out var fallback);
        Assert.False(
            fallbackCan,
            "Fallback must not be able to shorten System.Int32 without using System. Got: "
            + fallback?.ToFullString());

        var operation = new SimplifyNameOperation(workspace.Context);
        var result = await operation.ExecuteAsync(new SimplifyNameParams
        {
            SourceFile = workspace.SourcePath
        });

        Assert.True(result.Success);
        Assert.True(result.SimplificationsApplied >= 1);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.DoesNotContain("System.Int32", updated, StringComparison.Ordinal);
        Assert.Contains("int", updated, StringComparison.Ordinal);
        await AssertCompilesAsync(workspace);
    }

    [SkippableFact]
    public async Task SimplifyName_RetainedInnerComment_Survives()
    {
        const string source = """
            using System.Collections.Generic;

            namespace TestApp;

            public class Processor
            {
                public System.Collections.Generic./* type */List<int> Items()
                {
                    return new System.Collections.Generic.List<int>();
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new SimplifyNameOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new SimplifyNameParams
        {
            SourceFile = workspace.SourcePath,
            Scope = "location",
            Line = FindLine(source, "/* type */")
        });

        Assert.True(result.Success);
        Assert.True(result.SimplificationsApplied >= 1);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("/* type */", updated, StringComparison.Ordinal);
        Assert.DoesNotContain("System.Collections.Generic./* type */List", updated, StringComparison.Ordinal);
        Assert.Contains("List<int>", updated, StringComparison.Ordinal);
        await AssertCompilesAsync(workspace);
    }

    [SkippableFact]
    public async Task SimplifyName_CurrentNamespace_Shortens()
    {
        const string source = """
            namespace TestApp.Services;

            public class Worker
            {
            }

            public class Consumer
            {
                public TestApp.Services.Worker Create() => new TestApp.Services.Worker();
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new SimplifyNameOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new SimplifyNameParams
        {
            SourceFile = workspace.SourcePath
        });

        Assert.True(result.Success);
        Assert.True(result.SimplificationsApplied >= 1);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.DoesNotContain("TestApp.Services.Worker", updated, StringComparison.Ordinal);
        Assert.Contains("Worker", updated, StringComparison.Ordinal);
        await AssertCompilesAsync(workspace);
    }

    #endregion

    #region AllFiles

    [SkippableFact]
    public async Task SimplifyName_AllFilesFalse_SimplifiesOnlySpecifiedFile()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("QualifiedA.cs", QualifiedA),
            ("QualifiedB.cs", QualifiedB),
            ("AlreadySimple.cs", AlreadySimple));
        var operation = new SimplifyNameOperation(workspace.Context);
        var beforeB = await File.ReadAllTextAsync(workspace.SourcePaths["QualifiedB.cs"]);
        var beforeSimple = await File.ReadAllTextAsync(workspace.SourcePaths["AlreadySimple.cs"]);

        var result = await operation.ExecuteAsync(new SimplifyNameParams
        {
            SourceFile = workspace.SourcePaths["QualifiedA.cs"],
            AllFiles = false
        });

        Assert.True(result.Success);
        Assert.False(result.Preview);
        var updatedA = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePaths["QualifiedA.cs"]));
        Assert.DoesNotContain("System.Collections.Generic.List", updatedA, StringComparison.Ordinal);
        Assert.Contains("List<int>", updatedA, StringComparison.Ordinal);
        Assert.Equal(beforeB, await File.ReadAllTextAsync(workspace.SourcePaths["QualifiedB.cs"]));
        Assert.Equal(beforeSimple, await File.ReadAllTextAsync(workspace.SourcePaths["AlreadySimple.cs"]));
        Assert.Single(result.Changes!.FilesModified);
        Assert.Contains(result.Changes.FilesModified, p => PathEquals(p, workspace.SourcePaths["QualifiedA.cs"]));
    }

    [SkippableFact]
    public async Task SimplifyName_AllFilesTrue_WithoutSourceFile_SimplifiesMultipleFiles_LeavesAlreadySimpleUntouched()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("QualifiedA.cs", QualifiedA),
            ("QualifiedB.cs", QualifiedB),
            ("AlreadySimple.cs", AlreadySimple));
        var operation = new SimplifyNameOperation(workspace.Context);
        var beforeSimple = await File.ReadAllTextAsync(workspace.SourcePaths["AlreadySimple.cs"]);

        var result = await operation.ExecuteAsync(new SimplifyNameParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        Assert.False(result.Preview);
        var updatedA = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePaths["QualifiedA.cs"]));
        var updatedB = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePaths["QualifiedB.cs"]));
        Assert.DoesNotContain("System.Collections.Generic.List", updatedA, StringComparison.Ordinal);
        Assert.Contains("List<int>", updatedA, StringComparison.Ordinal);
        Assert.DoesNotContain("System.Text.StringBuilder", updatedB, StringComparison.Ordinal);
        Assert.Contains("StringBuilder", updatedB, StringComparison.Ordinal);
        Assert.Equal(beforeSimple, await File.ReadAllTextAsync(workspace.SourcePaths["AlreadySimple.cs"]));
        Assert.Equal(2, result.Changes!.FilesModified.Count);
        Assert.Contains(result.Changes.FilesModified, p => PathEquals(p, workspace.SourcePaths["QualifiedA.cs"]));
        Assert.Contains(result.Changes.FilesModified, p => PathEquals(p, workspace.SourcePaths["QualifiedB.cs"]));
        Assert.DoesNotContain(result.Changes.FilesModified, p => PathEquals(p, workspace.SourcePaths["AlreadySimple.cs"]));
    }

    [SkippableFact]
    public async Task SimplifyName_AllFilesTrue_EveryFileAlreadySimple_SucceedsWithEmptyChanges()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("AlreadySimple.cs", AlreadySimple),
            ("AlreadySimpleB.cs", AlreadySimpleB));
        var operation = new SimplifyNameOperation(workspace.Context);
        var beforeA = await File.ReadAllTextAsync(workspace.SourcePaths["AlreadySimple.cs"]);
        var beforeB = await File.ReadAllTextAsync(workspace.SourcePaths["AlreadySimpleB.cs"]);

        var result = await operation.ExecuteAsync(new SimplifyNameParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        Assert.False(result.Preview);
        Assert.NotNull(result.Changes);
        Assert.Empty(result.Changes.FilesModified);
        Assert.Equal(beforeA, await File.ReadAllTextAsync(workspace.SourcePaths["AlreadySimple.cs"]));
        Assert.Equal(beforeB, await File.ReadAllTextAsync(workspace.SourcePaths["AlreadySimpleB.cs"]));
    }

    [SkippableFact]
    public async Task SimplifyName_AllFilesFalse_WithoutSourceFile_MissingRequiredParam()
    {
        await using var workspace = await TempWorkspace.CreateAsync(QualifiedA);
        var operation = new SimplifyNameOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new SimplifyNameParams
            {
                AllFiles = false
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [SkippableFact]
    public async Task SimplifyName_AllFilesTrue_WithLocationScope_Rejects()
    {
        await using var workspace = await TempWorkspace.CreateAsync(QualifiedA);
        var operation = new SimplifyNameOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new SimplifyNameParams
            {
                AllFiles = true,
                Scope = "location",
                Line = 1
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("location", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task SimplifyName_AllFilesTrue_AmbiguousFile_ReportsSkipsWithoutWritingThatFile()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("QualifiedA.cs", QualifiedA),
            ("Ambiguous.cs", AllAmbiguous));
        var operation = new SimplifyNameOperation(workspace.Context);
        var beforeAmbiguous = await File.ReadAllTextAsync(workspace.SourcePaths["Ambiguous.cs"]);

        var result = await operation.ExecuteAsync(new SimplifyNameParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        Assert.False(result.Preview);
        var updatedA = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePaths["QualifiedA.cs"]));
        Assert.DoesNotContain("System.Collections.Generic.List", updatedA, StringComparison.Ordinal);
        Assert.Equal(beforeAmbiguous, await File.ReadAllTextAsync(workspace.SourcePaths["Ambiguous.cs"]));
        Assert.Contains(result.Changes!.FilesModified, p => PathEquals(p, workspace.SourcePaths["QualifiedA.cs"]));
        Assert.DoesNotContain(result.Changes.FilesModified, p => PathEquals(p, workspace.SourcePaths["Ambiguous.cs"]));
        Assert.True(result.SimplificationsSkipped >= 1);
        Assert.NotNull(result.SkippedReasons);
        Assert.Contains(result.SkippedReasons, skip =>
            skip.Name.Contains("Widget", StringComparison.Ordinal));
    }

    [SkippableFact]
    public async Task SimplifyName_PreviewAllFiles_DoesNotWriteFiles()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("QualifiedA.cs", QualifiedA),
            ("QualifiedB.cs", QualifiedB),
            ("AlreadySimple.cs", AlreadySimple));
        var operation = new SimplifyNameOperation(workspace.Context);
        var beforeA = await File.ReadAllTextAsync(workspace.SourcePaths["QualifiedA.cs"]);
        var beforeB = await File.ReadAllTextAsync(workspace.SourcePaths["QualifiedB.cs"]);
        var beforeSimple = await File.ReadAllTextAsync(workspace.SourcePaths["AlreadySimple.cs"]);

        var result = await operation.ExecuteAsync(new SimplifyNameParams
        {
            AllFiles = true,
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.Equal(2, result.PendingChanges.Count);
        Assert.Contains(result.PendingChanges, c => PathEquals(c.File, workspace.SourcePaths["QualifiedA.cs"]));
        Assert.Contains(result.PendingChanges, c => PathEquals(c.File, workspace.SourcePaths["QualifiedB.cs"]));
        Assert.DoesNotContain(result.PendingChanges, c => PathEquals(c.File, workspace.SourcePaths["AlreadySimple.cs"]));
        Assert.Equal(beforeA, await File.ReadAllTextAsync(workspace.SourcePaths["QualifiedA.cs"]));
        Assert.Equal(beforeB, await File.ReadAllTextAsync(workspace.SourcePaths["QualifiedB.cs"]));
        Assert.Equal(beforeSimple, await File.ReadAllTextAsync(workspace.SourcePaths["AlreadySimple.cs"]));
    }

    #endregion

    #region Helpers

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

    private const string QualifiedA = """
        using System.Collections.Generic;

        namespace TestApp;

        public class QualifiedA
        {
            public System.Collections.Generic.List<int> Items()
            {
                return new System.Collections.Generic.List<int>();
            }
        }
        """;

    private const string QualifiedB = """
        using System.Text;

        namespace TestApp;

        public class QualifiedB
        {
            public System.Text.StringBuilder Builder()
            {
                return new System.Text.StringBuilder();
            }
        }
        """;

    private const string AlreadySimple = """
        using System.Collections.Generic;

        namespace TestApp;

        public class AlreadySimple
        {
            public List<int> Items() => new List<int>();
        }
        """;

    private const string AllAmbiguous = """
        namespace Other
        {
            public class Widget
            {
            }
        }

        namespace TestApp
        {
            public class Widget
            {
            }

            public class AmbiguousProcessor
            {
                public Other.Widget Create()
                {
                    return new Other.Widget();
                }
            }
        }
        """;

    private const string AlreadySimpleB = """
        using System.Text;

        namespace TestApp;

        public class AlreadySimpleB
        {
            public StringBuilder Builder() => new StringBuilder();
        }
        """;

    private static bool PathEquals(string left, string right) =>
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

    private static string AbsoluteTestPath() =>
        Path.Combine(Path.GetTempPath(), "RoslynMcpSimplifyNameMissing.cs");

    private static string NormalizeNewlines(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal);

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

    private static int ColumnOnLine(string source, int index)
    {
        var lineStart = source.LastIndexOf('\n', index) + 1;
        return index - lineStart + 1;
    }

    private sealed class TempWorkspace : IAsyncDisposable
    {
        public required string DirectoryPath { get; init; }
        public required string ProjectPath { get; init; }
        public required string SourcePath { get; init; }
        public required IReadOnlyDictionary<string, string> SourcePaths { get; init; }
        public required WorkspaceContext Context { get; init; }

        public static Task<TempWorkspace> CreateAsync(
            string source,
            string fileName = "Types.cs",
            bool implicitUsings = true) =>
            CreateWithFilesAsync(implicitUsings, (fileName, source));

        public static Task<TempWorkspace> CreateWithFilesAsync(params (string FileName, string Source)[] files) =>
            CreateWithFilesAsync(implicitUsings: true, files: files);

        public static async Task<TempWorkspace> CreateWithFilesAsync(
            bool implicitUsings,
            params (string FileName, string Source)[] files)
        {
            Skip.IfNot(ModuleInitializer.MsBuildAvailable, ModuleInitializer.MsBuildError ?? "MSBuild not available");

            var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpSimplifyName_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);

            var projectPath = Path.Combine(directory, "TestApp.csproj");
            var sourcePaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var implicitUsingsValue = implicitUsings ? "enable" : "disable";

            await File.WriteAllTextAsync(projectPath, $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net9.0</TargetFramework>
                    <Nullable>enable</Nullable>
                    <ImplicitUsings>{implicitUsingsValue}</ImplicitUsings>
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

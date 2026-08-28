using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynMcp.Contracts.Enums;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Extract;
using RoslynMcp.Core.Workspace;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Extract;

/// <summary>
/// Operation-level tests for <see cref="ExtractInterfaceOperation"/>, including <c>separateFile</c>.
/// </summary>
public class ExtractInterfaceOperationTests
{
    private const string CalculatorSource = """
        namespace TestApp;

        public class Calculator
        {
            public int Add(int a, int b) => a + b;

            public int Multiply(int a, int b) => a * b;
        }
        """;

    private const string MixedIndexerSource = """
        namespace TestApp;

        public class Lookup
        {
            public int Count { get; set; }

            public string this[int i]
            {
                get => "";
                set { }
            }

            public int Add(int a, int b) => a + b;
        }
        """;

    [SkippableFact]
    public async Task ExtractInterface_Default_WritesInterfaceIntoSourceFile()
    {
        await using var workspace = await TempWorkspace.CreateAsync(CalculatorSource);
        var operation = new ExtractInterfaceOperation(workspace.Context);
        var sibling = Path.GetFullPath(Path.Combine(workspace.DirectoryPath, "ICalculator.cs"));

        var result = await operation.ExecuteAsync(new ExtractInterfaceParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Calculator",
            InterfaceName = "ICalculator"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("interface ICalculator", updated);
        AssertImplementsInterface(updated, "Calculator", "ICalculator");
        Assert.False(File.Exists(sibling));
        Assert.DoesNotContain(sibling, result.Changes!.FilesCreated);
    }

    [SkippableFact]
    public async Task ExtractInterface_SeparateFileFalse_WritesInterfaceIntoSourceFile()
    {
        await using var workspace = await TempWorkspace.CreateAsync(CalculatorSource);
        var operation = new ExtractInterfaceOperation(workspace.Context);
        var sibling = Path.GetFullPath(Path.Combine(workspace.DirectoryPath, "ICalculator.cs"));

        var result = await operation.ExecuteAsync(new ExtractInterfaceParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Calculator",
            InterfaceName = "ICalculator",
            SeparateFile = false
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("interface ICalculator", updated);
        AssertImplementsInterface(updated, "Calculator", "ICalculator");
        Assert.False(File.Exists(sibling));
    }

    [SkippableFact]
    public async Task ExtractInterface_SeparateFileTrue_WritesSiblingFileAndRemovesInterfaceFromSource()
    {
        await using var workspace = await TempWorkspace.CreateAsync(CalculatorSource);
        var operation = new ExtractInterfaceOperation(workspace.Context);
        var sibling = Path.GetFullPath(Path.Combine(workspace.DirectoryPath, "ICalculator.cs"));

        var result = await operation.ExecuteAsync(new ExtractInterfaceParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Calculator",
            InterfaceName = "ICalculator",
            SeparateFile = true
        });

        Assert.True(result.Success);
        Assert.True(File.Exists(sibling));
        Assert.Contains(sibling, result.Changes!.FilesCreated);

        var source = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var interfaceFile = NormalizeNewlines(await File.ReadAllTextAsync(sibling));

        Assert.DoesNotContain("interface ICalculator", source);
        AssertImplementsInterface(source, "Calculator", "ICalculator");
        Assert.Contains("interface ICalculator", interfaceFile);
        Assert.Contains("int Add(int a, int b);", interfaceFile);
        Assert.Contains("int Multiply(int a, int b);", interfaceFile);
    }

    [SkippableFact]
    public async Task ExtractInterface_TargetFileWinsOverSeparateFile()
    {
        await using var workspace = await TempWorkspace.CreateAsync(CalculatorSource);
        var operation = new ExtractInterfaceOperation(workspace.Context);
        var sibling = Path.GetFullPath(Path.Combine(workspace.DirectoryPath, "ICalculator.cs"));
        var explicitTarget = Path.GetFullPath(Path.Combine(workspace.DirectoryPath, "CustomInterface.cs"));

        var result = await operation.ExecuteAsync(new ExtractInterfaceParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Calculator",
            InterfaceName = "ICalculator",
            SeparateFile = true,
            TargetFile = explicitTarget
        });

        Assert.True(result.Success);
        Assert.True(File.Exists(explicitTarget));
        Assert.False(File.Exists(sibling));
        Assert.Contains(explicitTarget, result.Changes!.FilesCreated);
        Assert.DoesNotContain(sibling, result.Changes.FilesCreated);

        var source = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var custom = NormalizeNewlines(await File.ReadAllTextAsync(explicitTarget));

        Assert.DoesNotContain("interface ICalculator", source);
        Assert.Contains("interface ICalculator", custom);
        AssertImplementsInterface(source, "Calculator", "ICalculator");
    }

    [SkippableFact]
    public async Task ExtractInterface_Preview_DoesNotWriteFiles()
    {
        await using var workspace = await TempWorkspace.CreateAsync(CalculatorSource);
        var operation = new ExtractInterfaceOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);
        var sibling = Path.GetFullPath(Path.Combine(workspace.DirectoryPath, "ICalculator.cs"));

        var result = await operation.ExecuteAsync(new ExtractInterfaceParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Calculator",
            InterfaceName = "ICalculator",
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.NotEmpty(result.PendingChanges);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.False(File.Exists(sibling));
    }

    [SkippableFact]
    public async Task ExtractInterface_SeparateFileTrue_Preview_DoesNotWriteFiles()
    {
        await using var workspace = await TempWorkspace.CreateAsync(CalculatorSource);
        var operation = new ExtractInterfaceOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);
        var sibling = Path.GetFullPath(Path.Combine(workspace.DirectoryPath, "ICalculator.cs"));

        var result = await operation.ExecuteAsync(new ExtractInterfaceParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Calculator",
            InterfaceName = "ICalculator",
            SeparateFile = true,
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.NotEmpty(result.PendingChanges);
        Assert.Equal(ChangeKind.Create, result.PendingChanges[0].ChangeType);
        Assert.Equal(sibling, result.PendingChanges[0].File);
        Assert.Contains("ICalculator", result.PendingChanges[0].AfterSnippet);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.False(File.Exists(sibling));
    }

    [SkippableFact]
    public async Task ExtractInterface_SeparateFileTrue_SiblingExists_ThrowsTargetFileExists()
    {
        await using var workspace = await TempWorkspace.CreateAsync(CalculatorSource);
        var sibling = Path.GetFullPath(Path.Combine(workspace.DirectoryPath, "ICalculator.cs"));
        await File.WriteAllTextAsync(sibling, """
            namespace TestApp;

            public interface IExisting
            {
            }
            """);
        var sourceBefore = await File.ReadAllTextAsync(workspace.SourcePath);
        var siblingBefore = await File.ReadAllTextAsync(sibling);
        var operation = new ExtractInterfaceOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ExtractInterfaceParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Calculator",
                InterfaceName = "ICalculator",
                SeparateFile = true
            }));

        Assert.Equal(ErrorCodes.TargetFileExists, ex.ErrorCode);
        Assert.Equal("3019", ex.ErrorCode);
        Assert.Equal(sourceBefore, await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Equal(siblingBefore, await File.ReadAllTextAsync(sibling));
    }

    [SkippableFact]
    public async Task ExtractInterface_Default_PublicIndexer_EmitsLegalIndexerAndCompiles()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MixedIndexerSource, "Lookup.cs");
        var operation = new ExtractInterfaceOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ExtractInterfaceParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Lookup",
            InterfaceName = "ILookup"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var iface = FindType(updated, "ILookup");
        var indexer = Assert.Single(FindIndexers(updated, "ILookup"));
        Assert.Contains("this[int i]", updated);
        Assert.Equal("i", Assert.Single(indexer.ParameterList.Parameters).Identifier.Text);
        Assert.DoesNotContain(iface.Members.OfType<PropertyDeclarationSyntax>(),
            p => p.Identifier.Text.Contains("this", StringComparison.Ordinal));
        Assert.NotNull(FindProperty(updated, "ILookup", "Count"));
        Assert.NotNull(FindMethod(updated, "ILookup", "Add"));
        AssertImplementsInterface(updated, "Lookup", "ILookup");
        AssertCompiles(updated);
    }

    [SkippableTheory]
    [InlineData("this[]")]
    [InlineData("Item")]
    [InlineData("this[int i]")]
    public async Task ExtractInterface_MembersFilter_IndexerAliases_ExtractsOnlyIndexer(string memberName)
    {
        await using var workspace = await TempWorkspace.CreateAsync(MixedIndexerSource, "Lookup.cs");
        var operation = new ExtractInterfaceOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ExtractInterfaceParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Lookup",
            InterfaceName = "ILookup",
            Members = new[] { memberName }
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var iface = FindType(updated, "ILookup");
        Assert.Single(FindIndexers(updated, "ILookup"));
        Assert.Contains("this[int i]", updated);
        Assert.Null(FindProperty(updated, "ILookup", "Count"));
        Assert.Null(FindMethod(updated, "ILookup", "Add"));
        Assert.DoesNotContain(iface.Members.OfType<PropertyDeclarationSyntax>(),
            p => p.Identifier.Text.Contains("this", StringComparison.Ordinal));
        AssertImplementsInterface(updated, "Lookup", "ILookup");
        AssertCompiles(updated);
    }

    [SkippableFact]
    public async Task ExtractInterface_MembersFilter_OrdinaryProperty_DoesNotExtractIndexer()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MixedIndexerSource, "Lookup.cs");
        var operation = new ExtractInterfaceOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ExtractInterfaceParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Lookup",
            InterfaceName = "ILookup",
            Members = new[] { "Count" }
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var property = FindProperty(updated, "ILookup", "Count");
        Assert.NotNull(property);
        Assert.Empty(FindIndexers(updated, "ILookup"));
        Assert.DoesNotContain("this[]", property!.Identifier.Text);
        Assert.Null(FindMethod(updated, "ILookup", "Add"));
        AssertCompiles(updated);
    }

    [SkippableFact]
    public async Task ExtractInterface_GetOnlyIndexer_EmitsGetOnly()
    {
        const string source = """
            namespace TestApp;

            public class Lookup
            {
                public int Count { get; set; }

                public int this[int i] => i;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Lookup.cs");
        var operation = new ExtractInterfaceOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ExtractInterfaceParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Lookup",
            InterfaceName = "ILookup"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var indexer = Assert.Single(FindIndexers(updated, "ILookup"));
        Assert.Contains(indexer.AccessorList!.Accessors, a => a.IsKind(SyntaxKind.GetAccessorDeclaration));
        Assert.DoesNotContain(indexer.AccessorList.Accessors, a => a.IsKind(SyntaxKind.SetAccessorDeclaration));
        Assert.Contains("this[int i]", updated);
        Assert.NotNull(FindProperty(updated, "ILookup", "Count"));
        AssertCompiles(updated);
    }

    [SkippableFact]
    public async Task ExtractInterface_RefIndexer_KeepsRef()
    {
        const string source = """
            namespace TestApp;

            public class Cell
            {
                private int _value;

                public ref int this[int i] => ref _value;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Cell.cs");
        var operation = new ExtractInterfaceOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ExtractInterfaceParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Cell",
            InterfaceName = "ICell"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var indexer = Assert.Single(FindIndexers(updated, "ICell"));
        Assert.IsType<RefTypeSyntax>(indexer.Type);
        Assert.False(((RefTypeSyntax)indexer.Type).ReadOnlyKeyword.IsKind(SyntaxKind.ReadOnlyKeyword));
        Assert.Contains("ref int this[int i]", updated);
        AssertCompiles(updated);
    }

    [SkippableFact]
    public async Task ExtractInterface_RefReadonlyIndexer_KeepsRefReadonly()
    {
        const string source = """
            namespace TestApp;

            public class Origin
            {
                private readonly int _value;

                public ref readonly int this[int i] => ref _value;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Origin.cs");
        var operation = new ExtractInterfaceOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ExtractInterfaceParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Origin",
            InterfaceName = "IOrigin"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var indexer = Assert.Single(FindIndexers(updated, "IOrigin"));
        Assert.IsType<RefTypeSyntax>(indexer.Type);
        Assert.True(((RefTypeSyntax)indexer.Type).ReadOnlyKeyword.IsKind(SyntaxKind.ReadOnlyKeyword));
        Assert.Contains("ref readonly int this[int i]", updated);
        AssertCompiles(updated);
    }

    [SkippableFact]
    public async Task ExtractInterface_Indexer_RefKindParameter_Preserved()
    {
        const string source = """
            namespace TestApp;

            public class Lookup
            {
                public int this[in int i] => i;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Lookup.cs");
        var operation = new ExtractInterfaceOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ExtractInterfaceParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Lookup",
            InterfaceName = "ILookup"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var indexer = Assert.Single(FindIndexers(updated, "ILookup"));
        var parameter = Assert.Single(indexer.ParameterList.Parameters);
        Assert.Contains(parameter.Modifiers, t => t.IsKind(SyntaxKind.InKeyword));
        Assert.Contains("this[in int i]", updated);
        AssertCompiles(updated);
    }

    [SkippableFact]
    public async Task ExtractInterface_PublicIndexer_PrivateSetter_EmitsGetOnlyAndCompiles()
    {
        const string source = """
            namespace TestApp;

            public class Lookup
            {
                public int this[int i] { get => i; private set { } }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Lookup.cs");
        var operation = new ExtractInterfaceOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ExtractInterfaceParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Lookup",
            InterfaceName = "ILookup"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var indexer = Assert.Single(FindIndexers(updated, "ILookup"));
        Assert.Contains(indexer.AccessorList!.Accessors, a => a.IsKind(SyntaxKind.GetAccessorDeclaration));
        Assert.DoesNotContain(indexer.AccessorList.Accessors, a => a.IsKind(SyntaxKind.SetAccessorDeclaration));
        Assert.DoesNotContain(indexer.AccessorList.Accessors, a => a.IsKind(SyntaxKind.InitAccessorDeclaration));
        AssertImplementsInterface(updated, "Lookup", "ILookup");
        AssertCompiles(updated);
    }

    [SkippableFact]
    public async Task ExtractInterface_PublicIndexer_PrivateGetter_EmitsSetOnlyAndCompiles()
    {
        const string source = """
            namespace TestApp;

            public class Lookup
            {
                public int this[int i] { private get => 0; set { } }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Lookup.cs");
        var operation = new ExtractInterfaceOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ExtractInterfaceParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Lookup",
            InterfaceName = "ILookup"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var indexer = Assert.Single(FindIndexers(updated, "ILookup"));
        Assert.DoesNotContain(indexer.AccessorList!.Accessors, a => a.IsKind(SyntaxKind.GetAccessorDeclaration));
        Assert.Contains(indexer.AccessorList.Accessors, a => a.IsKind(SyntaxKind.SetAccessorDeclaration));
        AssertImplementsInterface(updated, "Lookup", "ILookup");
        AssertCompiles(updated);
    }

    [SkippableFact]
    public async Task ExtractInterface_InitOnlyIndexer_EmitsInitAndCompiles()
    {
        const string source = """
            namespace TestApp;

            public class Lookup
            {
                private int _value;

                public int this[int i]
                {
                    get => _value;
                    init => _value = value;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Lookup.cs");
        var operation = new ExtractInterfaceOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ExtractInterfaceParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Lookup",
            InterfaceName = "ILookup"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var indexer = Assert.Single(FindIndexers(updated, "ILookup"));
        Assert.Contains(indexer.AccessorList!.Accessors, a => a.IsKind(SyntaxKind.GetAccessorDeclaration));
        Assert.Contains(indexer.AccessorList.Accessors, a => a.IsKind(SyntaxKind.InitAccessorDeclaration));
        Assert.DoesNotContain(indexer.AccessorList.Accessors, a => a.IsKind(SyntaxKind.SetAccessorDeclaration));
        Assert.Contains("this[int i]", updated);
        Assert.Contains("init;", ExtractMemberText(indexer));
        AssertImplementsInterface(updated, "Lookup", "ILookup");
        AssertCompiles(updated);
    }

    [SkippableFact]
    public async Task ExtractInterface_Indexer_Preview_DescribesIndexerAndWritesNothing()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MixedIndexerSource, "Lookup.cs");
        var operation = new ExtractInterfaceOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);
        var sibling = Path.GetFullPath(Path.Combine(workspace.DirectoryPath, "ILookup.cs"));

        var result = await operation.ExecuteAsync(new ExtractInterfaceParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Lookup",
            InterfaceName = "ILookup",
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.NotEmpty(result.PendingChanges);
        Assert.Contains("this[]", result.PendingChanges[0].Description);
        Assert.Contains("this[int i]", result.PendingChanges[0].AfterSnippet);
        Assert.DoesNotContain("this[]", result.PendingChanges[0].AfterSnippet?.Replace("this[int i]", "", StringComparison.Ordinal) ?? "");
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.False(File.Exists(sibling));
    }

    [SkippableFact]
    public async Task ExtractInterface_MembersFilter_UnknownIndexerAlias_ThrowsMemberNotFound()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MixedIndexerSource, "Lookup.cs");
        var operation = new ExtractInterfaceOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ExtractInterfaceParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Lookup",
                InterfaceName = "ILookup",
                Members = new[] { "DoesNotExist" }
            }));

        Assert.Equal(ErrorCodes.MemberNotFound, ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    private static string NormalizeNewlines(string text) =>
        text.Replace("\r\n", "\n");

    private static TypeDeclarationSyntax FindType(string source, string typeName)
    {
        var type = CSharpSyntaxTree.ParseText(source).GetCompilationUnitRoot().DescendantNodes()
            .OfType<TypeDeclarationSyntax>()
            .FirstOrDefault(t => t.Identifier.Text == typeName);
        Assert.True(type != null, $"Generated source did not contain type '{typeName}':\n{source}");
        return type!;
    }

    private static IReadOnlyList<IndexerDeclarationSyntax> FindIndexers(string source, string typeName) =>
        FindType(source, typeName).Members.OfType<IndexerDeclarationSyntax>().ToList();

    private static PropertyDeclarationSyntax? FindProperty(string source, string typeName, string name) =>
        FindType(source, typeName).Members.OfType<PropertyDeclarationSyntax>()
            .FirstOrDefault(p => p.Identifier.Text == name);

    private static MethodDeclarationSyntax? FindMethod(string source, string typeName, string name) =>
        FindType(source, typeName).Members.OfType<MethodDeclarationSyntax>()
            .FirstOrDefault(m => m.Identifier.Text == name);

    private static string ExtractMemberText(MemberDeclarationSyntax member) =>
        NormalizeNewlines(member.NormalizeWhitespace().ToFullString());

    private static void AssertCompiles(string source)
    {
        var compilation = CSharpCompilation.Create(
                "ExtractInterfaceCompileTest",
                new[]
                {
                    CSharpSyntaxTree.ParseText("global using System;"),
                    CSharpSyntaxTree.ParseText(source)
                },
                new[]
                {
                    MetadataReference.CreateFromFile(typeof(object).Assembly.Location)
                },
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Select(d => d.ToString())
            .ToList();
        Assert.True(errors.Count == 0, "Generated extract_interface members did not compile:\n" + string.Join("\n", errors) + "\n\n" + source);
    }

    /// <summary>
    /// Base-list trivia from <c>WithBaseList</c> may omit spaces; compare a compacted form.
    /// </summary>
    private static void AssertImplementsInterface(string source, string typeName, string interfaceName)
    {
        var compact = new string(source.Where(c => !char.IsWhiteSpace(c)).ToArray());
        Assert.Contains($"class{typeName}:{interfaceName}", compact);
    }

    private sealed class TempWorkspace : IAsyncDisposable
    {
        public required string DirectoryPath { get; init; }
        public required string ProjectPath { get; init; }
        public required string SourcePath { get; init; }
        public required WorkspaceContext Context { get; init; }

        public static async Task<TempWorkspace> CreateAsync(string source, string fileName = "Calculator.cs")
        {
            Skip.IfNot(ModuleInitializer.MsBuildAvailable, ModuleInitializer.MsBuildError ?? "MSBuild not available");

            var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpExtractInterface_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);

            var projectPath = Path.Combine(directory, "TestApp.csproj");
            var sourcePath = Path.Combine(directory, fileName);

            await File.WriteAllTextAsync(projectPath, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net9.0</TargetFramework>
                    <Nullable>enable</Nullable>
                  </PropertyGroup>
                </Project>
                """);
            await File.WriteAllTextAsync(sourcePath, source);

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
}

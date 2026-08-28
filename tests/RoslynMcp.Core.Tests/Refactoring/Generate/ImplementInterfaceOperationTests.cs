using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Generate;
using RoslynMcp.Core.Workspace;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Generate;

/// <summary>
/// Operation-level tests for <see cref="ImplementInterfaceOperation"/>, including
/// indexer stubs via <c>SyntaxGenerationHelper.CreateIndexerStub</c>.
/// </summary>
public class ImplementInterfaceOperationTests
{
    private const string MixedInterfaceSource = """
        namespace TestApp;

        public interface IWidget
        {
            void DoWork();
            int Count { get; set; }
            string this[int i] { get; set; }
            event EventHandler Changed;
        }

        public class Widget : IWidget
        {
        }
        """;

    private const string IndexerOnlySource = """
        namespace TestApp;

        public interface ILookup
        {
            string this[int i] { get; set; }
        }

        public class Lookup : ILookup
        {
        }
        """;

    #region Defaults

    [Fact]
    public void ThrowNotImplemented_DefaultsToTrue()
    {
        var @params = new ImplementInterfaceParams
        {
            SourceFile = AbsoluteTestPath(),
            TypeName = "Lookup",
            InterfaceName = "ILookup"
        };

        Assert.True(@params.ThrowNotImplemented);
        Assert.False(@params.ExplicitImplementation);
        Assert.False(@params.Preview);
    }

    #endregion

    #region Happy Path / Regressions

    [SkippableFact]
    public async Task ImplementInterface_Method_AddsStub()
    {
        const string source = """
            namespace TestApp;

            public interface IWidget
            {
                void DoWork();
            }

            public class Widget : IWidget
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ImplementInterfaceOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ImplementInterfaceParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget",
            InterfaceName = "IWidget"
        });

        Assert.True(result.Success);
        Assert.False(result.Preview);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("void DoWork()", updated);
        Assert.Contains("throw new NotImplementedException()", updated);
        Assert.NotNull(FindMethod(updated, "Widget", "DoWork"));
    }

    [SkippableFact]
    public async Task ImplementInterface_OrdinaryProperty_EmitsPropertyDeclaration()
    {
        const string source = """
            namespace TestApp;

            public interface IWidget
            {
                int Count { get; set; }
            }

            public class Widget : IWidget
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ImplementInterfaceOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ImplementInterfaceParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget",
            InterfaceName = "IWidget"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var property = FindProperty(updated, "Widget", "Count");
        Assert.NotNull(property);
        Assert.Empty(FindIndexers(updated, "Widget"));
        Assert.DoesNotContain("this[]", property!.Identifier.Text);
        Assert.Contains("throw new NotImplementedException()", updated);
    }

    [SkippableFact]
    public async Task ImplementInterface_Event_AddsStub()
    {
        const string source = """
            namespace TestApp;

            public interface IWidget
            {
                event EventHandler Changed;
            }

            public class Widget : IWidget
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ImplementInterfaceOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ImplementInterfaceParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget",
            InterfaceName = "IWidget"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var evt = FindEvent(updated, "Widget", "Changed");
        Assert.NotNull(evt);
        Assert.Contains("add", updated);
        Assert.Contains("remove", updated);
    }

    [SkippableFact]
    public async Task ImplementInterface_ExplicitImplementation_Method()
    {
        const string source = """
            namespace TestApp;

            public interface IWidget
            {
                void DoWork();
            }

            public class Widget : IWidget
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ImplementInterfaceOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ImplementInterfaceParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget",
            InterfaceName = "IWidget",
            ExplicitImplementation = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var method = FindMethod(updated, "Widget", "DoWork");
        Assert.NotNull(method);
        Assert.NotNull(method!.ExplicitInterfaceSpecifier);
        Assert.Contains("IWidget", method.ExplicitInterfaceSpecifier!.Name.ToString());
        Assert.Contains("throw new NotImplementedException()", updated);
    }

    [SkippableFact]
    public async Task ImplementInterface_ThrowNotImplementedFalse_Method_DefaultReturn()
    {
        const string source = """
            namespace TestApp;

            public interface IWidget
            {
                int Size();
            }

            public class Widget : IWidget
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ImplementInterfaceOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ImplementInterfaceParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget",
            InterfaceName = "IWidget",
            ThrowNotImplemented = false
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("int Size()", updated);
        Assert.DoesNotContain("NotImplementedException", updated);
        Assert.Contains("return default(int);", updated);
    }

    #endregion

    #region Indexers

    [SkippableFact]
    public async Task ImplementInterface_Indexer_EmitsIndexerDeclarationNotPropertyNamedThis()
    {
        await using var workspace = await TempWorkspace.CreateAsync(IndexerOnlySource);
        var operation = new ImplementInterfaceOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ImplementInterfaceParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Lookup",
            InterfaceName = "ILookup"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var indexer = Assert.Single(FindIndexers(updated, "Lookup"));
        Assert.Contains("this[int i]", updated);
        Assert.Equal("i", Assert.Single(indexer.ParameterList.Parameters).Identifier.Text);
        Assert.DoesNotContain(FindType(updated, "Lookup").Members.OfType<PropertyDeclarationSyntax>(),
            p => p.Identifier.Text.Contains("this", StringComparison.Ordinal));
    }

    [SkippableFact]
    public async Task ImplementInterface_Indexer_ThrowNotImplementedTrue_UsesThrowBodies()
    {
        await using var workspace = await TempWorkspace.CreateAsync(IndexerOnlySource);
        var operation = new ImplementInterfaceOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ImplementInterfaceParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Lookup",
            InterfaceName = "ILookup",
            ThrowNotImplemented = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var indexer = Assert.Single(FindIndexers(updated, "Lookup"));
        var getter = ExtractAccessor(indexer, SyntaxKind.GetAccessorDeclaration);
        var setter = ExtractAccessor(indexer, SyntaxKind.SetAccessorDeclaration);
        Assert.Contains("throw new NotImplementedException()", getter);
        Assert.Contains("throw new NotImplementedException()", setter);
    }

    [SkippableFact]
    public async Task ImplementInterface_Indexer_DefaultThrowNotImplemented_UsesThrowBodies()
    {
        await using var workspace = await TempWorkspace.CreateAsync(IndexerOnlySource);
        var operation = new ImplementInterfaceOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ImplementInterfaceParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Lookup",
            InterfaceName = "ILookup"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var indexer = Assert.Single(FindIndexers(updated, "Lookup"));
        Assert.Contains("throw new NotImplementedException()", ExtractAccessor(indexer, SyntaxKind.GetAccessorDeclaration));
        Assert.Contains("throw new NotImplementedException()", ExtractAccessor(indexer, SyntaxKind.SetAccessorDeclaration));
    }

    [SkippableFact]
    public async Task ImplementInterface_Indexer_ThrowNotImplementedFalse_DefaultReturnGetterEmptySetter()
    {
        await using var workspace = await TempWorkspace.CreateAsync(IndexerOnlySource);
        var operation = new ImplementInterfaceOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ImplementInterfaceParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Lookup",
            InterfaceName = "ILookup",
            ThrowNotImplemented = false
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var indexer = Assert.Single(FindIndexers(updated, "Lookup"));
        Assert.DoesNotContain("NotImplementedException", updated);
        Assert.Contains("return null;", ExtractAccessor(indexer, SyntaxKind.GetAccessorDeclaration));
        Assert.DoesNotContain("return", ExtractAccessor(indexer, SyntaxKind.SetAccessorDeclaration));
    }

    [SkippableFact]
    public async Task ImplementInterface_Indexer_ExplicitImplementation()
    {
        await using var workspace = await TempWorkspace.CreateAsync(IndexerOnlySource);
        var operation = new ImplementInterfaceOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ImplementInterfaceParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Lookup",
            InterfaceName = "ILookup",
            ExplicitImplementation = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var indexer = Assert.Single(FindIndexers(updated, "Lookup"));
        Assert.NotNull(indexer.ExplicitInterfaceSpecifier);
        Assert.Contains("ILookup", indexer.ExplicitInterfaceSpecifier!.Name.ToString());
        Assert.Contains("this[int i]", updated);
        Assert.Empty(indexer.Modifiers);
        Assert.Contains("throw new NotImplementedException()", ExtractAccessor(indexer, SyntaxKind.GetAccessorDeclaration));
    }

    [SkippableFact]
    public async Task ImplementInterface_Indexer_ExplicitImplementation_ThrowNotImplementedFalse()
    {
        await using var workspace = await TempWorkspace.CreateAsync(IndexerOnlySource);
        var operation = new ImplementInterfaceOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ImplementInterfaceParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Lookup",
            InterfaceName = "ILookup",
            ExplicitImplementation = true,
            ThrowNotImplemented = false
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var indexer = Assert.Single(FindIndexers(updated, "Lookup"));
        Assert.NotNull(indexer.ExplicitInterfaceSpecifier);
        Assert.DoesNotContain("NotImplementedException", updated);
        Assert.Contains("return null;", ExtractAccessor(indexer, SyntaxKind.GetAccessorDeclaration));
        Assert.DoesNotContain("return", ExtractAccessor(indexer, SyntaxKind.SetAccessorDeclaration));
    }

    [SkippableTheory]
    [InlineData("this[]")]
    [InlineData("Item")]
    [InlineData("this[int i]")]
    public async Task ImplementInterface_MembersFilter_IndexerAliases_ImplementsOnlyIndexer(string memberName)
    {
        await using var workspace = await TempWorkspace.CreateAsync(MixedInterfaceSource);
        var operation = new ImplementInterfaceOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ImplementInterfaceParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget",
            InterfaceName = "IWidget",
            Members = new[] { memberName }
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Single(FindIndexers(updated, "Widget"));
        Assert.Contains("this[int i]", updated);
        Assert.Null(FindMethod(updated, "Widget", "DoWork"));
        Assert.Null(FindProperty(updated, "Widget", "Count"));
        Assert.Null(FindEvent(updated, "Widget", "Changed"));
    }

    [SkippableFact]
    public async Task ImplementInterface_MembersFilter_Property_DoesNotImplementIndexer()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MixedInterfaceSource);
        var operation = new ImplementInterfaceOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ImplementInterfaceParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget",
            InterfaceName = "IWidget",
            Members = new[] { "Count" }
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.NotNull(FindProperty(updated, "Widget", "Count"));
        Assert.Empty(FindIndexers(updated, "Widget"));
        Assert.Null(FindMethod(updated, "Widget", "DoWork"));
        Assert.Null(FindEvent(updated, "Widget", "Changed"));
    }

    [SkippableFact]
    public async Task ImplementInterface_Indexer_Preview_WritesNothing()
    {
        await using var workspace = await TempWorkspace.CreateAsync(IndexerOnlySource);
        var operation = new ImplementInterfaceOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var result = await operation.ExecuteAsync(new ImplementInterfaceParams
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
    }

    [SkippableFact]
    public async Task ImplementInterface_MixedMembers_MethodPropertyIndexerEvent()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MixedInterfaceSource);
        var operation = new ImplementInterfaceOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ImplementInterfaceParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget",
            InterfaceName = "IWidget"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.NotNull(FindMethod(updated, "Widget", "DoWork"));
        Assert.NotNull(FindProperty(updated, "Widget", "Count"));
        Assert.Single(FindIndexers(updated, "Widget"));
        Assert.NotNull(FindEvent(updated, "Widget", "Changed"));
        Assert.Contains("this[int i]", updated);
    }

    #endregion

    #region Reject Cases

    [SkippableFact]
    public async Task ImplementInterface_TypeNotFound_Throws()
    {
        await using var workspace = await TempWorkspace.CreateAsync(IndexerOnlySource);
        var operation = new ImplementInterfaceOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ImplementInterfaceParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Missing",
                InterfaceName = "ILookup"
            }));

        Assert.Equal(ErrorCodes.TypeNotFound, ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task ImplementInterface_InterfaceNotFound_Throws()
    {
        await using var workspace = await TempWorkspace.CreateAsync(IndexerOnlySource);
        var operation = new ImplementInterfaceOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ImplementInterfaceParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Lookup",
                InterfaceName = "IMissing"
            }));

        Assert.Equal(ErrorCodes.InterfaceNotFound, ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task ImplementInterface_AlreadyImplemented_Throws()
    {
        const string source = """
            namespace TestApp;

            public interface ILookup
            {
                string this[int i] { get; set; }
            }

            public class Lookup : ILookup
            {
                public string this[int i]
                {
                    get => "";
                    set { }
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ImplementInterfaceOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ImplementInterfaceParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Lookup",
                InterfaceName = "ILookup"
            }));

        Assert.Equal(ErrorCodes.MemberAlreadyImplemented, ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task ImplementInterface_UnknownMemberFilter_ThrowsAlreadyImplemented()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MixedInterfaceSource);
        var operation = new ImplementInterfaceOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ImplementInterfaceParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Widget",
                InterfaceName = "IWidget",
                Members = new[] { "DoesNotExist" }
            }));

        Assert.Equal(ErrorCodes.MemberAlreadyImplemented, ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    #endregion

    #region Helpers

    private static string AbsoluteTestPath() =>
        Path.Combine(Path.GetTempPath(), "RoslynMcpImplementInterfaceMissing.cs");

    private static string NormalizeNewlines(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal);

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

    private static EventDeclarationSyntax? FindEvent(string source, string typeName, string name) =>
        FindType(source, typeName).Members.OfType<EventDeclarationSyntax>()
            .FirstOrDefault(e => e.Identifier.Text == name);

    private static string ExtractAccessor(IndexerDeclarationSyntax indexer, SyntaxKind kind)
    {
        var accessor = indexer.AccessorList?.Accessors.FirstOrDefault(a => a.Kind() == kind);
        Assert.NotNull(accessor);
        return accessor!.ToFullString();
    }

    private sealed class TempWorkspace : IAsyncDisposable
    {
        public required string DirectoryPath { get; init; }
        public required string ProjectPath { get; init; }
        public required string SourcePath { get; init; }
        public required WorkspaceContext Context { get; init; }

        public static async Task<TempWorkspace> CreateAsync(string source, string fileName = "Types.cs")
        {
            Skip.IfNot(ModuleInitializer.MsBuildAvailable, ModuleInitializer.MsBuildError ?? "MSBuild not available");

            var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpImplementInterface_" + Guid.NewGuid().ToString("N"));
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

    #endregion
}

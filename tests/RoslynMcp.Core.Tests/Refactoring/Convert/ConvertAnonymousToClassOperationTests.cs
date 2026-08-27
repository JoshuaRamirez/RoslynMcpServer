using Microsoft.CodeAnalysis;
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

    private static ConvertAnonymousToClassParams ValidParams(
        string? sourceFile = null,
        int line = 7,
        string newTypeName = "Person") => new()
        {
            SourceFile = sourceFile ?? Path.Combine(Path.GetTempPath(), "RoslynMcpConvertAnonMissing.cs"),
            Line = line,
            NewTypeName = newTypeName
        };

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

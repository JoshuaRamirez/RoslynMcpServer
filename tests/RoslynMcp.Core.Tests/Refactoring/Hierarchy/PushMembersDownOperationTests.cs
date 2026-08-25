using Microsoft.CodeAnalysis;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Hierarchy;
using RoslynMcp.Core.Workspace;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Hierarchy;

/// <summary>
/// Operation-level tests for <see cref="PushMembersDownOperation"/>.
/// </summary>
public class PushMembersDownOperationTests
{
    #region Input Validation

    [Fact]
    public void Validate_MissingSourceFile_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            PushMembersDownOperation.Validate(new PushMembersDownParams
            {
                SourceFile = "",
                TypeName = "Animal",
                Members = ["Foo"]
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public void Validate_MissingTypeName_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            PushMembersDownOperation.Validate(new PushMembersDownParams
            {
                SourceFile = AbsoluteTestPath(),
                TypeName = "",
                Members = ["Foo"]
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public void Validate_MissingMembers_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            PushMembersDownOperation.Validate(new PushMembersDownParams
            {
                SourceFile = AbsoluteTestPath(),
                TypeName = "Animal",
                Members = []
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public void Validate_RelativePath_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            PushMembersDownOperation.Validate(new PushMembersDownParams
            {
                SourceFile = "Types.cs",
                TypeName = "Animal",
                Members = ["Foo"]
            }));

        Assert.Equal(ErrorCodes.InvalidSourcePath, ex.ErrorCode);
    }

    [Fact]
    public void Validate_MissingFile_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            PushMembersDownOperation.Validate(new PushMembersDownParams
            {
                SourceFile = AbsoluteTestPath(),
                TypeName = "Animal",
                Members = ["Foo"]
            }));

        Assert.Equal(ErrorCodes.SourceFileNotFound, ex.ErrorCode);
    }

    #endregion

    #region P0 Happy Path

    [SkippableFact]
    public async Task PushMembersDown_MethodToAllDerived_MovesMember()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
                public int Speak()
                {
                    return 1;
                }
            }

            public class Dog : Animal
            {
            }

            public class Cat : Animal
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PushMembersDownOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PushMembersDownParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Animal",
            Members = ["Speak"]
        });

        Assert.True(result.Success);
        Assert.False(result.Preview);

        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        var animal = ExtractTypeBody(text, "Animal");
        var dog = ExtractTypeBody(text, "Dog");
        var cat = ExtractTypeBody(text, "Cat");
        Assert.DoesNotContain("Speak", animal);
        Assert.Contains("Speak", dog);
        Assert.Contains("return 1", dog);
        Assert.Contains("Speak", cat);
        Assert.Contains("return 1", cat);
        Assert.DoesNotContain("virtual", dog);
    }

    [SkippableFact]
    public async Task PushMembersDown_PropertyToAllDerived_MovesProperty()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
                public string Name { get; set; } = "";
            }

            public class Dog : Animal
            {
            }

            public class Cat : Animal
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PushMembersDownOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PushMembersDownParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Animal",
            Members = ["Name"]
        });

        Assert.True(result.Success);

        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.DoesNotContain("Name", ExtractTypeBody(text, "Animal"));
        Assert.Contains("Name", ExtractTypeBody(text, "Dog"));
        Assert.Contains("Name", ExtractTypeBody(text, "Cat"));
    }

    [SkippableFact]
    public async Task PushMembersDown_MultipleMembers_MovesAll()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
                public string Name { get; set; } = "";

                public int Speak()
                {
                    return 1;
                }
            }

            public class Dog : Animal
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PushMembersDownOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PushMembersDownParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Animal",
            Members = ["Name", "Speak"]
        });

        Assert.True(result.Success);

        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        var animal = ExtractTypeBody(text, "Animal");
        var dog = ExtractTypeBody(text, "Dog");
        Assert.DoesNotContain("Name", animal);
        Assert.DoesNotContain("Speak", animal);
        Assert.Contains("Name", dog);
        Assert.Contains("Speak", dog);
    }

    [SkippableFact]
    public async Task PushMembersDown_NamedSubset_PushesOnlySpecifiedDerived()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
                public int Speak()
                {
                    return 1;
                }
            }

            public class Dog : Animal
            {
            }

            public class Cat : Animal
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PushMembersDownOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PushMembersDownParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Animal",
            Members = ["Speak"],
            TargetDerivedTypes = ["Dog"]
        });

        Assert.True(result.Success);

        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.DoesNotContain("Speak", ExtractTypeBody(text, "Animal"));
        Assert.Contains("Speak", ExtractTypeBody(text, "Dog"));
        Assert.DoesNotContain("Speak", ExtractTypeBody(text, "Cat"));
    }

    [SkippableFact]
    public async Task PushMembersDown_LeaveAbstract_KeepsAbstractOnBase()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
                public int Speak()
                {
                    return 1;
                }
            }

            public class Dog : Animal
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PushMembersDownOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PushMembersDownParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Animal",
            Members = ["Speak"],
            LeaveAbstract = true
        });

        Assert.True(result.Success);

        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        var animal = ExtractTypeBody(text, "Animal");
        var dog = ExtractTypeBody(text, "Dog");
        Assert.Contains("abstract", text);
        Assert.Contains("abstract", animal);
        Assert.Contains("Speak", animal);
        Assert.DoesNotContain("return 1", animal);
        Assert.Contains("override", dog);
        Assert.Contains("return 1", dog);
    }

    [SkippableFact]
    public async Task PushMembersDown_Preview_ReturnsChangesAndWritesNothing()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
                public int Speak()
                {
                    return 1;
                }
            }

            public class Dog : Animal
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var original = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new PushMembersDownOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PushMembersDownParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Animal",
            Members = ["Speak"],
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.NotEmpty(result.PendingChanges);
        Assert.Contains(result.PendingChanges, c => c.AfterSnippet != null && c.AfterSnippet.Contains("Speak"));

        var after = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Equal(original, after);
    }

    [SkippableFact]
    public async Task PushMembersDown_DefaultInterfaceMethod_CopiesToImplementers()
    {
        const string source = """
            namespace TestApp;

            public interface IAnimal
            {
                int Speak()
                {
                    return 1;
                }
            }

            public class Dog : IAnimal
            {
            }

            public class Cat : IAnimal
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PushMembersDownOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PushMembersDownParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "IAnimal",
            Members = ["Speak"]
        });

        Assert.True(result.Success);

        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("Speak", ExtractTypeBody(text, "IAnimal"));
        Assert.Contains("Speak", ExtractTypeBody(text, "Dog"));
        Assert.Contains("Speak", ExtractTypeBody(text, "Cat"));
    }

    #endregion

    #region P0 Rejects

    [SkippableFact]
    public async Task PushMembersDown_NoDerived_Throws()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
                public int Speak()
                {
                    return 1;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PushMembersDownOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new PushMembersDownParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Animal",
                Members = ["Speak"]
            }));

        Assert.Equal(ErrorCodes.DerivedClassesNotFound, ex.ErrorCode);
    }

    [SkippableFact]
    public async Task PushMembersDown_MissingNamedDerived_Throws()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
                public int Speak()
                {
                    return 1;
                }
            }

            public class Dog : Animal
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PushMembersDownOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new PushMembersDownParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Animal",
                Members = ["Speak"],
                TargetDerivedTypes = ["Bird"]
            }));

        Assert.Equal(ErrorCodes.TypeNotFound, ex.ErrorCode);
    }

    [SkippableFact]
    public async Task PushMembersDown_NameConflict_Throws()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
                public int Speak()
                {
                    return 0;
                }
            }

            public class Dog : Animal
            {
                public new int Speak()
                {
                    return 1;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PushMembersDownOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new PushMembersDownParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Animal",
                Members = ["Speak"]
            }));

        Assert.Equal(ErrorCodes.ConflictsWithExistingMember, ex.ErrorCode);
    }

    [SkippableFact]
    public async Task PushMembersDown_SignatureConflict_Throws()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
                public void Log(string message)
                {
                }
            }

            public class Dog : Animal
            {
                public void Log(string message)
                {
                    System.Console.WriteLine(message);
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PushMembersDownOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new PushMembersDownParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Animal",
                Members = ["Log"]
            }));

        Assert.Equal(ErrorCodes.ConflictsWithExistingMember, ex.ErrorCode);
    }

    [SkippableFact]
    public async Task PushMembersDown_InterfaceImplementation_Throws()
    {
        const string source = """
            namespace TestApp;

            public interface IAnimal
            {
                int Speak();
            }

            public class Animal : IAnimal
            {
                public int Speak()
                {
                    return 1;
                }
            }

            public class Dog : Animal
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PushMembersDownOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new PushMembersDownParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Animal",
                Members = ["Speak"]
            }));

        Assert.Equal(ErrorCodes.MemberNotInterfaceCompatible, ex.ErrorCode);
    }

    [SkippableFact]
    public async Task PushMembersDown_StaticFieldToDerivedInterface_Throws()
    {
        const string source = """
            namespace TestApp;

            public interface IAnimal
            {
                public static int Count = 1;
            }

            public interface IDog : IAnimal
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PushMembersDownOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new PushMembersDownParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "IAnimal",
                Members = ["Count"]
            }));

        Assert.Equal(ErrorCodes.MemberNotInterfaceCompatible, ex.ErrorCode);
    }

    [SkippableFact]
    public async Task PushMembersDown_ExternalDerived_Throws()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
                public int Speak()
                {
                    return 1;
                }
            }

            public class Dog : Animal
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var compilation = await workspace.Context.Solution.Projects.First().GetCompilationAsync();
        Assert.NotNull(compilation);
        var exception = compilation.GetTypeByMetadataName("System.Exception");
        Assert.NotNull(exception);

        var ex = Assert.Throws<RefactoringException>(() =>
            PushMembersDownOperation.ValidateDerivedIsEditable(exception));

        Assert.Equal(ErrorCodes.DerivedClassNotEditable, ex.ErrorCode);
    }

    [SkippableFact]
    public async Task PushMembersDown_MemberNotFound_Throws()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
                public int Speak()
                {
                    return 1;
                }
            }

            public class Dog : Animal
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PushMembersDownOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new PushMembersDownParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Animal",
                Members = ["Missing"]
            }));

        Assert.Equal(ErrorCodes.MemberNotFound, ex.ErrorCode);
    }

    #endregion

    #region Helpers

    private static string AbsoluteTestPath() =>
        OperatingSystem.IsWindows() ? @"C:\test\file.cs" : "/test/file.cs";

    private static string NormalizeNewlines(string text) =>
        text.Replace("\r\n", "\n");

    private static string ExtractTypeBody(string source, string typeName)
    {
        var normalized = NormalizeNewlines(source);
        var start = normalized.IndexOf("class " + typeName, StringComparison.Ordinal);
        if (start < 0)
            start = normalized.IndexOf("interface " + typeName, StringComparison.Ordinal);
        if (start < 0)
            throw new InvalidOperationException($"Type '{typeName}' not found.");

        var open = normalized.IndexOf('{', start);
        var depth = 0;
        for (var i = open; i < normalized.Length; i++)
        {
            if (normalized[i] == '{') depth++;
            else if (normalized[i] == '}')
            {
                depth--;
                if (depth == 0)
                    return normalized.Substring(open, i - open + 1);
            }
        }

        return normalized[open..];
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

            var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpPushMembersDown_" + Guid.NewGuid().ToString("N"));
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

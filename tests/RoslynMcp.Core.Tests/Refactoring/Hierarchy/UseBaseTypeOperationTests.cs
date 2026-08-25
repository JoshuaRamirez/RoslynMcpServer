using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Hierarchy;
using RoslynMcp.Core.Workspace;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Hierarchy;

/// <summary>
/// Operation-level tests for <see cref="UseBaseTypeOperation"/>.
/// </summary>
public class UseBaseTypeOperationTests
{
    #region Input Validation

    [Fact]
    public void Validate_MissingSourceFile_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            UseBaseTypeOperation.Validate(new UseBaseTypeParams
            {
                SourceFile = "",
                TypeName = "Dog"
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public void Validate_MissingTypeName_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            UseBaseTypeOperation.Validate(new UseBaseTypeParams
            {
                SourceFile = AbsoluteTestPath(),
                TypeName = ""
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public void Validate_RelativePath_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            UseBaseTypeOperation.Validate(new UseBaseTypeParams
            {
                SourceFile = "Types.cs",
                TypeName = "Dog"
            }));

        Assert.Equal(ErrorCodes.InvalidSourcePath, ex.ErrorCode);
    }

    [Fact]
    public void Validate_MissingFile_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            UseBaseTypeOperation.Validate(new UseBaseTypeParams
            {
                SourceFile = AbsoluteTestPath(),
                TypeName = "Dog"
            }));

        Assert.Equal(ErrorCodes.SourceFileNotFound, ex.ErrorCode);
    }

    #endregion

    #region P0 Happy Path

    [SkippableFact]
    public async Task UseBaseType_ParameterUsingOnlyBaseMembers_RewritesToBase()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
                public int Eat()
                {
                    return 1;
                }
            }

            public class Dog : Animal
            {
                public int Bark()
                {
                    return 2;
                }
            }

            public static class Use
            {
                public static int Feed(Dog dog)
                {
                    return dog.Eat();
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new UseBaseTypeOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new UseBaseTypeParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog"
        });

        Assert.True(result.Success);
        var updated = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("Feed(Animal dog)", NormalizeNewlines(updated));
        Assert.DoesNotContain("Feed(Dog dog)", NormalizeNewlines(updated));
        Assert.Contains("class Dog : Animal", updated);
    }

    [SkippableFact]
    public async Task UseBaseType_InterfaceBase_RewritesCompatibleParameter()
    {
        const string source = """
            namespace TestApp;

            public interface IAnimal
            {
                int Eat();
            }

            public class Dog : IAnimal
            {
                public int Eat()
                {
                    return 1;
                }

                public int Bark()
                {
                    return 2;
                }
            }

            public static class Use
            {
                public static int Feed(Dog dog)
                {
                    return dog.Eat();
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new UseBaseTypeOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new UseBaseTypeParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            TargetBaseType = "IAnimal"
        });

        Assert.True(result.Success);
        var updated = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("Feed(IAnimal dog)", NormalizeNewlines(updated));
    }

    [SkippableFact]
    public async Task UseBaseType_MixedUsages_RewritesOnlyCompatibleReferences()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
                public int Eat()
                {
                    return 1;
                }
            }

            public class Dog : Animal
            {
                public int Bark()
                {
                    return 2;
                }
            }

            public static class Use
            {
                public static int Feed(Dog dog)
                {
                    return dog.Eat();
                }

                public static int Speak(Dog dog)
                {
                    return dog.Bark();
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new UseBaseTypeOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new UseBaseTypeParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("Feed(Animal dog)", updated);
        Assert.Contains("Speak(Dog dog)", updated);
    }

    [SkippableFact]
    public async Task UseBaseType_ExplicitTarget_UsesNamedBase()
    {
        const string source = """
            namespace TestApp;

            public class Creature
            {
                public int Live()
                {
                    return 1;
                }
            }

            public class Animal : Creature
            {
                public int Eat()
                {
                    return 2;
                }
            }

            public class Dog : Animal
            {
            }

            public static class Use
            {
                public static int Keep(Dog dog)
                {
                    return dog.Live();
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new UseBaseTypeOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new UseBaseTypeParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            TargetBaseType = "Creature"
        });

        Assert.True(result.Success);
        var updated = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("Keep(Creature dog)", NormalizeNewlines(updated));
    }

    [SkippableFact]
    public async Task UseBaseType_Preview_ReturnsChangesAndWritesNothing()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
                public int Eat()
                {
                    return 1;
                }
            }

            public class Dog : Animal
            {
            }

            public static class Use
            {
                public static int Feed(Dog dog)
                {
                    return dog.Eat();
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var original = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new UseBaseTypeOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new UseBaseTypeParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.NotEmpty(result.PendingChanges);
        Assert.Contains(result.PendingChanges, change =>
            change.AfterSnippet != null && change.AfterSnippet.Contains("Animal"));

        var after = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Equal(original, after);
    }

    [SkippableFact]
    public async Task UseBaseType_LocalVariable_RewritesDeclaration()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
                public int Eat()
                {
                    return 1;
                }
            }

            public class Dog : Animal
            {
            }

            public static class Use
            {
                public static int Feed()
                {
                    Dog dog = new Dog();
                    return dog.Eat();
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new UseBaseTypeOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new UseBaseTypeParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("Animal dog = new Dog();", updated);
    }

    #endregion

    #region P0 Rejects

    [SkippableFact]
    public async Task UseBaseType_NoBase_Throws()
    {
        const string source = """
            namespace TestApp;

            public class Dog
            {
                public int Bark()
                {
                    return 2;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new UseBaseTypeOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new UseBaseTypeParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Dog"
            }));

        Assert.Equal(ErrorCodes.NoCommonBase, ex.ErrorCode);
    }

    [SkippableFact]
    public async Task UseBaseType_MissingNamedBase_Throws()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
            }

            public class Dog : Animal
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new UseBaseTypeOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new UseBaseTypeParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Dog",
                TargetBaseType = "Creature"
            }));

        Assert.Equal(ErrorCodes.BaseClassNotFound, ex.ErrorCode);
    }

    [SkippableFact]
    public async Task UseBaseType_NoEligibleReferences_Throws()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
                public int Eat()
                {
                    return 1;
                }
            }

            public class Dog : Animal
            {
            }

            public static class Use
            {
                public static object Create()
                {
                    return new Dog();
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new UseBaseTypeOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new UseBaseTypeParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Dog"
            }));

        Assert.Equal(ErrorCodes.NoEligibleReferences, ex.ErrorCode);
    }

    [SkippableFact]
    public async Task UseBaseType_BaseCannotSatisfyUsedMembers_Throws()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
                public int Eat()
                {
                    return 1;
                }
            }

            public class Dog : Animal
            {
                public int Bark()
                {
                    return 2;
                }
            }

            public static class Use
            {
                public static int Speak(Dog dog)
                {
                    return dog.Bark();
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new UseBaseTypeOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new UseBaseTypeParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Dog"
            }));

        Assert.Equal(ErrorCodes.BaseCannotSatisfyUsedMembers, ex.ErrorCode);
    }

    [Fact]
    public void UseBaseType_UneditableDocument_Throws()
    {
        var workspace = new AdhocWorkspace();
        var project = workspace.AddProject("P", LanguageNames.CSharp);
        var document = workspace.AddDocument(project.Id, "Generated.cs", SourceText.From("class C {}"));

        var ex = Assert.Throws<RefactoringException>(() =>
            UseBaseTypeOperation.ValidateDocumentIsEditable(document, workspace));

        Assert.Equal(ErrorCodes.DocumentNotEditable, ex.ErrorCode);
    }

    [SkippableFact]
    public async Task UseBaseType_TypeNotFound_Throws()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new UseBaseTypeOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new UseBaseTypeParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Dog"
            }));

        Assert.Equal(ErrorCodes.TypeNotFound, ex.ErrorCode);
    }

    [SkippableFact]
    public async Task UseBaseType_TargetTypedNew_IsNotRewritten()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
                public int Eat()
                {
                    return 1;
                }
            }

            public class Dog : Animal
            {
            }

            public static class Use
            {
                public static int Feed()
                {
                    Dog dog = new();
                    return dog.Eat();
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new UseBaseTypeOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new UseBaseTypeParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Dog"
            }));

        Assert.Equal(ErrorCodes.BaseCannotSatisfyUsedMembers, ex.ErrorCode);
    }

    [SkippableFact]
    public async Task UseBaseType_QualifiedTypeName_SelectsMatchingNamespace()
    {
        const string source = """
            namespace A
            {
                public class Animal
                {
                    public int Eat() => 1;
                }

                public class Dog : Animal
                {
                    public int Bark() => 2;
                }

                public static class Use
                {
                    public static int Feed(Dog dog) => dog.Eat();
                }
            }

            namespace B
            {
                public class Animal
                {
                    public int Eat() => 1;
                }

                public class Dog : Animal
                {
                    public int Bark() => 2;
                }

                public static class Use
                {
                    public static int Feed(Dog dog) => dog.Eat();
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new UseBaseTypeOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new UseBaseTypeParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "B.Dog"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("namespace A", updated);
        Assert.Contains("public static int Feed(Dog dog) => dog.Eat();", updated);
        Assert.Contains("namespace B", updated);
        Assert.Contains("public static int Feed(Animal dog) => dog.Eat();", updated);
    }

    [SkippableFact]
    public async Task UseBaseType_TargetTypedNewReturn_Throws()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
                public int Eat() => 1;
            }

            public class Dog : Animal
            {
            }

            public static class Use
            {
                public static Dog Create() => new();
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new UseBaseTypeOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new UseBaseTypeParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Dog"
            }));

        Assert.Equal(ErrorCodes.BaseCannotSatisfyUsedMembers, ex.ErrorCode);
    }

    [SkippableFact]
    public async Task UseBaseType_ConflictingSimpleName_QualifiesReplacement()
    {
        const string source = """
            namespace Other
            {
                public class Animal
                {
                    public int Eat() => 1;
                }
            }

            namespace TestApp
            {
                public class Animal
                {
                }

                public class Dog : Other.Animal
                {
                }

                public static class Use
                {
                    public static int Feed(Dog dog) => dog.Eat();
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new UseBaseTypeOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new UseBaseTypeParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "TestApp.Dog",
            TargetBaseType = "Animal"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("Feed(Other.Animal dog)", updated);
        Assert.DoesNotContain("Feed(Animal dog)", updated);
    }

    [SkippableFact]
    public async Task UseBaseType_OverrideParameter_IsNotRewritten()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
                public int Eat() => 1;
            }

            public class Dog : Animal
            {
            }

            public abstract class BaseHandler
            {
                public abstract int Feed(Dog dog);
            }

            public class Handler : BaseHandler
            {
                public override int Feed(Dog dog) => dog.Eat();
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new UseBaseTypeOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new UseBaseTypeParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Dog"
            }));

        Assert.Equal(ErrorCodes.BaseCannotSatisfyUsedMembers, ex.ErrorCode);
    }

    [SkippableFact]
    public async Task UseBaseType_GenericBaseMapping_UsesConstructedArguments()
    {
        const string source = """
            using System.Collections.Generic;

            namespace TestApp;

            public class Base<T>
            {
                public T Value { get; set; } = default!;
            }

            public class Dog<T> : Base<List<T>>
            {
            }

            public static class Use
            {
                public static int Count(Dog<int> dog) => dog.Value.Count;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new UseBaseTypeOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new UseBaseTypeParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("Count(Base<List<int>> dog)", updated);
        Assert.DoesNotContain("Count(Base<int> dog)", updated);
    }

    [SkippableFact]
    public async Task UseBaseType_TargetTypedNewAssignment_Throws()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
                public int Eat() => 1;
            }

            public class Dog : Animal
            {
            }

            public static class Use
            {
                public static Dog GetDog() => new Dog();

                public static int Feed()
                {
                    Dog x = GetDog();
                    x = new();
                    return x.Eat();
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new UseBaseTypeOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new UseBaseTypeParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Dog"
            }));

        Assert.Equal(ErrorCodes.BaseCannotSatisfyUsedMembers, ex.ErrorCode);
    }

    [SkippableFact]
    public async Task UseBaseType_StructWithSingleInterface_UsesInterface()
    {
        const string source = """
            namespace TestApp;

            public interface IAnimal
            {
                int Eat();
            }

            public struct Dog : IAnimal
            {
                public int Eat() => 1;
            }

            public static class Use
            {
                public static int Feed(Dog dog) => dog.Eat();
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new UseBaseTypeOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new UseBaseTypeParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("Feed(IAnimal dog)", updated);
    }

    #endregion

    #region Helpers

    private static string AbsoluteTestPath() =>
        OperatingSystem.IsWindows() ? @"C:\test\file.cs" : "/test/file.cs";

    private static string NormalizeNewlines(string text) =>
        text.Replace("\r\n", "\n");

    private sealed class TempWorkspace : IAsyncDisposable
    {
        public required string DirectoryPath { get; init; }
        public required string ProjectPath { get; init; }
        public required string SourcePath { get; init; }
        public required WorkspaceContext Context { get; init; }

        public static async Task<TempWorkspace> CreateAsync(string source, string fileName = "Types.cs")
        {
            Skip.IfNot(ModuleInitializer.MsBuildAvailable, ModuleInitializer.MsBuildError ?? "MSBuild not available");

            var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpUseBaseType_" + Guid.NewGuid().ToString("N"));
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

using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Hierarchy;
using RoslynMcp.Core.Workspace;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Hierarchy;

/// <summary>
/// Operation-level tests for <see cref="PullMembersUpOperation"/>.
/// </summary>
public class PullMembersUpOperationTests
{
    #region Input Validation

    [Fact]
    public void Validate_MissingSourceFile_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            PullMembersUpOperation.Validate(new PullMembersUpParams
            {
                SourceFile = "",
                TypeName = "Derived",
                Members = ["Foo"]
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public void Validate_MissingTypeName_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            PullMembersUpOperation.Validate(new PullMembersUpParams
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
            PullMembersUpOperation.Validate(new PullMembersUpParams
            {
                SourceFile = AbsoluteTestPath(),
                TypeName = "Derived",
                Members = []
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public void Validate_RelativePath_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            PullMembersUpOperation.Validate(new PullMembersUpParams
            {
                SourceFile = "Derived.cs",
                TypeName = "Derived",
                Members = ["Foo"]
            }));

        Assert.Equal(ErrorCodes.InvalidSourcePath, ex.ErrorCode);
    }

    [Fact]
    public void Validate_MissingFile_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            PullMembersUpOperation.Validate(new PullMembersUpParams
            {
                SourceFile = AbsoluteTestPath(),
                TypeName = "Derived",
                Members = ["Foo"]
            }));

        Assert.Equal(ErrorCodes.SourceFileNotFound, ex.ErrorCode);
    }

    #endregion

    #region P0 Happy Path

    [SkippableFact]
    public async Task PullMembersUp_MethodToBaseClass_MovesMemberAndMakesVirtual()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
            }

            public class Dog : Animal
            {
                public int Speak()
                {
                    return 1;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PullMembersUpOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PullMembersUpParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            Members = ["Speak"]
        });

        Assert.True(result.Success);
        Assert.False(result.Preview);

        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        var animal = ExtractTypeBody(text, "Animal");
        var dog = ExtractTypeBody(text, "Dog");
        Assert.Contains("Speak", animal);
        Assert.Contains("virtual", animal);
        Assert.DoesNotContain("Speak", dog);
    }

    [SkippableFact]
    public async Task PullMembersUp_PropertyToBaseClass_MovesProperty()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
            }

            public class Dog : Animal
            {
                public string Name { get; set; } = "";
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PullMembersUpOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PullMembersUpParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            Members = ["Name"]
        });

        Assert.True(result.Success);

        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("Name", ExtractTypeBody(text, "Animal"));
        Assert.DoesNotContain("Name", ExtractTypeBody(text, "Dog"));
    }

    [SkippableFact]
    public async Task PullMembersUp_MultipleMembers_MovesAll()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
            }

            public class Dog : Animal
            {
                public string Name { get; set; } = "";

                public int Speak()
                {
                    return 1;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PullMembersUpOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PullMembersUpParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            Members = ["Name", "Speak"]
        });

        Assert.True(result.Success);

        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        var animal = ExtractTypeBody(text, "Animal");
        var dog = ExtractTypeBody(text, "Dog");
        Assert.Contains("Name", animal);
        Assert.Contains("Speak", animal);
        Assert.DoesNotContain("Name", dog);
        Assert.DoesNotContain("Speak", dog);
    }

    [SkippableFact]
    public async Task PullMembersUp_PrivateMethod_BecomesProtectedVirtual()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
            }

            public class Dog : Animal
            {
                private void Log()
                {
                    System.Console.WriteLine("log");
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PullMembersUpOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PullMembersUpParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            Members = ["Log"]
        });

        Assert.True(result.Success);

        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        var animal = ExtractTypeBody(text, "Animal");
        Assert.Contains("protected", animal);
        Assert.Contains("virtual", animal);
        Assert.Contains("void Log()", animal);
        Assert.DoesNotContain("private", animal);
    }

    [SkippableFact]
    public async Task PullMembersUp_MakeAbstract_LeavesOverrideOnDerived()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
            }

            public class Dog : Animal
            {
                public int Speak()
                {
                    return 1;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PullMembersUpOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PullMembersUpParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            Members = ["Speak"],
            MakeAbstract = true
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
    public async Task PullMembersUp_MethodToInterface_AddsSignatureAndKeepsImplementation()
    {
        const string source = """
            namespace TestApp;

            public interface IAnimal
            {
            }

            public class Dog : IAnimal
            {
                public int Speak()
                {
                    return 1;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PullMembersUpOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PullMembersUpParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            Members = ["Speak"]
        });

        Assert.True(result.Success);

        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        var iface = ExtractTypeBody(text, "IAnimal");
        var dog = ExtractTypeBody(text, "Dog");
        Assert.Contains("Speak", iface);
        Assert.Contains(";", iface);
        Assert.DoesNotContain("return 1", iface);
        Assert.Contains("Speak", dog);
        Assert.Contains("return 1", dog);
    }

    [SkippableFact]
    public async Task PullMembersUp_Preview_ReturnsChangesAndWritesNothing()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
            }

            public class Dog : Animal
            {
                public int Speak()
                {
                    return 1;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var original = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new PullMembersUpOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PullMembersUpParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
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
    public async Task PullMembersUp_ExplicitTarget_UsesNamedBase()
    {
        const string source = """
            namespace TestApp;

            public class Creature
            {
            }

            public class Animal : Creature
            {
            }

            public class Dog : Animal
            {
                public int Speak()
                {
                    return 1;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PullMembersUpOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PullMembersUpParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            Members = ["Speak"],
            TargetBaseType = "Creature"
        });

        Assert.True(result.Success);

        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("Speak", ExtractTypeBody(text, "Creature"));
        Assert.DoesNotContain("Speak", ExtractTypeBody(text, "Animal"));
        Assert.DoesNotContain("Speak", ExtractTypeBody(text, "Dog"));
    }

    #endregion

    #region P0 Rejects

    [SkippableFact]
    public async Task PullMembersUp_NoBase_Throws()
    {
        const string source = """
            namespace TestApp;

            public class Dog
            {
                public int Speak()
                {
                    return 1;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PullMembersUpOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new PullMembersUpParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Dog",
                Members = ["Speak"]
            }));

        Assert.Equal(ErrorCodes.NoCommonBase, ex.ErrorCode);
    }

    [SkippableFact]
    public async Task PullMembersUp_MissingNamedBase_Throws()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
            }

            public class Dog : Animal
            {
                public int Speak()
                {
                    return 1;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PullMembersUpOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new PullMembersUpParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Dog",
                Members = ["Speak"],
                TargetBaseType = "IDisposable"
            }));

        Assert.Equal(ErrorCodes.BaseClassNotFound, ex.ErrorCode);
    }

    [SkippableFact]
    public async Task PullMembersUp_NameConflict_Throws()
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
        var operation = new PullMembersUpOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new PullMembersUpParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Dog",
                Members = ["Speak"]
            }));

        Assert.Equal(ErrorCodes.ConflictsWithExistingMember, ex.ErrorCode);
    }

    [SkippableFact]
    public async Task PullMembersUp_SignatureConflict_Throws()
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
        var operation = new PullMembersUpOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new PullMembersUpParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Dog",
                Members = ["Log"]
            }));

        Assert.Equal(ErrorCodes.ConflictsWithExistingMember, ex.ErrorCode);
    }

    [SkippableFact]
    public async Task PullMembersUp_DependsOnDerivedField_Throws()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
            }

            public class Dog : Animal
            {
                private int _age;

                public int Age()
                {
                    return _age;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PullMembersUpOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new PullMembersUpParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Dog",
                Members = ["Age"]
            }));

        Assert.Equal(ErrorCodes.MemberDependsOnDerived, ex.ErrorCode);
    }

    [SkippableFact]
    public async Task PullMembersUp_FieldToInterface_Throws()
    {
        const string source = """
            namespace TestApp;

            public interface IAnimal
            {
            }

            public class Dog : IAnimal
            {
                public int Age;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PullMembersUpOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new PullMembersUpParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Dog",
                Members = ["Age"]
            }));

        Assert.Equal(ErrorCodes.MemberNotInterfaceCompatible, ex.ErrorCode);
    }

    [SkippableFact]
    public async Task PullMembersUp_PrivateMethodToInterface_Throws()
    {
        const string source = """
            namespace TestApp;

            public interface IAnimal
            {
            }

            public class Dog : IAnimal
            {
                private void Log()
                {
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PullMembersUpOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new PullMembersUpParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Dog",
                Members = ["Log"]
            }));

        Assert.Equal(ErrorCodes.MemberNotInterfaceCompatible, ex.ErrorCode);
    }

    [SkippableFact]
    public async Task PullMembersUp_ExternalBase_Throws()
    {
        const string source = """
            namespace TestApp;

            public class Dog : System.Exception
            {
                public int Speak()
                {
                    return 1;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PullMembersUpOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new PullMembersUpParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Dog",
                Members = ["Speak"]
            }));

        Assert.Equal(ErrorCodes.BaseClassNotEditable, ex.ErrorCode);
    }

    [SkippableFact]
    public async Task PullMembersUp_MemberNotFound_Throws()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
            }

            public class Dog : Animal
            {
                public int Speak()
                {
                    return 1;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PullMembersUpOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new PullMembersUpParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Dog",
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

            var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpPullMembersUp_" + Guid.NewGuid().ToString("N"));
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

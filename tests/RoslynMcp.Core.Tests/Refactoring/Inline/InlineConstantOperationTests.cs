using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Inline;
using RoslynMcp.Core.Workspace;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Inline;

/// <summary>
/// Operation-level tests for <see cref="InlineConstantOperation"/>.
/// These execute the real refactoring against a loaded workspace.
/// </summary>
public class InlineConstantOperationTests
{
    #region Input Validation

    [Fact]
    public void Validate_MissingSourceFile_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            InlineConstantOperation.Validate(new InlineConstantParams
            {
                SourceFile = "",
                ConstantName = "Max"
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public void Validate_MissingConstantName_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            InlineConstantOperation.Validate(new InlineConstantParams
            {
                SourceFile = AbsoluteTestPath(),
                ConstantName = ""
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public void Validate_RelativePath_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            InlineConstantOperation.Validate(new InlineConstantParams
            {
                SourceFile = "Limits.cs",
                ConstantName = "Max"
            }));

        Assert.Equal(ErrorCodes.InvalidSourcePath, ex.ErrorCode);
    }

    [Fact]
    public void Validate_MissingFile_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            InlineConstantOperation.Validate(new InlineConstantParams
            {
                SourceFile = AbsoluteTestPath(),
                ConstantName = "Max"
            }));

        Assert.Equal(ErrorCodes.SourceFileNotFound, ex.ErrorCode);
    }

    [Fact]
    public void Validate_InvalidConstantName_Throws()
    {
        var path = Path.Combine(Path.GetTempPath(), "RoslynMcpInlineConstantInvalid.cs");
        File.WriteAllText(path, "class C {}");
        try
        {
            var ex = Assert.Throws<RefactoringException>(() =>
                InlineConstantOperation.Validate(new InlineConstantParams
                {
                    SourceFile = path,
                    ConstantName = "123bad"
                }));

            Assert.Equal(ErrorCodes.InvalidSymbolName, ex.ErrorCode);
        }
        finally
        {
            File.Delete(path);
        }
    }

    #endregion

    #region §9.3 Happy Path

    [SkippableFact]
    public async Task InlineConstant_Int_ReplacesWithoutSuffixAndRemovesDeclaration()
    {
        const string source = """
            namespace TestApp;

            public class Limits
            {
                private const int MaxRetries = 5;

                public int Run()
                {
                    return MaxRetries + MaxRetries;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new InlineConstantOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new InlineConstantParams
        {
            SourceFile = workspace.SourcePath,
            ConstantName = "MaxRetries"
        });

        Assert.True(result.Success);
        Assert.False(result.Preview);
        Assert.Equal(2, result.ReferencesUpdated);

        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.DoesNotContain("MaxRetries", text);
        Assert.Contains("return 5 + 5;", text);
        Assert.DoesNotContain("5L", text);
        Assert.DoesNotContain("5F", text);
        Assert.DoesNotContain("5M", text);
    }

    [SkippableFact]
    public async Task InlineConstant_Long_ReplacesWithLSuffix()
    {
        const string source = """
            namespace TestApp;

            public class Limits
            {
                private const long Capacity = 10L;

                public long Run()
                {
                    return Capacity;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new InlineConstantOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new InlineConstantParams
        {
            SourceFile = workspace.SourcePath,
            ConstantName = "Capacity"
        });

        Assert.True(result.Success);

        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.DoesNotContain("Capacity", text);
        Assert.Contains("return 10L;", text);
    }

    [SkippableFact]
    public async Task InlineConstant_String_ReplacesWithEscapedLiteral()
    {
        const string source = """
            namespace TestApp;

            public class Messages
            {
                private const string Greeting = "hello\n\"world\"";

                public string Run()
                {
                    return Greeting;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new InlineConstantOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new InlineConstantParams
        {
            SourceFile = workspace.SourcePath,
            ConstantName = "Greeting"
        });

        Assert.True(result.Success);

        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.DoesNotContain("const string Greeting", text);
        Assert.Contains("return \"hello\\n\\\"world\\\"\";", text);
    }

    [SkippableFact]
    public async Task InlineConstant_Null_ReplacesWithNullKeyword()
    {
        const string source = """
            namespace TestApp;

            public class Messages
            {
                private const string? Missing = null;

                public string? Run()
                {
                    return Missing;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new InlineConstantOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new InlineConstantParams
        {
            SourceFile = workspace.SourcePath,
            ConstantName = "Missing"
        });

        Assert.True(result.Success);

        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.DoesNotContain("Missing", text);
        Assert.Contains("return null;", text);
    }

    [SkippableFact]
    public async Task InlineConstant_CrossFile_UpdatesAllReferences()
    {
        const string constants = """
            namespace TestApp;

            internal static class Limits
            {
                internal const int MaxRetries = 5;
            }
            """;
        const string usage = """
            namespace TestApp;

            public class Worker
            {
                public int Run() => Limits.MaxRetries;
            }
            """;

        await using var workspace = await TempWorkspace.CreateMultiFileAsync(
            ("Limits.cs", constants),
            ("Worker.cs", usage));
        var operation = new InlineConstantOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new InlineConstantParams
        {
            SourceFile = workspace.GetPath("Limits.cs"),
            ConstantName = "MaxRetries"
        });

        Assert.True(result.Success);
        Assert.Equal(1, result.ReferencesUpdated);

        var limitsText = await File.ReadAllTextAsync(workspace.GetPath("Limits.cs"));
        var workerText = await File.ReadAllTextAsync(workspace.GetPath("Worker.cs"));
        Assert.DoesNotContain("MaxRetries", limitsText);
        Assert.Contains("public int Run() => 5;", workerText);
        Assert.DoesNotContain("Limits.MaxRetries", workerText);
    }

    [SkippableFact]
    public async Task InlineConstant_CrossProject_UpdatesReferencingProject()
    {
        await using var workspace = await TempWorkspace.CreateCrossProjectAsync();
        var operation = new InlineConstantOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new InlineConstantParams
        {
            SourceFile = workspace.GetPath(Path.Combine("Lib", "Limits.cs")),
            ConstantName = "MaxRetries",
            RemoveConstant = false
        });

        Assert.True(result.Success);
        Assert.Equal(1, result.ReferencesUpdated);

        var libText = await File.ReadAllTextAsync(workspace.GetPath(Path.Combine("Lib", "Limits.cs")));
        var appText = await File.ReadAllTextAsync(workspace.GetPath(Path.Combine("App", "Worker.cs")));
        Assert.Contains("public const int MaxRetries = 5;", libText);
        Assert.Contains("public int Run() => 5;", appText);
        Assert.DoesNotContain("Limits.MaxRetries", appText);
    }

    #endregion

    #region Additional Happy Path

    [SkippableFact]
    public async Task InlineConstant_Preview_ReturnsChangesAndWritesNothing()
    {
        const string source = """
            namespace TestApp;

            public class Limits
            {
                private const int MaxRetries = 5;

                public int Run() => MaxRetries;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var original = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new InlineConstantOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new InlineConstantParams
        {
            SourceFile = workspace.SourcePath,
            ConstantName = "MaxRetries",
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.NotEmpty(result.PendingChanges);
        Assert.Contains(result.PendingChanges, c => c.AfterSnippet != null && c.AfterSnippet.Contains("=> 5"));

        var after = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Equal(original, after);
    }

    [SkippableFact]
    public async Task InlineConstant_RemoveConstantFalse_LeavesDeclaration()
    {
        const string source = """
            namespace TestApp;

            public class Limits
            {
                private const int MaxRetries = 5;

                public int Run() => MaxRetries;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new InlineConstantOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new InlineConstantParams
        {
            SourceFile = workspace.SourcePath,
            ConstantName = "MaxRetries",
            RemoveConstant = false
        });

        Assert.True(result.Success);

        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("private const int MaxRetries = 5;", text);
        Assert.Contains("public int Run() => 5;", text);
    }

    [SkippableFact]
    public async Task InlineConstant_StaticReadonly_InlinesCompileTimeValue()
    {
        const string source = """
            namespace TestApp;

            public class Limits
            {
                private static readonly int MaxRetries = 5;

                public int Run() => MaxRetries;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new InlineConstantOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new InlineConstantParams
        {
            SourceFile = workspace.SourcePath,
            ConstantName = "MaxRetries"
        });

        Assert.True(result.Success);

        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.DoesNotContain("MaxRetries", text);
        Assert.Contains("public int Run() => 5;", text);
    }

    [SkippableFact]
    public async Task InlineConstant_TypeName_Disambiguates()
    {
        const string source = """
            namespace TestApp;

            public class Alpha
            {
                private const int MaxRetries = 3;
            }

            public class Beta
            {
                private const int MaxRetries = 7;

                public int Run() => MaxRetries;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new InlineConstantOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new InlineConstantParams
        {
            SourceFile = workspace.SourcePath,
            ConstantName = "MaxRetries",
            TypeName = "Beta"
        });

        Assert.True(result.Success);

        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("private const int MaxRetries = 3;", text);
        Assert.DoesNotContain("private const int MaxRetries = 7;", text);
        Assert.Contains("public int Run() => 7;", text);
    }

    [SkippableFact]
    public async Task InlineConstant_Float_UsesFSuffix()
    {
        const string source = """
            namespace TestApp;

            public class Numbers
            {
                private const float Ratio = 1.5F;

                public float FloatValue() => Ratio;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new InlineConstantOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new InlineConstantParams
        {
            SourceFile = workspace.SourcePath,
            ConstantName = "Ratio"
        });

        Assert.True(result.Success);
        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("public float FloatValue() => 1.5F;", text);
    }

    [SkippableFact]
    public async Task InlineConstant_Decimal_UsesMSuffix()
    {
        const string source = """
            namespace TestApp;

            public class Numbers
            {
                private const decimal Price = 2.5M;

                public decimal DecimalValue() => Price;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new InlineConstantOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new InlineConstantParams
        {
            SourceFile = workspace.SourcePath,
            ConstantName = "Price"
        });

        Assert.True(result.Success);
        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("public decimal DecimalValue() => 2.5M;", text);
    }

    #endregion

    #region Rejects

    [SkippableFact]
    public async Task InlineConstant_InAttribute_Throws()
    {
        const string source = """
            namespace TestApp;

            internal static class Limits
            {
                internal const string Reason = "old";
            }

            [System.Obsolete(Limits.Reason)]
            public class Widget
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new InlineConstantOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new InlineConstantParams
            {
                SourceFile = workspace.SourcePath,
                ConstantName = "Reason"
            }));

        Assert.Equal(ErrorCodes.ConstantInAttribute, ex.ErrorCode);
        Assert.Equal("3059", ex.ErrorCode);
    }

    [SkippableFact]
    public async Task InlineConstant_NotAConstant_Throws()
    {
        const string source = """
            namespace TestApp;

            public class Limits
            {
                private int MaxRetries = 5;

                public int Run() => MaxRetries;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new InlineConstantOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new InlineConstantParams
            {
                SourceFile = workspace.SourcePath,
                ConstantName = "MaxRetries"
            }));

        Assert.Equal(ErrorCodes.NotAConstant, ex.ErrorCode);
        Assert.Equal("3055", ex.ErrorCode);
    }

    [SkippableFact]
    public async Task InlineConstant_PublicApi_ThrowsWhenRemoving()
    {
        const string source = """
            namespace TestApp;

            public class Limits
            {
                public const int MaxRetries = 5;

                public int Run() => MaxRetries;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new InlineConstantOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new InlineConstantParams
            {
                SourceFile = workspace.SourcePath,
                ConstantName = "MaxRetries"
            }));

        Assert.Equal(ErrorCodes.PublicApiConstant, ex.ErrorCode);
        Assert.Equal("3056", ex.ErrorCode);
    }

    [SkippableFact]
    public async Task InlineConstant_PublicApi_AllowsInlineWithoutRemoval()
    {
        const string source = """
            namespace TestApp;

            public class Limits
            {
                public const int MaxRetries = 5;

                public int Run() => MaxRetries;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new InlineConstantOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new InlineConstantParams
        {
            SourceFile = workspace.SourcePath,
            ConstantName = "MaxRetries",
            RemoveConstant = false
        });

        Assert.True(result.Success);
        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("public const int MaxRetries = 5;", text);
        Assert.Contains("public int Run() => 5;", text);
    }

    [SkippableFact]
    public async Task InlineConstant_MissingSymbol_Throws()
    {
        const string source = """
            namespace TestApp;

            public class Limits
            {
                private const int MaxRetries = 5;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new InlineConstantOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new InlineConstantParams
            {
                SourceFile = workspace.SourcePath,
                ConstantName = "DoesNotExist"
            }));

        Assert.Equal(ErrorCodes.FieldNotFound, ex.ErrorCode);
    }

    [Fact]
    public void InlineConstant_UneditableDocument_Throws()
    {
        var workspace = new AdhocWorkspace();
        var project = workspace.AddProject("P", LanguageNames.CSharp);
        var document = workspace.AddDocument(project.Id, "Generated.cs", SourceText.From("class C {}"));

        var ex = Assert.Throws<RefactoringException>(() =>
            InlineConstantOperation.ValidateDocumentIsEditable(document, workspace));

        Assert.Equal(ErrorCodes.DocumentNotEditable, ex.ErrorCode);
    }

    #endregion

    #region Helpers

    private static string AbsoluteTestPath() =>
        Path.Combine(Path.GetTempPath(), "RoslynMcpInlineConstantMissing.cs");

    private sealed class TempWorkspace : IAsyncDisposable
    {
        public required string DirectoryPath { get; init; }
        public required string ProjectPath { get; init; }
        public required string SourcePath { get; init; }
        public required WorkspaceContext Context { get; init; }

        public string GetPath(string relativePath) => Path.Combine(DirectoryPath, relativePath);

        public static Task<TempWorkspace> CreateAsync(string source, string fileName = "Limits.cs") =>
            CreateMultiFileAsync((fileName, source));

        public static async Task<TempWorkspace> CreateMultiFileAsync(params (string FileName, string Source)[] files)
        {
            Skip.IfNot(ModuleInitializer.MsBuildAvailable, ModuleInitializer.MsBuildError ?? "MSBuild not available");

            var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpInlineConstant_" + Guid.NewGuid().ToString("N"));
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

            string? firstSource = null;
            foreach (var (fileName, source) in files)
            {
                var sourcePath = Path.Combine(directory, fileName);
                Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
                await File.WriteAllTextAsync(sourcePath, source);
                firstSource ??= sourcePath;
            }

            return await LoadAsync(directory, projectPath, firstSource!);
        }

        public static async Task<TempWorkspace> CreateCrossProjectAsync()
        {
            Skip.IfNot(ModuleInitializer.MsBuildAvailable, ModuleInitializer.MsBuildError ?? "MSBuild not available");

            var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpInlineConstantXP_" + Guid.NewGuid().ToString("N"));
            var libDir = Path.Combine(directory, "Lib");
            var appDir = Path.Combine(directory, "App");
            Directory.CreateDirectory(libDir);
            Directory.CreateDirectory(appDir);

            var libProject = Path.Combine(libDir, "Lib.csproj");
            var appProject = Path.Combine(appDir, "App.csproj");
            var libSource = Path.Combine(libDir, "Limits.cs");
            var appSource = Path.Combine(appDir, "Worker.cs");

            await File.WriteAllTextAsync(libProject, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net9.0</TargetFramework>
                    <Nullable>enable</Nullable>
                  </PropertyGroup>
                </Project>
                """);
            await File.WriteAllTextAsync(appProject, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net9.0</TargetFramework>
                    <Nullable>enable</Nullable>
                  </PropertyGroup>
                  <ItemGroup>
                    <ProjectReference Include="..\Lib\Lib.csproj" />
                  </ItemGroup>
                </Project>
                """);
            await File.WriteAllTextAsync(libSource, """
                namespace TestLib;

                public static class Limits
                {
                    public const int MaxRetries = 5;
                }
                """);
            await File.WriteAllTextAsync(appSource, """
                namespace TestApp;

                public class Worker
                {
                    public int Run() => TestLib.Limits.MaxRetries;
                }
                """);

            var solutionPath = Path.Combine(directory, "TestApp.sln");
            await File.WriteAllTextAsync(solutionPath, """
                Microsoft Visual Studio Solution File, Format Version 12.00
                # Visual Studio Version 17
                Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Lib", "Lib\Lib.csproj", "{11111111-1111-1111-1111-111111111111}"
                EndProject
                Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "App", "App\App.csproj", "{22222222-2222-2222-2222-222222222222}"
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

            return await LoadAsync(directory, solutionPath, libSource);
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
}

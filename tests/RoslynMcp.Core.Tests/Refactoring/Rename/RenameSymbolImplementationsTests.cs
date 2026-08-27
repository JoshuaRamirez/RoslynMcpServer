using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring.Rename;
using RoslynMcp.Core.Workspace;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Rename;

/// <summary>
/// Operation-level tests for <see cref="RenameSymbolOperation"/>
/// <c>renameImplementations</c> (UC-R1 BR-R1.4).
/// </summary>
public class RenameSymbolImplementationsTests
{
    private const string InterfaceSource = """
        namespace TestApp;

        public interface IWorker
        {
            void Process();
        }
        """;

    private const string ImplementationSource = """
        namespace TestApp;

        public class Worker : IWorker
        {
            public void Process()
            {
            }
        }

        public class ExplicitWorker : IWorker
        {
            void IWorker.Process()
            {
            }
        }

        public class Consumer
        {
            public void Use(IWorker contract, Worker impl)
            {
                contract.Process();
                impl.Process();
            }
        }
        """;

    [SkippableFact]
    public async Task RenameSymbol_Default_RenamesImplicitAndExplicitImplementations()
    {
        await using var workspace = await TempWorkspace.CreateAsync(
            ("IWorker.cs", InterfaceSource),
            ("Worker.cs", ImplementationSource));
        var operation = new RenameSymbolOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new RenameSymbolParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Process",
            NewName = "Execute"
        });

        Assert.True(result.Success);
        Assert.False(result.Preview);

        var iface = await File.ReadAllTextAsync(workspace.SourcePath);
        var impl = await File.ReadAllTextAsync(workspace.SecondarySourcePath);

        Assert.Contains("void Execute();", iface);
        Assert.DoesNotContain("void Process();", iface);
        Assert.Contains("public void Execute()", impl);
        Assert.DoesNotContain("public void Process()", impl);
        Assert.Contains("void IWorker.Execute()", impl);
        Assert.DoesNotContain("void IWorker.Process()", impl);
        Assert.Contains("contract.Execute();", impl);
        Assert.Contains("impl.Execute();", impl);
        Assert.DoesNotContain("contract.Process();", impl);
        Assert.DoesNotContain("impl.Process();", impl);
    }

    [SkippableFact]
    public async Task RenameSymbol_RenameImplementationsTrue_RenamesImplementations()
    {
        await using var workspace = await TempWorkspace.CreateAsync(
            ("IWorker.cs", InterfaceSource),
            ("Worker.cs", ImplementationSource));
        var operation = new RenameSymbolOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new RenameSymbolParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Process",
            NewName = "Execute",
            RenameImplementations = true
        });

        Assert.True(result.Success);

        var impl = await File.ReadAllTextAsync(workspace.SecondarySourcePath);
        Assert.Contains("public void Execute()", impl);
        Assert.Contains("void IWorker.Execute()", impl);
        Assert.Contains("impl.Execute();", impl);
    }

    [SkippableFact]
    public async Task RenameSymbol_RenameImplementationsFalse_LeavesImplementingMembers()
    {
        await using var workspace = await TempWorkspace.CreateAsync(
            ("IWorker.cs", InterfaceSource),
            ("Worker.cs", ImplementationSource));
        var operation = new RenameSymbolOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new RenameSymbolParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Process",
            NewName = "Execute",
            RenameImplementations = false
        });

        Assert.True(result.Success);
        Assert.False(result.Preview);

        var iface = await File.ReadAllTextAsync(workspace.SourcePath);
        var impl = await File.ReadAllTextAsync(workspace.SecondarySourcePath);

        Assert.Contains("void Execute();", iface);
        Assert.DoesNotContain("void Process();", iface);
        Assert.Contains("public void Process()", impl);
        Assert.DoesNotContain("public void Execute()", impl);
        Assert.Contains("void IWorker.Process()", impl);
        Assert.DoesNotContain("void IWorker.Execute()", impl);
        Assert.Contains("contract.Execute();", impl);
        Assert.Contains("impl.Process();", impl);
        Assert.DoesNotContain("contract.Process();", impl);
        Assert.DoesNotContain("impl.Execute();", impl);
    }

    [SkippableFact]
    public async Task RenameSymbol_RenameImplementationsFalse_PreviewWritesNothing()
    {
        await using var workspace = await TempWorkspace.CreateAsync(
            ("IWorker.cs", InterfaceSource),
            ("Worker.cs", ImplementationSource));
        var originalInterface = await File.ReadAllTextAsync(workspace.SourcePath);
        var originalImplementation = await File.ReadAllTextAsync(workspace.SecondarySourcePath);
        var operation = new RenameSymbolOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new RenameSymbolParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Process",
            NewName = "Execute",
            RenameImplementations = false,
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.NotEmpty(result.PendingChanges);
        Assert.Equal(originalInterface, await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Equal(originalImplementation, await File.ReadAllTextAsync(workspace.SecondarySourcePath));
        Assert.Contains("void Process();", await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("public void Process()", await File.ReadAllTextAsync(workspace.SecondarySourcePath));
        Assert.Contains("void IWorker.Process()", await File.ReadAllTextAsync(workspace.SecondarySourcePath));
    }

    [SkippableFact]
    public async Task RenameSymbol_RenameImplementationsTrue_PreviewWritesNothing()
    {
        await using var workspace = await TempWorkspace.CreateAsync(
            ("IWorker.cs", InterfaceSource),
            ("Worker.cs", ImplementationSource));
        var originalInterface = await File.ReadAllTextAsync(workspace.SourcePath);
        var originalImplementation = await File.ReadAllTextAsync(workspace.SecondarySourcePath);
        var operation = new RenameSymbolOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new RenameSymbolParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Process",
            NewName = "Execute",
            RenameImplementations = true,
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.Equal(originalInterface, await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Equal(originalImplementation, await File.ReadAllTextAsync(workspace.SecondarySourcePath));
    }

    [SkippableFact]
    public async Task RenameSymbol_Property_RenameImplementationsFalse_LeavesImplementingProperty()
    {
        const string iface = """
            namespace TestApp;

            public interface IWidget
            {
                int Value { get; set; }
            }
            """;
        const string impl = """
            namespace TestApp;

            public class Widget : IWidget
            {
                public int Value { get; set; }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(
            ("IWidget.cs", iface),
            ("Widget.cs", impl));
        var operation = new RenameSymbolOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new RenameSymbolParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Value",
            NewName = "Amount",
            RenameImplementations = false
        });

        Assert.True(result.Success);
        Assert.Contains("int Amount { get; set; }", await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("public int Value { get; set; }", await File.ReadAllTextAsync(workspace.SecondarySourcePath));
        Assert.DoesNotContain("public int Amount { get; set; }", await File.ReadAllTextAsync(workspace.SecondarySourcePath));
    }

    [SkippableFact]
    public async Task RenameSymbol_RenameImplementationsFalse_DoesNotRestoreOtherProjectWithSameInterfaceName()
    {
        const string earlierInterface = """
            namespace TestApp;

            public interface IWorker
            {
                void Execute();
            }
            """;
        const string earlierImplementation = """
            namespace TestApp;

            public class Worker : IWorker
            {
                public void Execute()
                {
                }
            }
            """;
        const string laterInterface = """
            namespace TestApp;

            public interface IWorker
            {
                void Process();
            }
            """;
        const string laterImplementation = """
            namespace TestApp;

            public class Worker : IWorker
            {
                public void Process()
                {
                }
            }

            public class ExplicitWorker : IWorker
            {
                void IWorker.Process()
                {
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateTwoProjectSolutionAsync(
            "Other",
            [("IWorker.cs", earlierInterface), ("Worker.cs", earlierImplementation)],
            "App",
            [("IWorker.cs", laterInterface), ("Worker.cs", laterImplementation)]);
        var operation = new RenameSymbolOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new RenameSymbolParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Process",
            NewName = "Execute",
            RenameImplementations = false
        });

        Assert.True(result.Success);

        var appInterface = await File.ReadAllTextAsync(workspace.SourcePath);
        var appImpl = await File.ReadAllTextAsync(workspace.SecondarySourcePath);
        var otherInterface = await File.ReadAllTextAsync(workspace.EarlierSourcePath);
        var otherImpl = await File.ReadAllTextAsync(workspace.EarlierSecondaryPath);

        Assert.Contains("void Execute();", appInterface);
        Assert.DoesNotContain("void Process();", appInterface);
        Assert.Contains("public void Process()", appImpl);
        Assert.DoesNotContain("public void Execute()", appImpl);
        Assert.Contains("void IWorker.Process()", appImpl);
        Assert.DoesNotContain("void IWorker.Execute()", appImpl);

        Assert.Contains("void Execute();", otherInterface);
        Assert.DoesNotContain("void Process();", otherInterface);
        Assert.Contains("public void Execute()", otherImpl);
        Assert.DoesNotContain("public void Process()", otherImpl);
    }

    [SkippableFact]
    public async Task RenameSymbol_VerbatimKeyword_RenameImplementationsFalse_RestoresEscapedName()
    {
        const string iface = """
            namespace TestApp;

            public interface IWorker
            {
                void @class();
            }
            """;
        const string impl = """
            namespace TestApp;

            public class Worker : IWorker
            {
                public void @class()
                {
                }
            }

            public class ExplicitWorker : IWorker
            {
                void IWorker.@class()
                {
                }
            }

            public class Consumer
            {
                public void Use(IWorker contract, Worker impl)
                {
                    contract.@class();
                    impl.@class();
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(
            ("IWorker.cs", iface),
            ("Worker.cs", impl));
        var operation = new RenameSymbolOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new RenameSymbolParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "class",
            NewName = "Execute",
            RenameImplementations = false
        });

        Assert.True(result.Success);

        var interfaceText = await File.ReadAllTextAsync(workspace.SourcePath);
        var implementationText = await File.ReadAllTextAsync(workspace.SecondarySourcePath);

        Assert.Contains("void Execute();", interfaceText);
        Assert.DoesNotContain("void @class();", interfaceText);
        Assert.Contains("public void @class()", implementationText);
        Assert.DoesNotContain("public void class()", implementationText);
        Assert.DoesNotContain("public void Execute()", implementationText);
        Assert.Contains("void IWorker.@class()", implementationText);
        Assert.DoesNotContain("void IWorker.class()", implementationText);
        Assert.DoesNotContain("void IWorker.Execute()", implementationText);
        Assert.Contains("contract.Execute();", implementationText);
        Assert.Contains("impl.@class();", implementationText);
        Assert.DoesNotContain("impl.class();", implementationText);
        Assert.DoesNotContain("impl.Execute();", implementationText);
    }

    private sealed class TempWorkspace : IAsyncDisposable
    {
        public required string DirectoryPath { get; init; }
        public required string ProjectPath { get; init; }
        public required string SourcePath { get; init; }
        public required WorkspaceContext Context { get; init; }
        public string SecondarySourcePath { get; init; } = "";
        public string EarlierSourcePath { get; init; } = "";
        public string EarlierSecondaryPath { get; init; } = "";

        public static Task<TempWorkspace> CreateAsync(string source, string fileName = "Foo.cs") =>
            CreateAsync((fileName, source));

        public static Task<TempWorkspace> CreateAsync(params (string FileName, string Source)[] files) =>
            CreateAsync("""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net9.0</TargetFramework>
                    <Nullable>enable</Nullable>
                  </PropertyGroup>
                </Project>
                """, files);

        public static async Task<TempWorkspace> CreateAsync(
            string projectXml,
            params (string FileName, string Source)[] files)
        {
            Skip.IfNot(ModuleInitializer.MsBuildAvailable, ModuleInitializer.MsBuildError ?? "MSBuild not available");

            var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpRenameImpl_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);

            var projectPath = Path.Combine(directory, "TestApp.csproj");
            await File.WriteAllTextAsync(projectPath, projectXml);

            string? sourcePath = null;
            string? secondary = null;
            foreach (var (fileName, source) in files)
            {
                var path = Path.Combine(directory, fileName);
                await File.WriteAllTextAsync(path, source);
                if (sourcePath == null)
                    sourcePath = path;
                else
                    secondary ??= path;
            }

            sourcePath ??= Path.Combine(directory, "Foo.cs");
            return await LoadAsync(directory, projectPath, sourcePath, secondary ?? "");
        }

        /// <summary>
        /// Two independent projects in one solution. The earlier project is listed
        /// first so a metadata-name-only lookup would see it before the target.
        /// SourcePath is the first file of the later project.
        /// </summary>
        public static async Task<TempWorkspace> CreateTwoProjectSolutionAsync(
            string earlierProjectName,
            (string FileName, string Source)[] earlierFiles,
            string laterProjectName,
            (string FileName, string Source)[] laterFiles)
        {
            Skip.IfNot(ModuleInitializer.MsBuildAvailable, ModuleInitializer.MsBuildError ?? "MSBuild not available");

            var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpRenameImplXP_" + Guid.NewGuid().ToString("N"));
            var earlierDir = Path.Combine(directory, earlierProjectName);
            var laterDir = Path.Combine(directory, laterProjectName);
            Directory.CreateDirectory(earlierDir);
            Directory.CreateDirectory(laterDir);

            const string csproj = """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net9.0</TargetFramework>
                    <Nullable>enable</Nullable>
                  </PropertyGroup>
                </Project>
                """;
            await File.WriteAllTextAsync(Path.Combine(earlierDir, earlierProjectName + ".csproj"), csproj);
            await File.WriteAllTextAsync(Path.Combine(laterDir, laterProjectName + ".csproj"), csproj);

            string? earlierSource = null;
            string? earlierSecondary = null;
            foreach (var (fileName, source) in earlierFiles)
            {
                var path = Path.Combine(earlierDir, fileName);
                await File.WriteAllTextAsync(path, source);
                if (earlierSource == null)
                    earlierSource = path;
                else
                    earlierSecondary ??= path;
            }

            string? laterSource = null;
            string? laterSecondary = null;
            foreach (var (fileName, source) in laterFiles)
            {
                var path = Path.Combine(laterDir, fileName);
                await File.WriteAllTextAsync(path, source);
                if (laterSource == null)
                    laterSource = path;
                else
                    laterSecondary ??= path;
            }

            laterSource ??= Path.Combine(laterDir, "IWorker.cs");
            earlierSource ??= Path.Combine(earlierDir, "IWorker.cs");

            var solutionPath = Path.Combine(directory, "TestApp.sln");
            await File.WriteAllTextAsync(solutionPath, $$"""
                Microsoft Visual Studio Solution File, Format Version 12.00
                # Visual Studio Version 17
                Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "{{earlierProjectName}}", "{{earlierProjectName}}\{{earlierProjectName}}.csproj", "{11111111-1111-1111-1111-111111111111}"
                EndProject
                Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "{{laterProjectName}}", "{{laterProjectName}}\{{laterProjectName}}.csproj", "{22222222-2222-2222-2222-222222222222}"
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

            var workspace = await LoadAsync(
                directory,
                solutionPath,
                laterSource,
                laterSecondary ?? "",
                earlierSource,
                earlierSecondary ?? "");
            return workspace;
        }

        private static async Task<TempWorkspace> LoadAsync(
            string directory,
            string projectPath,
            string sourcePath,
            string secondarySourcePath,
            string earlierSourcePath = "",
            string earlierSecondaryPath = "")
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
                    SecondarySourcePath = secondarySourcePath,
                    EarlierSourcePath = earlierSourcePath,
                    EarlierSecondaryPath = earlierSecondaryPath,
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

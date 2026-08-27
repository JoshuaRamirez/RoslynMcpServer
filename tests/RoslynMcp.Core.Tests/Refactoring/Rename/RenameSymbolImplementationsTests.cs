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

    private sealed class TempWorkspace : IAsyncDisposable
    {
        public required string DirectoryPath { get; init; }
        public required string ProjectPath { get; init; }
        public required string SourcePath { get; init; }
        public required WorkspaceContext Context { get; init; }
        public string SecondarySourcePath { get; init; } = "";

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
                    SecondarySourcePath = secondary ?? "",
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

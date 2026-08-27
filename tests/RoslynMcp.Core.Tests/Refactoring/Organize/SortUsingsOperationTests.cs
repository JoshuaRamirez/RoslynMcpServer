using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring.Organize;
using RoslynMcp.Core.Workspace;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Organize;

/// <summary>
/// Operation-level tests for <see cref="SortUsingsOperation"/>, including <c>systemFirst</c>.
/// </summary>
public class SortUsingsOperationTests
{
    private const string UnsortedSource = """
        using MyApp.Services;
        using System.Collections.Generic;
        using ThirdParty;
        using System;
        using static MyApp.Helpers;
        using static System.Math;
        using Z = MyApp.Zeta;
        using A = System.IO;

        namespace TestApp;

        public class Foo
        {
        }
        """;

    [SkippableFact]
    public async Task SortUsings_DefaultSystemFirst_PlacesSystemNamespacesFirst()
    {
        await using var workspace = await TempWorkspace.CreateAsync(UnsortedSource);
        var operation = new SortUsingsOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new SortUsingsParams
        {
            SourceFile = workspace.SourcePath
        });

        Assert.True(result.Success);
        Assert.False(result.Preview);
        Assert.Equal(
            new[]
            {
                "System",
                "System.Collections.Generic",
                "MyApp.Services",
                "ThirdParty",
                "static System.Math",
                "static MyApp.Helpers",
                "A = System.IO",
                "Z = MyApp.Zeta"
            },
            GetUsingKeys(await File.ReadAllTextAsync(workspace.SourcePath)));
    }

    [SkippableFact]
    public async Task SortUsings_SystemFirstTrue_PlacesSystemNamespacesFirst()
    {
        await using var workspace = await TempWorkspace.CreateAsync(UnsortedSource);
        var operation = new SortUsingsOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new SortUsingsParams
        {
            SourceFile = workspace.SourcePath,
            SystemFirst = true
        });

        Assert.True(result.Success);
        Assert.Equal(
            new[]
            {
                "System",
                "System.Collections.Generic",
                "MyApp.Services",
                "ThirdParty",
                "static System.Math",
                "static MyApp.Helpers",
                "A = System.IO",
                "Z = MyApp.Zeta"
            },
            GetUsingKeys(await File.ReadAllTextAsync(workspace.SourcePath)));
    }

    [SkippableFact]
    public async Task SortUsings_SystemFirstFalse_SortsAlphabeticallyWithinGroups()
    {
        await using var workspace = await TempWorkspace.CreateAsync(UnsortedSource);
        var operation = new SortUsingsOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new SortUsingsParams
        {
            SourceFile = workspace.SourcePath,
            SystemFirst = false
        });

        Assert.True(result.Success);
        Assert.False(result.Preview);
        Assert.Equal(
            new[]
            {
                "MyApp.Services",
                "System",
                "System.Collections.Generic",
                "ThirdParty",
                "static MyApp.Helpers",
                "static System.Math",
                "A = System.IO",
                "Z = MyApp.Zeta"
            },
            GetUsingKeys(await File.ReadAllTextAsync(workspace.SourcePath)));
    }

    [SkippableFact]
    public async Task SortUsings_Preview_DoesNotWriteFiles()
    {
        await using var workspace = await TempWorkspace.CreateAsync(UnsortedSource);
        var operation = new SortUsingsOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var result = await operation.ExecuteAsync(new SortUsingsParams
        {
            SourceFile = workspace.SourcePath,
            SystemFirst = false,
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.NotEmpty(result.PendingChanges);
        Assert.Contains("MyApp.Services", result.PendingChanges[0].AfterSnippet);
        Assert.Contains("System", result.PendingChanges[0].AfterSnippet);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task SortUsings_PreviewSystemFirstTrue_DoesNotWriteFiles()
    {
        await using var workspace = await TempWorkspace.CreateAsync(UnsortedSource);
        var operation = new SortUsingsOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var result = await operation.ExecuteAsync(new SortUsingsParams
        {
            SourceFile = workspace.SourcePath,
            SystemFirst = true,
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.NotEmpty(result.PendingChanges);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    private static List<string> GetUsingKeys(string source)
    {
        var root = CSharpSyntaxTree.ParseText(source).GetCompilationUnitRoot();
        return root.Usings.Select(ToUsingKey).ToList();
    }

    private static string ToUsingKey(UsingDirectiveSyntax usingDirective)
    {
        var name = usingDirective.Name?.ToString() ?? string.Empty;
        if (usingDirective.Alias != null)
            return $"{usingDirective.Alias.Name} = {name}";
        if (usingDirective.StaticKeyword.IsKind(SyntaxKind.StaticKeyword))
            return $"static {name}";
        return name;
    }

    private sealed class TempWorkspace : IAsyncDisposable
    {
        public required string DirectoryPath { get; init; }
        public required string SourcePath { get; init; }
        public required WorkspaceContext Context { get; init; }

        public static async Task<TempWorkspace> CreateAsync(string source, string fileName = "Foo.cs")
        {
            Skip.IfNot(ModuleInitializer.MsBuildAvailable, ModuleInitializer.MsBuildError ?? "MSBuild not available");

            var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpSortUsings_" + Guid.NewGuid().ToString("N"));
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

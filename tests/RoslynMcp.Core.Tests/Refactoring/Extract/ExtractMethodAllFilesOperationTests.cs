using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Extract;
using RoslynMcp.Core.Workspace;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Extract;

/// <summary>
/// Operation-level tests for <see cref="ExtractMethodOperation"/> allFiles.
/// </summary>
public class ExtractMethodAllFilesOperationTests
{
    private const string EligibleFileA = """
        namespace TestApp;

        public class FileA
        {
            public void Run()
            {
                System.Console.WriteLine("one");
                System.Console.WriteLine("two");
                System.Console.WriteLine("three");
            }
        }
        """;

    private const string EligibleFileB = """
        namespace TestApp;

        public class FileB
        {
            public int Compute()
            {
                System.Console.WriteLine(1);
                System.Console.WriteLine(2);
                return 3;
            }
        }
        """;

    private const string IneligibleFileC = """
        namespace TestApp;

        public class FileC
        {
            public int Run()
            {
                return 1;
            }
        }
        """;

    private const string CollisionFile = """
        namespace TestApp;

        public class CollisionHost
        {
            public void Run()
            {
                System.Console.WriteLine(1);
                System.Console.WriteLine(2);
                System.Console.WriteLine(3);
            }

            private void WriteLine() { }
        }
        """;

    [SkippableFact]
    public async Task ExtractMethod_OmittedAllFiles_KeepsSingleSiteExtract()
    {
        await using var workspace = await TempWorkspace.CreateAsync(EligibleFileA);
        var operation = new ExtractMethodOperation(workspace.Context);
        // Single-statement span: multi-statement single-site selection can resolve to the
        // containing Block via FindNode (pre-existing); allFiles uses statement lists directly.
        var span = FindSpan(EligibleFileA, "System.Console.WriteLine(\"one\");");

        var result = await operation.ExecuteAsync(new ExtractMethodParams
        {
            SourceFile = workspace.SourcePath,
            StartLine = span.StartLine,
            StartColumn = span.StartColumn,
            EndLine = span.EndLine,
            EndColumn = span.EndColumn,
            MethodName = "Extracted"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("Extracted()", updated, StringComparison.Ordinal);
        Assert.Contains("void Extracted()", updated, StringComparison.Ordinal);
        // Call site in Run should invoke Extracted; original statement lives in the new method.
        Assert.Contains("Extracted();", updated, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task ExtractMethod_AllFilesTrue_ExtractsEligibleRunsAcrossFiles()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("FileA.cs", EligibleFileA),
            ("FileB.cs", EligibleFileB),
            ("FileC.cs", IneligibleFileC));
        var operation = new ExtractMethodOperation(workspace.Context);
        var beforeC = await File.ReadAllTextAsync(workspace.SourcePaths["FileC.cs"]);

        var result = await operation.ExecuteAsync(new ExtractMethodParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        Assert.False(result.Preview);
        var updatedA = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePaths["FileA.cs"]));
        var updatedB = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePaths["FileB.cs"]));
        Assert.Contains("WriteLine()", updatedA, StringComparison.Ordinal);
        Assert.Contains("private void WriteLine()", updatedA, StringComparison.Ordinal);
        Assert.Contains("System.Console.WriteLine(\"three\");", updatedA, StringComparison.Ordinal);

        Assert.Contains("return 3;", updatedB, StringComparison.Ordinal);
        Assert.Contains("WriteLine()", updatedB, StringComparison.Ordinal);
        Assert.Contains("private void WriteLine()", updatedB, StringComparison.Ordinal);

        Assert.Equal(beforeC, await File.ReadAllTextAsync(workspace.SourcePaths["FileC.cs"]));
        Assert.True(result.Changes!.FilesModified.Count >= 2);
        Assert.Contains(result.Changes.FilesModified, p => PathEquals(p, workspace.SourcePaths["FileA.cs"]));
        Assert.Contains(result.Changes.FilesModified, p => PathEquals(p, workspace.SourcePaths["FileB.cs"]));
        Assert.DoesNotContain(result.Changes.FilesModified, p => PathEquals(p, workspace.SourcePaths["FileC.cs"]));
    }

    [SkippableFact]
    public async Task ExtractMethod_AllFilesTrue_WithoutSourceFileOrMethodName_Succeeds()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("FileA.cs", EligibleFileA),
            ("FileB.cs", EligibleFileB));
        var operation = new ExtractMethodOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ExtractMethodParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        Assert.True(result.Changes!.FilesModified.Count >= 2);
    }

    [SkippableFact]
    public async Task ExtractMethod_AllFilesFalse_WithoutSourceFile_MissingRequiredParam()
    {
        await using var workspace = await TempWorkspace.CreateAsync(EligibleFileA);
        var operation = new ExtractMethodOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ExtractMethodParams
            {
                AllFiles = false,
                MethodName = "Extracted",
                StartLine = 1,
                StartColumn = 1,
                EndLine = 1,
                EndColumn = 5
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("sourceFile", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task ExtractMethod_AllFilesTrue_WithMethodName_Rejects()
    {
        await using var workspace = await TempWorkspace.CreateAsync(EligibleFileA);
        var operation = new ExtractMethodOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ExtractMethodParams
            {
                AllFiles = true,
                MethodName = "Extracted"
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("methodName", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task ExtractMethod_AllFilesTrue_WithStartLine_Rejects()
    {
        await using var workspace = await TempWorkspace.CreateAsync(EligibleFileA);
        var operation = new ExtractMethodOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ExtractMethodParams
            {
                AllFiles = true,
                StartLine = 1
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("startLine", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task ExtractMethod_PreviewAllFiles_AggregatesChangedFilesAndWritesNothing()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("FileA.cs", EligibleFileA),
            ("FileB.cs", EligibleFileB),
            ("FileC.cs", IneligibleFileC));
        var operation = new ExtractMethodOperation(workspace.Context);
        var beforeA = await File.ReadAllTextAsync(workspace.SourcePaths["FileA.cs"]);
        var beforeB = await File.ReadAllTextAsync(workspace.SourcePaths["FileB.cs"]);

        var result = await operation.ExecuteAsync(new ExtractMethodParams
        {
            AllFiles = true,
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.True(result.PendingChanges!.Count >= 2);
        Assert.Equal(beforeA, await File.ReadAllTextAsync(workspace.SourcePaths["FileA.cs"]));
        Assert.Equal(beforeB, await File.ReadAllTextAsync(workspace.SourcePaths["FileB.cs"]));
    }

    [SkippableFact]
    public async Task ExtractMethod_AllFilesTrue_EveryFileIneligible_SucceedsWithEmptyChanges()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("FileC.cs", IneligibleFileC),
            ("FileC2.cs", IneligibleFileC));
        var operation = new ExtractMethodOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ExtractMethodParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        Assert.Empty(result.Changes!.FilesModified);
    }

    [SkippableFact]
    public async Task ExtractMethod_AllFilesTrue_OptionalSourceFile_LimitsWalk()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("FileA.cs", EligibleFileA),
            ("FileB.cs", EligibleFileB));
        var operation = new ExtractMethodOperation(workspace.Context);
        var beforeB = await File.ReadAllTextAsync(workspace.SourcePaths["FileB.cs"]);

        var result = await operation.ExecuteAsync(new ExtractMethodParams
        {
            AllFiles = true,
            SourceFile = workspace.SourcePaths["FileA.cs"]
        });

        Assert.True(result.Success);
        Assert.Contains(result.Changes!.FilesModified, p => PathEquals(p, workspace.SourcePaths["FileA.cs"]));
        Assert.DoesNotContain(result.Changes.FilesModified, p => PathEquals(p, workspace.SourcePaths["FileB.cs"]));
        Assert.Equal(beforeB, await File.ReadAllTextAsync(workspace.SourcePaths["FileB.cs"]));
    }

    [SkippableFact]
    public async Task ExtractMethod_AllFilesTrue_SkipsOutboundLocals()
    {
        const string source = """
            namespace TestApp;

            public class Host
            {
                public int Run()
                {
                    var a = 1;
                    var b = 2;
                    return a + b;
                }
            }
            """;
        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ExtractMethodOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var result = await operation.ExecuteAsync(new ExtractMethodParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Empty(result.Changes!.FilesModified);
    }

    [SkippableFact]
    public async Task ExtractMethod_AllFilesTrue_SkipsReturnControlFlow()
    {
        const string source = """
            namespace TestApp;

            public class Host
            {
                public void Run(bool ok)
                {
                    if (!ok) return;
                    System.Console.WriteLine(1);
                    System.Console.WriteLine(2);
                }
            }
            """;
        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ExtractMethodOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ExtractMethodParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        // First pair includes `if (!ok) return;` — skipped. Second pair WriteLine/WriteLine
        // leaves nothing behind (n==3, only i=0 is valid) so the return pair is the only
        // candidate and must be skipped → no rewrite.
        Assert.DoesNotContain("private void", updated, StringComparison.Ordinal);
        Assert.Empty(result.Changes!.FilesModified);
    }

    [SkippableFact]
    public async Task ExtractMethod_AllFilesTrue_SkipsFieldNameCollision()
    {
        const string source = """
            namespace TestApp;

            public class Host
            {
                private int Value;

                public void Run()
                {
                    Value++;
                    Value++;
                    Value++;
                }
            }
            """;
        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ExtractMethodOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var result = await operation.ExecuteAsync(new ExtractMethodParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Empty(result.Changes!.FilesModified);
    }

    [SkippableFact]
    public async Task ExtractMethod_AllFilesTrue_ExtractsFromAccessorBody()
    {
        const string source = """
            namespace TestApp;

            public class Host
            {
                public int Prop
                {
                    get
                    {
                        System.Console.WriteLine(1);
                        System.Console.WriteLine(2);
                        return 3;
                    }
                }
            }
            """;
        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ExtractMethodOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ExtractMethodParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("WriteLine()", updated, StringComparison.Ordinal);
        Assert.Contains("private void WriteLine()", updated, StringComparison.Ordinal);
        Assert.Contains("return 3;", updated, StringComparison.Ordinal);
        Assert.Single(result.Changes!.FilesModified);
    }

    [SkippableFact]
    public async Task ExtractMethod_AllFilesTrue_SkipsNameCollisionOnDerivedName()
    {
        await using var workspace = await TempWorkspace.CreateAsync(CollisionFile);
        var operation = new ExtractMethodOperation(workspace.Context);
        var before = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));

        var result = await operation.ExecuteAsync(new ExtractMethodParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        // Derived name WriteLine collides with existing private void WriteLine() — skip, no rewrite.
        Assert.Equal(before, updated);
        Assert.Empty(result.Changes!.FilesModified);
    }

    [SkippableFact]
    public async Task ExtractMethod_AllFilesTrue_MakeStatic_SkipsInstanceCapture()
    {
        const string source = """
            namespace TestApp;

            public class Host
            {
                private int _n;

                public void Run()
                {
                    _n++;
                    _n++;
                    System.Console.WriteLine(_n);
                }
            }
            """;
        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ExtractMethodOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var result = await operation.ExecuteAsync(new ExtractMethodParams
        {
            AllFiles = true,
            MakeStatic = true
        });

        Assert.True(result.Success);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Empty(result.Changes!.FilesModified);
    }

    [SkippableFact]
    public async Task ExtractMethod_AllFilesTrue_ExtractsMultipleRunsBottomUp()
    {
        const string source = """
            namespace TestApp;

            public class Host
            {
                public void Run()
                {
                    System.Console.Write(1);
                    System.Console.Write(2);
                    System.Console.WriteLine(3);
                    System.Console.WriteLine(4);
                    System.Console.WriteLine(5);
                }
            }
            """;
        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ExtractMethodOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ExtractMethodParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        // Pairs: (Write,Write) -> Write; (WriteLine,WriteLine) -> WriteLine; leave WriteLine(5).
        Assert.Contains("private void Write()", updated, StringComparison.Ordinal);
        Assert.Contains("private void WriteLine()", updated, StringComparison.Ordinal);
        Assert.Contains("System.Console.WriteLine(5);", updated, StringComparison.Ordinal);
        Assert.Single(result.Changes!.FilesModified);
    }


    private static string NormalizeNewlines(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    private static bool PathEquals(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);


    private static (int StartLine, int StartColumn, int EndLine, int EndColumn) FindSpan(string source, string snippet)
    {
        var index = source.IndexOf(snippet, StringComparison.Ordinal);
        if (index < 0)
            throw new InvalidOperationException($"Snippet not found: {snippet}");

        return (GetLineColumn(source, index).Line, GetLineColumn(source, index).Column,
            GetLineColumn(source, index + snippet.Length).Line, GetLineColumn(source, index + snippet.Length).Column);
    }

    private static (int Line, int Column) GetLineColumn(string source, int index)
    {
        var line = 1;
        var column = 1;
        for (var i = 0; i < index; i++)
        {
            if (source[i] == '\n')
            {
                line++;
                column = 1;
            }
            else if (source[i] != '\r')
            {
                column++;
            }
        }

        return (line, column);
    }

    private sealed class TempWorkspace : IAsyncDisposable
    {
        public required string DirectoryPath { get; init; }
        public required string ProjectPath { get; init; }
        public required string SourcePath { get; init; }
        public required IReadOnlyDictionary<string, string> SourcePaths { get; init; }
        public required WorkspaceContext Context { get; init; }

        public static Task<TempWorkspace> CreateAsync(string source, string fileName = "Types.cs") =>
            CreateMultiFileAsync((fileName, source));

        public static Task<TempWorkspace> CreateWithFilesAsync(params (string FileName, string Source)[] files) =>
            CreateMultiFileAsync(files);

        public static async Task<TempWorkspace> CreateMultiFileAsync(params (string FileName, string Source)[] files)
        {
            Skip.IfNot(ModuleInitializer.MsBuildAvailable, ModuleInitializer.MsBuildError ?? "MSBuild not available");

            var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpExtractMethodAllFiles_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);

            var projectPath = Path.Combine(directory, "TestApp.csproj");
            var sourcePaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            await File.WriteAllTextAsync(projectPath, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net9.0</TargetFramework>
                    <Nullable>enable</Nullable>
                    <GenerateAssemblyInfo>false</GenerateAssemblyInfo>
                    <GenerateTargetFrameworkAttribute>false</GenerateTargetFrameworkAttribute>
                  </PropertyGroup>
                </Project>
                """);

            string? firstSource = null;
            foreach (var (fileName, source) in files)
            {
                var sourcePath = Path.Combine(directory, fileName);
                Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
                await File.WriteAllTextAsync(sourcePath, source);
                sourcePaths[fileName] = sourcePath;
                firstSource ??= sourcePath;
            }

            try
            {
                var provider = new MSBuildWorkspaceProvider();
                var context = await provider.CreateContextAsync(projectPath);
                foreach (var sourcePath in sourcePaths.Values)
                {
                    if (context.GetDocumentByPath(sourcePath) == null)
                    {
                        context.Dispose();
                        throw new InvalidOperationException($"Workspace loaded but did not include {sourcePath}.");
                    }
                }

                return new TempWorkspace
                {
                    DirectoryPath = directory,
                    ProjectPath = projectPath,
                    SourcePath = firstSource!,
                    SourcePaths = sourcePaths,
                    Context = context
                };
            }
            catch (Exception ex) when (ex is not SkipException)
            {
                try { Directory.Delete(directory, recursive: true); } catch { }
                Skip.If(true, $"Workspace load failed: {ex.Message}");
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            Context.Dispose();
            await Task.Run(() =>
            {
                try { Directory.Delete(DirectoryPath, recursive: true); } catch { }
            });
        }
    }
}

using Microsoft.CodeAnalysis;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Inline;
using RoslynMcp.Core.Workspace;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Inline;

/// <summary>
/// Regression tests for #3090: inline_variable treated every identifier under an assignment's
/// left side as a write, so a local only read inside an index (<c>values[index] = 10</c>) was
/// refused with MultipleAssignments (single-site) or silently skipped (allFiles).
/// </summary>
public class InlineVariableAssignmentTargetReadTests
{
    private const string IndexSource = """
        namespace Demo;

        public class Slots
        {
            public int[] Fill(int[] values)
            {
                int index = 2;
                values[index] = 10;
                values[index + 1] += 3;
                return values;
            }
        }
        """;

    [SkippableFact]
    public async Task SingleSite_LocalReadInsideIndexOfTarget_IsInlined()
    {
        await using var workspace = await TempWorkspace.CreateAsync(IndexSource, "latest");

        var result = await new InlineVariableOperation(workspace.Context).ExecuteAsync(new InlineVariableParams
        {
            SourceFile = workspace.SourcePath,
            VariableName = "index"
        });

        Assert.True(result.Success, result.Error?.Message);
        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("values[2] = 10;", text, StringComparison.Ordinal);
        Assert.Contains("values[2 + 1] += 3;", text, StringComparison.Ordinal);
        Assert.DoesNotContain("int index", text, StringComparison.Ordinal);
        Assert.Empty(await workspace.GetDiskErrorsAsync());
    }

    [SkippableFact]
    public async Task AllFiles_LocalReadInsideIndexOfTarget_IsInlined()
    {
        await using var workspace = await TempWorkspace.CreateAsync(IndexSource, "latest");

        var result = await new InlineVariableOperation(workspace.Context).ExecuteAsync(new InlineVariableParams
        {
            AllFiles = true
        });

        Assert.True(result.Success, result.Error?.Message);
        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("values[2] = 10;", text, StringComparison.Ordinal);
        Assert.DoesNotContain("int index", text, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task SingleSite_ReferenceTypeReceiverOfMemberWrite_IsInlined()
    {
        const string source = """
            namespace Demo;

            public class Widget { public string Name = ""; }

            public class Holder
            {
                private Widget _current = new Widget();

                public Widget Rename()
                {
                    Widget target = _current;
                    target.Name = "renamed";
                    return target;
                }
            }
            """;
        await using var workspace = await TempWorkspace.CreateAsync(source, "latest");

        var result = await new InlineVariableOperation(workspace.Context).ExecuteAsync(new InlineVariableParams
        {
            SourceFile = workspace.SourcePath,
            VariableName = "target"
        });

        Assert.True(result.Success, result.Error?.Message);
        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("_current.Name = \"renamed\";", text, StringComparison.Ordinal);
        Assert.Contains("return _current;", text, StringComparison.Ordinal);
    }

    [SkippableTheory]
    [InlineData("copy.Value = 5;")]
    [InlineData("(copy).Value = 5;")]
    [InlineData("copy.Inner.Value = 5;")]
    [InlineData("copy!.Value = 5;")]
    [InlineData("(copy!).Value = 5;")]
    [InlineData("copy!.Inner.Value = 5;")]
    [InlineData("copy = default;")]
    [InlineData("(copy, other) = (other, copy);")]
    public async Task SingleSite_LocalItselfOrValueTypeReceiverWritten_StillRefused(string write)
    {
        var source = $$"""
            namespace Demo;

            public struct Cell { public int Value; public Inner Inner; }
            public struct Inner { public int Value; }

            public class Grid
            {
                private Cell _cell;

                public int Touch()
                {
                    Cell copy = _cell;
                    Cell other = _cell;
                    {{write}}
                    return copy.Value + other.Value;
                }
            }
            """;
        await using var workspace = await TempWorkspace.CreateAsync(source, "latest");

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            new InlineVariableOperation(workspace.Context).ExecuteAsync(new InlineVariableParams
            {
                SourceFile = workspace.SourcePath,
                VariableName = "copy"
            }));

        Assert.Equal(ErrorCodes.MultipleAssignments, ex.ErrorCode);
        Assert.Equal(source, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    /// <summary>
    /// Sources where writing through the local and through the inlined initializer would not hit the same
    /// object (#3090 review): a boxed struct copy, or a fresh instance re-created at every use site (which
    /// also fails to compile: CS0178, CS9176, CS1002).
    /// </summary>
    public static TheoryData<string, string> NotSharedReceiverSources => new()
    {
        {
            "boxing",
            """
            namespace Demo;

            public interface IHas { int Value { get; set; } }
            public struct S : IHas { public int Value { get; set; } }

            public class Box
            {
                private S _s;

                public int Touch()
                {
                    IHas v = _s;
                    v.Value = 5;
                    return _s.Value;
                }
            }
            """
        },
        {
            "boxing-suppressed",
            """
            namespace Demo;

            public interface IHas { int Value { get; set; } }
            public struct S : IHas { public int Value { get; set; } }

            public class Box
            {
                private S _s;

                public int Touch()
                {
                    IHas v = _s;
                    v!.Value = 5;
                    return _s.Value;
                }
            }
            """
        },
        {
            "array-creation-suppressed",
            """
            namespace Demo;

            public class Arrays
            {
                public int[] Make()
                {
                    int[] arr = new int[3];
                    arr![0] = 1;
                    return arr;
                }
            }
            """
        },
        {
            "array-creation",
            """
            namespace Demo;

            public class Arrays
            {
                public int[] Make()
                {
                    int[] arr = new int[3];
                    arr[0] = 1;
                    return arr;
                }
            }
            """
        },
        {
            "array-initializer",
            """
            namespace Demo;

            public class Arrays
            {
                public int[] Make()
                {
                    int[] arr = { 1, 2 };
                    arr[0] = 5;
                    return arr;
                }
            }
            """
        },
        {
            "collection-expression",
            """
            using System.Collections.Generic;

            namespace Demo;

            public class Lists
            {
                public List<int> Make()
                {
                    List<int> list = [1, 2];
                    list[0] = 5;
                    return list;
                }
            }
            """
        },
        {
            "with-expression",
            """
            namespace Demo;

            public record R(int[] A);

            public class Records
            {
                private R _r = new R(new int[1]);

                public R Make()
                {
                    R r = _r with { };
                    r.A[0] = 1;
                    return r;
                }
            }
            """
        },
        {
            "target-typed-new",
            """
            namespace Demo;

            public class Widget { public string Name = ""; }

            public class Factory
            {
                public Widget Make()
                {
                    Widget w = new();
                    w.Name = "made";
                    return w;
                }
            }
            """
        },
    };

    [SkippableTheory]
    [MemberData(nameof(NotSharedReceiverSources))]
    public async Task SingleSite_ReceiverNotSharedWithInitializer_IsRefused(string kind, string source)
    {
        await using var workspace = await TempWorkspace.CreateAsync(source, "latest");
        var variableName = kind switch
        {
            "boxing" or "boxing-suppressed" => "v",
            "collection-expression" => "list",
            "with-expression" => "r",
            "target-typed-new" => "w",
            _ => "arr"
        };

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            new InlineVariableOperation(workspace.Context).ExecuteAsync(new InlineVariableParams
            {
                SourceFile = workspace.SourcePath,
                VariableName = variableName
            }));

        Assert.Equal(ErrorCodes.MultipleAssignments, ex.ErrorCode);
        Assert.Equal(source, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableTheory]
    [MemberData(nameof(NotSharedReceiverSources))]
    public async Task AllFiles_ReceiverNotSharedWithInitializer_IsLeftAlone(string kind, string source)
    {
        await using var workspace = await TempWorkspace.CreateAsync(source, "latest");

        var result = await new InlineVariableOperation(workspace.Context).ExecuteAsync(new InlineVariableParams
        {
            AllFiles = true
        });

        Assert.True(result.Success, $"{kind}: {result.Error?.Message}");
        Assert.Equal(source, await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Empty(await workspace.GetDiskErrorsAsync());
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ImplicitReferenceConversionReceiver_IsInlined_FullOutput(bool allFiles)
    {
        // Class -> interface is an implicit reference conversion: the local and the field are the same object.
        // The doubled indentation is the existing declaration-removal trivia behavior, not part of #3090.
        const string source = """
            namespace Demo;

            public interface IHas { int Value { get; set; } }
            public class C : IHas { public int Value { get; set; } }

            public class Holder
            {
                private C _c = new C();

                public int Touch()
                {
                    IHas v = _c;
                    v.Value = 5;
                    return _c.Value;
                }
            }
            """;
        const string expected = """
            namespace Demo;

            public interface IHas { int Value { get; set; } }
            public class C : IHas { public int Value { get; set; } }

            public class Holder
            {
                private C _c = new C();

                public int Touch()
                {
                            _c.Value = 5;
                    return _c.Value;
                }
            }
            """;
        await using var workspace = await TempWorkspace.CreateAsync(source, "latest");

        var result = await new InlineVariableOperation(workspace.Context).ExecuteAsync(allFiles
            ? new InlineVariableParams { AllFiles = true }
            : new InlineVariableParams { SourceFile = workspace.SourcePath, VariableName = "v" });

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(expected, await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Empty(await workspace.GetDiskErrorsAsync());
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CastInitializer_IsParenthesizedBeforeMemberAndElementAccess(bool allFiles)
    {
        // (Derived)_base.X casts _base.X, and (IList<int>)_a[0] casts _a[0] (#3090 review).
        // The extra indentation is the existing declaration-removal trivia behavior (one level per removed line).
        const string source = """
            using System.Collections.Generic;

            namespace Demo;

            public class Base { }
            public class Derived : Base { public int X; }

            public class Holder
            {
                private Base _base = new Derived();
                private object _a = new List<int> { 7 };

                public int Read()
                {
                    var d = (Derived)_base;
                    var a = (IList<int>)_a;
                    return d.X + a[0];
                }
            }
            """;
        const string expected = """
            using System.Collections.Generic;

            namespace Demo;

            public class Base { }
            public class Derived : Base { public int X; }

            public class Holder
            {
                private Base _base = new Derived();
                private object _a = new List<int> { 7 };

                public int Read()
                {
                                    return ((Derived)_base).X + ((IList<int>)_a)[0];
                }
            }
            """;
        await using var workspace = await TempWorkspace.CreateAsync(source, "latest");
        var operation = new InlineVariableOperation(workspace.Context);

        if (allFiles)
        {
            var result = await operation.ExecuteAsync(new InlineVariableParams { AllFiles = true });
            Assert.True(result.Success, result.Error?.Message);
        }
        else
        {
            foreach (var name in new[] { "d", "a" })
            {
                var result = await new InlineVariableOperation(workspace.Context).ExecuteAsync(
                    new InlineVariableParams { SourceFile = workspace.SourcePath, VariableName = name });
                Assert.True(result.Success, result.Error?.Message);
            }
        }

        Assert.Equal(expected, await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Empty(await workspace.GetDiskErrorsAsync());
    }

    private sealed class TempWorkspace : IAsyncDisposable
    {
        public required string DirectoryPath { get; init; }
        public required string SourcePath { get; init; }
        public required WorkspaceContext Context { get; init; }

        public static async Task<TempWorkspace> CreateAsync(string source, string langVersion)
        {
            Skip.IfNot(ModuleInitializer.MsBuildAvailable, ModuleInitializer.MsBuildError ?? "MSBuild not available");

            var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpLangVersion_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var projectPath = Path.Combine(directory, "Case.csproj");
            var sourcePath = Path.Combine(directory, "Program.cs");
            await File.WriteAllTextAsync(projectPath, $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <OutputType>Library</OutputType>
                    <TargetFramework>net9.0</TargetFramework>
                    <ImplicitUsings>disable</ImplicitUsings>
                    <LangVersion>{langVersion}</LangVersion>
                  </PropertyGroup>
                </Project>
                """);
            await File.WriteAllTextAsync(sourcePath, source);

            try
            {
                var context = await new MSBuildWorkspaceProvider().CreateContextAsync(projectPath);
                if (context.GetDocumentByPath(sourcePath) == null)
                {
                    context.Dispose();
                    throw new InvalidOperationException($"Workspace loaded but did not include {sourcePath}.");
                }

                return new TempWorkspace { DirectoryPath = directory, SourcePath = sourcePath, Context = context };
            }
            catch (Exception ex) when (ex is not SkipException)
            {
                try { Directory.Delete(directory, recursive: true); } catch { /* ignore cleanup failures */ }
                Skip.If(true, $"Workspace load failed: {ex.Message}");
                throw;
            }
        }

        public async Task<List<string>> GetErrorsAsync()
        {
            var document = Context.GetDocumentByPath(SourcePath);
            Assert.NotNull(document);
            var compilation = await document.Project.GetCompilationAsync();
            Assert.NotNull(compilation);
            return compilation.GetDiagnostics()
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(d => d.ToString())
                .ToList();
        }

        /// <summary>Compile errors of the project with every .cs file re-read from disk.</summary>
        public async Task<List<string>> GetDiskErrorsAsync()
        {
            var document = Context.GetDocumentByPath(SourcePath);
            Assert.NotNull(document);
            var project = document.Project;
            foreach (var path in Directory.GetFiles(DirectoryPath, "*.cs"))
            {
                var existing = project.Documents.FirstOrDefault(d => string.Equals(d.FilePath, path, StringComparison.Ordinal));
                var text = Microsoft.CodeAnalysis.Text.SourceText.From(await File.ReadAllTextAsync(path));
                project = existing != null
                    ? existing.WithText(text).Project
                    : project.AddDocument(Path.GetFileName(path), text, filePath: path).Project;
            }

            var compilation = await project.GetCompilationAsync();
            Assert.NotNull(compilation);
            return compilation.GetDiagnostics()
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(d => d.ToString())
                .ToList();
        }

        public async ValueTask DisposeAsync()
        {
            Context.Dispose();
            await Task.Run(() =>
            {
                try { Directory.Delete(DirectoryPath, recursive: true); } catch { /* ignore locked temp files */ }
            });
        }
    }
}

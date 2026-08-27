using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Generate;
using RoslynMcp.Core.Workspace;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Generate;

/// <summary>
/// Operation-level tests for <see cref="GenerateEqualsHashCodeOperation"/>, including <c>implementIEquatable</c>.
/// </summary>
public class GenerateEqualsHashCodeOperationTests
{
    private const string PersonSource = """
        namespace TestApp;

        public class Person
        {
            public string Name { get; set; }

            public int Age { get; set; }
        }
        """;

    private const string PointSource = """
        namespace TestApp;

        public struct Point
        {
            public int X { get; set; }

            public int Y { get; set; }
        }
        """;

    #region Default / false — no IEquatable

    [SkippableFact]
    public async Task GenerateEquals_ImplementIEquatableOmitted_DoesNotAddIEquatable()
    {
        await using var workspace = await TempWorkspace.CreateAsync(PersonSource);
        var operation = new GenerateEqualsHashCodeOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateEqualsHashCodeParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Person"
        });

        Assert.True(result.Success);
        AssertDefaultEqualsShape(NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath)));
    }

    [SkippableFact]
    public async Task GenerateEquals_ImplementIEquatableFalse_DoesNotAddIEquatable()
    {
        await using var workspace = await TempWorkspace.CreateAsync(PersonSource);
        var operation = new GenerateEqualsHashCodeOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateEqualsHashCodeParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Person",
            ImplementIEquatable = false
        });

        Assert.True(result.Success);
        AssertDefaultEqualsShape(NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath)));
    }

    #endregion

    #region true — IEquatable + typed Equals + object delegates

    [SkippableFact]
    public async Task GenerateEquals_ImplementIEquatableTrue_AddsInterfaceTypedEqualsAndDelegatingObjectEquals()
    {
        await using var workspace = await TempWorkspace.CreateAsync(PersonSource);
        var operation = new GenerateEqualsHashCodeOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateEqualsHashCodeParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Person",
            ImplementIEquatable = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        AssertIEquatableClassShape(updated);
    }

    [SkippableFact]
    public async Task GenerateEquals_ImplementIEquatableTrue_Struct_UsesNonNullableTypedEquals()
    {
        await using var workspace = await TempWorkspace.CreateAsync(PointSource, "Point.cs");
        var operation = new GenerateEqualsHashCodeOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateEqualsHashCodeParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Point",
            ImplementIEquatable = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("global::System.IEquatable<Point>", updated);
        Assert.Contains("public bool Equals(Point other)", updated);
        Assert.DoesNotContain("Equals(Point?", updated);
        AssertObjectEqualsDelegates(updated, "Point");
        Assert.Contains("public override int GetHashCode()", updated);
    }

    [SkippableFact]
    public async Task GenerateEquals_ImplementIEquatableTrue_UserTypeNamedIEquatable_UsesGlobalQualifiedInterface()
    {
        const string source = """
            namespace TestApp;

            public interface IEquatable { }

            public class Widget
            {
                public string Name { get; set; }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Widget.cs");
        var operation = new GenerateEqualsHashCodeOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateEqualsHashCodeParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget",
            ImplementIEquatable = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("global::System.IEquatable<Widget>", updated);
        Assert.Contains("public bool Equals(Widget? other)", updated);
    }

    #endregion

    #region Preview

    [SkippableFact]
    public async Task GenerateEquals_PreviewDefault_DoesNotWriteFiles()
    {
        await using var workspace = await TempWorkspace.CreateAsync(PersonSource);
        var operation = new GenerateEqualsHashCodeOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var result = await operation.ExecuteAsync(new GenerateEqualsHashCodeParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Person",
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.NotEmpty(result.PendingChanges);
        var snippet = result.PendingChanges[0].AfterSnippet!;
        Assert.Contains("public override bool Equals(object?", snippet);
        Assert.DoesNotContain("IEquatable", snippet);
        Assert.DoesNotContain("Equals(Person", snippet);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task GenerateEquals_PreviewImplementIEquatableTrue_DoesNotWriteFiles_AndDescribesShape()
    {
        await using var workspace = await TempWorkspace.CreateAsync(PersonSource);
        var operation = new GenerateEqualsHashCodeOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var result = await operation.ExecuteAsync(new GenerateEqualsHashCodeParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Person",
            ImplementIEquatable = true,
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.NotEmpty(result.PendingChanges);
        Assert.Contains("IEquatable", result.PendingChanges[0].Description);
        var snippet = result.PendingChanges[0].AfterSnippet!;
        Assert.Contains("global::System.IEquatable<Person>", snippet);
        Assert.Contains("public bool Equals(Person? other)", snippet);
        Assert.Contains("Equals(other)", snippet);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    #endregion

    #region Already implements / typed Equals

    [SkippableFact]
    public async Task GenerateEquals_AlreadyImplementsIEquatable_FailsWith3144()
    {
        const string source = """
            namespace TestApp;

            public class Person : System.IEquatable<Person>
            {
                public string Name { get; set; }

                public bool Equals(Person? other) => other is not null && Name == other.Name;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new GenerateEqualsHashCodeOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GenerateEqualsHashCodeParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Person",
                ImplementIEquatable = true
            }));

        Assert.Equal(ErrorCodes.AlreadyImplementsIEquatable, ex.ErrorCode);
        Assert.Equal("3144", ex.ErrorCode);
        Assert.Contains("IEquatable", ex.Message);
        Assert.Equal(source, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task GenerateEquals_AlreadyHasTypedEquals_FailsWith3144()
    {
        const string source = """
            namespace TestApp;

            public class Person
            {
                public string Name { get; set; }

                public bool Equals(Person? other) => other is not null && Name == other.Name;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new GenerateEqualsHashCodeOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GenerateEqualsHashCodeParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Person",
                ImplementIEquatable = true
            }));

        Assert.Equal(ErrorCodes.AlreadyImplementsIEquatable, ex.ErrorCode);
        Assert.Equal("3144", ex.ErrorCode);
        Assert.Equal(source, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    #endregion

    #region Helpers

    private static void AssertDefaultEqualsShape(string text)
    {
        Assert.DoesNotContain("IEquatable", text);
        Assert.DoesNotContain("public bool Equals(Person", text);
        var objectEquals = ExtractMethod(text, "public override bool Equals(object?");
        Assert.Contains("obj is Person other", objectEquals);
        Assert.DoesNotContain("Equals(other)", objectEquals);
        Assert.Contains("public override int GetHashCode()", text);
    }

    private static void AssertIEquatableClassShape(string text)
    {
        Assert.Contains("global::System.IEquatable<Person>", text);
        Assert.Contains("public bool Equals(Person? other)", text);
        AssertObjectEqualsDelegates(text, "Person");
        Assert.Contains("public override int GetHashCode()", text);

        var typedEquals = ExtractMethod(text, "public bool Equals(Person? other)");
        Assert.Contains("other", typedEquals);
        Assert.Contains("Name", typedEquals);
        Assert.Contains("Age", typedEquals);
    }

    private static void AssertObjectEqualsDelegates(string text, string typeName)
    {
        var objectEquals = ExtractMethod(text, "public override bool Equals(object?");
        Assert.Contains($"obj is {typeName} other", objectEquals);
        Assert.Contains("Equals(other)", objectEquals);
        Assert.DoesNotContain("other.Name", objectEquals);
        Assert.DoesNotContain("other.Age", objectEquals);
        Assert.DoesNotContain("other.X", objectEquals);
    }

    private static string ExtractMethod(string text, string signature)
    {
        var start = text.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Generated source did not contain '{signature}':\n{text}");
        var open = text.IndexOf('{', start);
        Assert.True(open >= 0, $"Generated method '{signature}' had no body:\n{text}");
        var depth = 0;
        for (var i = open; i < text.Length; i++)
        {
            if (text[i] == '{')
                depth++;
            else if (text[i] == '}')
            {
                depth--;
                if (depth == 0)
                    return text[start..(i + 1)];
            }
        }

        return text[start..];
    }

    private static string NormalizeNewlines(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal);

    private sealed class TempWorkspace : IAsyncDisposable
    {
        public required string DirectoryPath { get; init; }
        public required string ProjectPath { get; init; }
        public required string SourcePath { get; init; }
        public required WorkspaceContext Context { get; init; }

        public static async Task<TempWorkspace> CreateAsync(string source, string fileName = "Person.cs")
        {
            Skip.IfNot(ModuleInitializer.MsBuildAvailable, ModuleInitializer.MsBuildError ?? "MSBuild not available");

            var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpGenerateEquals_" + Guid.NewGuid().ToString("N"));
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

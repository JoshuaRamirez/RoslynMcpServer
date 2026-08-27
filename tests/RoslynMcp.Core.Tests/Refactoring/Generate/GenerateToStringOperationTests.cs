using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Generate;
using RoslynMcp.Core.Workspace;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Generate;

/// <summary>
/// Operation-level tests for <see cref="GenerateToStringOperation"/>, including <c>format</c>
/// and <c>includeInheritedMembers</c>.
/// </summary>
public class GenerateToStringOperationTests
{
    private const string PersonSource = """
        namespace TestApp;

        public class Person
        {
            public string Name { get; set; }

            public int Age { get; set; }
        }
        """;

    #region Input Validation

    [Fact]
    public void Validate_MissingSourceFile_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            GenerateToStringOperation.Validate(new GenerateToStringParams
            {
                SourceFile = "",
                TypeName = "Person"
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public void Validate_MissingTypeName_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            GenerateToStringOperation.Validate(new GenerateToStringParams
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
            GenerateToStringOperation.Validate(new GenerateToStringParams
            {
                SourceFile = "Types.cs",
                TypeName = "Person"
            }));

        Assert.Equal(ErrorCodes.InvalidSourcePath, ex.ErrorCode);
    }

    [Fact]
    public void Validate_MissingFile_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            GenerateToStringOperation.Validate(new GenerateToStringParams
            {
                SourceFile = AbsoluteTestPath(),
                TypeName = "Person"
            }));

        Assert.Equal(ErrorCodes.SourceFileNotFound, ex.ErrorCode);
    }

    [Theory]
    [InlineData("json")]
    [InlineData("xml")]
    [InlineData("foo")]
    [InlineData("string_builder")]
    public void Validate_UnknownFormat_Throws(string format)
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            GenerateToStringOperation.Validate(new GenerateToStringParams
            {
                SourceFile = AbsoluteTestPath(),
                TypeName = "Person",
                Format = format
            }));

        Assert.Equal(ErrorCodes.InvalidToStringFormat, ex.ErrorCode);
        Assert.Equal("3143", ex.ErrorCode);
        Assert.Contains(format, ex.Message);
        Assert.Contains("interpolated", ex.Message);
        Assert.Contains("stringbuilder", ex.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("interpolated")]
    [InlineData("INTERPOLATED")]
    [InlineData("Interpolated")]
    [InlineData("stringbuilder")]
    [InlineData("STRINGBUILDER")]
    [InlineData("StringBuilder")]
    public void Validate_KnownOrOmittedFormat_DoesNotRejectFormat(string? format)
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            GenerateToStringOperation.Validate(new GenerateToStringParams
            {
                SourceFile = AbsoluteTestPath(),
                TypeName = "Person",
                Format = format
            }));

        Assert.Equal(ErrorCodes.SourceFileNotFound, ex.ErrorCode);
    }

    #endregion

    #region Interpolated (default)

    [SkippableFact]
    public async Task GenerateToString_FormatOmitted_WritesInterpolatedBody()
    {
        await using var workspace = await TempWorkspace.CreateAsync(PersonSource);
        var operation = new GenerateToStringOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateToStringParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Person"
        });

        Assert.True(result.Success);
        AssertInterpolatedToString(NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath)));
    }

    [SkippableFact]
    public async Task GenerateToString_FormatEmpty_WritesInterpolatedBody()
    {
        await using var workspace = await TempWorkspace.CreateAsync(PersonSource);
        var operation = new GenerateToStringOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateToStringParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Person",
            Format = ""
        });

        Assert.True(result.Success);
        AssertInterpolatedToString(NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath)));
    }

    [SkippableTheory]
    [InlineData("interpolated")]
    [InlineData("INTERPOLATED")]
    [InlineData("Interpolated")]
    public async Task GenerateToString_FormatInterpolated_WritesInterpolatedBody(string format)
    {
        await using var workspace = await TempWorkspace.CreateAsync(PersonSource);
        var operation = new GenerateToStringOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateToStringParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Person",
            Format = format
        });

        Assert.True(result.Success);
        AssertInterpolatedToString(NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath)));
    }

    [SkippableFact]
    public async Task GenerateToString_Default_StillIncludesProperties()
    {
        const string source = """
            namespace TestApp;

            public class Widget
            {
                public string _id;

                public string Name { get; set; }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Widget.cs");
        var operation = new GenerateToStringOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateToStringParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var toString = ExtractToStringMethod(updated);
        Assert.Contains("{_id}", toString);
        Assert.Contains("{Name}", toString);
    }

    [SkippableFact]
    public async Task GenerateToString_Default_DoesNotCollectInheritedMembers()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
                public string Species;
            }

            public class Dog : Animal
            {
                public string Name;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Dog.cs");
        var operation = new GenerateToStringOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateToStringParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var toString = ExtractToStringMethod(updated);
        Assert.Contains("{Name}", toString);
        Assert.DoesNotContain("Species", toString);
    }

    #endregion

    #region StringBuilder

    [SkippableTheory]
    [InlineData("stringbuilder")]
    [InlineData("STRINGBUILDER")]
    [InlineData("StringBuilder")]
    public async Task GenerateToString_FormatStringBuilder_WritesStringBuilderBody(string format)
    {
        await using var workspace = await TempWorkspace.CreateAsync(PersonSource);
        var operation = new GenerateToStringOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateToStringParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Person",
            Format = format
        });

        Assert.True(result.Success);
        AssertStringBuilderToString(NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath)), "Name", "Age");
    }

    [SkippableFact]
    public async Task GenerateToString_FormatStringBuilder_HonorsFields()
    {
        await using var workspace = await TempWorkspace.CreateAsync(PersonSource);
        var operation = new GenerateToStringOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateToStringParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Person",
            Fields = new[] { "Name" },
            Format = "stringbuilder"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        AssertStringBuilderToString(updated, "Name");
        Assert.DoesNotContain("Age = ", updated);
    }

    [SkippableFact]
    public async Task GenerateToString_FormatStringBuilder_FieldNamedSb_UsesThisQualification()
    {
        const string source = """
            namespace TestApp;

            public class Holder
            {
                public string sb;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Holder.cs");
        var operation = new GenerateToStringOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateToStringParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Holder",
            Format = "stringbuilder"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var toString = ExtractToStringMethod(updated);
        Assert.Contains("global::System.Text.StringBuilder", toString);
        Assert.Contains("Append(this.sb)", toString);
        Assert.DoesNotContain("Append(sb)", toString);
        Assert.Contains("sb.ToString()", toString);
    }

    [SkippableFact]
    public async Task GenerateToString_FormatStringBuilder_NamespaceNamedSystem_UsesGlobalQualifiedStringBuilder()
    {
        const string source = """
            namespace System;

            public class Widget
            {
                public string Name { get; set; }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Widget.cs");
        var operation = new GenerateToStringOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateToStringParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget",
            Format = "stringbuilder"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var toString = ExtractToStringMethod(updated);
        Assert.Contains("global::System.Text.StringBuilder", toString);
        Assert.Contains("Append(this.Name)", toString);
        Assert.DoesNotContain("new System.Text.StringBuilder", toString);
    }

    #endregion

    #region Preview

    [SkippableFact]
    public async Task GenerateToString_PreviewInterpolated_DoesNotWriteFiles()
    {
        await using var workspace = await TempWorkspace.CreateAsync(PersonSource);
        var operation = new GenerateToStringOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var result = await operation.ExecuteAsync(new GenerateToStringParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Person",
            Format = "interpolated",
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.NotEmpty(result.PendingChanges);
        AssertInterpolatedToString(result.PendingChanges[0].AfterSnippet!);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task GenerateToString_PreviewStringBuilder_DoesNotWriteFiles()
    {
        await using var workspace = await TempWorkspace.CreateAsync(PersonSource);
        var operation = new GenerateToStringOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var result = await operation.ExecuteAsync(new GenerateToStringParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Person",
            Format = "stringbuilder",
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.NotEmpty(result.PendingChanges);
        AssertStringBuilderToString(result.PendingChanges[0].AfterSnippet!, "Name", "Age");
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    #endregion

    #region includeInheritedMembers

    private const string DogOnAnimalSource = """
        namespace TestApp;

        public class Animal
        {
            public string Species;

            protected int Legs;

            private string Secret;

            public string Nickname { get; set; }
        }

        public class Dog : Animal
        {
            public string Name;
        }
        """;

    [SkippableFact]
    public async Task GenerateToString_IncludeInheritedMembersFalse_DoesNotIncludeBaseField()
    {
        await using var workspace = await TempWorkspace.CreateAsync(DogOnAnimalSource, "Dog.cs");
        var operation = new GenerateToStringOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateToStringParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            IncludeInheritedMembers = false
        });

        Assert.True(result.Success);
        var toString = ExtractToStringMethod(NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath)));
        Assert.Contains("{Name}", toString);
        Assert.DoesNotContain("Species", toString);
        Assert.DoesNotContain("Legs", toString);
        Assert.DoesNotContain("Secret", toString);
        Assert.DoesNotContain("Nickname", toString);
    }

    [SkippableFact]
    public async Task GenerateToString_IncludeInheritedMembersTrue_IncludesPublicAndProtectedBaseFields()
    {
        await using var workspace = await TempWorkspace.CreateAsync(DogOnAnimalSource, "Dog.cs");
        var operation = new GenerateToStringOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateToStringParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            IncludeInheritedMembers = true
        });

        Assert.True(result.Success);
        var toString = ExtractToStringMethod(NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath)));
        Assert.Contains("{Name}", toString);
        Assert.Contains("{Species}", toString);
        Assert.Contains("{Legs}", toString);
        Assert.Contains("{Nickname}", toString);
        Assert.DoesNotContain("Secret", toString);
    }

    [SkippableFact]
    public async Task GenerateToString_IncludeInheritedMembersTrue_SkipsPrivateBaseField()
    {
        await using var workspace = await TempWorkspace.CreateAsync(DogOnAnimalSource, "Dog.cs");
        var operation = new GenerateToStringOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateToStringParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            IncludeInheritedMembers = true
        });

        Assert.True(result.Success);
        var toString = ExtractToStringMethod(NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath)));
        Assert.DoesNotContain("Secret", toString);
        Assert.DoesNotContain("{Secret}", toString);
    }

    [SkippableFact]
    public async Task GenerateToString_IncludeInheritedMembersTrue_FieldsNamesInheritedMember_IncludesIt()
    {
        await using var workspace = await TempWorkspace.CreateAsync(DogOnAnimalSource, "Dog.cs");
        var operation = new GenerateToStringOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateToStringParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            IncludeInheritedMembers = true,
            Fields = new[] { "Species" }
        });

        Assert.True(result.Success);
        var toString = ExtractToStringMethod(NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath)));
        Assert.Contains("{Species}", toString);
        Assert.DoesNotContain("{Name}", toString);
        Assert.DoesNotContain("Legs", toString);
        Assert.DoesNotContain("Nickname", toString);
    }

    [SkippableFact]
    public async Task GenerateToString_IncludeInheritedMembersFalse_FieldsNamesInheritedMember_NotFound()
    {
        await using var workspace = await TempWorkspace.CreateAsync(DogOnAnimalSource, "Dog.cs");
        var operation = new GenerateToStringOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GenerateToStringParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Dog",
                IncludeInheritedMembers = false,
                Fields = new[] { "Species" }
            }));

        Assert.Equal(ErrorCodes.NoMembersToGenerate, ex.ErrorCode);
        Assert.Equal("3055", ex.ErrorCode);
        Assert.Equal(DogOnAnimalSource, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task GenerateToString_IncludeInheritedMembersTrue_ObjectOnlyBase_NoExtraMembers()
    {
        await using var workspace = await TempWorkspace.CreateAsync(PersonSource);
        var operation = new GenerateToStringOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateToStringParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Person",
            IncludeInheritedMembers = true
        });

        Assert.True(result.Success);
        var toString = ExtractToStringMethod(NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath)));
        Assert.Contains("{Name}", toString);
        Assert.Contains("{Age}", toString);
        Assert.DoesNotContain("Equals", toString);
        Assert.DoesNotContain("GetHashCode", toString);
        Assert.DoesNotContain("GetType", toString);
    }

    [SkippableFact]
    public async Task GenerateToString_IncludeInheritedMembersTrue_StringBuilder_IncludesInheritedMembers()
    {
        await using var workspace = await TempWorkspace.CreateAsync(DogOnAnimalSource, "Dog.cs");
        var operation = new GenerateToStringOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateToStringParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            IncludeInheritedMembers = true,
            Format = "stringbuilder"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var toString = ExtractToStringMethod(updated);
        Assert.Contains("global::System.Text.StringBuilder", toString);
        Assert.Contains("Append(this.Name)", toString);
        Assert.Contains("Append(this.Species)", toString);
        Assert.Contains("Append(this.Legs)", toString);
        Assert.Contains("Append(this.Nickname)", toString);
        Assert.DoesNotContain("Secret", toString);
        Assert.DoesNotContain("$\"Dog", toString);
    }

    [SkippableFact]
    public async Task GenerateToString_IncludeInheritedMembersTrue_Preview_DoesNotWriteFiles()
    {
        await using var workspace = await TempWorkspace.CreateAsync(DogOnAnimalSource, "Dog.cs");
        var operation = new GenerateToStringOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var result = await operation.ExecuteAsync(new GenerateToStringParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            IncludeInheritedMembers = true,
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.NotEmpty(result.PendingChanges);
        Assert.Contains("inherited members", result.PendingChanges[0].Description);
        Assert.Contains("interpolated", result.PendingChanges[0].Description);
        Assert.Contains("Name", result.PendingChanges[0].Description);
        Assert.Contains("Species", result.PendingChanges[0].Description);
        var snippet = result.PendingChanges[0].AfterSnippet!;
        Assert.Contains("{Name}", snippet);
        Assert.Contains("{Species}", snippet);
        Assert.Contains("{Legs}", snippet);
        Assert.Contains("{Nickname}", snippet);
        Assert.DoesNotContain("Secret", snippet);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    #endregion

    #region Helpers

    private static void AssertInterpolatedToString(string text)
    {
        Assert.Contains("public override string ToString()", text);
        Assert.DoesNotContain("StringBuilder", text);
        Assert.Contains("{Name}", text);
        Assert.Contains("{Age}", text);

        var toString = ExtractToStringMethod(text);
        Assert.Contains("$\"Person", toString);
        Assert.DoesNotContain("new System.Text.StringBuilder", toString);
    }

    private static string ExtractToStringMethod(string text)
    {
        var start = text.IndexOf("public override string ToString()", StringComparison.Ordinal);
        Assert.True(start >= 0, $"Generated source did not contain ToString:\n{text}");
        return text[start..];
    }

    private static void AssertStringBuilderToString(string text, params string[] members)
    {
        Assert.Contains("public override string ToString()", text);
        Assert.Contains("global::System.Text.StringBuilder", text);
        Assert.Contains("sb.ToString()", text);
        Assert.Contains("Person { ", text);
        foreach (var member in members)
        {
            Assert.Contains($"{member} = ", text);
            Assert.Contains($"Append(this.{member})", text);
        }

        Assert.DoesNotContain("$\"Person", text);
    }

    private static string AbsoluteTestPath() =>
        Path.Combine(Path.GetTempPath(), "RoslynMcpGenerateToStringMissing.cs");

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

            var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpGenerateToString_" + Guid.NewGuid().ToString("N"));
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

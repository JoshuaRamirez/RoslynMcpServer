using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Generate;
using RoslynMcp.Core.Workspace;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Generate;

/// <summary>
/// Operation-level tests for <see cref="GenerateToStringOperation"/>, including <c>format</c>,
/// <c>includeProperties</c>, <c>includeInheritedMembers</c>, and <c>replaceExisting</c>.
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

    [SkippableFact]
    public async Task GenerateToString_IncludeInheritedMembersTrue_CloserMethodHidesInheritedField()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
                public string Name;

                public string Species;
            }

            public class Dog : Animal
            {
                public string Extra;

                public string Name() => Extra;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Dog.cs");
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
        Assert.Contains("Append(this.Extra)", toString);
        Assert.Contains("Append(this.Species)", toString);
        Assert.DoesNotContain("Append(this.Name)", toString);
        Assert.DoesNotContain("Name = ", toString);
        Assert.DoesNotContain("{Name}", toString);
        AssertCompiles(updated);
    }

    [SkippableFact]
    public async Task GenerateToString_IncludeInheritedMembersTrue_InheritedMemberNamedToString_IsOmitted()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
                public string ToString;

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
            TypeName = "Dog",
            IncludeInheritedMembers = true,
            Format = "stringbuilder"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var toString = ExtractToStringMethod(updated);
        Assert.Contains("Append(this.Name)", toString);
        Assert.Contains("Append(this.Species)", toString);
        Assert.DoesNotContain("Append(this.ToString)", toString);
        Assert.DoesNotContain("ToString = ", toString);
        Assert.DoesNotContain("{ToString}", toString);
        Assert.Contains("sb.ToString()", toString);
    }

    #endregion

    #region includeProperties

    private const string WidgetWithFieldAndPropertySource = """
        namespace TestApp;

        public class Widget
        {
            public string _id;

            public string Name { get; set; }
        }
        """;

    [SkippableFact]
    public async Task GenerateToString_IncludePropertiesOmitted_IncludesFieldAndProperty()
    {
        await using var workspace = await TempWorkspace.CreateAsync(WidgetWithFieldAndPropertySource, "Widget.cs");
        var operation = new GenerateToStringOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateToStringParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget"
        });

        Assert.True(result.Success);
        var toString = ExtractToStringMethod(NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath)));
        Assert.Contains("{_id}", toString);
        Assert.Contains("{Name}", toString);
    }

    [SkippableFact]
    public async Task GenerateToString_IncludePropertiesTrue_IncludesFieldAndProperty()
    {
        await using var workspace = await TempWorkspace.CreateAsync(WidgetWithFieldAndPropertySource, "Widget.cs");
        var operation = new GenerateToStringOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateToStringParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget",
            IncludeProperties = true
        });

        Assert.True(result.Success);
        var toString = ExtractToStringMethod(NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath)));
        Assert.Contains("{_id}", toString);
        Assert.Contains("{Name}", toString);
    }

    [SkippableFact]
    public async Task GenerateToString_IncludePropertiesFalse_IncludesFieldOnly()
    {
        await using var workspace = await TempWorkspace.CreateAsync(WidgetWithFieldAndPropertySource, "Widget.cs");
        var operation = new GenerateToStringOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateToStringParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget",
            IncludeProperties = false
        });

        Assert.True(result.Success);
        var toString = ExtractToStringMethod(NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath)));
        Assert.Contains("{_id}", toString);
        Assert.DoesNotContain("{Name}", toString);
        Assert.DoesNotContain("Name = ", toString);
    }

    [SkippableFact]
    public async Task GenerateToString_IncludePropertiesFalse_EmptyFieldsList_IncludesFieldOnly()
    {
        await using var workspace = await TempWorkspace.CreateAsync(WidgetWithFieldAndPropertySource, "Widget.cs");
        var operation = new GenerateToStringOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateToStringParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget",
            Fields = Array.Empty<string>(),
            IncludeProperties = false
        });

        Assert.True(result.Success);
        var toString = ExtractToStringMethod(NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath)));
        Assert.Contains("{_id}", toString);
        Assert.DoesNotContain("{Name}", toString);
        Assert.DoesNotContain("Name = ", toString);
    }

    [SkippableFact]
    public async Task GenerateToString_IncludePropertiesFalse_PropertiesOnly_FailsWithNoMembersToGenerate()
    {
        await using var workspace = await TempWorkspace.CreateAsync(PersonSource);
        var operation = new GenerateToStringOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GenerateToStringParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Person",
                IncludeProperties = false
            }));

        Assert.Equal(ErrorCodes.NoMembersToGenerate, ex.ErrorCode);
        Assert.Equal("3055", ex.ErrorCode);
        Assert.Contains("No fields or properties", ex.Message);
        Assert.Equal(PersonSource, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task GenerateToString_IncludePropertiesFalse_FieldsNamesProperty_IncludesThatProperty()
    {
        await using var workspace = await TempWorkspace.CreateAsync(WidgetWithFieldAndPropertySource, "Widget.cs");
        var operation = new GenerateToStringOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateToStringParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget",
            Fields = new[] { "Name" },
            IncludeProperties = false
        });

        Assert.True(result.Success);
        var toString = ExtractToStringMethod(NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath)));
        Assert.Contains("{Name}", toString);
        Assert.DoesNotContain("{_id}", toString);
        Assert.DoesNotContain("_id = ", toString);
    }

    [SkippableFact]
    public async Task GenerateToString_IncludePropertiesFalse_IncludeInheritedMembersTrue_SkipsInheritedProperties()
    {
        await using var workspace = await TempWorkspace.CreateAsync(DogOnAnimalSource, "Dog.cs");
        var operation = new GenerateToStringOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateToStringParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            IncludeProperties = false,
            IncludeInheritedMembers = true
        });

        Assert.True(result.Success);
        var toString = ExtractToStringMethod(NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath)));
        Assert.Contains("{Name}", toString);
        Assert.Contains("{Species}", toString);
        Assert.Contains("{Legs}", toString);
        Assert.DoesNotContain("Nickname", toString);
        Assert.DoesNotContain("Secret", toString);
    }

    [SkippableFact]
    public async Task GenerateToString_IncludePropertiesFalse_FieldsNamesInheritedProperty_IncludesIt()
    {
        await using var workspace = await TempWorkspace.CreateAsync(DogOnAnimalSource, "Dog.cs");
        var operation = new GenerateToStringOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateToStringParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            IncludeProperties = false,
            IncludeInheritedMembers = true,
            Fields = new[] { "Nickname" }
        });

        Assert.True(result.Success);
        var toString = ExtractToStringMethod(NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath)));
        Assert.Contains("{Nickname}", toString);
        Assert.DoesNotContain("{Name}", toString);
        Assert.DoesNotContain("Species", toString);
        Assert.DoesNotContain("Legs", toString);
    }

    [SkippableFact]
    public async Task GenerateToString_IncludePropertiesTrue_StringBuilderAndInheritedMembers_StillWorks()
    {
        await using var workspace = await TempWorkspace.CreateAsync(DogOnAnimalSource, "Dog.cs");
        var operation = new GenerateToStringOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateToStringParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            IncludeProperties = true,
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
    public async Task GenerateToString_IncludePropertiesFalse_Preview_DoesNotWriteFiles_AndDescribesFieldOnly()
    {
        await using var workspace = await TempWorkspace.CreateAsync(WidgetWithFieldAndPropertySource, "Widget.cs");
        var operation = new GenerateToStringOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var result = await operation.ExecuteAsync(new GenerateToStringParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget",
            IncludeProperties = false,
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.NotEmpty(result.PendingChanges);
        Assert.Contains("_id", result.PendingChanges[0].Description);
        Assert.DoesNotContain("Name", result.PendingChanges[0].Description);
        var snippet = result.PendingChanges[0].AfterSnippet!;
        Assert.Contains("{_id}", snippet);
        Assert.DoesNotContain("{Name}", snippet);
        Assert.DoesNotContain("Name = ", snippet);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task GenerateToString_IncludePropertiesFalse_ReplaceExisting_StillWorks()
    {
        const string source = """
            namespace TestApp;

            public class Widget
            {
                public string _id;

                public string Name { get; set; }

                public override string ToString() => "old";
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Widget.cs");
        var operation = new GenerateToStringOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateToStringParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget",
            IncludeProperties = false,
            ReplaceExisting = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var toString = ExtractToStringMethod(updated);
        Assert.DoesNotContain("=> \"old\"", updated);
        Assert.Contains("{_id}", toString);
        Assert.DoesNotContain("{Name}", toString);
        Assert.Equal(1, CountOccurrences(updated, "public override string ToString()"));
    }

    [SkippableFact]
    public async Task GenerateToString_IncludePropertiesFalse_MemberNamedToString_IsOmitted()
    {
        const string source = """
            namespace TestApp;

            public class Widget
            {
                public string ToString;

                public string Name;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Widget.cs");
        var operation = new GenerateToStringOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateToStringParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget",
            IncludeProperties = false
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var toString = ExtractToStringMethod(updated);
        Assert.Contains("{Name}", toString);
        Assert.DoesNotContain("{ToString}", toString);
        Assert.DoesNotContain("ToString = ", toString);
    }

    #endregion

    #region replaceExisting

    private const string PersonWithToStringSource = """
        namespace TestApp;

        public class Person
        {
            public string Name { get; set; }

            public int Age { get; set; }

            public override string ToString() => "old";
        }
        """;

    [SkippableFact]
    public async Task GenerateToString_ReplaceExistingOmitted_ExistingToString_FailsWith3056()
    {
        await using var workspace = await TempWorkspace.CreateAsync(PersonWithToStringSource);
        var operation = new GenerateToStringOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GenerateToStringParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Person"
            }));

        Assert.Equal(ErrorCodes.AlreadyHasOverride, ex.ErrorCode);
        Assert.Equal("3056", ex.ErrorCode);
        Assert.Equal("Type already has a ToString override.", ex.Message);
        Assert.Equal(PersonWithToStringSource, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task GenerateToString_ReplaceExistingFalse_ExistingToString_FailsWith3056()
    {
        await using var workspace = await TempWorkspace.CreateAsync(PersonWithToStringSource);
        var operation = new GenerateToStringOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GenerateToStringParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Person",
                ReplaceExisting = false
            }));

        Assert.Equal(ErrorCodes.AlreadyHasOverride, ex.ErrorCode);
        Assert.Equal("3056", ex.ErrorCode);
        Assert.Equal("Type already has a ToString override.", ex.Message);
        Assert.Equal(PersonWithToStringSource, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task GenerateToString_ReplaceExistingTrue_ReplacesParameterlessToString()
    {
        await using var workspace = await TempWorkspace.CreateAsync(PersonWithToStringSource);
        var operation = new GenerateToStringOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateToStringParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Person",
            ReplaceExisting = true,
            Fields = new[] { "Name" }
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var toString = ExtractToStringMethod(updated);
        Assert.DoesNotContain("=> \"old\"", updated);
        Assert.DoesNotContain("old", toString);
        Assert.Contains("{Name}", toString);
        Assert.DoesNotContain("{Age}", toString);
        Assert.Equal(1, CountOccurrences(updated, "public override string ToString()"));
    }

    [SkippableFact]
    public async Task GenerateToString_ReplaceExistingTrue_NoExistingToString_GeneratesAsToday()
    {
        await using var workspace = await TempWorkspace.CreateAsync(PersonSource);
        var operation = new GenerateToStringOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateToStringParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Person",
            ReplaceExisting = true
        });

        Assert.True(result.Success);
        AssertInterpolatedToString(NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath)));
    }

    [SkippableFact]
    public async Task GenerateToString_ReplaceExistingTrue_ExistingToStringStringOverload_LeavesOverloadAndGeneratesParameterless()
    {
        const string source = """
            namespace TestApp;

            public class Person
            {
                public string Name { get; set; }

                public int Age { get; set; }

                public string ToString(string format) => format;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new GenerateToStringOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateToStringParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Person",
            ReplaceExisting = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("public string ToString(string format) => format;", updated);
        AssertInterpolatedToString(updated);
        Assert.Equal(1, CountOccurrences(updated, "public override string ToString()"));
        Assert.Equal(1, CountOccurrences(updated, "ToString(string format)"));
    }

    [SkippableFact]
    public async Task GenerateToString_ReplaceExistingTrue_PartialOtherFile_RemovesOtherPartToString()
    {
        const string fieldsPart = """
            namespace TestApp;

            public partial class Person
            {
                public string Name { get; set; }

                public int Age { get; set; }
            }
            """;

        const string toStringPart = """
            namespace TestApp;

            public partial class Person
            {
                public override string ToString() => "old";
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(
            ("Person.cs", fieldsPart),
            ("Person.ToString.cs", toStringPart));
        var otherPath = workspace.PathFor("Person.ToString.cs");
        var operation = new GenerateToStringOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateToStringParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Person",
            ReplaceExisting = true
        });

        Assert.True(result.Success);
        var selected = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var other = NormalizeNewlines(await File.ReadAllTextAsync(otherPath));
        AssertInterpolatedToString(selected);
        Assert.DoesNotContain("=> \"old\"", selected);
        Assert.DoesNotContain("public override string ToString", other);
        Assert.DoesNotContain("=> \"old\"", other);
        Assert.Equal(1, CountOccurrences(selected, "public override string ToString()"));
    }

    [SkippableFact]
    public async Task GenerateToString_ReplaceExistingTrue_StringBuilderAndInheritedMembers_StillWorks()
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

                public override string ToString() => "old";
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Dog.cs");
        var operation = new GenerateToStringOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateToStringParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            ReplaceExisting = true,
            Format = "stringbuilder",
            IncludeInheritedMembers = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var toString = ExtractToStringMethod(updated);
        Assert.DoesNotContain("=> \"old\"", updated);
        Assert.Contains("global::System.Text.StringBuilder", toString);
        Assert.Contains("Append(this.Name)", toString);
        Assert.Contains("Append(this.Species)", toString);
        Assert.DoesNotContain("$\"Dog", toString);
        Assert.Equal(1, CountOccurrences(updated, "public override string ToString()"));
    }

    [SkippableFact]
    public async Task GenerateToString_ReplaceExistingTrue_Preview_DoesNotWriteFiles_AndDescribesReplacement()
    {
        await using var workspace = await TempWorkspace.CreateAsync(PersonWithToStringSource);
        var operation = new GenerateToStringOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var result = await operation.ExecuteAsync(new GenerateToStringParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Person",
            ReplaceExisting = true,
            Format = "stringbuilder",
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.NotEmpty(result.PendingChanges);
        Assert.Contains("Replace", result.PendingChanges[0].Description);
        Assert.Contains("stringbuilder", result.PendingChanges[0].Description);
        Assert.Contains("Name", result.PendingChanges[0].Description);
        Assert.Contains("Age", result.PendingChanges[0].Description);
        Assert.Contains("replacing existing ToString", result.PendingChanges[0].BeforeSnippet);
        AssertStringBuilderToString(result.PendingChanges[0].AfterSnippet!, "Name", "Age");
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task GenerateToString_ReplaceExistingTrue_PartialOtherFile_Preview_DoesNotWriteFiles()
    {
        const string fieldsPart = """
            namespace TestApp;

            public partial class Person
            {
                public string Name { get; set; }

                public int Age { get; set; }
            }
            """;

        const string toStringPart = """
            namespace TestApp;

            public partial class Person
            {
                public override string ToString() => "old";
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(
            ("Person.cs", fieldsPart),
            ("Person.ToString.cs", toStringPart));
        var otherPath = workspace.PathFor("Person.ToString.cs");
        var operation = new GenerateToStringOperation(workspace.Context);
        var beforeSelected = await File.ReadAllTextAsync(workspace.SourcePath);
        var beforeOther = await File.ReadAllTextAsync(otherPath);

        var result = await operation.ExecuteAsync(new GenerateToStringParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Person",
            ReplaceExisting = true,
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.Equal(beforeSelected, await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Equal(beforeOther, await File.ReadAllTextAsync(otherPath));
    }

    private const string PersonWithGenericToStringSource = """
        namespace TestApp;

        public class Person
        {
            public string Name { get; set; }

            public string ToString<T>() => typeof(T).Name;
        }
        """;

    [SkippableFact]
    public async Task GenerateToString_ReplaceExistingOmitted_GenericToString_LeavesGenericAndGeneratesInstance()
    {
        await using var workspace = await TempWorkspace.CreateAsync(PersonWithGenericToStringSource);
        var operation = new GenerateToStringOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateToStringParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Person"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("public string ToString<T>() => typeof(T).Name;", updated);
        Assert.Contains("public override string ToString()", updated);
        Assert.Contains("{Name}", updated);
        Assert.Equal(1, CountOccurrences(updated, "ToString<T>()"));
        Assert.Equal(1, CountOccurrences(updated, "public override string ToString()"));
    }

    [SkippableFact]
    public async Task GenerateToString_ReplaceExistingFalse_GenericToString_LeavesGenericAndGeneratesInstance()
    {
        await using var workspace = await TempWorkspace.CreateAsync(PersonWithGenericToStringSource);
        var operation = new GenerateToStringOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateToStringParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Person",
            ReplaceExisting = false
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("public string ToString<T>() => typeof(T).Name;", updated);
        Assert.Contains("public override string ToString()", updated);
        Assert.Equal(1, CountOccurrences(updated, "ToString<T>()"));
    }

    [SkippableFact]
    public async Task GenerateToString_ReplaceExistingTrue_GenericToString_LeavesGenericAndGeneratesInstance()
    {
        await using var workspace = await TempWorkspace.CreateAsync(PersonWithGenericToStringSource);
        var operation = new GenerateToStringOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateToStringParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Person",
            ReplaceExisting = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("public string ToString<T>() => typeof(T).Name;", updated);
        Assert.Contains("public override string ToString()", updated);
        Assert.Contains("{Name}", updated);
        Assert.Equal(1, CountOccurrences(updated, "ToString<T>()"));
        Assert.Equal(1, CountOccurrences(updated, "public override string ToString()"));
    }

    [SkippableFact]
    public async Task GenerateToString_ReplaceExistingTrue_GenericPlusInstanceToString_ReplacesOnlyNonGeneric()
    {
        const string source = """
            namespace TestApp;

            public class Person
            {
                public string Name { get; set; }

                public string ToString<T>() => typeof(T).Name;

                public override string ToString() => "old";
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new GenerateToStringOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateToStringParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Person",
            ReplaceExisting = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("public string ToString<T>() => typeof(T).Name;", updated);
        Assert.DoesNotContain("=> \"old\"", updated);
        Assert.Contains("{Name}", updated);
        Assert.Equal(1, CountOccurrences(updated, "ToString<T>()"));
        Assert.Equal(1, CountOccurrences(updated, "public override string ToString()"));
    }

    private const string PersonWithStaticToStringSource = """
        namespace TestApp;

        public class Person
        {
            public string Name { get; set; }

            public static new string ToString() => "x";
        }
        """;

    [SkippableFact]
    public async Task GenerateToString_ReplaceExistingOmitted_StaticToString_FailsWith3056()
    {
        await using var workspace = await TempWorkspace.CreateAsync(PersonWithStaticToStringSource);
        var operation = new GenerateToStringOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GenerateToStringParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Person"
            }));

        Assert.Equal(ErrorCodes.AlreadyHasOverride, ex.ErrorCode);
        Assert.Equal("3056", ex.ErrorCode);
        Assert.Equal("Type already has a ToString override.", ex.Message);
        Assert.Equal(PersonWithStaticToStringSource, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task GenerateToString_ReplaceExistingFalse_StaticToString_FailsWith3056()
    {
        await using var workspace = await TempWorkspace.CreateAsync(PersonWithStaticToStringSource);
        var operation = new GenerateToStringOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GenerateToStringParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Person",
                ReplaceExisting = false
            }));

        Assert.Equal(ErrorCodes.AlreadyHasOverride, ex.ErrorCode);
        Assert.Equal(PersonWithStaticToStringSource, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task GenerateToString_ReplaceExistingTrue_StaticToString_RemovesStaticAndGeneratesInstance()
    {
        await using var workspace = await TempWorkspace.CreateAsync(PersonWithStaticToStringSource);
        var operation = new GenerateToStringOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateToStringParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Person",
            ReplaceExisting = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.DoesNotContain("static new string ToString()", updated);
        Assert.DoesNotContain("=> \"x\"", updated);
        Assert.Contains("public override string ToString()", updated);
        Assert.Contains("{Name}", updated);
        Assert.Equal(1, CountOccurrences(updated, "ToString()"));
        Assert.DoesNotContain("static", ExtractToStringMethod(updated));
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

    private static void AssertCompiles(string source)
    {
        var compilation = CSharpCompilation.Create(
                "ToStringCompileTest",
                new[] { CSharpSyntaxTree.ParseText(source) },
                new[]
                {
                    MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                    MetadataReference.CreateFromFile(typeof(System.Text.StringBuilder).Assembly.Location)
                },
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Select(d => d.ToString())
            .ToList();
        Assert.True(errors.Count == 0, "Generated ToString did not compile:\n" + string.Join("\n", errors) + "\n\n" + source);
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

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
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

        public string PathFor(string fileName) => Path.Combine(DirectoryPath, fileName);

        public static Task<TempWorkspace> CreateAsync(string source, string fileName = "Person.cs") =>
            CreateAsync((fileName, source));

        public static async Task<TempWorkspace> CreateAsync(params (string FileName, string Source)[] files)
        {
            Skip.IfNot(ModuleInitializer.MsBuildAvailable, ModuleInitializer.MsBuildError ?? "MSBuild not available");

            var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpGenerateToString_" + Guid.NewGuid().ToString("N"));
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

            string? sourcePath = null;
            foreach (var (fileName, source) in files)
            {
                var path = Path.Combine(directory, fileName);
                await File.WriteAllTextAsync(path, source);
                sourcePath ??= path;
            }

            sourcePath ??= Path.Combine(directory, "Person.cs");

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

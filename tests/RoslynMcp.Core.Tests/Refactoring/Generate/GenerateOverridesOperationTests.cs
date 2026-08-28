using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Generate;
using RoslynMcp.Core.Workspace;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Generate;

/// <summary>
/// Operation-level tests for <see cref="GenerateOverridesOperation"/>, including
/// <c>callBase</c>, <c>members</c>, <c>preview</c>, and <c>replaceExisting</c>.
/// </summary>
public class GenerateOverridesOperationTests
{
    private const string AnimalAndDogSource = """
        namespace TestApp;

        public class Animal
        {
            public virtual void Speak() { }

            public virtual string Label { get; set; }
        }

        public class Dog : Animal
        {
        }
        """;

    private const string DogWithToStringSource = """
        namespace TestApp;

        public class Animal
        {
            public virtual void Speak() { }

            public virtual string Label { get; set; }
        }

        public class Dog : Animal
        {
            public override string ToString() => "old";
        }
        """;

    private const string DogWithToStringAndSpeakSource = """
        namespace TestApp;

        public class Animal
        {
            public virtual void Speak() { }

            public virtual string Label { get; set; }
        }

        public class Dog : Animal
        {
            public override string ToString() => "old-tostring";

            public override void Speak() { /* old-speak */ }
        }
        """;

    #region Input Validation

    [Fact]
    public void Validate_MissingSourceFile_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            GenerateOverridesOperation.Validate(new GenerateOverridesParams
            {
                SourceFile = "",
                TypeName = "Dog"
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public void Validate_MissingTypeName_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            GenerateOverridesOperation.Validate(new GenerateOverridesParams
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
            GenerateOverridesOperation.Validate(new GenerateOverridesParams
            {
                SourceFile = "Types.cs",
                TypeName = "Dog"
            }));

        Assert.Equal(ErrorCodes.InvalidSourcePath, ex.ErrorCode);
    }

    [Fact]
    public void Validate_MissingFile_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            GenerateOverridesOperation.Validate(new GenerateOverridesParams
            {
                SourceFile = AbsoluteTestPath(),
                TypeName = "Dog"
            }));

        Assert.Equal(ErrorCodes.SourceFileNotFound, ex.ErrorCode);
    }

    [Fact]
    public void ReplaceExisting_DefaultsToFalse()
    {
        var @params = new GenerateOverridesParams
        {
            SourceFile = AbsoluteTestPath(),
            TypeName = "Dog"
        };

        Assert.False(@params.ReplaceExisting);
    }

    [Fact]
    public void CallBase_DefaultsToTrue()
    {
        var @params = new GenerateOverridesParams
        {
            SourceFile = AbsoluteTestPath(),
            TypeName = "Dog"
        };

        Assert.True(@params.CallBase);
    }

    #endregion

    #region callBase / members / preview regressions

    [SkippableFact]
    public async Task GenerateOverrides_CallBaseTrue_EmitsBaseCallForVirtual()
    {
        await using var workspace = await TempWorkspace.CreateAsync(AnimalAndDogSource, "Dog.cs");
        var operation = new GenerateOverridesOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateOverridesParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            Members = new[] { "Speak" },
            CallBase = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("base.Speak()", updated);
        Assert.DoesNotContain("throw new NotImplementedException()", ExtractMember(updated, "void Speak()"));
    }

    [SkippableFact]
    public async Task GenerateOverrides_CallBaseOmitted_DefaultsToTrue()
    {
        await using var workspace = await TempWorkspace.CreateAsync(AnimalAndDogSource, "Dog.cs");
        var operation = new GenerateOverridesOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateOverridesParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            Members = new[] { "Speak" }
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("base.Speak()", updated);
    }

    [SkippableFact]
    public async Task GenerateOverrides_CallBaseFalse_HasNoBaseCall()
    {
        await using var workspace = await TempWorkspace.CreateAsync(AnimalAndDogSource, "Dog.cs");
        var operation = new GenerateOverridesOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateOverridesParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            Members = new[] { "Speak" },
            CallBase = false
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("public override void Speak()", updated);
        Assert.DoesNotContain("base.", updated);
    }

    [SkippableFact]
    public async Task GenerateOverrides_Members_OnlyNamedMembersAreGenerated()
    {
        await using var workspace = await TempWorkspace.CreateAsync(AnimalAndDogSource, "Dog.cs");
        var operation = new GenerateOverridesOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateOverridesParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            Members = new[] { "Speak" }
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("public override void Speak()", updated);
        Assert.DoesNotContain("public override string Label", updated);
        Assert.DoesNotContain("public override string ToString()", updated);
        Assert.DoesNotContain("public override bool Equals(", updated);
        Assert.DoesNotContain("public override int GetHashCode()", updated);
    }

    [SkippableFact]
    public async Task GenerateOverrides_UnknownMember_OverrideTargetNotFound_WritesNothing()
    {
        await using var workspace = await TempWorkspace.CreateAsync(AnimalAndDogSource, "Dog.cs");
        var operation = new GenerateOverridesOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GenerateOverridesParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Dog",
                Members = new[] { "DoesNotExist" }
            }));

        Assert.Equal(ErrorCodes.OverrideTargetNotFound, ex.ErrorCode);
        Assert.Equal("2018", ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task GenerateOverrides_Preview_WritesNothing_AndDescribesGeneration()
    {
        await using var workspace = await TempWorkspace.CreateAsync(AnimalAndDogSource, "Dog.cs");
        var operation = new GenerateOverridesOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var result = await operation.ExecuteAsync(new GenerateOverridesParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            Members = new[] { "Speak" },
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.NotEmpty(result.PendingChanges);
        Assert.Contains("Generate overrides for:", result.PendingChanges[0].Description);
        Assert.Contains("Speak", result.PendingChanges[0].Description);
        Assert.DoesNotContain("replace existing", result.PendingChanges[0].Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("base.Speak()", result.PendingChanges[0].AfterSnippet);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task GenerateOverrides_OmittedMembers_GeneratesMissingOverrides()
    {
        await using var workspace = await TempWorkspace.CreateAsync(AnimalAndDogSource, "Dog.cs");
        var operation = new GenerateOverridesOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateOverridesParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("public override void Speak()", updated);
        Assert.Contains("base.Speak()", updated);
        Assert.Contains("public override string Label", updated);
        Assert.Contains("public override string ToString()", updated);
        Assert.Contains("base.ToString()", updated);
        Assert.Contains("public override bool Equals(", updated);
        Assert.Contains("public override int GetHashCode()", updated);
    }

    #endregion

    #region replaceExisting false / omitted

    [SkippableFact]
    public async Task GenerateOverrides_ReplaceExistingOmitted_ExistingToString_IsSkipped()
    {
        await using var workspace = await TempWorkspace.CreateAsync(DogWithToStringSource, "Dog.cs");
        var operation = new GenerateOverridesOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateOverridesParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("=> \"old\"", updated);
        Assert.Equal(1, CountOccurrences(updated, "public override string ToString()"));
        Assert.DoesNotContain("base.ToString()", updated);
        Assert.Contains("public override void Speak()", updated);
        Assert.Contains("public override string Label", updated);
    }

    [SkippableFact]
    public async Task GenerateOverrides_ReplaceExistingFalse_ExistingToString_IsSkipped()
    {
        await using var workspace = await TempWorkspace.CreateAsync(DogWithToStringSource, "Dog.cs");
        var operation = new GenerateOverridesOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateOverridesParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            ReplaceExisting = false
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("=> \"old\"", updated);
        Assert.Equal(1, CountOccurrences(updated, "public override string ToString()"));
        Assert.DoesNotContain("base.ToString()", updated);
        Assert.Contains("public override void Speak()", updated);
        Assert.Contains("public override string Label", updated);
    }

    [SkippableFact]
    public async Task GenerateOverrides_ReplaceExistingFalse_NamedExistingToString_OverrideTargetNotFound_WritesNothing()
    {
        await using var workspace = await TempWorkspace.CreateAsync(DogWithToStringSource, "Dog.cs");
        var operation = new GenerateOverridesOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GenerateOverridesParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Dog",
                Members = new[] { "ToString" },
                ReplaceExisting = false
            }));

        Assert.Equal(ErrorCodes.OverrideTargetNotFound, ex.ErrorCode);
        Assert.Equal("2018", ex.ErrorCode);
        Assert.Contains("ToString", ex.Message);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    #endregion

    #region replaceExisting true

    [SkippableFact]
    public async Task GenerateOverrides_ReplaceExistingTrue_OmittedMembers_ReplacesToString_AndAddsMissing()
    {
        await using var workspace = await TempWorkspace.CreateAsync(DogWithToStringSource, "Dog.cs");
        var operation = new GenerateOverridesOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateOverridesParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            ReplaceExisting = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.DoesNotContain("=> \"old\"", updated);
        Assert.Equal(1, CountOccurrences(updated, "public override string ToString()"));
        Assert.Contains("base.ToString()", updated);
        Assert.Contains("public override void Speak()", updated);
        Assert.Contains("base.Speak()", updated);
        Assert.Contains("public override string Label", updated);
        Assert.Contains("public override bool Equals(", updated);
        Assert.Contains("public override int GetHashCode()", updated);
    }

    [SkippableFact]
    public async Task GenerateOverrides_ReplaceExistingTrue_NamedToString_OnlyReplacesToString()
    {
        await using var workspace = await TempWorkspace.CreateAsync(DogWithToStringAndSpeakSource, "Dog.cs");
        var operation = new GenerateOverridesOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateOverridesParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            Members = new[] { "ToString" },
            ReplaceExisting = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.DoesNotContain("old-tostring", updated);
        Assert.Contains("base.ToString()", updated);
        Assert.Equal(1, CountOccurrences(updated, "public override string ToString()"));
        Assert.Contains("old-speak", updated);
        Assert.Equal(1, CountOccurrences(updated, "public override void Speak()"));
        Assert.DoesNotContain("public override string Label", updated);
        Assert.DoesNotContain("public override bool Equals(", updated);
        Assert.DoesNotContain("public override int GetHashCode()", updated);
    }

    [SkippableFact]
    public async Task GenerateOverrides_ReplaceExistingTrue_NoExistingOverrides_GeneratesAsToday()
    {
        await using var workspace = await TempWorkspace.CreateAsync(AnimalAndDogSource, "Dog.cs");
        var operation = new GenerateOverridesOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateOverridesParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            ReplaceExisting = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("public override void Speak()", updated);
        Assert.Contains("base.Speak()", updated);
        Assert.Contains("public override string Label", updated);
        Assert.Contains("public override string ToString()", updated);
        Assert.Contains("base.ToString()", updated);
        Assert.Contains("public override bool Equals(", updated);
        Assert.Contains("public override int GetHashCode()", updated);
    }

    [SkippableFact]
    public async Task GenerateOverrides_ReplaceExistingTrue_CallBaseFalse_ReplacedMethodHasNoBaseCall()
    {
        await using var workspace = await TempWorkspace.CreateAsync(DogWithToStringSource, "Dog.cs");
        var operation = new GenerateOverridesOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateOverridesParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            Members = new[] { "ToString" },
            ReplaceExisting = true,
            CallBase = false
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.DoesNotContain("=> \"old\"", updated);
        Assert.Contains("public override string ToString()", updated);
        Assert.DoesNotContain("base.", updated);
    }

    [SkippableFact]
    public async Task GenerateOverrides_ReplaceExistingTrue_AbstractBaseMethod_ReplacesWithNotImplemented()
    {
        const string source = """
            namespace TestApp;

            public abstract class Animal
            {
                public abstract void Work();
            }

            public class Dog : Animal
            {
                public override void Work() { /* old-work */ }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Dog.cs");
        var operation = new GenerateOverridesOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateOverridesParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            Members = new[] { "Work" },
            ReplaceExisting = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.DoesNotContain("old-work", updated);
        Assert.Contains("public override void Work()", updated);
        Assert.Contains("throw new NotImplementedException()", ExtractMember(updated, "void Work()"));
        Assert.DoesNotContain("base.Work()", updated);
    }

    [SkippableFact]
    public async Task GenerateOverrides_ReplaceExistingTrue_PropertyOverride_IsReplaced()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
                public virtual string Label { get; set; }
            }

            public class Dog : Animal
            {
                public override string Label { get; set; } = "old-label";
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Dog.cs");
        var operation = new GenerateOverridesOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateOverridesParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            Members = new[] { "Label" },
            ReplaceExisting = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.DoesNotContain("old-label", updated);
        Assert.Equal(1, CountOccurrences(updated, "public override string Label"));
    }

    [SkippableFact]
    public async Task GenerateOverrides_ReplaceExistingTrue_PartialOtherFile_RemovesThere_InsertsOnTarget()
    {
        const string typePart = """
            namespace TestApp;

            public class Animal
            {
                public virtual void Speak() { }
            }

            public partial class Dog : Animal
            {
            }
            """;

        const string overridePart = """
            namespace TestApp;

            public partial class Dog
            {
                public override string ToString() => "old";
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(
            ("Dog.cs", typePart),
            ("Dog.Overrides.cs", overridePart));
        var otherPath = workspace.PathFor("Dog.Overrides.cs");
        var operation = new GenerateOverridesOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateOverridesParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            Members = new[] { "ToString" },
            ReplaceExisting = true
        });

        Assert.True(result.Success);
        var selected = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var other = NormalizeNewlines(await File.ReadAllTextAsync(otherPath));
        Assert.Contains("public override string ToString()", selected);
        Assert.Contains("base.ToString()", selected);
        Assert.DoesNotContain("=> \"old\"", selected);
        Assert.DoesNotContain("public override string ToString", other);
        Assert.DoesNotContain("=> \"old\"", other);
        Assert.Equal(1, CountOccurrences(selected, "public override string ToString()"));
    }

    [SkippableFact]
    public async Task GenerateOverrides_ReplaceExistingTrue_NewHidingMethod_Named_OverrideTargetNotFound()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
                public virtual void Speak() { }
            }

            public class Dog : Animal
            {
                public new string ToString() => "hid";
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Dog.cs");
        var operation = new GenerateOverridesOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GenerateOverridesParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Dog",
                Members = new[] { "ToString" },
                ReplaceExisting = true
            }));

        Assert.Equal(ErrorCodes.OverrideTargetNotFound, ex.ErrorCode);
        Assert.Equal("2018", ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task GenerateOverrides_ReplaceExistingTrue_NewHidingMethod_IsNotReplaced()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
                public virtual void Speak() { }
            }

            public class Dog : Animal
            {
                public new string ToString() => "hid";
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Dog.cs");
        var operation = new GenerateOverridesOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateOverridesParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            Members = new[] { "Speak" },
            ReplaceExisting = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("public new string ToString() => \"hid\";", updated);
        Assert.DoesNotContain("public override string ToString()", updated);
        Assert.Contains("public override void Speak()", updated);
    }

    [SkippableFact]
    public async Task GenerateOverrides_ReplaceExistingTrue_Preview_WritesNothing_AndMentionsReplacement()
    {
        await using var workspace = await TempWorkspace.CreateAsync(DogWithToStringSource, "Dog.cs");
        var operation = new GenerateOverridesOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var result = await operation.ExecuteAsync(new GenerateOverridesParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            ReplaceExisting = true,
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.NotEmpty(result.PendingChanges);
        Assert.Contains("replace existing overrides", result.PendingChanges[0].Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ToString", result.PendingChanges[0].Description);
        Assert.Contains("Speak", result.PendingChanges[0].Description);
        Assert.Contains("replacing existing overrides", result.PendingChanges[0].BeforeSnippet);
        Assert.Contains("base.ToString()", result.PendingChanges[0].AfterSnippet);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task GenerateOverrides_ReplaceExistingTrue_DoesNotCopySealedOrAttributes()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
                public virtual void Speak() { }
            }

            public class Dog : Animal
            {
                [Obsolete]
                public sealed override string ToString() => "old";
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Dog.cs");
        var operation = new GenerateOverridesOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateOverridesParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            Members = new[] { "ToString" },
            ReplaceExisting = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var toString = ExtractMember(updated, "string ToString()");
        Assert.DoesNotContain("sealed", toString);
        Assert.DoesNotContain("Obsolete", toString);
        Assert.DoesNotContain("=> \"old\"", updated);
        Assert.Contains("base.ToString()", toString);
    }

    [SkippableFact]
    public async Task GenerateOverrides_ReplaceExistingTrue_AmbiguousSameName_OverrideExists_WritesNothing()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
                public virtual void Handle(int x) { }

                public virtual void Handle(string s) { }

                public virtual void Handle(object o) { }
            }

            public class Dog : Animal
            {
                public override void Handle(int x) { }

                public override void Handle(string s) { }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Dog.cs");
        var operation = new GenerateOverridesOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GenerateOverridesParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Dog",
                ReplaceExisting = true
            }));

        Assert.Equal(ErrorCodes.OverrideExists, ex.ErrorCode);
        Assert.Equal("3152", ex.ErrorCode);
        Assert.Contains("Handle", ex.Message);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task GenerateOverrides_ReplaceExistingTrue_UnknownName_OverrideTargetNotFound_WritesNothing()
    {
        await using var workspace = await TempWorkspace.CreateAsync(DogWithToStringSource, "Dog.cs");
        var operation = new GenerateOverridesOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GenerateOverridesParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Dog",
                Members = new[] { "DoesNotExist" },
                ReplaceExisting = true
            }));

        Assert.Equal(ErrorCodes.OverrideTargetNotFound, ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    #endregion

    #region Helpers

    private static string AbsoluteTestPath(string extension = ".cs") =>
        OperatingSystem.IsWindows()
            ? $"C:\\test\\file{extension}"
            : $"/test/file{extension}";

    private static string NormalizeNewlines(string text) => text.Replace("\r\n", "\n");

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }

    private static string ExtractMember(string source, string signatureMarker)
    {
        var start = source.IndexOf(signatureMarker, StringComparison.Ordinal);
        if (start < 0)
            return string.Empty;

        var open = source.IndexOf('{', start);
        if (open < 0)
            return source[start..];

        var depth = 0;
        for (var i = open; i < source.Length; i++)
        {
            if (source[i] == '{')
                depth++;
            else if (source[i] == '}')
            {
                depth--;
                if (depth == 0)
                    return source[start..(i + 1)];
            }
        }

        return source[start..];
    }

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

            var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpGenerateOverrides_" + Guid.NewGuid().ToString("N"));
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

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
    public async Task GenerateOverrides_CallBaseTrue_VirtualProperty_EmitsBaseAccessors()
    {
        await using var workspace = await TempWorkspace.CreateAsync(AnimalAndDogSource, "Dog.cs");
        var operation = new GenerateOverridesOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateOverridesParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            Members = new[] { "Label" },
            CallBase = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var label = ExtractMember(updated, "public override string Label");
        Assert.Contains("return base.Label;", label);
        Assert.Contains("base.Label = value;", label);
        Assert.DoesNotContain("throw new NotImplementedException()", label);
    }

    [SkippableFact]
    public async Task GenerateOverrides_CallBaseOmitted_VirtualProperty_EmitsBaseAccessors()
    {
        await using var workspace = await TempWorkspace.CreateAsync(AnimalAndDogSource, "Dog.cs");
        var operation = new GenerateOverridesOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateOverridesParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            Members = new[] { "Label" }
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var label = ExtractMember(updated, "public override string Label");
        Assert.Contains("return base.Label;", label);
        Assert.Contains("base.Label = value;", label);
        Assert.DoesNotContain("throw new NotImplementedException()", label);
    }

    [SkippableFact]
    public async Task GenerateOverrides_CallBaseTrue_VirtualPropertyGetOnly_EmitsBaseGetter()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
                public virtual string Label { get { return "base"; } }
            }

            public class Dog : Animal
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Dog.cs");
        var operation = new GenerateOverridesOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateOverridesParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            Members = new[] { "Label" },
            CallBase = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var label = ExtractMember(updated, "public override string Label");
        Assert.Contains("return base.Label;", label);
        Assert.DoesNotContain("set", label);
        Assert.DoesNotContain("base.Label = value;", label);
    }

    [SkippableFact]
    public async Task GenerateOverrides_CallBaseFalse_VirtualProperty_HasNoBaseAccessors()
    {
        await using var workspace = await TempWorkspace.CreateAsync(AnimalAndDogSource, "Dog.cs");
        var operation = new GenerateOverridesOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateOverridesParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            Members = new[] { "Label" },
            CallBase = false
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var label = ExtractMember(updated, "public override string Label");
        Assert.DoesNotContain("base.Label", label);
        Assert.Contains("return null;", label);
        Assert.DoesNotContain("throw new NotImplementedException()", label);
    }

    [SkippableFact]
    public async Task GenerateOverrides_CallBaseTrue_AbstractProperty_StillThrows()
    {
        const string source = """
            namespace TestApp;

            public abstract class Animal
            {
                public abstract string Label { get; set; }
            }

            public class Dog : Animal
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Dog.cs");
        var operation = new GenerateOverridesOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateOverridesParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            Members = new[] { "Label" },
            CallBase = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var label = ExtractMember(updated, "public override string Label");
        Assert.Contains("throw new NotImplementedException()", label);
        Assert.DoesNotContain("base.Label", label);
    }

    [SkippableFact]
    public async Task GenerateOverrides_CallBaseTrue_VirtualIndexer_EmitsBaseAccessors()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
                public virtual string this[int i]
                {
                    get { return ""; }
                    set { }
                }
            }

            public class Dog : Animal
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Dog.cs");
        var operation = new GenerateOverridesOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateOverridesParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            Members = new[] { "this[]" },
            CallBase = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var indexer = ExtractMember(updated, "public override string this[int i]");
        Assert.Contains("return base[i];", indexer);
        Assert.Contains("base[i] = value;", indexer);
        Assert.DoesNotContain("throw new NotImplementedException()", indexer);
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
        Assert.False(HasOverrideToString(updated));
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
        Assert.DoesNotContain("property accessors", result.PendingChanges[0].Description, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task GenerateOverrides_Preview_Property_DescribesBaseAccessors_WritesNothing()
    {
        await using var workspace = await TempWorkspace.CreateAsync(AnimalAndDogSource, "Dog.cs");
        var operation = new GenerateOverridesOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var result = await operation.ExecuteAsync(new GenerateOverridesParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            Members = new[] { "Label" },
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.NotEmpty(result.PendingChanges);
        Assert.Contains("Generate overrides for:", result.PendingChanges[0].Description);
        Assert.Contains("Label", result.PendingChanges[0].Description);
        Assert.Contains("; property accessors will call base", result.PendingChanges[0].Description, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("non-abstract", result.PendingChanges[0].Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("return base.Label;", result.PendingChanges[0].AfterSnippet);
        Assert.Contains("base.Label = value;", result.PendingChanges[0].AfterSnippet);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task GenerateOverrides_Preview_CallBaseTrue_MixedAbstractAndVirtualProperties_QualifiesBaseNote_WritesNothing()
    {
        const string source = """
            namespace TestApp;

            public abstract class Animal
            {
                public abstract string Title { get; set; }

                public virtual string Label { get; set; }
            }

            public class Dog : Animal
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Dog.cs");
        var operation = new GenerateOverridesOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var result = await operation.ExecuteAsync(new GenerateOverridesParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            Members = new[] { "Title", "Label" },
            CallBase = true,
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.NotEmpty(result.PendingChanges);
        var description = result.PendingChanges[0].Description;
        Assert.Contains("Title", description);
        Assert.Contains("Label", description);
        Assert.Contains("non-abstract property accessors will call base", description, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("; property accessors will call base", description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("return base.Label;", result.PendingChanges[0].AfterSnippet);
        Assert.Contains("throw new NotImplementedException()", result.PendingChanges[0].AfterSnippet);
        Assert.DoesNotContain("base.Title", result.PendingChanges[0].AfterSnippet);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task GenerateOverrides_Preview_CallBaseTrue_AllAbstractProperties_DescribesNoBaseAccessors_WritesNothing()
    {
        const string source = """
            namespace TestApp;

            public abstract class Animal
            {
                public abstract string Title { get; set; }

                public abstract string Label { get; set; }
            }

            public class Dog : Animal
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Dog.cs");
        var operation = new GenerateOverridesOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var result = await operation.ExecuteAsync(new GenerateOverridesParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            Members = new[] { "Title", "Label" },
            CallBase = true,
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        var description = result.PendingChanges![0].Description;
        Assert.Contains("; property accessors will not call base", description, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("non-abstract", description, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("base.Label", result.PendingChanges[0].AfterSnippet);
        Assert.DoesNotContain("base.Title", result.PendingChanges[0].AfterSnippet);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task GenerateOverrides_Preview_CallBaseFalse_Property_DescribesNoBaseAccessors_WritesNothing()
    {
        await using var workspace = await TempWorkspace.CreateAsync(AnimalAndDogSource, "Dog.cs");
        var operation = new GenerateOverridesOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var result = await operation.ExecuteAsync(new GenerateOverridesParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            Members = new[] { "Label" },
            CallBase = false,
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.Contains("property accessors will not call base", result.PendingChanges![0].Description, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("base.Label", result.PendingChanges[0].AfterSnippet);
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
        Assert.True(HasOverrideToString(updated));
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
        Assert.Equal(1, CountOverrideToString(updated));
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
        Assert.Equal(1, CountOverrideToString(updated));
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
        Assert.Equal(1, CountOverrideToString(updated));
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
        Assert.Equal(1, CountOverrideToString(updated));
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
        Assert.True(HasOverrideToString(updated));
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
        Assert.True(HasOverrideToString(updated));
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
        var label = ExtractMember(updated, "public override string Label");
        Assert.Contains("return base.Label;", label);
        Assert.Contains("base.Label = value;", label);
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
        Assert.True(HasOverrideToString(selected));
        Assert.Contains("base.ToString()", selected);
        Assert.DoesNotContain("=> \"old\"", selected);
        Assert.False(HasOverrideToString(other));
        Assert.DoesNotContain("=> \"old\"", other);
        Assert.Equal(1, CountOverrideToString(selected));
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
        Assert.False(HasOverrideToString(updated));
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
        var toString = ExtractOverrideToString(updated);
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
    public async Task GenerateOverrides_ReplaceExistingTrue_ProtectedMethod_KeepsProtectedAccessibility()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
                protected virtual void Speak() { }
            }

            public class Dog : Animal
            {
                protected override void Speak() { /* old-speak */ }
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
        Assert.DoesNotContain("old-speak", updated);
        Assert.Contains("protected override void Speak()", updated);
        Assert.DoesNotContain("public override void Speak()", updated);
        Assert.Contains("base.Speak()", updated);
        Assert.Equal(1, CountOccurrences(updated, "override void Speak()"));
    }

    [SkippableFact]
    public async Task GenerateOverrides_ReplaceExistingTrue_ProtectedProperty_KeepsProtectedAccessibility()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
                protected virtual string Label { get; set; }
            }

            public class Dog : Animal
            {
                protected override string Label { get; set; } = "old-label";
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
        Assert.Contains("protected override string Label", updated);
        Assert.DoesNotContain("public override string Label", updated);
        Assert.Equal(1, CountOccurrences(updated, "override string Label"));
    }

    [SkippableFact]
    public async Task GenerateOverrides_ReplaceExistingTrue_RefOutInParameters_PreservedOnSignatureAndBaseCall()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
                public virtual void Mutate(ref int x, out int y, in int z) { y = x; }
            }

            public class Dog : Animal
            {
                public override void Mutate(ref int x, out int y, in int z) { y = x; /* old-mutate */ }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Dog.cs");
        var operation = new GenerateOverridesOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateOverridesParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            Members = new[] { "Mutate" },
            ReplaceExisting = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.DoesNotContain("old-mutate", updated);
        Assert.Contains("override void Mutate(ref int x, out int y, in int z)", updated);
        Assert.DoesNotContain("override void Mutate(int x", updated);
        var mutate = ExtractMember(updated, "override void Mutate(");
        Assert.Contains("base.Mutate", mutate);
        Assert.Contains("ref x", mutate);
        Assert.Contains("out y", mutate);
        Assert.Contains("in z", mutate);
        Assert.Equal(1, CountOccurrences(updated, "override void Mutate("));
    }

    [SkippableFact]
    public async Task GenerateOverrides_ReplaceExistingTrue_GenericMethod_ReplacesOverride()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
                public virtual void Handle<T>(T value) { }
            }

            public class Dog : Animal
            {
                public override void Handle<T>(T value) { /* old-generic */ }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Dog.cs");
        var operation = new GenerateOverridesOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateOverridesParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            Members = new[] { "Handle" },
            ReplaceExisting = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.DoesNotContain("old-generic", updated);
        Assert.Contains("override void Handle<T>(T value)", updated);
        Assert.Equal(1, CountOccurrences(updated, "override void Handle"));
        Assert.Contains("base.Handle(value)", updated);
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

    #region Events

    [SkippableFact]
    public async Task GenerateOverrides_VirtualEvent_AddsOverrideStub()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MissingVirtualEventSource, "Dog.cs");
        var operation = new GenerateOverridesOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateOverridesParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            Members = new[] { "Changed" }
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("public override event System.EventHandler Changed", updated);
        var evt = ExtractMember(updated, "public override event System.EventHandler Changed");
        Assert.Contains("add", evt);
        Assert.Contains("remove", evt);
        Assert.DoesNotContain("NotImplementedException", evt);
        Assert.DoesNotContain("base.", evt);
        Assert.Equal(1, CountOccurrences(
            updated[updated.IndexOf("public class Dog", StringComparison.Ordinal)..],
            "event System.EventHandler Changed"));
        AssertCompiles(updated);
    }

    [SkippableFact]
    public async Task GenerateOverrides_AbstractEvent_AddsOverrideStub()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MissingAbstractEventSource, "Dog.cs");
        var operation = new GenerateOverridesOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateOverridesParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            Members = new[] { "Changed" }
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("public override event System.EventHandler Changed", updated);
        var evt = ExtractMember(updated, "public override event System.EventHandler Changed");
        Assert.Contains("add", evt);
        Assert.Contains("remove", evt);
        Assert.DoesNotContain("NotImplementedException", evt);
        Assert.DoesNotContain("base.", evt);
        AssertCompiles(updated);
    }

    [SkippableFact]
    public async Task GenerateOverrides_OnlyEvents_Succeeds()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
                public virtual event System.EventHandler Changed;
                public virtual event System.EventHandler Resized;
            }

            public class Dog : Animal
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Dog.cs");
        var operation = new GenerateOverridesOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateOverridesParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            Members = new[] { "Changed", "Resized" }
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("public override event System.EventHandler Changed", updated);
        Assert.Contains("public override event System.EventHandler Resized", updated);
        Assert.DoesNotContain("NotImplementedException", updated);
        Assert.False(HasOverrideToString(updated));
        AssertCompiles(updated);
    }

    [SkippableFact]
    public async Task GenerateOverrides_MembersFilter_Event_GeneratesRequestedOnly()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
                public virtual event System.EventHandler Changed;
                public virtual event System.EventHandler Resized;
                public virtual void Speak() { }
            }

            public class Dog : Animal
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Dog.cs");
        var operation = new GenerateOverridesOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateOverridesParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            Members = new[] { "Changed" }
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("public override event System.EventHandler Changed", updated);
        Assert.DoesNotContain("public override event System.EventHandler Resized", updated);
        Assert.DoesNotContain("public override void Speak()", updated);
    }

    [SkippableFact]
    public async Task GenerateOverrides_MembersFilter_UnknownEventName_Throws()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MissingVirtualEventSource, "Dog.cs");
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
    public async Task GenerateOverrides_AlreadyOverriddenEvent_IsSkipped()
    {
        await using var workspace = await TempWorkspace.CreateAsync(AlreadyOverriddenEventSource, "Dog.cs");
        var operation = new GenerateOverridesOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateOverridesParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            Members = new[] { "Speak" }
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("old-event", updated);
        Assert.Equal(1, CountOccurrences(
            updated[updated.IndexOf("public class Dog", StringComparison.Ordinal)..],
            "event System.EventHandler Changed"));
        Assert.Contains("public override void Speak()", updated);
        Assert.DoesNotContain("public override event System.EventHandler Changed",
            updated[updated.IndexOf("public override void Speak()", StringComparison.Ordinal)..]);
    }

    [SkippableFact]
    public async Task GenerateOverrides_AlreadyOverriddenEvent_Named_OverrideTargetNotFound()
    {
        await using var workspace = await TempWorkspace.CreateAsync(AlreadyOverriddenEventSource, "Dog.cs");
        var operation = new GenerateOverridesOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GenerateOverridesParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Dog",
                Members = new[] { "Changed" }
            }));

        Assert.Equal(ErrorCodes.OverrideTargetNotFound, ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("old-event", before);
    }

    [SkippableFact]
    public async Task GenerateOverrides_ReplaceExistingTrue_ReplacesEvent()
    {
        await using var workspace = await TempWorkspace.CreateAsync(AlreadyOverriddenEventSource, "Dog.cs");
        var operation = new GenerateOverridesOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateOverridesParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            Members = new[] { "Changed" },
            ReplaceExisting = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("public override event System.EventHandler Changed", updated);
        Assert.DoesNotContain("old-event", updated);
        var evt = ExtractMember(updated, "public override event System.EventHandler Changed");
        Assert.Contains("add", evt);
        Assert.Contains("remove", evt);
        Assert.DoesNotContain("NotImplementedException", evt);
        Assert.Equal(1, CountOccurrences(
            updated[updated.IndexOf("public class Dog", StringComparison.Ordinal)..],
            "event System.EventHandler Changed"));
        AssertCompiles(updated);
    }

    [SkippableFact]
    public async Task GenerateOverrides_ReplaceExistingTrue_FieldLikeEvent_IsReplaced()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
                public virtual event System.EventHandler Changed;
            }

            public class Dog : Animal
            {
                public override event System.EventHandler Changed;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Dog.cs");
        var operation = new GenerateOverridesOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateOverridesParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            Members = new[] { "Changed" },
            ReplaceExisting = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var derived = updated[updated.IndexOf("public class Dog", StringComparison.Ordinal)..];
        Assert.DoesNotContain("public override event System.EventHandler Changed;", derived);
        Assert.Contains("public override event System.EventHandler Changed", derived);
        var evt = ExtractMember(updated, "public override event System.EventHandler Changed");
        Assert.Contains("add", evt);
        Assert.Contains("remove", evt);
        Assert.Equal(1, CountOccurrences(derived, "event System.EventHandler Changed"));
        AssertCompiles(updated);
    }

    [SkippableFact]
    public async Task GenerateOverrides_ReplaceExistingTrue_MultiVariableEventField_LeavesUnrelated()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
                public virtual event System.EventHandler Changed;
                public virtual event System.EventHandler Resized;
            }

            public class Dog : Animal
            {
                public override event System.EventHandler Changed, Resized;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Dog.cs");
        var operation = new GenerateOverridesOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateOverridesParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            Members = new[] { "Changed" },
            ReplaceExisting = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var derived = updated[updated.IndexOf("public class Dog", StringComparison.Ordinal)..];
        Assert.DoesNotContain("Changed, Resized", updated);
        Assert.Contains("public override event System.EventHandler Resized;", derived);
        Assert.Contains("public override event System.EventHandler Changed", derived);
        var evt = ExtractMember(updated, "public override event System.EventHandler Changed");
        Assert.Contains("add", evt);
        Assert.Contains("remove", evt);
        Assert.DoesNotContain("public override event System.EventHandler Changed;", derived);
        Assert.Equal(1, CountOccurrences(derived, "event System.EventHandler Changed"));
        AssertCompiles(updated);
    }

    [SkippableFact]
    public async Task GenerateOverrides_Event_Preview_WritesNothing_AndDescribesGeneration()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MissingVirtualEventSource, "Dog.cs");
        var operation = new GenerateOverridesOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var result = await operation.ExecuteAsync(new GenerateOverridesParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            Members = new[] { "Changed" },
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.NotEmpty(result.PendingChanges);
        Assert.Contains("Generate overrides for:", result.PendingChanges[0].Description);
        Assert.Contains("Changed", result.PendingChanges[0].Description);
        Assert.DoesNotContain("replace existing", result.PendingChanges[0].Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("event", result.PendingChanges[0].AfterSnippet, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Changed", result.PendingChanges[0].AfterSnippet);
        Assert.DoesNotContain("NotImplementedException", result.PendingChanges[0].AfterSnippet);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task GenerateOverrides_ReplaceExistingTrue_Event_Preview_WritesNothing_AndMentionsReplacement()
    {
        await using var workspace = await TempWorkspace.CreateAsync(AlreadyOverriddenEventSource, "Dog.cs");
        var operation = new GenerateOverridesOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var result = await operation.ExecuteAsync(new GenerateOverridesParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            Members = new[] { "Changed" },
            ReplaceExisting = true,
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.Contains("replace existing overrides", result.PendingChanges![0].Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Changed", result.PendingChanges[0].Description);
        Assert.Contains("event", result.PendingChanges[0].AfterSnippet, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("old-event", before);
    }

    [SkippableFact]
    public async Task GenerateOverrides_IntermediateConcreteEventOverride_HidesAncestorSlot()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
                public virtual event System.EventHandler Changed;
                public virtual event System.EventHandler Resized;
            }

            public class Mammal : Animal
            {
                public override event System.EventHandler Changed
                {
                    add { }
                    remove { }
                }
            }

            public class Dog : Mammal
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Dog.cs");
        var operation = new GenerateOverridesOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateOverridesParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            Members = new[] { "Changed", "Resized" }
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var derived = updated[updated.IndexOf("public class Dog", StringComparison.Ordinal)..];
        Assert.Equal(1, CountOccurrences(derived, "event System.EventHandler Changed"));
        Assert.Contains("public override event System.EventHandler Changed", derived);
        Assert.Contains("public override event System.EventHandler Resized", derived);
        AssertCompiles(updated);
    }

    [SkippableFact]
    public async Task GenerateOverrides_IntermediateNewVirtualEventHider_DoesNotEmitWrongOverride()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
                public virtual event System.EventHandler Changed;
                public virtual event System.EventHandler Resized;
            }

            public class Mammal : Animal
            {
                public new virtual event System.EventHandler Changed
                {
                    add { }
                    remove { }
                }
            }

            public class Dog : Mammal
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Dog.cs");
        var operation = new GenerateOverridesOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateOverridesParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            Members = new[] { "Changed", "Resized" }
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var derived = updated[updated.IndexOf("public class Dog", StringComparison.Ordinal)..];
        Assert.Equal(1, CountOccurrences(derived, "event System.EventHandler Changed"));
        Assert.Contains("public override event System.EventHandler Changed", derived);
        Assert.Contains("public override event System.EventHandler Resized", derived);
        Assert.Contains("public new virtual event System.EventHandler Changed", updated);
        AssertCompiles(updated);
    }

    [SkippableFact]
    public async Task GenerateOverrides_IntermediateNewEventHider_DoesNotEmitOverride()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
                public virtual event System.EventHandler Changed;
                public virtual event System.EventHandler Resized;
            }

            public class Mammal : Animal
            {
                public new event System.EventHandler Changed
                {
                    add { }
                    remove { }
                }
            }

            public class Dog : Mammal
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Dog.cs");
        var operation = new GenerateOverridesOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateOverridesParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            Members = new[] { "Resized" }
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var derived = updated[updated.IndexOf("public class Dog", StringComparison.Ordinal)..];
        Assert.Contains("public override event System.EventHandler Resized", derived);
        Assert.DoesNotContain("public override event System.EventHandler Changed", derived);
        Assert.Contains("public new event System.EventHandler Changed", updated);
        AssertCompiles(updated);
    }

    [SkippableFact]
    public async Task GenerateOverrides_IntermediateNewEventHider_Named_OverrideTargetNotFound()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
                public virtual event System.EventHandler Changed;
            }

            public class Mammal : Animal
            {
                public new event System.EventHandler Changed
                {
                    add { }
                    remove { }
                }
            }

            public class Dog : Mammal
            {
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
                Members = new[] { "Changed" }
            }));

        Assert.Equal(ErrorCodes.OverrideTargetNotFound, ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task GenerateOverrides_ExplicitInterfaceEvent_DoesNotCountAsOverride()
    {
        const string source = """
            namespace TestApp;

            public interface INotify
            {
                event System.EventHandler Changed;
            }

            public class Animal
            {
                public virtual event System.EventHandler Changed;
            }

            public class Dog : Animal, INotify
            {
                event System.EventHandler INotify.Changed
                {
                    add { /* explicit */ }
                    remove { }
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Dog.cs");
        var operation = new GenerateOverridesOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateOverridesParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            Members = new[] { "Changed" }
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("event System.EventHandler INotify.Changed", updated);
        Assert.Contains("/* explicit */", updated);
        Assert.Contains("public override event System.EventHandler Changed", updated);
        Assert.Equal(1, CountOccurrences(updated, "INotify.Changed"));
        AssertCompiles(updated);
    }

    [SkippableFact]
    public async Task GenerateOverrides_ReplaceExistingTrue_NewHidingEvent_IsNotReplaced()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
                public virtual event System.EventHandler Changed;
                public virtual void Speak() { }
            }

            public class Dog : Animal
            {
                public new event System.EventHandler Changed
                {
                    add { /* hid */ }
                    remove { }
                }
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
        Assert.Contains("add { /* hid */ }", updated);
        Assert.Contains("public new event System.EventHandler Changed", updated);
        Assert.DoesNotContain("public override event System.EventHandler Changed", updated);
        Assert.Contains("public override void Speak()", updated);
    }

    [SkippableFact]
    public async Task GenerateOverrides_ReplaceExistingTrue_NewHidingEvent_Named_OverrideTargetNotFound()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
                public virtual event System.EventHandler Changed;
            }

            public class Dog : Animal
            {
                public new event System.EventHandler Changed
                {
                    add { /* hid */ }
                    remove { }
                }
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
                Members = new[] { "Changed" },
                ReplaceExisting = true
            }));

        Assert.Equal(ErrorCodes.OverrideTargetNotFound, ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("/* hid */", before);
    }

    [SkippableFact]
    public async Task GenerateOverrides_CallBaseTrue_Event_LeavesEmptyAddRemove()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MissingVirtualEventSource, "Dog.cs");
        var operation = new GenerateOverridesOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateOverridesParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            Members = new[] { "Changed" },
            CallBase = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var evt = ExtractMember(updated, "public override event System.EventHandler Changed");
        Assert.Contains("add", evt);
        Assert.Contains("remove", evt);
        Assert.DoesNotContain("base.", evt);
        Assert.DoesNotContain("NotImplementedException", evt);
        AssertCompiles(updated);
    }

    [SkippableFact]
    public async Task GenerateOverrides_CallBaseFalse_Event_LeavesEmptyAddRemove()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MissingVirtualEventSource, "Dog.cs");
        var operation = new GenerateOverridesOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateOverridesParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            Members = new[] { "Changed" },
            CallBase = false
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var evt = ExtractMember(updated, "public override event System.EventHandler Changed");
        Assert.Contains("add", evt);
        Assert.Contains("remove", evt);
        Assert.DoesNotContain("base.", evt);
        Assert.DoesNotContain("NotImplementedException", evt);
        AssertCompiles(updated);
    }

    [SkippableFact]
    public async Task GenerateOverrides_CallBaseTrue_AbstractEvent_LeavesEmptyAddRemove()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MissingAbstractEventSource, "Dog.cs");
        var operation = new GenerateOverridesOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateOverridesParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            Members = new[] { "Changed" },
            CallBase = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var evt = ExtractMember(updated, "public override event System.EventHandler Changed");
        Assert.Contains("add", evt);
        Assert.Contains("remove", evt);
        Assert.DoesNotContain("base.", evt);
        Assert.DoesNotContain("NotImplementedException", evt);
        AssertCompiles(updated);
    }

    [SkippableFact]
    public async Task GenerateOverrides_ProtectedEvent_KeepsProtectedAccessibility()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
                protected virtual event System.EventHandler Changed;
            }

            public class Dog : Animal
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Dog.cs");
        var operation = new GenerateOverridesOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateOverridesParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            Members = new[] { "Changed" }
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("protected override event System.EventHandler Changed", updated);
        Assert.DoesNotContain("public override event System.EventHandler Changed", updated);
        AssertCompiles(updated);
    }

    [SkippableFact]
    public async Task GenerateOverrides_ProtectedInternalEvent_SameAssembly_KeepsProtectedInternal()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
                protected internal virtual event System.EventHandler Changed;
            }

            public class Dog : Animal
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Dog.cs");
        var operation = new GenerateOverridesOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateOverridesParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            Members = new[] { "Changed" }
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("protected internal override event System.EventHandler Changed", updated);
        Assert.DoesNotContain("protected override event System.EventHandler Changed", updated);
        AssertCompiles(updated);
    }

    [SkippableFact]
    public async Task GenerateOverrides_CrossAssembly_InternalEvent_IsNotGenerated()
    {
        await using var workspace = await TempWorkspace.CreateReferencedLibraryAsync(
            """
            namespace TestLib;

            public class Animal
            {
                internal virtual event System.EventHandler Hidden;
                public virtual event System.EventHandler Changed;
            }
            """,
            """
            namespace TestApp;

            public class Dog : TestLib.Animal
            {
            }
            """);
        var operation = new GenerateOverridesOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var namedHidden = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GenerateOverridesParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Dog",
                Members = new[] { "Hidden" }
            }));

        Assert.Equal(ErrorCodes.OverrideTargetNotFound, namedHidden.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));

        var result = await operation.ExecuteAsync(new GenerateOverridesParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            Members = new[] { "Changed" }
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("public override event System.EventHandler Changed", updated);
        Assert.DoesNotContain("Hidden", updated[updated.IndexOf("public class Dog", StringComparison.Ordinal)..]);
    }

    [SkippableFact]
    public async Task GenerateOverrides_CrossAssembly_PrivateProtectedEvent_IsNotGenerated()
    {
        await using var workspace = await TempWorkspace.CreateReferencedLibraryAsync(
            """
            namespace TestLib;

            public class Animal
            {
                private protected virtual event System.EventHandler Hidden;
                public virtual void Speak() { }
            }
            """,
            """
            namespace TestApp;

            public class Dog : TestLib.Animal
            {
            }
            """);
        var operation = new GenerateOverridesOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GenerateOverridesParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Dog",
                Members = new[] { "Hidden" }
            }));

        Assert.Equal(ErrorCodes.OverrideTargetNotFound, ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task GenerateOverrides_CrossAssembly_ProtectedInternalEvent_EmitsProtected()
    {
        await using var workspace = await TempWorkspace.CreateReferencedLibraryAsync(
            """
            namespace TestLib;

            public class Animal
            {
                protected internal virtual event System.EventHandler Changed;
            }
            """,
            """
            namespace TestApp;

            public class Dog : TestLib.Animal
            {
            }
            """);
        var operation = new GenerateOverridesOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateOverridesParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            Members = new[] { "Changed" }
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var derived = updated[updated.IndexOf("public class Dog", StringComparison.Ordinal)..];
        Assert.Contains("protected override event System.EventHandler Changed", derived);
        Assert.DoesNotContain("protected internal override", derived);
        Assert.DoesNotContain("public override event", derived);
        var evt = ExtractMember(updated, "protected override event System.EventHandler Changed");
        Assert.Contains("add", evt);
        Assert.Contains("remove", evt);
    }

    [SkippableFact]
    public async Task GenerateOverrides_MethodsAndPropertiesUnchanged_WhenEventsPresent()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
                public virtual void Speak() { }

                public virtual string Label { get; set; }

                public virtual event System.EventHandler Changed;
            }

            public class Dog : Animal
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Dog.cs");
        var operation = new GenerateOverridesOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateOverridesParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            Members = new[] { "Speak", "Label", "Changed" },
            CallBase = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("base.Speak()", updated);
        var label = ExtractMember(updated, "public override string Label");
        Assert.Contains("return base.Label;", label);
        Assert.Contains("base.Label = value;", label);
        var evt = ExtractMember(updated, "public override event System.EventHandler Changed");
        Assert.DoesNotContain("base.", evt);
        AssertCompiles(updated);
    }

    #endregion

    #region Helpers

    private const string MissingVirtualEventSource = """
        namespace TestApp;

        public class Animal
        {
            public virtual event System.EventHandler Changed;
        }

        public class Dog : Animal
        {
        }
        """;

    private const string MissingAbstractEventSource = """
        namespace TestApp;

        public abstract class Animal
        {
            public abstract event System.EventHandler Changed;
        }

        public class Dog : Animal
        {
        }
        """;

    private const string AlreadyOverriddenEventSource = """
        namespace TestApp;

        public class Animal
        {
            public virtual event System.EventHandler Changed;

            public virtual void Speak() { }
        }

        public class Dog : Animal
        {
            public override event System.EventHandler Changed
            {
                add { /* old-event */ }
                remove { }
            }
        }
        """;

    private static string AbsoluteTestPath(string extension = ".cs") =>
        OperatingSystem.IsWindows()
            ? $"C:\\test\\file{extension}"
            : $"/test/file{extension}";

    private static string NormalizeNewlines(string text) => text.Replace("\r\n", "\n");

    private static bool HasOverrideToString(string source) =>
        source.Contains("override string ToString()", StringComparison.Ordinal)
        || source.Contains("override string? ToString()", StringComparison.Ordinal);

    private static int CountOverrideToString(string source) =>
        CountOccurrences(source, "override string ToString()")
        + CountOccurrences(source, "override string? ToString()");

    private static string ExtractOverrideToString(string source)
    {
        var extracted = ExtractMember(source, "string? ToString()");
        return extracted.Length > 0 ? extracted : ExtractMember(source, "string ToString()");
    }

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

    private static void AssertCompiles(string source)
    {
        var compilation = CSharpCompilation.Create(
                "GenerateOverridesCompileTest",
                new[] { CSharpSyntaxTree.ParseText(source) },
                new[]
                {
                    MetadataReference.CreateFromFile(typeof(object).Assembly.Location)
                },
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Select(d => d.ToString())
            .ToList();
        Assert.True(errors.Count == 0, "Generated generate_overrides stubs did not compile:\n" + string.Join("\n", errors) + "\n\n" + source);
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
            return await LoadAsync(directory, projectPath, sourcePath);
        }

        /// <summary>
        /// Lib project referenced by App. <see cref="SourcePath"/> is App/Dog.cs.
        /// </summary>
        public static async Task<TempWorkspace> CreateReferencedLibraryAsync(string librarySource, string appSource)
        {
            Skip.IfNot(ModuleInitializer.MsBuildAvailable, ModuleInitializer.MsBuildError ?? "MSBuild not available");

            var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpGenerateOverridesXP_" + Guid.NewGuid().ToString("N"));
            var libDir = Path.Combine(directory, "Lib");
            var appDir = Path.Combine(directory, "App");
            Directory.CreateDirectory(libDir);
            Directory.CreateDirectory(appDir);

            var libProject = Path.Combine(libDir, "Lib.csproj");
            var appProject = Path.Combine(appDir, "App.csproj");
            var libSource = Path.Combine(libDir, "Animal.cs");
            var appSourcePath = Path.Combine(appDir, "Dog.cs");

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
            await File.WriteAllTextAsync(libSource, librarySource);
            await File.WriteAllTextAsync(appSourcePath, appSource);

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

            return await LoadAsync(directory, solutionPath, appSourcePath);
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

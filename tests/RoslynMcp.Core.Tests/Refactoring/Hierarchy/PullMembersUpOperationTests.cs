using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Hierarchy;
using RoslynMcp.Core.Workspace;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Hierarchy;

/// <summary>
/// Operation-level tests for <see cref="PullMembersUpOperation"/>.
/// </summary>
public class PullMembersUpOperationTests
{
    #region Input Validation

    [Fact]
    public void Validate_MissingSourceFile_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            PullMembersUpOperation.Validate(new PullMembersUpParams
            {
                SourceFile = "",
                TypeName = "Derived",
                Members = ["Foo"]
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public void Validate_MissingTypeName_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            PullMembersUpOperation.Validate(new PullMembersUpParams
            {
                SourceFile = AbsoluteTestPath(),
                TypeName = "",
                Members = ["Foo"]
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public void Validate_MissingMembers_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            PullMembersUpOperation.Validate(new PullMembersUpParams
            {
                SourceFile = AbsoluteTestPath(),
                TypeName = "Derived",
                Members = []
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public void Validate_RelativePath_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            PullMembersUpOperation.Validate(new PullMembersUpParams
            {
                SourceFile = "Derived.cs",
                TypeName = "Derived",
                Members = ["Foo"]
            }));

        Assert.Equal(ErrorCodes.InvalidSourcePath, ex.ErrorCode);
    }

    [Fact]
    public void Validate_MissingFile_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            PullMembersUpOperation.Validate(new PullMembersUpParams
            {
                SourceFile = AbsoluteTestPath(),
                TypeName = "Derived",
                Members = ["Foo"]
            }));

        Assert.Equal(ErrorCodes.SourceFileNotFound, ex.ErrorCode);
    }

    #endregion

    #region P0 Happy Path

    [SkippableFact]
    public async Task PullMembersUp_MethodToBaseClass_MovesMemberAndMakesVirtual()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
            }

            public class Dog : Animal
            {
                public int Speak()
                {
                    return 1;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PullMembersUpOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PullMembersUpParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            Members = ["Speak"]
        });

        Assert.True(result.Success);
        Assert.False(result.Preview);

        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        var animal = ExtractTypeBody(text, "Animal");
        var dog = ExtractTypeBody(text, "Dog");
        Assert.Contains("Speak", animal);
        Assert.Contains("virtual", animal);
        Assert.DoesNotContain("Speak", dog);
    }

    [SkippableFact]
    public async Task PullMembersUp_PropertyToBaseClass_MovesProperty()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
            }

            public class Dog : Animal
            {
                public string Name { get; set; } = "";
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PullMembersUpOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PullMembersUpParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            Members = ["Name"]
        });

        Assert.True(result.Success);

        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("Name", ExtractTypeBody(text, "Animal"));
        Assert.DoesNotContain("Name", ExtractTypeBody(text, "Dog"));
    }

    [SkippableFact]
    public async Task PullMembersUp_MultipleMembers_MovesAll()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
            }

            public class Dog : Animal
            {
                public string Name { get; set; } = "";

                public int Speak()
                {
                    return 1;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PullMembersUpOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PullMembersUpParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            Members = ["Name", "Speak"]
        });

        Assert.True(result.Success);

        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        var animal = ExtractTypeBody(text, "Animal");
        var dog = ExtractTypeBody(text, "Dog");
        Assert.Contains("Name", animal);
        Assert.Contains("Speak", animal);
        Assert.DoesNotContain("Name", dog);
        Assert.DoesNotContain("Speak", dog);
    }

    [SkippableFact]
    public async Task PullMembersUp_PrivateMethod_BecomesProtectedVirtual()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
            }

            public class Dog : Animal
            {
                private void Log()
                {
                    System.Console.WriteLine("log");
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PullMembersUpOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PullMembersUpParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            Members = ["Log"]
        });

        Assert.True(result.Success);

        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        var animal = ExtractTypeBody(text, "Animal");
        Assert.Contains("protected", animal);
        Assert.Contains("virtual", animal);
        Assert.Contains("void Log()", animal);
        Assert.DoesNotContain("private", animal);
    }

    [SkippableFact]
    public async Task PullMembersUp_MakeAbstract_LeavesOverrideOnDerived()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
            }

            public class Dog : Animal
            {
                public int Speak()
                {
                    return 1;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PullMembersUpOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PullMembersUpParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            Members = ["Speak"],
            MakeAbstract = true
        });

        Assert.True(result.Success);

        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        var animal = ExtractTypeBody(text, "Animal");
        var dog = ExtractTypeBody(text, "Dog");
        Assert.Contains("abstract", text);
        Assert.Contains("abstract", animal);
        Assert.Contains("Speak", animal);
        Assert.DoesNotContain("return 1", animal);
        Assert.Contains("override", dog);
        Assert.Contains("return 1", dog);
    }

    [SkippableFact]
    public async Task PullMembersUp_MethodToInterface_AddsSignatureAndKeepsImplementation()
    {
        const string source = """
            namespace TestApp;

            public interface IAnimal
            {
            }

            public class Dog : IAnimal
            {
                public int Speak()
                {
                    return 1;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PullMembersUpOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PullMembersUpParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            Members = ["Speak"]
        });

        Assert.True(result.Success);

        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        var iface = ExtractTypeBody(text, "IAnimal");
        var dog = ExtractTypeBody(text, "Dog");
        Assert.Contains("Speak", iface);
        Assert.Contains(";", iface);
        Assert.DoesNotContain("return 1", iface);
        Assert.Contains("Speak", dog);
        Assert.Contains("return 1", dog);
    }

    [SkippableFact]
    public async Task PullMembersUp_Preview_ReturnsChangesAndWritesNothing()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
            }

            public class Dog : Animal
            {
                public int Speak()
                {
                    return 1;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var original = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new PullMembersUpOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PullMembersUpParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            Members = ["Speak"],
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.NotEmpty(result.PendingChanges);
        Assert.Contains(result.PendingChanges, c => c.AfterSnippet != null && c.AfterSnippet.Contains("Speak"));

        var after = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Equal(original, after);
    }

    [SkippableFact]
    public async Task PullMembersUp_FieldLikeEvent_MovesEventOntoBase()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
            }

            public class Dog : Animal
            {
                public event System.EventHandler Changed;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PullMembersUpOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PullMembersUpParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            Members = ["Changed"]
        });

        Assert.True(result.Success);

        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        var animal = ExtractTypeBody(text, "Animal");
        var dog = ExtractTypeBody(text, "Dog");
        Assert.Contains("public event System.EventHandler Changed", animal);
        Assert.DoesNotContain("virtual", animal);
        Assert.DoesNotContain("event System.EventHandler Changed", dog);
    }

    [SkippableFact]
    public async Task PullMembersUp_AccessorStyleEvent_MovesEventOntoBaseAsVirtual()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
            }

            public class Dog : Animal
            {
                public event System.EventHandler Changed
                {
                    add { }
                    remove { }
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PullMembersUpOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PullMembersUpParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            Members = ["Changed"]
        });

        Assert.True(result.Success);

        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        var animal = ExtractTypeBody(text, "Animal");
        var dog = ExtractTypeBody(text, "Dog");
        Assert.Contains("public virtual event System.EventHandler Changed", animal);
        Assert.Contains("add", animal);
        Assert.Contains("remove", animal);
        Assert.DoesNotContain("event System.EventHandler Changed", dog);
    }

    [SkippableFact]
    public async Task PullMembersUp_MultiVariableEventField_LeavesUnrelatedDeclarator()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
            }

            public class Dog : Animal
            {
                public event System.EventHandler Changed, Other;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PullMembersUpOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PullMembersUpParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            Members = ["Changed"]
        });

        Assert.True(result.Success);

        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        var animal = ExtractTypeBody(text, "Animal");
        var dog = ExtractTypeBody(text, "Dog");
        Assert.Contains("public event System.EventHandler Changed", animal);
        Assert.DoesNotContain("Other", animal);
        Assert.DoesNotContain("Changed, Other", text);
        Assert.Contains("public event System.EventHandler Other", dog);
        Assert.DoesNotContain("event System.EventHandler Changed", dog);
    }

    [SkippableFact]
    public async Task PullMembersUp_PrivateEvent_BecomesProtectedOnBase()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
            }

            public class Dog : Animal
            {
                private event System.EventHandler Changed;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PullMembersUpOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PullMembersUpParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            Members = ["Changed"]
        });

        Assert.True(result.Success);

        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        var animal = ExtractTypeBody(text, "Animal");
        Assert.Contains("protected event System.EventHandler Changed", animal);
        Assert.DoesNotContain("private event", animal);
        Assert.DoesNotContain("virtual", animal);
        Assert.DoesNotContain("event System.EventHandler Changed", ExtractTypeBody(text, "Dog"));
    }

    [SkippableFact]
    public async Task PullMembersUp_MakeAbstract_EventLeavesOverrideOnDerived()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
            }

            public class Dog : Animal
            {
                public event System.EventHandler Changed;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PullMembersUpOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PullMembersUpParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            Members = ["Changed"],
            MakeAbstract = true
        });

        Assert.True(result.Success);

        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        var animal = ExtractTypeBody(text, "Animal");
        var dog = ExtractTypeBody(text, "Dog");
        Assert.Contains("abstract", text);
        Assert.Contains("abstract event System.EventHandler Changed", animal);
        Assert.Contains("override event System.EventHandler Changed", dog);
    }

    [SkippableFact]
    public async Task PullMembersUp_MakeAbstract_AccessorStyleEventLeavesOverrideOnDerived()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
            }

            public class Dog : Animal
            {
                public event System.EventHandler Changed
                {
                    add { }
                    remove { }
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PullMembersUpOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PullMembersUpParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            Members = ["Changed"],
            MakeAbstract = true
        });

        Assert.True(result.Success);

        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        var animal = ExtractTypeBody(text, "Animal");
        var dog = ExtractTypeBody(text, "Dog");
        Assert.Contains("abstract event System.EventHandler Changed", animal);
        Assert.DoesNotContain("add", animal);
        Assert.Contains("override event System.EventHandler Changed", dog);
        Assert.Contains("add", dog);
        Assert.Contains("remove", dog);
    }

    [SkippableFact]
    public async Task PullMembersUp_MakeAbstract_MultiVariableEventField_LeavesUnrelatedAndOverridesSelected()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
            }

            public class Dog : Animal
            {
                public event System.EventHandler Changed, Other;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PullMembersUpOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PullMembersUpParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            Members = ["Changed"],
            MakeAbstract = true
        });

        Assert.True(result.Success);

        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        var animal = ExtractTypeBody(text, "Animal");
        var dog = ExtractTypeBody(text, "Dog");
        Assert.Contains("abstract event System.EventHandler Changed", animal);
        Assert.DoesNotContain("Other", animal);
        Assert.Contains("override event System.EventHandler Changed", dog);
        Assert.Contains("public event System.EventHandler Other", dog);
        Assert.DoesNotContain("Changed, Other", text);
    }

    [SkippableFact]
    public async Task PullMembersUp_MakeAbstract_StaticEvent_Throws()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
            }

            public class Dog : Animal
            {
                public static event System.EventHandler Changed;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PullMembersUpOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new PullMembersUpParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Dog",
                Members = ["Changed"],
                MakeAbstract = true
            }));

        Assert.Equal(ErrorCodes.MemberNotMoveable, ex.ErrorCode);
    }

    [SkippableFact]
    public async Task PullMembersUp_MakeAbstract_ExplicitInterfaceEvent_Throws()
    {
        const string source = """
            namespace TestApp;

            public interface INotify
            {
                event System.EventHandler Changed;
            }

            public class Animal
            {
            }

            public class Dog : Animal, INotify
            {
                event System.EventHandler INotify.Changed
                {
                    add { }
                    remove { }
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PullMembersUpOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new PullMembersUpParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Dog",
                Members = ["Changed"],
                MakeAbstract = true
            }));

        Assert.Equal(ErrorCodes.MemberNotMoveable, ex.ErrorCode);
    }

    [SkippableFact]
    public async Task PullMembersUp_MultiVariableEventField_IgnoresUnrelatedDeclaratorDependency()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
            }

            public class Dog : Animal
            {
                public event System.Action Selected, Other = DerivedHandler;

                private static void DerivedHandler() { }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PullMembersUpOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PullMembersUpParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            Members = ["Selected"]
        });

        Assert.True(result.Success);

        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        var animal = ExtractTypeBody(text, "Animal");
        var dog = ExtractTypeBody(text, "Dog");
        Assert.Contains("event System.Action Selected", animal);
        Assert.DoesNotContain("Other", animal);
        Assert.Contains("Other", dog);
        Assert.Contains("DerivedHandler", dog);
        Assert.DoesNotContain("Selected", dog);
    }

    [SkippableFact]
    public async Task PullMembersUp_EventToInterface_AddsSignatureAndKeepsImplementation()
    {
        const string source = """
            namespace TestApp;

            public interface IAnimal
            {
            }

            public class Dog : IAnimal
            {
                public event System.EventHandler Changed;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PullMembersUpOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PullMembersUpParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            Members = ["Changed"]
        });

        Assert.True(result.Success);

        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        var iface = ExtractTypeBody(text, "IAnimal");
        var dog = ExtractTypeBody(text, "Dog");
        Assert.Contains("event System.EventHandler Changed", iface);
        Assert.DoesNotContain("public event", iface);
        Assert.Contains("public event System.EventHandler Changed", dog);
    }

    [SkippableFact]
    public async Task PullMembersUp_Event_Preview_WritesNothing_AndDescribesEvent()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
            }

            public class Dog : Animal
            {
                public event System.EventHandler Changed;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var original = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new PullMembersUpOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PullMembersUpParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            Members = ["Changed"],
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.NotEmpty(result.PendingChanges);
        Assert.Contains(result.PendingChanges, c => c.Description != null && c.Description.Contains("Changed"));
        Assert.Contains(result.PendingChanges, c =>
            c.AfterSnippet != null &&
            c.AfterSnippet.Contains("event", StringComparison.OrdinalIgnoreCase) &&
            c.AfterSnippet.Contains("Changed"));

        var after = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Equal(original, after);
    }

    [SkippableFact]
    public async Task PullMembersUp_Event_LeavesMethodsPropertiesAndFieldsOnDerived()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
            }

            public class Dog : Animal
            {
                public string Name { get; set; } = "";
                public int Age;
                public event System.EventHandler Changed;

                public int Speak()
                {
                    return 1;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PullMembersUpOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PullMembersUpParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            Members = ["Changed"]
        });

        Assert.True(result.Success);

        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        var animal = ExtractTypeBody(text, "Animal");
        var dog = ExtractTypeBody(text, "Dog");
        Assert.Contains("public event System.EventHandler Changed", animal);
        Assert.DoesNotContain("Name", animal);
        Assert.DoesNotContain("Age", animal);
        Assert.DoesNotContain("Speak", animal);
        Assert.Contains("public string Name { get; set; }", dog);
        Assert.Contains("public int Age", dog);
        Assert.Contains("public int Speak()", dog);
        Assert.DoesNotContain("event System.EventHandler Changed", dog);
    }

    [SkippableFact]
    public async Task PullMembersUp_ExplicitTarget_UsesNamedBase()
    {
        const string source = """
            namespace TestApp;

            public class Creature
            {
            }

            public class Animal : Creature
            {
            }

            public class Dog : Animal
            {
                public int Speak()
                {
                    return 1;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PullMembersUpOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PullMembersUpParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            Members = ["Speak"],
            TargetBaseType = "Creature"
        });

        Assert.True(result.Success);

        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("Speak", ExtractTypeBody(text, "Creature"));
        Assert.DoesNotContain("Speak", ExtractTypeBody(text, "Animal"));
        Assert.DoesNotContain("Speak", ExtractTypeBody(text, "Dog"));
    }

    #endregion

    #region P0 Rejects

    [SkippableFact]
    public async Task PullMembersUp_NoBase_Throws()
    {
        const string source = """
            namespace TestApp;

            public class Dog
            {
                public int Speak()
                {
                    return 1;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PullMembersUpOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new PullMembersUpParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Dog",
                Members = ["Speak"]
            }));

        Assert.Equal(ErrorCodes.NoCommonBase, ex.ErrorCode);
    }

    [SkippableFact]
    public async Task PullMembersUp_MissingNamedBase_Throws()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
            }

            public class Dog : Animal
            {
                public int Speak()
                {
                    return 1;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PullMembersUpOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new PullMembersUpParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Dog",
                Members = ["Speak"],
                TargetBaseType = "IDisposable"
            }));

        Assert.Equal(ErrorCodes.BaseClassNotFound, ex.ErrorCode);
    }

    [SkippableFact]
    public async Task PullMembersUp_NameConflict_Throws()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
                public int Speak()
                {
                    return 0;
                }
            }

            public class Dog : Animal
            {
                public new int Speak()
                {
                    return 1;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PullMembersUpOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new PullMembersUpParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Dog",
                Members = ["Speak"]
            }));

        Assert.Equal(ErrorCodes.ConflictsWithExistingMember, ex.ErrorCode);
    }

    [SkippableFact]
    public async Task PullMembersUp_SignatureConflict_Throws()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
                public void Log(string message)
                {
                }
            }

            public class Dog : Animal
            {
                public void Log(string message)
                {
                    System.Console.WriteLine(message);
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PullMembersUpOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new PullMembersUpParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Dog",
                Members = ["Log"]
            }));

        Assert.Equal(ErrorCodes.ConflictsWithExistingMember, ex.ErrorCode);
    }

    [SkippableFact]
    public async Task PullMembersUp_DependsOnDerivedField_Throws()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
            }

            public class Dog : Animal
            {
                private int _age;

                public int Age()
                {
                    return _age;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PullMembersUpOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new PullMembersUpParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Dog",
                Members = ["Age"]
            }));

        Assert.Equal(ErrorCodes.MemberDependsOnDerived, ex.ErrorCode);
    }

    [SkippableFact]
    public async Task PullMembersUp_FieldToInterface_Throws()
    {
        const string source = """
            namespace TestApp;

            public interface IAnimal
            {
            }

            public class Dog : IAnimal
            {
                public int Age;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PullMembersUpOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new PullMembersUpParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Dog",
                Members = ["Age"]
            }));

        Assert.Equal(ErrorCodes.MemberNotInterfaceCompatible, ex.ErrorCode);
    }

    [SkippableFact]
    public async Task PullMembersUp_PrivateMethodToInterface_Throws()
    {
        const string source = """
            namespace TestApp;

            public interface IAnimal
            {
            }

            public class Dog : IAnimal
            {
                private void Log()
                {
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PullMembersUpOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new PullMembersUpParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Dog",
                Members = ["Log"]
            }));

        Assert.Equal(ErrorCodes.MemberNotInterfaceCompatible, ex.ErrorCode);
    }

    [SkippableFact]
    public async Task PullMembersUp_ExternalBase_Throws()
    {
        const string source = """
            namespace TestApp;

            public class Dog : System.Exception
            {
                public int Speak()
                {
                    return 1;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PullMembersUpOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new PullMembersUpParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Dog",
                Members = ["Speak"]
            }));

        Assert.Equal(ErrorCodes.BaseClassNotEditable, ex.ErrorCode);
    }

    [SkippableFact]
    public async Task PullMembersUp_MemberNotFound_Throws()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
            }

            public class Dog : Animal
            {
                public int Speak()
                {
                    return 1;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PullMembersUpOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new PullMembersUpParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Dog",
                Members = ["Missing"]
            }));

        Assert.Equal(ErrorCodes.MemberNotFound, ex.ErrorCode);
    }

    #endregion

    #region Indexers

    private const string MixedIndexerSource = """
        namespace TestApp;

        public class Animal
        {
        }

        public class Dog : Animal
        {
            public int Count { get; set; }

            public string this[int i]
            {
                get => "";
                set { }
            }

            public void Work() { }
        }
        """;

    [SkippableFact]
    public async Task PullMembersUp_Default_PublicIndexer_MovesIndexerOntoBase()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MixedIndexerSource);
        var operation = new PullMembersUpOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PullMembersUpParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            Members = ["this[]"]
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var indexer = Assert.Single(FindIndexers(updated, "Animal"));
        Assert.Contains("this[int i]", updated);
        Assert.Equal("i", Assert.Single(indexer.ParameterList.Parameters).Identifier.Text);
        Assert.Contains(indexer.Modifiers, t => t.IsKind(SyntaxKind.VirtualKeyword));
        Assert.Empty(FindIndexers(updated, "Dog"));
        Assert.Contains("public int Count { get; set; }", GetTypeSection(updated, "Dog"));
        Assert.Contains("public void Work()", GetTypeSection(updated, "Dog"));
        Assert.DoesNotContain(
            FindType(updated, "Animal").Members.OfType<PropertyDeclarationSyntax>(),
            p => p.Identifier.Text.Contains("this", StringComparison.Ordinal));
        AssertCompiles(updated);
    }

    [SkippableTheory]
    [InlineData("this[]")]
    [InlineData("Item")]
    [InlineData("this[int i]")]
    public async Task PullMembersUp_MembersFilter_IndexerAliases_MovesOnlyIndexer(string memberName)
    {
        await using var workspace = await TempWorkspace.CreateAsync(MixedIndexerSource);
        var operation = new PullMembersUpOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PullMembersUpParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            Members = [memberName]
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Single(FindIndexers(updated, "Animal"));
        Assert.Contains("this[int i]", GetTypeSection(updated, "Animal"));
        Assert.Empty(FindIndexers(updated, "Dog"));
        Assert.Contains("public int Count { get; set; }", GetTypeSection(updated, "Dog"));
        Assert.Contains("public void Work()", GetTypeSection(updated, "Dog"));
        Assert.DoesNotContain("Count", GetTypeSection(updated, "Animal"));
        Assert.DoesNotContain("Work", GetTypeSection(updated, "Animal"));
        AssertCompiles(updated);
    }

    [SkippableFact]
    public async Task PullMembersUp_OrdinaryProperty_LeavesIndexerOnDerived()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MixedIndexerSource);
        var operation = new PullMembersUpOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PullMembersUpParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            Members = ["Count"]
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var property = FindProperty(updated, "Animal", "Count");
        Assert.NotNull(property);
        Assert.DoesNotContain("this[]", property!.Identifier.Text);
        Assert.Empty(FindIndexers(updated, "Animal"));
        Assert.Single(FindIndexers(updated, "Dog"));
        Assert.Contains("this[int i]", GetTypeSection(updated, "Dog"));
        Assert.DoesNotContain("public int Count", GetTypeSection(updated, "Dog"));
        AssertCompiles(updated);
    }

    [SkippableFact]
    public async Task PullMembersUp_GetOnlyIndexer_PreservesGetOnly()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
            }

            public class Dog : Animal
            {
                public int this[int i] => i;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PullMembersUpOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PullMembersUpParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            Members = ["this[]"]
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var indexer = Assert.Single(FindIndexers(updated, "Animal"));
        Assert.True(
            indexer.ExpressionBody != null
            || (indexer.AccessorList != null
                && indexer.AccessorList.Accessors.Any(a => a.IsKind(SyntaxKind.GetAccessorDeclaration))
                && indexer.AccessorList.Accessors.All(a => !a.IsKind(SyntaxKind.SetAccessorDeclaration))));
        Assert.Contains("this[int i]", GetTypeSection(updated, "Animal"));
        Assert.Empty(FindIndexers(updated, "Dog"));
        AssertCompiles(updated);
    }

    [SkippableFact]
    public async Task PullMembersUp_RefIndexer_KeepsRef()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
                protected int _value;
            }

            public class Dog : Animal
            {
                public ref int this[int i] => ref _value;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PullMembersUpOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PullMembersUpParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            Members = ["this[]"]
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var indexer = Assert.Single(FindIndexers(updated, "Animal"));
        Assert.IsType<RefTypeSyntax>(indexer.Type);
        Assert.False(((RefTypeSyntax)indexer.Type).ReadOnlyKeyword.IsKind(SyntaxKind.ReadOnlyKeyword));
        Assert.Contains("ref int this[int i]", updated);
        Assert.Empty(FindIndexers(updated, "Dog"));
        AssertCompiles(updated);
    }

    [SkippableFact]
    public async Task PullMembersUp_Indexer_RefKindParameter_Preserved()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
            }

            public class Dog : Animal
            {
                public int this[in int i] => i;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PullMembersUpOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PullMembersUpParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            Members = ["this[]"]
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var indexer = Assert.Single(FindIndexers(updated, "Animal"));
        var parameter = Assert.Single(indexer.ParameterList.Parameters);
        Assert.Contains(parameter.Modifiers, t => t.IsKind(SyntaxKind.InKeyword));
        Assert.Contains("this[in int i]", updated);
        Assert.Empty(FindIndexers(updated, "Dog"));
        AssertCompiles(updated);
    }

    [SkippableFact]
    public async Task PullMembersUp_InitOnlyIndexer_PreservesInit()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
            }

            public class Dog : Animal
            {
                public int this[int i]
                {
                    get => 0;
                    init { }
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PullMembersUpOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PullMembersUpParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            Members = ["this[]"]
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var indexer = Assert.Single(FindIndexers(updated, "Animal"));
        Assert.Contains(indexer.AccessorList!.Accessors, a => a.IsKind(SyntaxKind.InitAccessorDeclaration));
        Assert.Contains("this[int i]", updated);
        Assert.Empty(FindIndexers(updated, "Dog"));
        AssertCompiles(updated);
    }

    [SkippableFact]
    public async Task PullMembersUp_PrivateIndexer_BecomesProtectedOnBase()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
            }

            public class Dog : Animal
            {
                private string this[int i]
                {
                    get => "";
                    set { }
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PullMembersUpOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PullMembersUpParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            Members = ["this[]"]
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var indexer = Assert.Single(FindIndexers(updated, "Animal"));
        Assert.Contains(indexer.Modifiers, t => t.IsKind(SyntaxKind.ProtectedKeyword));
        Assert.DoesNotContain(indexer.Modifiers, t => t.IsKind(SyntaxKind.PrivateKeyword));
        Assert.Contains(indexer.Modifiers, t => t.IsKind(SyntaxKind.VirtualKeyword));
        Assert.Empty(FindIndexers(updated, "Dog"));
        AssertCompiles(updated);
    }

    [SkippableFact]
    public async Task PullMembersUp_SpecificIndexerDisplay_LeavesOtherIndexerOnDerived()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
            }

            public class Dog : Animal
            {
                public string this[int i]
                {
                    get => "";
                    set { }
                }

                public string this[string key]
                {
                    get => "";
                    set { }
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PullMembersUpOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PullMembersUpParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            Members = ["this[int i]"]
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var baseIndexer = Assert.Single(FindIndexers(updated, "Animal"));
        var derivedIndexer = Assert.Single(FindIndexers(updated, "Dog"));
        Assert.Equal("int", Assert.Single(baseIndexer.ParameterList.Parameters).Type!.ToString());
        Assert.Equal("string", Assert.Single(derivedIndexer.ParameterList.Parameters).Type!.ToString());
        AssertCompiles(updated);
    }

    [SkippableFact]
    public async Task PullMembersUp_IndexerToInterface_AddsSignatureAndKeepsImplementation()
    {
        const string source = """
            namespace TestApp;

            public interface IAnimal
            {
            }

            public class Dog : IAnimal
            {
                public string this[int i]
                {
                    get => "";
                    set { }
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PullMembersUpOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PullMembersUpParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            Members = ["this[]"]
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var ifaceIndexer = Assert.Single(FindIndexers(updated, "IAnimal"));
        var dogIndexer = Assert.Single(FindIndexers(updated, "Dog"));
        Assert.Empty(ifaceIndexer.Modifiers);
        Assert.Null(ifaceIndexer.ExpressionBody);
        Assert.Contains(ifaceIndexer.AccessorList!.Accessors, a => a.IsKind(SyntaxKind.GetAccessorDeclaration));
        Assert.Contains(ifaceIndexer.AccessorList.Accessors, a => a.IsKind(SyntaxKind.SetAccessorDeclaration));
        Assert.All(ifaceIndexer.AccessorList.Accessors, a => Assert.True(a.Body == null && a.ExpressionBody == null));
        Assert.Contains("this[int i]", GetTypeSection(updated, "IAnimal"));
        Assert.DoesNotContain("return", GetTypeSection(updated, "IAnimal"));
        Assert.NotNull(dogIndexer.AccessorList);
        Assert.Contains("get =>", GetTypeSection(updated, "Dog"));
        Assert.DoesNotContain(
            FindType(updated, "IAnimal").Members.OfType<PropertyDeclarationSyntax>(),
            p => p.Identifier.Text.Contains("this", StringComparison.Ordinal));
        AssertCompiles(updated);
    }

    [SkippableFact]
    public async Task PullMembersUp_ExplicitInterfaceIndexer_ThrowsMemberNotFound()
    {
        const string source = """
            namespace TestApp;

            public interface ILookup
            {
                string this[int i] { get; }
            }

            public class Animal
            {
            }

            public class Dog : Animal, ILookup
            {
                string ILookup.this[int i] => "";

                public int Count { get; set; }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PullMembersUpOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        foreach (var memberName in new[] { "this[]", "Item", "this[int i]" })
        {
            var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
                operation.ExecuteAsync(new PullMembersUpParams
                {
                    SourceFile = workspace.SourcePath,
                    TypeName = "Dog",
                    Members = [memberName]
                }));

            Assert.Equal(ErrorCodes.MemberNotFound, ex.ErrorCode);
            Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
        }
    }

    [SkippableFact]
    public async Task PullMembersUp_OrdinaryIndexer_LeavesExplicitInterfaceIndexerOnDerived()
    {
        const string source = """
            namespace TestApp;

            public interface ILookup
            {
                string this[int i] { get; }
            }

            public interface IOther
            {
                string this[int i] { get; }
            }

            public class Animal
            {
            }

            public class Dog : Animal, ILookup, IOther
            {
                public string this[int i]
                {
                    get => "";
                    set { }
                }

                string IOther.this[int i] => this[i];
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PullMembersUpOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PullMembersUpParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            Members = ["this[]"]
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var baseIndexer = Assert.Single(FindIndexers(updated, "Animal"));
        var derivedIndexer = Assert.Single(FindIndexers(updated, "Dog"));
        Assert.Null(baseIndexer.ExplicitInterfaceSpecifier);
        Assert.Equal("IOther", derivedIndexer.ExplicitInterfaceSpecifier!.Name.ToString());
        Assert.DoesNotContain("ILookup.this", GetTypeSection(updated, "Animal"));
        Assert.DoesNotContain("IOther.this", GetTypeSection(updated, "Animal"));
        AssertCompiles(updated);
    }

    [SkippableFact]
    public async Task PullMembersUp_Indexer_LeavesMethodsPropertiesFieldsAndEventsOnDerived()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
            }

            public class Dog : Animal
            {
                public string Name { get; set; } = "";
                public int Age;
                public event System.EventHandler Changed;

                public string this[int i]
                {
                    get => "";
                    set { }
                }

                public int Speak()
                {
                    return 1;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PullMembersUpOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PullMembersUpParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            Members = ["this[]"]
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var animal = ExtractTypeBody(updated, "Animal");
        var dog = ExtractTypeBody(updated, "Dog");
        Assert.Single(FindIndexers(updated, "Animal"));
        Assert.Empty(FindIndexers(updated, "Dog"));
        Assert.Contains("Name", dog);
        Assert.Contains("Age", dog);
        Assert.Contains("Changed", dog);
        Assert.Contains("Speak", dog);
        Assert.DoesNotContain("Name", animal);
        Assert.DoesNotContain("Age", animal);
        Assert.DoesNotContain("Changed", animal);
        Assert.DoesNotContain("Speak", animal);
        AssertCompiles(updated);
    }

    [SkippableFact]
    public async Task PullMembersUp_Indexer_Preview_WritesNothing_AndDescribesIndexer()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MixedIndexerSource);
        var original = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new PullMembersUpOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PullMembersUpParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            Members = ["this[]"],
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.NotEmpty(result.PendingChanges);
        Assert.Contains(result.PendingChanges, c => c.Description != null && c.Description.Contains("this[]"));
        Assert.Contains(result.PendingChanges, c =>
            c.AfterSnippet != null && c.AfterSnippet.Contains("this[int i]"));
        Assert.Contains(result.PendingChanges, c =>
            c.AfterSnippet != null &&
            !c.AfterSnippet.Replace("this[int i]", "", StringComparison.Ordinal).Contains("this[]"));

        var after = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Equal(original, after);
    }

    [SkippableFact]
    public async Task PullMembersUp_Indexer_UnknownName_ThrowsMemberNotFound()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MixedIndexerSource);
        var operation = new PullMembersUpOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new PullMembersUpParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Dog",
                Members = ["this[string key]"]
            }));

        Assert.Equal(ErrorCodes.MemberNotFound, ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task PullMembersUp_MakeAbstract_IndexerLeavesOverrideOnDerived()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MixedIndexerSource);
        var operation = new PullMembersUpOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PullMembersUpParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            Members = ["this[]"],
            MakeAbstract = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var animalIndexer = Assert.Single(FindIndexers(updated, "Animal"));
        var dogIndexer = Assert.Single(FindIndexers(updated, "Dog"));
        Assert.Contains(FindType(updated, "Animal").Modifiers, t => t.IsKind(SyntaxKind.AbstractKeyword));
        Assert.Contains(animalIndexer.Modifiers, t => t.IsKind(SyntaxKind.AbstractKeyword));
        Assert.DoesNotContain(animalIndexer.Modifiers, t => t.IsKind(SyntaxKind.VirtualKeyword));
        Assert.Null(animalIndexer.ExpressionBody);
        Assert.All(animalIndexer.AccessorList!.Accessors, a => Assert.True(a.Body == null && a.ExpressionBody == null));
        Assert.Contains(dogIndexer.Modifiers, t => t.IsKind(SyntaxKind.OverrideKeyword));
        Assert.Contains("get =>", GetTypeSection(updated, "Dog"));
        Assert.DoesNotContain("get =>", GetTypeSection(updated, "Animal"));
        AssertCompiles(updated);
    }

    [SkippableFact]
    public async Task PullMembersUp_MakeAbstract_IndexerToInterface_KeepsImplementation()
    {
        const string source = """
            namespace TestApp;

            public interface IAnimal
            {
            }

            public class Dog : IAnimal
            {
                public string this[int i]
                {
                    get => "";
                    set { }
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PullMembersUpOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PullMembersUpParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            Members = ["this[]"],
            MakeAbstract = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var ifaceIndexer = Assert.Single(FindIndexers(updated, "IAnimal"));
        var dogIndexer = Assert.Single(FindIndexers(updated, "Dog"));
        Assert.Empty(ifaceIndexer.Modifiers);
        Assert.DoesNotContain(dogIndexer.Modifiers, t => t.IsKind(SyntaxKind.OverrideKeyword));
        Assert.DoesNotContain(dogIndexer.Modifiers, t => t.IsKind(SyntaxKind.AbstractKeyword));
        Assert.Contains("get =>", GetTypeSection(updated, "Dog"));
        AssertCompiles(updated);
    }

    [SkippableFact]
    public async Task PullMembersUp_MakeAbstract_ExplicitInterfaceIndexer_Throws()
    {
        const string source = """
            namespace TestApp;

            public interface ILookup
            {
                string this[int i] { get; }
            }

            public abstract class Animal : ILookup
            {
            }

            public class Dog : Animal
            {
                string ILookup.this[int i] => "";
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PullMembersUpOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new PullMembersUpParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Dog",
                Members = ["this[int i]"],
                MakeAbstract = true
            }));

        Assert.Equal(ErrorCodes.MemberNotMoveable, ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task PullMembersUp_IndexerToInterface_PrivateSetter_EmitsGetOnlyAndCompiles()
    {
        const string source = """
            namespace TestApp;

            public interface IAnimal
            {
            }

            public class Dog : IAnimal
            {
                public int this[int i] { get => i; private set { } }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PullMembersUpOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PullMembersUpParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            Members = ["this[]"]
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var ifaceIndexer = Assert.Single(FindIndexers(updated, "IAnimal"));
        var dogIndexer = Assert.Single(FindIndexers(updated, "Dog"));
        Assert.Contains(ifaceIndexer.AccessorList!.Accessors, a => a.IsKind(SyntaxKind.GetAccessorDeclaration));
        Assert.DoesNotContain(ifaceIndexer.AccessorList.Accessors, a => a.IsKind(SyntaxKind.SetAccessorDeclaration));
        Assert.DoesNotContain(ifaceIndexer.AccessorList.Accessors, a => a.IsKind(SyntaxKind.InitAccessorDeclaration));
        Assert.Contains(dogIndexer.AccessorList!.Accessors, a => a.IsKind(SyntaxKind.GetAccessorDeclaration));
        Assert.Contains(dogIndexer.AccessorList.Accessors, a =>
            a.IsKind(SyntaxKind.SetAccessorDeclaration)
            && a.Modifiers.Any(SyntaxKind.PrivateKeyword));
        AssertCompiles(updated);
    }

    [SkippableFact]
    public async Task PullMembersUp_MakeAbstract_PrivateSetter_Throws()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
            }

            public class Dog : Animal
            {
                public int this[int i] { get => i; private set { } }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PullMembersUpOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new PullMembersUpParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Dog",
                Members = ["this[]"],
                MakeAbstract = true
            }));

        Assert.Equal(ErrorCodes.MemberNotMoveable, ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task PullMembersUp_SelectedIndexer_DependsOnOtherIndexerOverload_Throws()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
            }

            public class Dog : Animal
            {
                public string this[int i] => this[i.ToString()];

                public string this[string key] => key;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PullMembersUpOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new PullMembersUpParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Dog",
                Members = ["this[int i]"]
            }));

        Assert.Equal(ErrorCodes.MemberDependsOnDerived, ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task PullMembersUp_BothIndexerOverloads_AllowsCrossReference()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
            }

            public class Dog : Animal
            {
                public string this[int i] => this[i.ToString()];

                public string this[string key] => key;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new PullMembersUpOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new PullMembersUpParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            Members = ["this[int i]", "this[string key]"]
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Equal(2, FindIndexers(updated, "Animal").Count);
        Assert.Empty(FindIndexers(updated, "Dog"));
        AssertCompiles(updated);
    }

    #endregion

    #region Helpers

    private static string AbsoluteTestPath() =>
        OperatingSystem.IsWindows() ? @"C:\test\file.cs" : "/test/file.cs";

    private static string NormalizeNewlines(string text) =>
        text.Replace("\r\n", "\n");

    private static string ExtractTypeBody(string source, string typeName)
    {
        var normalized = NormalizeNewlines(source);
        var start = normalized.IndexOf("class " + typeName, StringComparison.Ordinal);
        if (start < 0)
            start = normalized.IndexOf("interface " + typeName, StringComparison.Ordinal);
        if (start < 0)
            throw new InvalidOperationException($"Type '{typeName}' not found.");

        var open = normalized.IndexOf('{', start);
        var depth = 0;
        for (var i = open; i < normalized.Length; i++)
        {
            if (normalized[i] == '{') depth++;
            else if (normalized[i] == '}')
            {
                depth--;
                if (depth == 0)
                    return normalized.Substring(open, i - open + 1);
            }
        }

        return normalized[open..];
    }

    private static string GetTypeSection(string source, string typeName)
    {
        foreach (var keyword in new[] { "class ", "interface " })
        {
            var marker = keyword + typeName;
            var start = source.IndexOf(marker, StringComparison.Ordinal);
            if (start < 0)
                continue;

            var nextClass = source.IndexOf("class ", start + marker.Length, StringComparison.Ordinal);
            var nextInterface = source.IndexOf("interface ", start + marker.Length, StringComparison.Ordinal);
            var next = nextClass < 0 ? nextInterface
                : nextInterface < 0 ? nextClass
                : Math.Min(nextClass, nextInterface);
            return next < 0 ? source[start..] : source[start..next];
        }

        throw new InvalidOperationException($"Type '{typeName}' not found.");
    }

    private static TypeDeclarationSyntax FindType(string source, string typeName)
    {
        var type = CSharpSyntaxTree.ParseText(source).GetCompilationUnitRoot().DescendantNodes()
            .OfType<TypeDeclarationSyntax>()
            .FirstOrDefault(t => t.Identifier.Text == typeName);
        Assert.True(type != null, $"Generated source did not contain type '{typeName}':\n{source}");
        return type!;
    }

    private static IReadOnlyList<IndexerDeclarationSyntax> FindIndexers(string source, string typeName) =>
        FindType(source, typeName).Members.OfType<IndexerDeclarationSyntax>().ToList();

    private static PropertyDeclarationSyntax? FindProperty(string source, string typeName, string name) =>
        FindType(source, typeName).Members.OfType<PropertyDeclarationSyntax>()
            .FirstOrDefault(p => p.Identifier.Text == name);

    private static void AssertCompiles(string source)
    {
        var compilation = CSharpCompilation.Create(
                "PullMembersUpCompileTest",
                new[]
                {
                    CSharpSyntaxTree.ParseText("global using System;"),
                    CSharpSyntaxTree.ParseText(source)
                },
                new[]
                {
                    MetadataReference.CreateFromFile(typeof(object).Assembly.Location)
                },
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Select(d => d.ToString())
            .ToList();
        Assert.True(errors.Count == 0, "Generated pull_members_up members did not compile:\n" + string.Join("\n", errors) + "\n\n" + source);
    }

    private sealed class TempWorkspace : IAsyncDisposable
    {
        public required string DirectoryPath { get; init; }
        public required string ProjectPath { get; init; }
        public required string SourcePath { get; init; }
        public required WorkspaceContext Context { get; init; }

        public static async Task<TempWorkspace> CreateAsync(string source, string fileName = "Types.cs")
        {
            Skip.IfNot(ModuleInitializer.MsBuildAvailable, ModuleInitializer.MsBuildError ?? "MSBuild not available");

            var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpPullMembersUp_" + Guid.NewGuid().ToString("N"));
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

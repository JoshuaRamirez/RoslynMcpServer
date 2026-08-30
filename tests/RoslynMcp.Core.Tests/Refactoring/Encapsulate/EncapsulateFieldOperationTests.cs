using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Encapsulate;
using RoslynMcp.Core.Workspace;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring;

/// <summary>
/// Operation-level tests for <see cref="EncapsulateFieldOperation"/> (UC-EF1 updateReferences).
/// </summary>
public class EncapsulateFieldOperationTests
{
    private const string PersonSource = """
        namespace TestApp;

        public class Person
        {
            public string _name;

            public string Display() => _name;
        }
        """;

    private const string CallerSource = """
        namespace TestApp;

        public class Caller
        {
            public static string Read(Person person) => person._name;
        }
        """;

    private const string PersonNameFieldSource = """
        namespace TestApp;

        public class Person
        {
            public string name;

            public string Display() => name;
        }
        """;

    private const string CallerNameFieldSource = """
        namespace TestApp;

        public class Caller
        {
            public static string Read(Person person) => person.name;
        }
        """;

    #region P0 Default / omitted updateReferences

    [SkippableFact]
    public async Task EncapsulateField_DefaultUpdateReferences_UpdatesExternalRefs()
    {
        await using var workspace = await TempWorkspace.CreateAsync(
            ("Person.cs", PersonSource),
            ("Caller.cs", CallerSource));
        var operation = new EncapsulateFieldOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new EncapsulateFieldParams
        {
            SourceFile = workspace.SourcePath,
            FieldName = "_name"
        });

        Assert.True(result.Success);
        Assert.False(result.Preview);
        Assert.Equal(1, result.ReferencesUpdated);

        var person = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var caller = NormalizeNewlines(await File.ReadAllTextAsync(workspace.GetPath("Caller.cs")));

        AssertEncapsulatedField(person, "_name");
        Assert.Contains("public string Name", person);
        Assert.Contains("=> _name", person);
        Assert.Contains("person.Name", caller);
        Assert.DoesNotContain("person._name", caller);
    }

    [SkippableFact]
    public async Task EncapsulateField_UpdateReferencesTrue_UpdatesExternalRefs()
    {
        await using var workspace = await TempWorkspace.CreateAsync(
            ("Person.cs", PersonSource),
            ("Caller.cs", CallerSource));
        var operation = new EncapsulateFieldOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new EncapsulateFieldParams
        {
            SourceFile = workspace.SourcePath,
            FieldName = "_name",
            UpdateReferences = true
        });

        Assert.True(result.Success);
        Assert.Equal(1, result.ReferencesUpdated);

        var caller = NormalizeNewlines(await File.ReadAllTextAsync(workspace.GetPath("Caller.cs")));
        Assert.Contains("person.Name", caller);
        Assert.DoesNotContain("person._name", caller);
    }

    #endregion

    #region P0 updateReferences false leaves external callers on the field

    [SkippableFact]
    public async Task EncapsulateField_UpdateReferencesFalse_LeavesExternalCallersOnField()
    {
        await using var workspace = await TempWorkspace.CreateAsync(
            ("Person.cs", PersonSource),
            ("Caller.cs", CallerSource));
        var operation = new EncapsulateFieldOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new EncapsulateFieldParams
        {
            SourceFile = workspace.SourcePath,
            FieldName = "_name",
            UpdateReferences = false
        });

        Assert.True(result.Success);
        Assert.Equal(0, result.ReferencesUpdated);

        var person = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var caller = NormalizeNewlines(await File.ReadAllTextAsync(workspace.GetPath("Caller.cs")));

        AssertEncapsulatedField(person, "_name");
        Assert.Contains("public string Name", person);
        Assert.Contains("person._name", caller);
        Assert.DoesNotContain("person.Name", caller);
    }

    [SkippableFact]
    public async Task EncapsulateField_UpdateReferencesFalse_RenamedField_RewritesExternalRefsToNewFieldName()
    {
        await using var workspace = await TempWorkspace.CreateAsync(
            ("Person.cs", PersonNameFieldSource),
            ("Caller.cs", CallerNameFieldSource));
        var operation = new EncapsulateFieldOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new EncapsulateFieldParams
        {
            SourceFile = workspace.SourcePath,
            FieldName = "name",
            UpdateReferences = false
        });

        Assert.True(result.Success);
        Assert.Equal(1, result.ReferencesUpdated);

        var person = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var caller = NormalizeNewlines(await File.ReadAllTextAsync(workspace.GetPath("Caller.cs")));

        AssertEncapsulatedField(person, "_name");
        Assert.Contains("public string Name", person);
        Assert.Contains("person._name", caller);
        Assert.DoesNotContain("person.Name", caller);
        Assert.DoesNotContain("person.name;", caller);
        Assert.DoesNotContain("=> person.name", caller);
    }

    #endregion

    #region P0 Preview

    [SkippableFact]
    public async Task EncapsulateField_Preview_DefaultUpdateReferences_DescribesRewriteAndWritesNothing()
    {
        await using var workspace = await TempWorkspace.CreateAsync(
            ("Person.cs", PersonSource),
            ("Caller.cs", CallerSource));
        var personBefore = await File.ReadAllTextAsync(workspace.SourcePath);
        var callerBefore = await File.ReadAllTextAsync(workspace.GetPath("Caller.cs"));
        var operation = new EncapsulateFieldOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new EncapsulateFieldParams
        {
            SourceFile = workspace.SourcePath,
            FieldName = "_name",
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.Contains(result.PendingChanges, change =>
            change.Description.Contains("external references to update", StringComparison.Ordinal));
        Assert.Equal(personBefore, await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Equal(callerBefore, await File.ReadAllTextAsync(workspace.GetPath("Caller.cs")));
    }

    [SkippableFact]
    public async Task EncapsulateField_Preview_UpdateReferencesFalse_DescribesNoRewriteAndWritesNothing()
    {
        await using var workspace = await TempWorkspace.CreateAsync(
            ("Person.cs", PersonSource),
            ("Caller.cs", CallerSource));
        var personBefore = await File.ReadAllTextAsync(workspace.SourcePath);
        var callerBefore = await File.ReadAllTextAsync(workspace.GetPath("Caller.cs"));
        var operation = new EncapsulateFieldOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new EncapsulateFieldParams
        {
            SourceFile = workspace.SourcePath,
            FieldName = "_name",
            UpdateReferences = false,
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.Contains(result.PendingChanges, change =>
            change.Description.Contains("external references will not be updated", StringComparison.Ordinal));
        Assert.Equal(personBefore, await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Equal(callerBefore, await File.ReadAllTextAsync(workspace.GetPath("Caller.cs")));
    }

    [Fact]
    public void DescribeReferenceUpdates_True_MentionsCountToUpdate()
    {
        var text = EncapsulateFieldOperation.DescribeReferenceUpdates(
            "_name", "Name", updateReferences: true, externalRefCount: 2);
        Assert.Contains("Encapsulate field '_name' as property 'Name'", text);
        Assert.Contains("2 external references to update", text);
    }

    [Fact]
    public void DescribeReferenceUpdates_False_MentionsRefsWillNotBeUpdated()
    {
        var text = EncapsulateFieldOperation.DescribeReferenceUpdates(
            "_name", "Name", updateReferences: false, externalRefCount: 2);
        Assert.Contains("Encapsulate field '_name' as property 'Name'", text);
        Assert.Contains("external references will not be updated", text);
        Assert.DoesNotContain("to update", text);
    }

    #endregion

    #region Existing readOnly / propertyName / same-class

    [SkippableFact]
    public async Task EncapsulateField_ReadOnly_EmitsGetterOnly()
    {
        await using var workspace = await TempWorkspace.CreateAsync(
            ("Person.cs", PersonSource),
            ("Caller.cs", CallerSource));
        var operation = new EncapsulateFieldOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new EncapsulateFieldParams
        {
            SourceFile = workspace.SourcePath,
            FieldName = "_name",
            ReadOnly = true
        });

        Assert.True(result.Success);

        var person = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("public string Name", person);
        Assert.Contains("get", person);
        Assert.DoesNotContain("set", person);
        Assert.Contains("=> _name", person);
    }

    [SkippableFact]
    public async Task EncapsulateField_CustomPropertyName_UsesProvidedName()
    {
        await using var workspace = await TempWorkspace.CreateAsync(
            ("Person.cs", PersonSource),
            ("Caller.cs", CallerSource));
        var operation = new EncapsulateFieldOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new EncapsulateFieldParams
        {
            SourceFile = workspace.SourcePath,
            FieldName = "_name",
            PropertyName = "FullName"
        });

        Assert.True(result.Success);

        var person = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var caller = NormalizeNewlines(await File.ReadAllTextAsync(workspace.GetPath("Caller.cs")));
        Assert.Contains("public string FullName", person);
        Assert.Contains("person.FullName", caller);
        Assert.DoesNotContain("person._name", caller);
    }

    [SkippableFact]
    public async Task EncapsulateField_SameClassReferences_StayOnField()
    {
        await using var workspace = await TempWorkspace.CreateAsync(
            ("Person.cs", PersonSource),
            ("Caller.cs", CallerSource));
        var operation = new EncapsulateFieldOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new EncapsulateFieldParams
        {
            SourceFile = workspace.SourcePath,
            FieldName = "_name"
        });

        Assert.True(result.Success);

        var person = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("public string Display() => _name", person);
        Assert.DoesNotContain("Display() => Name", person);
    }

    [SkippableFact]
    public async Task EncapsulateField_ReadOnly_UpdateReferencesFalse_StillEncapsulates()
    {
        await using var workspace = await TempWorkspace.CreateAsync(
            ("Person.cs", PersonSource),
            ("Caller.cs", CallerSource));
        var operation = new EncapsulateFieldOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new EncapsulateFieldParams
        {
            SourceFile = workspace.SourcePath,
            FieldName = "_name",
            ReadOnly = true,
            PropertyName = "FullName",
            UpdateReferences = false
        });

        Assert.True(result.Success);
        Assert.Equal(0, result.ReferencesUpdated);

        var person = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var caller = NormalizeNewlines(await File.ReadAllTextAsync(workspace.GetPath("Caller.cs")));
        AssertEncapsulatedField(person, "_name");
        Assert.Contains("public string FullName", person);
        Assert.DoesNotContain("set", person);
        Assert.Contains("person._name", caller);
        Assert.DoesNotContain("person.FullName", caller);
    }

    #endregion

    #region P0 optional line disambiguation

    private const string NestedSameNameFieldSource = """
        namespace TestApp;

        public class Person
        {
            public string name; /* outer-field */

            public class Nested
            {
                public string name; /* nested-field */
            }
        }
        """;

    [Fact]
    public void Line_DefaultsToNull()
    {
        var @params = new EncapsulateFieldParams
        {
            SourceFile = AbsoluteTestPath(),
            FieldName = "name"
        };

        Assert.Null(@params.Line);
    }

    [Fact]
    public void Validate_InvalidLine_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            EncapsulateFieldOperation.Validate(new EncapsulateFieldParams
            {
                SourceFile = AbsoluteTestPath(),
                FieldName = "name",
                Line = 0
            }));

        Assert.Equal(ErrorCodes.InvalidLineNumber, ex.ErrorCode);
        Assert.Equal("1006", ex.ErrorCode);
    }

    [Fact]
    public void Validate_NegativeLine_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            EncapsulateFieldOperation.Validate(new EncapsulateFieldParams
            {
                SourceFile = AbsoluteTestPath(),
                FieldName = "name",
                Line = -1
            }));

        Assert.Equal(ErrorCodes.InvalidLineNumber, ex.ErrorCode);
        Assert.Equal("1006", ex.ErrorCode);
    }

    [SkippableFact]
    public async Task EncapsulateField_OmittedLine_KeepsFieldNameFirstMatch()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NestedSameNameFieldSource);
        var operation = new EncapsulateFieldOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new EncapsulateFieldParams
        {
            SourceFile = workspace.SourcePath,
            FieldName = "name"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var (outer, nested) = SplitOuterAndNested(updated);
        AssertEncapsulatedField(outer, "_name");
        Assert.Contains("public string Name", outer);
        Assert.Contains("public string name; /* nested-field */", nested);
        Assert.DoesNotContain("public string Name", nested);
    }

    [SkippableFact]
    public async Task EncapsulateField_LineOnNestedIdentifier_PicksNestedField()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NestedSameNameFieldSource);
        var operation = new EncapsulateFieldOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new EncapsulateFieldParams
        {
            SourceFile = workspace.SourcePath,
            FieldName = "name",
            Line = FindLine(NestedSameNameFieldSource, "nested-field")
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var (outer, nested) = SplitOuterAndNested(updated);
        Assert.Contains("public string name; /* outer-field */", outer);
        AssertEncapsulatedField(nested, "_name");
        Assert.Contains("public string Name", nested);
    }

    [SkippableFact]
    public async Task EncapsulateField_LineOnOuterIdentifier_PicksOuterField()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NestedSameNameFieldSource);
        var operation = new EncapsulateFieldOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new EncapsulateFieldParams
        {
            SourceFile = workspace.SourcePath,
            FieldName = "name",
            Line = FindLine(NestedSameNameFieldSource, "outer-field")
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var (outer, nested) = SplitOuterAndNested(updated);
        AssertEncapsulatedField(outer, "_name");
        Assert.Contains("public string Name", outer);
        Assert.Contains("public string name; /* nested-field */", nested);
        Assert.DoesNotContain("public string Name", nested);
    }

    [SkippableFact]
    public async Task EncapsulateField_LineSetWithNoCoveringMatch_ThrowsFieldNotFound()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NestedSameNameFieldSource);
        var operation = new EncapsulateFieldOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new EncapsulateFieldParams
            {
                SourceFile = workspace.SourcePath,
                FieldName = "name",
                Line = 1
            }));

        Assert.Equal(ErrorCodes.FieldNotFound, ex.ErrorCode);
        Assert.Equal("2008", ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task EncapsulateField_SequentialCalls_ReusedWorkspace_ActsOnSecondSelectedField()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NestedSameNameFieldSource);
        var operation = new EncapsulateFieldOperation(workspace.Context);

        var first = await operation.ExecuteAsync(new EncapsulateFieldParams
        {
            SourceFile = workspace.SourcePath,
            FieldName = "name",
            Line = FindLine(NestedSameNameFieldSource, "outer-field")
        });
        Assert.True(first.Success);

        var afterFirst = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var (outerAfterFirst, nestedAfterFirst) = SplitOuterAndNested(afterFirst);
        AssertEncapsulatedField(outerAfterFirst, "_name");
        Assert.Contains("public string Name", outerAfterFirst);
        Assert.Contains("public string name; /* nested-field */", nestedAfterFirst);

        var second = await operation.ExecuteAsync(new EncapsulateFieldParams
        {
            SourceFile = workspace.SourcePath,
            FieldName = "name",
            Line = FindLine(afterFirst, "nested-field")
        });
        Assert.True(second.Success);

        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var (outer, nested) = SplitOuterAndNested(updated);
        AssertEncapsulatedField(outer, "_name");
        Assert.Contains("public string Name", outer);
        AssertEncapsulatedField(nested, "_name");
        Assert.Contains("public string Name", nested);
        Assert.DoesNotContain("public string name;", updated);
    }

    [SkippableFact]
    public async Task EncapsulateField_Preview_LineOnNested_DescribesEncapsulateAndWritesNothing()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NestedSameNameFieldSource);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new EncapsulateFieldOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new EncapsulateFieldParams
        {
            SourceFile = workspace.SourcePath,
            FieldName = "name",
            Line = FindLine(NestedSameNameFieldSource, "nested-field"),
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.Contains(result.PendingChanges, change =>
            change.Description.Contains("Encapsulate field 'name' as property 'Name'", StringComparison.Ordinal));
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    private const string NestedFieldWithOuterCallerSource = """
        namespace TestApp;

        public class Person
        {
            public class Nested
            {
                public string name; /* nested-field */
            }

            public static string Read(Nested nested) => nested.name;
        }
        """;

    private const string NestedFieldWithInternalAndOuterCallerSource = """
        namespace TestApp;

        public class Person
        {
            public class Nested
            {
                public string _name; /* nested-field */

                public string Display() => _name;
            }

            public static string Read(Nested nested) => nested._name;
        }
        """;

    [SkippableFact]
    public async Task EncapsulateField_LineOnNested_SameFileOuterCaller_RewritesToPropertyAndCompiles()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NestedFieldWithOuterCallerSource);
        var operation = new EncapsulateFieldOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new EncapsulateFieldParams
        {
            SourceFile = workspace.SourcePath,
            FieldName = "name",
            Line = FindLine(NestedFieldWithOuterCallerSource, "nested-field")
        });

        Assert.True(result.Success);
        Assert.Equal(1, result.ReferencesUpdated);

        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        AssertEncapsulatedField(updated, "_name");
        Assert.Contains("public string Name", updated);
        Assert.Contains("=> nested.Name", updated);
        Assert.DoesNotContain("=> nested.name", updated);
        await AssertCompilesAsync(workspace);
    }

    [SkippableFact]
    public async Task EncapsulateField_LineOnNested_SameTypeInternalUse_StaysOnField()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NestedFieldWithInternalAndOuterCallerSource);
        var operation = new EncapsulateFieldOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new EncapsulateFieldParams
        {
            SourceFile = workspace.SourcePath,
            FieldName = "_name",
            Line = FindLine(NestedFieldWithInternalAndOuterCallerSource, "nested-field")
        });

        Assert.True(result.Success);

        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        AssertEncapsulatedField(updated, "_name");
        Assert.Contains("public string Name", updated);
        Assert.Contains("public string Display() => _name", updated);
        Assert.DoesNotContain("Display() => Name", updated);
        Assert.Contains("=> nested.Name", updated);
        Assert.DoesNotContain("=> nested._name", updated);
        await AssertCompilesAsync(workspace);
    }

    [Fact]
    public void FindFieldDeclarator_OmittedLine_KeepsFirstMatch()
    {
        var root = Parse(NestedSameNameFieldSource);
        var found = EncapsulateFieldOperation.FindFieldDeclarator(root, "name", line: null);

        Assert.NotNull(found);
        var type = found.Ancestors().OfType<TypeDeclarationSyntax>().First();
        Assert.Equal("Person", type.Identifier.Text);
        Assert.False(type.Parent is TypeDeclarationSyntax);
    }

    [Fact]
    public void FindFieldDeclarator_LineOnNestedIdentifier_PicksNested()
    {
        var root = Parse(NestedSameNameFieldSource);
        var found = EncapsulateFieldOperation.FindFieldDeclarator(
            root, "name", FindLine(NestedSameNameFieldSource, "nested-field"));

        Assert.NotNull(found);
        var type = found.Ancestors().OfType<TypeDeclarationSyntax>().First();
        Assert.Equal("Nested", type.Identifier.Text);
        Assert.True(type.Parent is TypeDeclarationSyntax outer && outer.Identifier.Text == "Person");
    }

    [Fact]
    public void FindFieldDeclarator_LineOnOuterIdentifier_PicksOuter()
    {
        var root = Parse(NestedSameNameFieldSource);
        var found = EncapsulateFieldOperation.FindFieldDeclarator(
            root, "name", FindLine(NestedSameNameFieldSource, "outer-field"));

        Assert.NotNull(found);
        var type = found.Ancestors().OfType<TypeDeclarationSyntax>().First();
        Assert.Equal("Person", type.Identifier.Text);
        Assert.False(type.Parent is TypeDeclarationSyntax);
    }

    [Fact]
    public void FindFieldDeclarator_LineMiss_ReturnsNull()
    {
        var root = Parse(NestedSameNameFieldSource);
        var found = EncapsulateFieldOperation.FindFieldDeclarator(root, "name", line: 1);

        Assert.Null(found);
    }

    [Fact]
    public void FindFieldDeclarator_LineOnLocal_DoesNotPickLocal()
    {
        const string source = """
            namespace TestApp;

            public class Person
            {
                public string name; /* field-name */

                public void M()
                {
                    string name = "x"; /* local-name */
                }
            }
            """;

        var root = Parse(source);
        var omitted = EncapsulateFieldOperation.FindFieldDeclarator(root, "name", line: null);
        var onLocal = EncapsulateFieldOperation.FindFieldDeclarator(
            root, "name", FindLine(source, "local-name"));

        Assert.NotNull(omitted);
        Assert.True(omitted.Parent?.Parent is FieldDeclarationSyntax);
        Assert.Null(onLocal);
    }

    [Fact]
    public void FindFieldDeclarator_LineOnContinuationIdentifier_PicksField()
    {
        const string source = """
            namespace TestApp;

            public class Person
            {
                public string
                    name; /* split-field */
            }
            """;

        var root = Parse(source);
        var startLine = FindLine(source, "public string");
        var identifierLine = FindLine(source, "split-field");
        Assert.NotEqual(startLine, identifierLine);

        var found = EncapsulateFieldOperation.FindFieldDeclarator(root, "name", identifierLine);

        Assert.NotNull(found);
        Assert.Equal("name", found.Identifier.Text);
        Assert.True(found.Parent?.Parent is FieldDeclarationSyntax);
    }

    [Fact]
    public void SpanCoversLine_TreatsEndAsExclusive()
    {
        var span = new FileLinePositionSpan(
            "t.cs",
            new LinePosition(0, 0),
            new LinePosition(2, 0));

        Assert.True(EncapsulateFieldOperation.SpanCoversLine(span, 1));
        Assert.True(EncapsulateFieldOperation.SpanCoversLine(span, 2));
        Assert.False(EncapsulateFieldOperation.SpanCoversLine(span, 3));
    }

    #endregion

    #region P0 optional column disambiguation

    private const string SameLineNestedNameFieldSource = """
        namespace TestApp;

        public class Person { public string name; /* outer-field */ public class Nested { public string name; /* nested-field */ } }
        """;

    [Fact]
    public void Column_DefaultsToNull()
    {
        var @params = new EncapsulateFieldParams
        {
            SourceFile = AbsoluteTestPath(),
            FieldName = "name"
        };

        Assert.Null(@params.Column);
    }

    [Fact]
    public void Validate_InvalidColumn_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            EncapsulateFieldOperation.Validate(new EncapsulateFieldParams
            {
                SourceFile = AbsoluteTestPath(),
                FieldName = "name",
                Column = 0
            }));

        Assert.Equal(ErrorCodes.InvalidColumnNumber, ex.ErrorCode);
        Assert.Equal("1007", ex.ErrorCode);
    }

    [Fact]
    public void Validate_NegativeColumn_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            EncapsulateFieldOperation.Validate(new EncapsulateFieldParams
            {
                SourceFile = AbsoluteTestPath(),
                FieldName = "name",
                Column = -1
            }));

        Assert.Equal(ErrorCodes.InvalidColumnNumber, ex.ErrorCode);
        Assert.Equal("1007", ex.ErrorCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_EmptyFieldName_WithColumnAndLine_ThrowsMissingRequiredParam(string fieldName)
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            EncapsulateFieldOperation.Validate(new EncapsulateFieldParams
            {
                SourceFile = AbsoluteTestPath(),
                FieldName = fieldName,
                Line = 1,
                Column = 1
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [SkippableFact]
    public async Task EncapsulateField_OmittedColumn_KeepsFieldNameFirstOrDefaultPick()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NestedSameNameFieldSource);
        var operation = new EncapsulateFieldOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new EncapsulateFieldParams
        {
            SourceFile = workspace.SourcePath,
            FieldName = "name"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var (outer, nested) = SplitOuterAndNested(updated);
        AssertEncapsulatedField(outer, "_name");
        Assert.Contains("public string Name", outer);
        Assert.Contains("public string name; /* nested-field */", nested);
        Assert.DoesNotContain("public string Name", nested);
    }

    [SkippableFact]
    public async Task EncapsulateField_OmittedColumn_LineOnNestedIdentifier_PicksNestedField()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NestedSameNameFieldSource);
        var operation = new EncapsulateFieldOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new EncapsulateFieldParams
        {
            SourceFile = workspace.SourcePath,
            FieldName = "name",
            Line = FindLine(NestedSameNameFieldSource, "nested-field")
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var (outer, nested) = SplitOuterAndNested(updated);
        Assert.Contains("public string name; /* outer-field */", outer);
        AssertEncapsulatedField(nested, "_name");
        Assert.Contains("public string Name", nested);
    }

    [SkippableFact]
    public async Task EncapsulateField_ColumnOnNestedIdentifier_PicksNestedField()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SameLineNestedNameFieldSource);
        var operation = new EncapsulateFieldOperation(workspace.Context);
        var line = FindLine(SameLineNestedNameFieldSource, "public class Person { public string name;");
        var column = ColumnOf(SameLineNestedNameFieldSource, "name; /* nested-field */");

        var result = await operation.ExecuteAsync(new EncapsulateFieldParams
        {
            SourceFile = workspace.SourcePath,
            FieldName = "name",
            Line = line,
            Column = column
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var (outer, nested) = SplitOuterAndNested(updated);
        Assert.Contains("public string name; /* outer-field */", outer);
        AssertEncapsulatedField(nested, "_name");
        Assert.Contains("public string Name", nested);
    }

    [SkippableFact]
    public async Task EncapsulateField_ColumnOnOuterIdentifier_PicksOuterField()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SameLineNestedNameFieldSource);
        var operation = new EncapsulateFieldOperation(workspace.Context);
        var line = FindLine(SameLineNestedNameFieldSource, "public class Person { public string name;");
        var column = ColumnOf(SameLineNestedNameFieldSource, "name; /* outer-field */");

        var result = await operation.ExecuteAsync(new EncapsulateFieldParams
        {
            SourceFile = workspace.SourcePath,
            FieldName = "name",
            Line = line,
            Column = column
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var (outer, nested) = SplitOuterAndNested(updated);
        AssertEncapsulatedField(outer, "_name");
        Assert.Contains("public string Name", outer);
        Assert.Contains("public string name; /* nested-field */", nested);
        Assert.DoesNotContain("public string Name", nested);
    }

    [Fact]
    public void FindFieldDeclarator_OmittedColumn_FirstOrDefaultPicksOuter()
    {
        var root = Parse(NestedSameNameFieldSource);
        var found = EncapsulateFieldOperation.FindFieldDeclarator(root, "name", line: null, column: null);

        Assert.NotNull(found);
        var type = found.Ancestors().OfType<TypeDeclarationSyntax>().First();
        Assert.Equal("Person", type.Identifier.Text);
        Assert.False(type.Parent is TypeDeclarationSyntax);
    }

    [Fact]
    public void FindFieldDeclarator_ColumnOnNestedIdentifier_PicksNested()
    {
        var root = Parse(SameLineNestedNameFieldSource);
        var line = FindLine(SameLineNestedNameFieldSource, "public class Person { public string name;");
        var found = EncapsulateFieldOperation.FindFieldDeclarator(
            root, "name", line, ColumnOf(SameLineNestedNameFieldSource, "name; /* nested-field */"));

        Assert.NotNull(found);
        var type = found.Ancestors().OfType<TypeDeclarationSyntax>().First();
        Assert.Equal("Nested", type.Identifier.Text);
        Assert.True(type.Parent is TypeDeclarationSyntax outer && outer.Identifier.Text == "Person");
    }

    [Fact]
    public void FindFieldDeclarator_ColumnOnOuterIdentifier_PicksOuter()
    {
        var root = Parse(SameLineNestedNameFieldSource);
        var line = FindLine(SameLineNestedNameFieldSource, "public class Person { public string name;");
        var found = EncapsulateFieldOperation.FindFieldDeclarator(
            root, "name", line, ColumnOf(SameLineNestedNameFieldSource, "name; /* outer-field */"));

        Assert.NotNull(found);
        var type = found.Ancestors().OfType<TypeDeclarationSyntax>().First();
        Assert.Equal("Person", type.Identifier.Text);
        Assert.False(type.Parent is TypeDeclarationSyntax);
    }

    [Fact]
    public void FindFieldDeclarator_ColumnWithoutLine_KeepsFirstMatch()
    {
        var root = Parse(SameLineNestedNameFieldSource);
        var nestedColumn = ColumnOf(SameLineNestedNameFieldSource, "name; /* nested-field */");
        var found = EncapsulateFieldOperation.FindFieldDeclarator(
            root, "name", line: null, nestedColumn);

        Assert.NotNull(found);
        var type = found.Ancestors().OfType<TypeDeclarationSyntax>().First();
        Assert.Equal("Person", type.Identifier.Text);
        Assert.False(type.Parent is TypeDeclarationSyntax);
    }

    [Fact]
    public void FindFieldDeclarator_ColumnOnContinuationIdentifier_PicksField()
    {
        const string source = """
            namespace TestApp;

            public class Person
            {
                public string
                    name; /* split-field */
            }
            """;

        var root = Parse(source);
        var startLine = FindLine(source, "public string");
        var identifierLine = FindLine(source, "split-field");
        Assert.NotEqual(startLine, identifierLine);

        var found = EncapsulateFieldOperation.FindFieldDeclarator(
            root, "name", identifierLine, ColumnOf(source, "name; /* split-field */"));

        Assert.NotNull(found);
        Assert.Equal("name", found.Identifier.Text);
        Assert.True(found.Parent?.Parent is FieldDeclarationSyntax);
    }

    [SkippableFact]
    public async Task EncapsulateField_ColumnOnContinuationLine_PicksField()
    {
        const string source = """
            namespace TestApp;

            public class Person
            {
                public string
                    name; /* split-field */
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new EncapsulateFieldOperation(workspace.Context);
        var identifierLine = FindLine(source, "split-field");

        var result = await operation.ExecuteAsync(new EncapsulateFieldParams
        {
            SourceFile = workspace.SourcePath,
            FieldName = "name",
            Line = identifierLine,
            Column = ColumnOf(source, "name; /* split-field */")
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        AssertEncapsulatedField(updated, "_name");
        Assert.Contains("public string Name", updated);
    }

    [Fact]
    public void FindFieldDeclarator_ColumnAndLineMiss_DoesNotFallBackToFirst()
    {
        var root = Parse(NestedSameNameFieldSource);
        var found = EncapsulateFieldOperation.FindFieldDeclarator(root, "name", line: 1, column: 1);

        Assert.Null(found);
    }

    [SkippableFact]
    public async Task EncapsulateField_ColumnAndLineMiss_ThrowsFieldNotFound()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NestedSameNameFieldSource);
        var operation = new EncapsulateFieldOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new EncapsulateFieldParams
            {
                SourceFile = workspace.SourcePath,
                FieldName = "name",
                Line = 1,
                Column = 1
            }));

        Assert.Equal(ErrorCodes.FieldNotFound, ex.ErrorCode);
        Assert.Equal("2008", ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task EncapsulateField_ColumnAndLine_UnknownFieldName_ThrowsFieldNotFound()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NestedSameNameFieldSource);
        var operation = new EncapsulateFieldOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new EncapsulateFieldParams
            {
                SourceFile = workspace.SourcePath,
                FieldName = "missing",
                Line = 1,
                Column = 1
            }));

        Assert.Equal(ErrorCodes.FieldNotFound, ex.ErrorCode);
        Assert.Equal("2008", ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task EncapsulateField_Column_Preview_WritesNothing_AndDescribesEncapsulate()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SameLineNestedNameFieldSource);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new EncapsulateFieldOperation(workspace.Context);
        var line = FindLine(SameLineNestedNameFieldSource, "public class Person { public string name;");

        var result = await operation.ExecuteAsync(new EncapsulateFieldParams
        {
            SourceFile = workspace.SourcePath,
            FieldName = "name",
            Line = line,
            Column = ColumnOf(SameLineNestedNameFieldSource, "name; /* nested-field */"),
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.Contains(result.PendingChanges, change =>
            change.Description.Contains("Encapsulate field 'name' as property 'Name'", StringComparison.Ordinal));
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [Fact]
    public void SpanCoversColumn_TreatsEndAsExclusive()
    {
        const string source = "class Outer { int name; class Nested { int name; } }";
        var tree = CSharpSyntaxTree.ParseText(source);
        var nested = tree.GetRoot().DescendantNodes().OfType<VariableDeclaratorSyntax>()
            .Last(d => d.Identifier.Text == "name");
        var span = nested.GetLocation().GetLineSpan();
        var line = span.StartLinePosition.Line + 1;
        var startCol = span.StartLinePosition.Character + 1;
        var endCol = span.EndLinePosition.Character + 1;

        Assert.True(EncapsulateFieldOperation.SpanCoversColumn(span, line, startCol));
        Assert.True(EncapsulateFieldOperation.SpanCoversColumn(span, line, endCol - 1));
        Assert.False(EncapsulateFieldOperation.SpanCoversColumn(span, line, endCol));
        Assert.False(EncapsulateFieldOperation.SpanCoversColumn(span, line, startCol - 1));
    }

    [SkippableFact]
    public async Task EncapsulateField_SequentialColumn_ReusedWorkspace_ActsOnSecondSelectedField()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SameLineNestedNameFieldSource);
        var operation = new EncapsulateFieldOperation(workspace.Context);
        var line = FindLine(SameLineNestedNameFieldSource, "public class Person { public string name;");

        var first = await operation.ExecuteAsync(new EncapsulateFieldParams
        {
            SourceFile = workspace.SourcePath,
            FieldName = "name",
            Line = line,
            Column = ColumnOf(SameLineNestedNameFieldSource, "name; /* outer-field */")
        });
        Assert.True(first.Success);

        var afterFirst = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var (outerAfterFirst, nestedAfterFirst) = SplitOuterAndNested(afterFirst);
        AssertEncapsulatedField(outerAfterFirst, "_name");
        Assert.Contains("public string Name", outerAfterFirst);
        Assert.Contains("public string name; /* nested-field */", nestedAfterFirst);

        var second = await operation.ExecuteAsync(new EncapsulateFieldParams
        {
            SourceFile = workspace.SourcePath,
            FieldName = "name",
            Line = FindLine(afterFirst, "nested-field"),
            Column = ColumnOf(afterFirst, "name; /* nested-field */")
        });
        Assert.True(second.Success);

        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var (outer, nested) = SplitOuterAndNested(updated);
        AssertEncapsulatedField(outer, "_name");
        Assert.Contains("public string Name", outer);
        AssertEncapsulatedField(nested, "_name");
        Assert.Contains("public string Name", nested);
        Assert.DoesNotContain("public string name;", updated);
    }

    [Fact]
    public void FindFieldDeclarator_ColumnOnLocal_DoesNotPickLocal()
    {
        const string source = """
            namespace TestApp;

            public class Person
            {
                public string name; /* field-name */

                public void M()
                {
                    string name = "x"; /* local-name */
                }
            }
            """;

        var root = Parse(source);
        var omitted = EncapsulateFieldOperation.FindFieldDeclarator(root, "name", line: null, column: null);
        var onLocal = EncapsulateFieldOperation.FindFieldDeclarator(
            root, "name", FindLine(source, "local-name"), ColumnOf(source, "name = \"x\""));

        Assert.NotNull(omitted);
        Assert.True(omitted.Parent?.Parent is FieldDeclarationSyntax);
        Assert.Null(onLocal);
    }

    #endregion

    #region Helpers

    private static string NormalizeNewlines(string text) => text.Replace("\r\n", "\n");

    private static string AbsoluteTestPath() =>
        Path.Combine(Path.GetTempPath(), "RoslynMcpEncapsulateField_Missing.cs");

    private static SyntaxNode Parse(string source) =>
        CSharpSyntaxTree.ParseText(NormalizeNewlines(source)).GetRoot();

    private static int FindLine(string source, string snippet)
    {
        source = NormalizeNewlines(source);
        snippet = NormalizeNewlines(snippet);
        var index = source.IndexOf(snippet, StringComparison.Ordinal);
        if (index < 0)
            throw new InvalidOperationException($"Snippet not found: {snippet}");

        var line = 1;
        for (var i = 0; i < index; i++)
        {
            if (source[i] == '\n')
                line++;
        }

        return line;
    }

    private static int ColumnOf(string source, string snippet)
    {
        source = NormalizeNewlines(source);
        snippet = NormalizeNewlines(snippet);
        var index = source.IndexOf(snippet, StringComparison.Ordinal);
        if (index < 0)
            throw new InvalidOperationException($"Snippet not found: {snippet}");

        var lineStart = source.LastIndexOf('\n', index);
        return index - lineStart;
    }

    private static (string Outer, string Nested) SplitOuterAndNested(string source)
    {
        var nestedStart = source.IndexOf("class Nested", StringComparison.Ordinal);
        Assert.True(nestedStart >= 0, "Expected a Nested type in the source.");
        return (source[..nestedStart], source[nestedStart..]);
    }

    private static async Task AssertCompilesAsync(TempWorkspace workspace)
    {
        var document = workspace.Context.GetDocumentByPath(workspace.SourcePath);
        Assert.NotNull(document);
        var compilation = await document.Project.GetCompilationAsync();
        Assert.NotNull(compilation);
        var errors = compilation.GetDiagnostics()
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Select(diagnostic => diagnostic.ToString())
            .ToList();
        Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors));
    }

    /// <summary>
    /// Today's modifier rewrite prepends <c>private</c> without elastic space
    /// (<c>privatestring</c>). Do not invent a formatter leftover — match either form.
    /// </summary>
    private static void AssertEncapsulatedField(string source, string fieldName)
    {
        Assert.DoesNotContain($"public string {fieldName}", source);
        Assert.Matches($@"private\s*string\s+{System.Text.RegularExpressions.Regex.Escape(fieldName)}", source);
    }

    private sealed class TempWorkspace : IAsyncDisposable
    {
        public required string DirectoryPath { get; init; }
        public required string ProjectPath { get; init; }
        public required string SourcePath { get; init; }
        public required IReadOnlyDictionary<string, string> FilePaths { get; init; }
        public required WorkspaceContext Context { get; init; }

        public string GetPath(string fileName) => FilePaths[fileName];

        public static Task<TempWorkspace> CreateAsync(string source, string fileName = "Person.cs") =>
            CreateAsync((fileName, source));

        public static async Task<TempWorkspace> CreateAsync(params (string FileName, string Source)[] files)
        {
            Skip.IfNot(ModuleInitializer.MsBuildAvailable, ModuleInitializer.MsBuildError ?? "MSBuild not available");

            var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpEncapsulateField_" + Guid.NewGuid().ToString("N"));
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

            var filePaths = new Dictionary<string, string>(StringComparer.Ordinal);
            string? sourcePath = null;
            foreach (var (fileName, source) in files)
            {
                var path = Path.Combine(directory, fileName);
                var parent = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(parent))
                    Directory.CreateDirectory(parent);
                await File.WriteAllTextAsync(path, source);
                filePaths[fileName] = path;
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
                    FilePaths = filePaths,
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

using RoslynMcp.Contracts.Enums;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Extract;
using RoslynMcp.Core.Workspace;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Extract;

/// <summary>
/// Operation-level tests for <see cref="ExtractBaseClassOperation"/>, including <c>separateFile</c>.
/// </summary>
public class ExtractBaseClassOperationTests
{
    private const string EmployeeSource = """
        namespace TestApp;

        public class Employee
        {
            public string Name { get; set; }

            public int Age { get; set; }

            public void Work() { }
        }
        """;

    [SkippableFact]
    public async Task ExtractBaseClass_Default_WritesBaseClassIntoSourceFile()
    {
        await using var workspace = await TempWorkspace.CreateAsync(EmployeeSource);
        var operation = new ExtractBaseClassOperation(workspace.Context);
        var sibling = Path.GetFullPath(Path.Combine(workspace.DirectoryPath, "Person.cs"));

        var result = await operation.ExecuteAsync(new ExtractBaseClassParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Employee",
            BaseClassName = "Person",
            Members = new[] { "Name", "Age" }
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("class Person", updated);
        AssertInheritsFrom(updated, "Employee", "Person");
        Assert.False(File.Exists(sibling));
        Assert.DoesNotContain(sibling, result.Changes!.FilesCreated);
    }

    [SkippableFact]
    public async Task ExtractBaseClass_SeparateFileFalse_WritesBaseClassIntoSourceFile()
    {
        await using var workspace = await TempWorkspace.CreateAsync(EmployeeSource);
        var operation = new ExtractBaseClassOperation(workspace.Context);
        var sibling = Path.GetFullPath(Path.Combine(workspace.DirectoryPath, "Person.cs"));

        var result = await operation.ExecuteAsync(new ExtractBaseClassParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Employee",
            BaseClassName = "Person",
            Members = new[] { "Name", "Age" },
            SeparateFile = false
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("class Person", updated);
        AssertInheritsFrom(updated, "Employee", "Person");
        Assert.False(File.Exists(sibling));
    }

    [SkippableFact]
    public async Task ExtractBaseClass_SeparateFileTrue_WritesSiblingFileAndRemovesBaseClassFromSource()
    {
        await using var workspace = await TempWorkspace.CreateAsync(EmployeeSource);
        var operation = new ExtractBaseClassOperation(workspace.Context);
        var sibling = Path.GetFullPath(Path.Combine(workspace.DirectoryPath, "Person.cs"));

        var result = await operation.ExecuteAsync(new ExtractBaseClassParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Employee",
            BaseClassName = "Person",
            Members = new[] { "Name", "Age" },
            SeparateFile = true
        });

        Assert.True(result.Success);
        Assert.True(File.Exists(sibling));
        Assert.Contains(sibling, result.Changes!.FilesCreated);

        var source = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var baseFile = NormalizeNewlines(await File.ReadAllTextAsync(sibling));

        Assert.DoesNotContain("class Person", source);
        AssertInheritsFrom(source, "Employee", "Person");
        Assert.Contains("class Person", baseFile);
        Assert.Contains("Name", baseFile);
        Assert.Contains("Age", baseFile);
    }

    [SkippableFact]
    public async Task ExtractBaseClass_TargetFileWinsOverSeparateFile()
    {
        await using var workspace = await TempWorkspace.CreateAsync(EmployeeSource);
        var operation = new ExtractBaseClassOperation(workspace.Context);
        var sibling = Path.GetFullPath(Path.Combine(workspace.DirectoryPath, "Person.cs"));
        var explicitTarget = Path.GetFullPath(Path.Combine(workspace.DirectoryPath, "CustomBase.cs"));

        var result = await operation.ExecuteAsync(new ExtractBaseClassParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Employee",
            BaseClassName = "Person",
            Members = new[] { "Name", "Age" },
            SeparateFile = true,
            TargetFile = explicitTarget
        });

        Assert.True(result.Success);
        Assert.True(File.Exists(explicitTarget));
        Assert.False(File.Exists(sibling));
        Assert.Contains(explicitTarget, result.Changes!.FilesCreated);
        Assert.DoesNotContain(sibling, result.Changes.FilesCreated);

        var source = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var custom = NormalizeNewlines(await File.ReadAllTextAsync(explicitTarget));

        Assert.DoesNotContain("class Person", source);
        Assert.Contains("class Person", custom);
        AssertInheritsFrom(source, "Employee", "Person");
    }

    [SkippableFact]
    public async Task ExtractBaseClass_Preview_DoesNotWriteFiles()
    {
        await using var workspace = await TempWorkspace.CreateAsync(EmployeeSource);
        var operation = new ExtractBaseClassOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);
        var sibling = Path.GetFullPath(Path.Combine(workspace.DirectoryPath, "Person.cs"));

        var result = await operation.ExecuteAsync(new ExtractBaseClassParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Employee",
            BaseClassName = "Person",
            Members = new[] { "Name", "Age" },
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.NotEmpty(result.PendingChanges);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.False(File.Exists(sibling));
    }

    [SkippableFact]
    public async Task ExtractBaseClass_SeparateFileTrue_Preview_DoesNotWriteFiles()
    {
        await using var workspace = await TempWorkspace.CreateAsync(EmployeeSource);
        var operation = new ExtractBaseClassOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);
        var sibling = Path.GetFullPath(Path.Combine(workspace.DirectoryPath, "Person.cs"));

        var result = await operation.ExecuteAsync(new ExtractBaseClassParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Employee",
            BaseClassName = "Person",
            Members = new[] { "Name", "Age" },
            SeparateFile = true,
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.NotEmpty(result.PendingChanges);
        Assert.Equal(ChangeKind.Create, result.PendingChanges[0].ChangeType);
        Assert.Equal(sibling, result.PendingChanges[0].File);
        Assert.Contains("Person", result.PendingChanges[0].AfterSnippet);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.False(File.Exists(sibling));
    }

    [SkippableFact]
    public async Task ExtractBaseClass_SeparateFileTrue_SiblingExists_ThrowsTargetFileExists()
    {
        await using var workspace = await TempWorkspace.CreateAsync(EmployeeSource);
        var sibling = Path.GetFullPath(Path.Combine(workspace.DirectoryPath, "Person.cs"));
        await File.WriteAllTextAsync(sibling, """
            namespace TestApp;

            public class Existing
            {
            }
            """);
        var sourceBefore = await File.ReadAllTextAsync(workspace.SourcePath);
        var siblingBefore = await File.ReadAllTextAsync(sibling);
        var operation = new ExtractBaseClassOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ExtractBaseClassParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Employee",
                BaseClassName = "Person",
                Members = new[] { "Name", "Age" },
                SeparateFile = true
            }));

        Assert.Equal(ErrorCodes.TargetFileExists, ex.ErrorCode);
        Assert.Equal("3019", ex.ErrorCode);
        Assert.Equal(sourceBefore, await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Equal(siblingBefore, await File.ReadAllTextAsync(sibling));
    }

    [SkippableFact]
    public async Task ExtractBaseClass_SeparateFileTrue_ExplicitCompileItems_AddsCompileInclude()
    {
        await using var workspace = await TempWorkspace.CreateWithExplicitCompileItemsAsync(EmployeeSource);
        var operation = new ExtractBaseClassOperation(workspace.Context);
        var sibling = Path.GetFullPath(Path.Combine(workspace.DirectoryPath, "Person.cs"));
        var projectBefore = await File.ReadAllTextAsync(workspace.ProjectPath);

        var result = await operation.ExecuteAsync(new ExtractBaseClassParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Employee",
            BaseClassName = "Person",
            Members = new[] { "Name", "Age" },
            SeparateFile = true
        });

        Assert.True(result.Success);
        Assert.True(File.Exists(sibling));
        Assert.Contains(sibling, result.Changes!.FilesCreated);
        Assert.Contains(workspace.ProjectPath, result.Changes.FilesModified);

        var projectAfter = await File.ReadAllTextAsync(workspace.ProjectPath);
        Assert.NotEqual(projectBefore, projectAfter);
        Assert.Contains("Include=\"Person.cs\"", projectAfter);
        Assert.Contains("Include=\"Employee.cs\"", projectAfter);

        var source = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.DoesNotContain("class Person", source);
        AssertInheritsFrom(source, "Employee", "Person");
    }

    [SkippableFact]
    public async Task ExtractBaseClass_SeparateFileTrue_SdkDefaults_LeavesProjectFileUnchanged()
    {
        await using var workspace = await TempWorkspace.CreateAsync(EmployeeSource);
        var operation = new ExtractBaseClassOperation(workspace.Context);
        var projectBefore = await File.ReadAllTextAsync(workspace.ProjectPath);

        var result = await operation.ExecuteAsync(new ExtractBaseClassParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Employee",
            BaseClassName = "Person",
            Members = new[] { "Name", "Age" },
            SeparateFile = true
        });

        Assert.True(result.Success);
        Assert.Equal(projectBefore, await File.ReadAllTextAsync(workspace.ProjectPath));
        Assert.DoesNotContain(workspace.ProjectPath, result.Changes!.FilesModified);
        Assert.DoesNotContain("Include=\"Person.cs\"", projectBefore);
    }

    [SkippableFact]
    public async Task ExtractBaseClass_SeparateFileTrue_ExplicitCompileItems_Preview_WritesNothing()
    {
        await using var workspace = await TempWorkspace.CreateWithExplicitCompileItemsAsync(EmployeeSource);
        var operation = new ExtractBaseClassOperation(workspace.Context);
        var sourceBefore = await File.ReadAllTextAsync(workspace.SourcePath);
        var projectBefore = await File.ReadAllTextAsync(workspace.ProjectPath);
        var sibling = Path.GetFullPath(Path.Combine(workspace.DirectoryPath, "Person.cs"));

        var result = await operation.ExecuteAsync(new ExtractBaseClassParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Employee",
            BaseClassName = "Person",
            Members = new[] { "Name", "Age" },
            SeparateFile = true,
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.Equal(sourceBefore, await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Equal(projectBefore, await File.ReadAllTextAsync(workspace.ProjectPath));
        Assert.False(File.Exists(sibling));
        Assert.Contains(result.PendingChanges!, c => c.File == workspace.ProjectPath);
    }

    [SkippableFact]
    public async Task ExtractBaseClass_SeparateFileTrue_NestedClass_ThrowsCannotExtractNestedToSeparateFile()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NestedEmployeeSource);
        var sibling = Path.GetFullPath(Path.Combine(workspace.DirectoryPath, "Person.cs"));
        var sourceBefore = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new ExtractBaseClassOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ExtractBaseClassParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Employee",
                BaseClassName = "Person",
                Members = new[] { "Value" },
                SeparateFile = true
            }));

        Assert.Equal(ErrorCodes.CannotExtractNestedToSeparateFile, ex.ErrorCode);
        Assert.Equal("3142", ex.ErrorCode);
        Assert.Equal(sourceBefore, await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.False(File.Exists(sibling));
    }

    [SkippableFact]
    public async Task ExtractBaseClass_Default_NestedClass_WritesBaseClassInsideContainingType()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NestedEmployeeSource);
        var operation = new ExtractBaseClassOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ExtractBaseClassParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Employee",
            BaseClassName = "Person",
            Members = new[] { "Value" }
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("class Person", updated);
        AssertInheritsFrom(updated, "Employee", "Person");
        Assert.Contains("class Outer<T>", updated);
        Assert.False(File.Exists(Path.GetFullPath(Path.Combine(workspace.DirectoryPath, "Person.cs"))));
    }

    #region Events

    [SkippableFact]
    public async Task ExtractBaseClass_FieldLikeEvent_MovesEventOntoBase()
    {
        const string source = """
            namespace TestApp;

            public class Employee
            {
                public event System.EventHandler Changed;

                public void Work() { }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ExtractBaseClassOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ExtractBaseClassParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Employee",
            BaseClassName = "Person",
            Members = new[] { "Changed" }
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var person = GetClassSection(updated, "Person");
        var employee = GetClassSection(updated, "Employee");

        Assert.Contains("class Person", updated);
        AssertInheritsFrom(updated, "Employee", "Person");
        Assert.Contains("public event System.EventHandler Changed", person);
        Assert.DoesNotContain("event System.EventHandler Changed", employee);
        Assert.Contains("public void Work()", employee);
        Assert.DoesNotContain("abstract event", updated);
        Assert.DoesNotContain("public abstract class Person", updated);
    }

    [SkippableFact]
    public async Task ExtractBaseClass_AccessorStyleEvent_MovesEventOntoBase()
    {
        const string source = """
            namespace TestApp;

            public class Employee
            {
                public event System.EventHandler Changed
                {
                    add { }
                    remove { }
                }

                public string Name { get; set; }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ExtractBaseClassOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ExtractBaseClassParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Employee",
            BaseClassName = "Person",
            Members = new[] { "Changed" }
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var person = GetClassSection(updated, "Person");
        var employee = GetClassSection(updated, "Employee");

        Assert.Contains("class Person", updated);
        AssertInheritsFrom(updated, "Employee", "Person");
        Assert.Contains("public event System.EventHandler Changed", person);
        Assert.Contains("add", person);
        Assert.Contains("remove", person);
        Assert.DoesNotContain("event System.EventHandler Changed", employee);
        Assert.Contains("public string Name { get; set; }", employee);
    }

    [SkippableFact]
    public async Task ExtractBaseClass_MultiVariableEventField_LeavesUnrelatedDeclarator()
    {
        const string source = """
            namespace TestApp;

            public class Employee
            {
                public event System.EventHandler Changed, Other;

                public int Age { get; set; }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ExtractBaseClassOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ExtractBaseClassParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Employee",
            BaseClassName = "Person",
            Members = new[] { "Changed" }
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var person = GetClassSection(updated, "Person");
        var employee = GetClassSection(updated, "Employee");

        AssertInheritsFrom(updated, "Employee", "Person");
        Assert.Contains("public event System.EventHandler Changed", person);
        Assert.DoesNotContain("Other", person);
        Assert.DoesNotContain("Changed, Other", updated);
        Assert.Contains("public event System.EventHandler Other", employee);
        Assert.DoesNotContain("event System.EventHandler Changed", employee);
        Assert.Contains("public int Age { get; set; }", employee);
    }

    [SkippableFact]
    public async Task ExtractBaseClass_MultiVariableEventField_SecondDeclarator_LeavesFirst()
    {
        const string source = """
            namespace TestApp;

            public class Employee
            {
                public event System.EventHandler Changed, Other;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ExtractBaseClassOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ExtractBaseClassParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Employee",
            BaseClassName = "Person",
            Members = new[] { "Other" }
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var person = GetClassSection(updated, "Person");
        var employee = GetClassSection(updated, "Employee");

        AssertInheritsFrom(updated, "Employee", "Person");
        Assert.Contains("public event System.EventHandler Other", person);
        Assert.DoesNotContain("Changed", person);
        Assert.Contains("public event System.EventHandler Changed", employee);
        Assert.DoesNotContain("event System.EventHandler Other", employee);
    }

    [SkippableFact]
    public async Task ExtractBaseClass_PrivateEvent_BecomesProtectedOnBase()
    {
        const string source = """
            namespace TestApp;

            public class Employee
            {
                private event System.EventHandler Changed;

                public void Work() { }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ExtractBaseClassOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ExtractBaseClassParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Employee",
            BaseClassName = "Person",
            Members = new[] { "Changed" }
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var person = GetClassSection(updated, "Person");
        var employee = GetClassSection(updated, "Employee");

        AssertInheritsFrom(updated, "Employee", "Person");
        Assert.Contains("protected event System.EventHandler Changed", person);
        Assert.DoesNotContain("private event", person);
        Assert.DoesNotContain("event System.EventHandler Changed", employee);
        Assert.Contains("public void Work()", employee);
    }

    [SkippableFact]
    public async Task ExtractBaseClass_PrivateAccessorStyleEvent_BecomesProtectedOnBase()
    {
        const string source = """
            namespace TestApp;

            public class Employee
            {
                private event System.EventHandler Changed
                {
                    add { }
                    remove { }
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ExtractBaseClassOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ExtractBaseClassParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Employee",
            BaseClassName = "Person",
            Members = new[] { "Changed" }
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var person = GetClassSection(updated, "Person");

        AssertInheritsFrom(updated, "Employee", "Person");
        Assert.Contains("protected event System.EventHandler Changed", person);
        Assert.DoesNotContain("private event", person);
        Assert.Contains("add", person);
        Assert.Contains("remove", person);
    }

    [SkippableFact]
    public async Task ExtractBaseClass_Event_Preview_WritesNothing_AndDescribesEvent()
    {
        const string source = """
            namespace TestApp;

            public class Employee
            {
                public event System.EventHandler Changed;

                public void Work() { }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ExtractBaseClassOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var result = await operation.ExecuteAsync(new ExtractBaseClassParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Employee",
            BaseClassName = "Person",
            Members = new[] { "Changed" },
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.NotEmpty(result.PendingChanges);
        Assert.Contains("Changed", result.PendingChanges[0].Description);
        Assert.Contains("event", result.PendingChanges[0].AfterSnippet, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Changed", result.PendingChanges[0].AfterSnippet);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.False(File.Exists(Path.GetFullPath(Path.Combine(workspace.DirectoryPath, "Person.cs"))));
    }

    [SkippableFact]
    public async Task ExtractBaseClass_MakeAbstract_MarksClassOnly_DoesNotInventAbstractEvent()
    {
        const string source = """
            namespace TestApp;

            public class Employee
            {
                public event System.EventHandler Changed;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ExtractBaseClassOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ExtractBaseClassParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Employee",
            BaseClassName = "Person",
            Members = new[] { "Changed" },
            MakeAbstract = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var person = GetClassSection(updated, "Person");

        Assert.Contains("abstract class Person", updated);
        Assert.Contains("public event System.EventHandler Changed", person);
        Assert.DoesNotContain("abstract event", updated);
    }

    [SkippableFact]
    public async Task ExtractBaseClass_Event_LeavesMethodsPropertiesAndFieldsOnDerived()
    {
        const string source = """
            namespace TestApp;

            public class Employee
            {
                public string Name { get; set; }
                public int Age;
                public event System.EventHandler Changed;
                public void Work() { }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ExtractBaseClassOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ExtractBaseClassParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Employee",
            BaseClassName = "Person",
            Members = new[] { "Changed" }
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var person = GetClassSection(updated, "Person");
        var employee = GetClassSection(updated, "Employee");

        Assert.Contains("public event System.EventHandler Changed", person);
        Assert.DoesNotContain("Name", person);
        Assert.DoesNotContain("Age", person);
        Assert.DoesNotContain("Work", person);
        Assert.Contains("public string Name { get; set; }", employee);
        Assert.Contains("public int Age", employee);
        Assert.Contains("public void Work()", employee);
        Assert.DoesNotContain("event System.EventHandler Changed", employee);
    }

    #endregion

    [Fact]
    public void AddExplicitCompileItemIfNeeded_SdkDefaults_Unchanged()
    {
        const string xml = """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net9.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """;
        var projectDir = Path.DirectorySeparatorChar == '/' ? "/tmp/proj" : @"C:\tmp\proj";
        var updated = ExtractBaseClassOperation.AddExplicitCompileItemIfNeeded(
            xml,
            projectDir,
            Path.Combine(projectDir, "Person.cs"));

        Assert.Equal(xml.ReplaceLineEndings(), updated.ReplaceLineEndings());
    }

    [Fact]
    public void AddExplicitCompileItemIfNeeded_ExplicitItems_AddsInclude()
    {
        const string xml = """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
              </PropertyGroup>
              <ItemGroup>
                <Compile Include="Employee.cs" />
              </ItemGroup>
            </Project>
            """;
        var projectDir = Path.DirectorySeparatorChar == '/' ? "/tmp/proj" : @"C:\tmp\proj";
        var updated = ExtractBaseClassOperation.AddExplicitCompileItemIfNeeded(
            xml,
            projectDir,
            Path.Combine(projectDir, "Person.cs"));

        Assert.Contains("Include=\"Employee.cs\"", updated);
        Assert.Contains("Include=\"Person.cs\"", updated);
    }

    [Fact]
    public void AddExplicitCompileItemIfNeeded_AlreadyIncluded_Unchanged()
    {
        const string xml = """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <EnableDefaultItems>false</EnableDefaultItems>
              </PropertyGroup>
              <ItemGroup>
                <Compile Include="Employee.cs" />
                <Compile Include="Person.cs" />
              </ItemGroup>
            </Project>
            """;
        var projectDir = Path.DirectorySeparatorChar == '/' ? "/tmp/proj" : @"C:\tmp\proj";
        var updated = ExtractBaseClassOperation.AddExplicitCompileItemIfNeeded(
            xml,
            projectDir,
            Path.Combine(projectDir, "Person.cs"));

        Assert.Equal(xml.ReplaceLineEndings(), updated.ReplaceLineEndings());
    }

    private const string NestedEmployeeSource = """
        namespace TestApp;

        public class Outer<T>
        {
            public class Employee
            {
                public T Value { get; set; }

                public void Work() { }
            }
        }
        """;

    private static string NormalizeNewlines(string text) =>
        text.Replace("\r\n", "\n");

    /// <summary>
    /// Base-list trivia from <c>WithBaseList</c> may omit spaces; compare a compacted form.
    /// </summary>
    private static void AssertInheritsFrom(string source, string typeName, string baseClassName)
    {
        var compact = new string(source.Where(c => !char.IsWhiteSpace(c)).ToArray());
        Assert.Contains($"class{typeName}:{baseClassName}", compact);
    }

    private static string GetClassSection(string source, string className)
    {
        var marker = $"class {className}";
        var start = source.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Class '{className}' not found in:\n{source}");
        var next = source.IndexOf("class ", start + marker.Length, StringComparison.Ordinal);
        return next < 0 ? source[start..] : source[start..next];
    }

    private sealed class TempWorkspace : IAsyncDisposable
    {
        public required string DirectoryPath { get; init; }
        public required string ProjectPath { get; init; }
        public required string SourcePath { get; init; }
        public required WorkspaceContext Context { get; init; }

        public static Task<TempWorkspace> CreateAsync(string source, string fileName = "Employee.cs") =>
            CreateAsync("""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net9.0</TargetFramework>
                    <Nullable>enable</Nullable>
                  </PropertyGroup>
                </Project>
                """, source, fileName);

        public static Task<TempWorkspace> CreateWithExplicitCompileItemsAsync(
            string source,
            string fileName = "Employee.cs") =>
            CreateAsync($"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net9.0</TargetFramework>
                    <Nullable>enable</Nullable>
                    <EnableDefaultItems>false</EnableDefaultItems>
                    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
                  </PropertyGroup>
                  <ItemGroup>
                    <Compile Include="{fileName}" />
                  </ItemGroup>
                </Project>
                """, source, fileName);

        public static async Task<TempWorkspace> CreateAsync(string projectXml, string source, string fileName)
        {
            Skip.IfNot(ModuleInitializer.MsBuildAvailable, ModuleInitializer.MsBuildError ?? "MSBuild not available");

            var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpExtractBaseClass_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);

            var projectPath = Path.Combine(directory, "TestApp.csproj");
            var sourcePath = Path.Combine(directory, fileName);

            await File.WriteAllTextAsync(projectPath, projectXml);
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
}

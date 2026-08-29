using RoslynMcp.Contracts.Models;
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

    #region Helpers

    private static string NormalizeNewlines(string text) => text.Replace("\r\n", "\n");

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

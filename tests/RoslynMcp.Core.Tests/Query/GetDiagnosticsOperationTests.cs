using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Query;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Workspace;
using Xunit;

namespace RoslynMcp.Core.Tests.Query;

/// <summary>
/// Operation-level tests for <see cref="GetDiagnosticsOperation"/> optional <c>maxResults</c>, <c>diagnosticIds</c>, <c>excludeDiagnosticIds</c>, <c>projectPath</c>, <c>namespaceFilter</c>, and <c>accessibilityFilter</c>
/// (peer of <see cref="FindReferencesOperation"/> / <see cref="SearchSymbolsOperation"/>).
/// </summary>
public class GetDiagnosticsOperationTests
{
    #region Input Validation

    [Fact]
    public void MaxResults_DefaultsToNull()
    {
        var @params = new GetDiagnosticsParams();
        Assert.Null(@params.MaxResults);
    }

    [SkippableFact]
    public async Task Validate_MaxResultsZero_ThrowsMissingRequiredParam()
    {
        await using var workspace = await TempWorkspace.CreateAsync("#warning One\nclass C {}\n");
        var operation = new GetDiagnosticsOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GetDiagnosticsParams { MaxResults = 0 }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Equal("maxResults must be >= 1.", ex.Message);
    }

    #endregion

    #region Execute truncation

    [SkippableFact]
    public async Task GetDiagnostics_OmittedMaxResults_ReturnsFullListNotTruncated()
    {
        await using var workspace = await TempWorkspace.CreateAsync("""
            #warning One
            #warning Two
            #warning Three
            class C {}
            """);

        var operation = new GetDiagnosticsOperation(workspace.Context);
        var result = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            SeverityFilter = "Warning"
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.True(result.Data.TotalCount >= 3, $"Expected >= 3 diagnostics, got {result.Data.TotalCount}");
        Assert.Equal(result.Data.TotalCount, result.Data.Diagnostics.Count);
        Assert.False(result.Data.Truncated);
    }

    [SkippableFact]
    public async Task GetDiagnostics_MaxResultsTruncates_SetsTruncatedTrue()
    {
        await using var workspace = await TempWorkspace.CreateAsync("""
            #warning One
            #warning Two
            #warning Three
            class C {}
            """);

        var operation = new GetDiagnosticsOperation(workspace.Context);
        var full = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            SeverityFilter = "Warning"
        });
        Assert.True(full.Success);
        Assert.NotNull(full.Data);
        Assert.True(full.Data.TotalCount >= 3);

        var capped = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            SeverityFilter = "Warning",
            MaxResults = 2
        });

        Assert.True(capped.Success);
        Assert.NotNull(capped.Data);
        Assert.Equal(full.Data.TotalCount, capped.Data.TotalCount);
        Assert.Equal(2, capped.Data.Diagnostics.Count);
        Assert.True(capped.Data.Truncated);
        // Deterministic: first N of today's enumeration order
        Assert.Equal(full.Data.Diagnostics[0].Id, capped.Data.Diagnostics[0].Id);
        Assert.Equal(full.Data.Diagnostics[0].Message, capped.Data.Diagnostics[0].Message);
        Assert.Equal(full.Data.Diagnostics[1].Id, capped.Data.Diagnostics[1].Id);
        Assert.Equal(full.Data.Diagnostics[1].Message, capped.Data.Diagnostics[1].Message);
    }

    [SkippableFact]
    public async Task GetDiagnostics_MaxResultsAtOrAboveTotal_NotTruncated()
    {
        await using var workspace = await TempWorkspace.CreateAsync("""
            #warning One
            #warning Two
            class C {}
            """);

        var operation = new GetDiagnosticsOperation(workspace.Context);
        var full = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            SeverityFilter = "Warning"
        });
        Assert.True(full.Success);
        Assert.NotNull(full.Data);
        Assert.True(full.Data.TotalCount >= 2);

        var capped = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            SeverityFilter = "Warning",
            MaxResults = full.Data.TotalCount
        });

        Assert.True(capped.Success);
        Assert.NotNull(capped.Data);
        Assert.Equal(full.Data.TotalCount, capped.Data.TotalCount);
        Assert.Equal(full.Data.TotalCount, capped.Data.Diagnostics.Count);
        Assert.False(capped.Data.Truncated);
    }

    #endregion

    #region diagnosticIds filter

    [Fact]
    public void DiagnosticIds_DefaultsToNull()
    {
        var @params = new GetDiagnosticsParams();
        Assert.Null(@params.DiagnosticIds);
    }

    [SkippableFact]
    public async Task Validate_DiagnosticIdsWhitespaceEntry_ThrowsMissingRequiredParam()
    {
        await using var workspace = await TempWorkspace.CreateAsync("#warning One\nclass C {}\n");
        var operation = new GetDiagnosticsOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GetDiagnosticsParams { DiagnosticIds = new[] { "CS1030", " " } }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Equal("diagnosticIds entries must be non-empty.", ex.Message);
    }

    [SkippableFact]
    public async Task GetDiagnostics_EmptyDiagnosticIds_ReturnsSameAsOmitted()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MixedSource);
        var operation = new GetDiagnosticsOperation(workspace.Context);

        var omitted = await operation.ExecuteAsync(new GetDiagnosticsParams { SeverityFilter = "Warning" });
        var empty = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            SeverityFilter = "Warning",
            DiagnosticIds = Array.Empty<string>()
        });

        Assert.True(omitted.Success);
        Assert.True(empty.Success);
        Assert.Equal(omitted.Data!.TotalCount, empty.Data!.TotalCount);
        Assert.Equal(
            omitted.Data.Diagnostics.Select(d => d.Id + d.Message),
            empty.Data.Diagnostics.Select(d => d.Id + d.Message));
    }

    [SkippableFact]
    public async Task GetDiagnostics_DiagnosticIds_KeepsOnlyMatchingIds()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MixedSource);
        var operation = new GetDiagnosticsOperation(workspace.Context);

        var full = await operation.ExecuteAsync(new GetDiagnosticsParams { SeverityFilter = "Warning" });
        Assert.True(full.Success);
        Assert.Contains(full.Data!.Diagnostics, d => d.Id == "CS1030");
        Assert.Contains(full.Data.Diagnostics, d => d.Id == "CS0168");

        var filtered = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            SeverityFilter = "Warning",
            DiagnosticIds = new[] { "CS0168" }
        });

        Assert.True(filtered.Success);
        Assert.NotNull(filtered.Data);
        Assert.NotEmpty(filtered.Data.Diagnostics);
        Assert.All(filtered.Data.Diagnostics, d => Assert.Equal("CS0168", d.Id));
        Assert.Equal(full.Data.Diagnostics.Count(d => d.Id == "CS0168"), filtered.Data.TotalCount);
        Assert.False(filtered.Data.Truncated);
    }

    [SkippableFact]
    public async Task GetDiagnostics_DiagnosticIds_MultipleIdsCaseInsensitive()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MixedSource);
        var operation = new GetDiagnosticsOperation(workspace.Context);

        var full = await operation.ExecuteAsync(new GetDiagnosticsParams { SeverityFilter = "Warning" });
        Assert.True(full.Success);
        var expected = full.Data!.Diagnostics.Count(d => d.Id is "CS1030" or "CS0168");

        var filtered = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            SeverityFilter = "Warning",
            DiagnosticIds = new[] { "cs1030", " Cs0168 " }
        });

        Assert.True(filtered.Success);
        Assert.Equal(expected, filtered.Data!.TotalCount);
        Assert.Equal(expected, filtered.Data.Diagnostics.Count);
        Assert.All(filtered.Data.Diagnostics, d => Assert.True(d.Id is "CS1030" or "CS0168", d.Id));
    }

    [SkippableFact]
    public async Task GetDiagnostics_DiagnosticIds_NoMatch_ReturnsEmpty()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MixedSource);
        var operation = new GetDiagnosticsOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            SeverityFilter = "Warning",
            DiagnosticIds = new[] { "CS9999" }
        });

        Assert.True(result.Success);
        Assert.Empty(result.Data!.Diagnostics);
        Assert.Equal(0, result.Data.TotalCount);
        Assert.False(result.Data.Truncated);
    }

    [SkippableFact]
    public async Task GetDiagnostics_DiagnosticIdsWithMaxResults_CapsAfterFilter()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MixedSource);
        var operation = new GetDiagnosticsOperation(workspace.Context);

        var filtered = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            SeverityFilter = "Warning",
            DiagnosticIds = new[] { "CS1030" }
        });
        Assert.True(filtered.Success);
        Assert.True(filtered.Data!.TotalCount >= 3, $"Expected >= 3 CS1030, got {filtered.Data.TotalCount}");

        var capped = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            SeverityFilter = "Warning",
            DiagnosticIds = new[] { "CS1030" },
            MaxResults = 2
        });

        Assert.True(capped.Success);
        Assert.Equal(filtered.Data.TotalCount, capped.Data!.TotalCount);
        Assert.Equal(2, capped.Data.Diagnostics.Count);
        Assert.True(capped.Data.Truncated);
        Assert.All(capped.Data.Diagnostics, d => Assert.Equal("CS1030", d.Id));
    }

    private const string MixedSource = """
        #warning One
        #warning Two
        #warning Three
        class C
        {
            void M()
            {
                int unused;
            }
        }
        """;

    #endregion

    #region excludeDiagnosticIds filter

    [Fact]
    public void ExcludeDiagnosticIds_DefaultsToNull()
    {
        var @params = new GetDiagnosticsParams();
        Assert.Null(@params.ExcludeDiagnosticIds);
    }

    [SkippableFact]
    public async Task Validate_ExcludeDiagnosticIdsWhitespaceEntry_ThrowsMissingRequiredParam()
    {
        await using var workspace = await TempWorkspace.CreateAsync("#warning One\nclass C {}\n");
        var operation = new GetDiagnosticsOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GetDiagnosticsParams { ExcludeDiagnosticIds = new[] { "CS1030", " " } }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Equal("excludeDiagnosticIds entries must be non-empty.", ex.Message);
    }

    [SkippableFact]
    public async Task Validate_ExcludeDiagnosticIdsEmptyStringEntry_ThrowsMissingRequiredParam()
    {
        await using var workspace = await TempWorkspace.CreateAsync("#warning One\nclass C {}\n");
        var operation = new GetDiagnosticsOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GetDiagnosticsParams { ExcludeDiagnosticIds = new[] { string.Empty } }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Equal("excludeDiagnosticIds entries must be non-empty.", ex.Message);
    }

    [SkippableFact]
    public async Task GetDiagnostics_EmptyExcludeDiagnosticIds_ReturnsSameAsOmitted()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MixedSource);
        var operation = new GetDiagnosticsOperation(workspace.Context);

        var omitted = await operation.ExecuteAsync(new GetDiagnosticsParams { SeverityFilter = "Warning" });
        var empty = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            SeverityFilter = "Warning",
            ExcludeDiagnosticIds = Array.Empty<string>()
        });

        Assert.True(omitted.Success);
        Assert.True(empty.Success);
        Assert.Equal(omitted.Data!.TotalCount, empty.Data!.TotalCount);
        Assert.Equal(
            omitted.Data.Diagnostics.Select(d => d.Id + d.Message),
            empty.Data.Diagnostics.Select(d => d.Id + d.Message));
    }

    [SkippableFact]
    public async Task GetDiagnostics_ExcludeDiagnosticIds_DropsMatchingId()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MixedSource);
        var operation = new GetDiagnosticsOperation(workspace.Context);

        var full = await operation.ExecuteAsync(new GetDiagnosticsParams { SeverityFilter = "Warning" });
        Assert.True(full.Success);
        Assert.Contains(full.Data!.Diagnostics, d => d.Id == "CS1030");
        Assert.Contains(full.Data.Diagnostics, d => d.Id == "CS0168");

        var filtered = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            SeverityFilter = "Warning",
            ExcludeDiagnosticIds = new[] { "CS0168" }
        });

        Assert.True(filtered.Success);
        Assert.NotNull(filtered.Data);
        Assert.DoesNotContain(filtered.Data.Diagnostics, d => d.Id == "CS0168");
        Assert.Contains(filtered.Data.Diagnostics, d => d.Id == "CS1030");
        Assert.Equal(full.Data.Diagnostics.Count(d => d.Id != "CS0168"), filtered.Data.TotalCount);
        Assert.Equal(filtered.Data.TotalCount, filtered.Data.Diagnostics.Count);
        Assert.False(filtered.Data.Truncated);
    }

    [SkippableFact]
    public async Task GetDiagnostics_ExcludeDiagnosticIds_MultipleIdsCaseInsensitiveWithPadding()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MixedSource);
        var operation = new GetDiagnosticsOperation(workspace.Context);

        var full = await operation.ExecuteAsync(new GetDiagnosticsParams { SeverityFilter = "Warning" });
        Assert.True(full.Success);
        var expected = full.Data!.Diagnostics.Count(d => d.Id is not ("CS1030" or "CS0168"));

        var filtered = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            SeverityFilter = "Warning",
            ExcludeDiagnosticIds = new[] { "cs1030", " Cs0168 " }
        });

        Assert.True(filtered.Success);
        Assert.Equal(expected, filtered.Data!.TotalCount);
        Assert.Equal(expected, filtered.Data.Diagnostics.Count);
        Assert.All(filtered.Data.Diagnostics, d => Assert.False(d.Id is "CS1030" or "CS0168", d.Id));
    }

    [SkippableFact]
    public async Task GetDiagnostics_ExcludeDiagnosticIds_AllIds_ReturnsEmpty()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MixedSource);
        var operation = new GetDiagnosticsOperation(workspace.Context);

        var full = await operation.ExecuteAsync(new GetDiagnosticsParams { SeverityFilter = "Warning" });
        Assert.True(full.Success);
        var allIds = full.Data!.Diagnostics.Select(d => d.Id).Distinct().ToArray();
        Assert.NotEmpty(allIds);

        var result = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            SeverityFilter = "Warning",
            ExcludeDiagnosticIds = allIds.Select(id => id.ToLowerInvariant()).ToArray()
        });

        Assert.True(result.Success);
        Assert.Empty(result.Data!.Diagnostics);
        Assert.Equal(0, result.Data.TotalCount);
        Assert.False(result.Data.Truncated);
    }

    [SkippableFact]
    public async Task GetDiagnostics_ExcludeDiagnosticIds_NoMatch_ReturnsSameAsOmitted()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MixedSource);
        var operation = new GetDiagnosticsOperation(workspace.Context);

        var omitted = await operation.ExecuteAsync(new GetDiagnosticsParams { SeverityFilter = "Warning" });
        var result = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            SeverityFilter = "Warning",
            ExcludeDiagnosticIds = new[] { "CS9999" }
        });

        Assert.True(omitted.Success);
        Assert.True(result.Success);
        Assert.Equal(omitted.Data!.TotalCount, result.Data!.TotalCount);
        Assert.Equal(
            omitted.Data.Diagnostics.Select(d => d.Id + d.Message),
            result.Data.Diagnostics.Select(d => d.Id + d.Message));
    }

    [SkippableFact]
    public async Task GetDiagnostics_ExcludeDiagnosticIdsWithDiagnosticIds_ExclusionWins()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MixedSource);
        var operation = new GetDiagnosticsOperation(workspace.Context);

        var included = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            SeverityFilter = "Warning",
            DiagnosticIds = new[] { "CS1030", "CS0168" }
        });
        Assert.True(included.Success);
        var expected = included.Data!.Diagnostics.Count(d => d.Id == "CS1030");
        Assert.True(expected >= 3, $"Expected >= 3 CS1030, got {expected}");

        var result = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            SeverityFilter = "Warning",
            DiagnosticIds = new[] { "CS1030", "CS0168" },
            ExcludeDiagnosticIds = new[] { "cs0168" }
        });

        Assert.True(result.Success);
        Assert.Equal(expected, result.Data!.TotalCount);
        Assert.All(result.Data.Diagnostics, d => Assert.Equal("CS1030", d.Id));

        var fullOverlap = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            SeverityFilter = "Warning",
            DiagnosticIds = new[] { "CS1030" },
            ExcludeDiagnosticIds = new[] { "CS1030" }
        });

        Assert.True(fullOverlap.Success);
        Assert.Empty(fullOverlap.Data!.Diagnostics);
        Assert.Equal(0, fullOverlap.Data.TotalCount);
        Assert.False(fullOverlap.Data.Truncated);
    }

    [SkippableFact]
    public async Task GetDiagnostics_ExcludeDiagnosticIdsWithMaxResults_CapsAfterExclusion()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MixedSource);
        var operation = new GetDiagnosticsOperation(workspace.Context);

        var filtered = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            SeverityFilter = "Warning",
            ExcludeDiagnosticIds = new[] { "CS0168" }
        });
        Assert.True(filtered.Success);
        Assert.True(filtered.Data!.TotalCount >= 3, $"Expected >= 3 after exclusion, got {filtered.Data.TotalCount}");

        var capped = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            SeverityFilter = "Warning",
            ExcludeDiagnosticIds = new[] { "CS0168" },
            MaxResults = 2
        });

        Assert.True(capped.Success);
        Assert.Equal(filtered.Data.TotalCount, capped.Data!.TotalCount);
        Assert.Equal(2, capped.Data.Diagnostics.Count);
        Assert.True(capped.Data.Truncated);
        Assert.DoesNotContain(capped.Data.Diagnostics, d => d.Id == "CS0168");
        Assert.Equal(
            filtered.Data.Diagnostics.Take(2).Select(d => d.Id + d.Message),
            capped.Data.Diagnostics.Select(d => d.Id + d.Message));
    }

    #endregion

    #region projectPath filter

    [Fact]
    public void ProjectPath_DefaultsToNull()
    {
        var @params = new GetDiagnosticsParams();
        Assert.Null(@params.ProjectPath);
    }

    [SkippableFact]
    public async Task Validate_ProjectPathRelative_ThrowsInvalidSourcePath()
    {
        await using var workspace = await TempWorkspace.CreateAsync("#warning One\nclass C {}\n");
        var operation = new GetDiagnosticsOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GetDiagnosticsParams { ProjectPath = Path.Combine("Lib", "Lib.csproj") }));

        Assert.Equal(ErrorCodes.InvalidSourcePath, ex.ErrorCode);
        Assert.Equal("projectPath must be an absolute path.", ex.Message);
    }

    [SkippableTheory]
    [InlineData("C:Lib.csproj")]
    [InlineData("C:Lib\\Lib.csproj")]
    [InlineData("\\Lib.csproj")]
    [InlineData("\\Lib\\Lib.csproj")]
    public async Task Validate_ProjectPathDriveOrRootRelative_ThrowsInvalidSourcePath(string projectPath)
    {
        // Windows drive-relative / root-relative forms pass Path.IsPathRooted but are not fully
        // qualified; on non-Windows they are plain relative paths. Both must be InvalidSourcePath,
        // never resolved against process state into a project match or SourceNotInWorkspace.
        await using var workspace = await TempWorkspace.CreateAsync("#warning One\nclass C {}\n");
        var operation = new GetDiagnosticsOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GetDiagnosticsParams { ProjectPath = projectPath }));

        Assert.Equal(ErrorCodes.InvalidSourcePath, ex.ErrorCode);
        Assert.Equal("projectPath must be an absolute path.", ex.Message);
    }

    [SkippableFact]
    public async Task Validate_ProjectPathNotCsproj_ThrowsInvalidSourcePath()
    {
        await using var workspace = await TempWorkspace.CreateAsync("#warning One\nclass C {}\n");
        var operation = new GetDiagnosticsOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GetDiagnosticsParams { ProjectPath = workspace.SourcePath }));

        Assert.Equal(ErrorCodes.InvalidSourcePath, ex.ErrorCode);
        Assert.Equal("projectPath must be a .csproj file.", ex.Message);
    }

    [SkippableFact]
    public async Task GetDiagnostics_ProjectPathNotInWorkspace_ThrowsSourceNotInWorkspace()
    {
        await using var workspace = await TempWorkspace.CreateAsync("#warning One\nclass C {}\n");
        var operation = new GetDiagnosticsOperation(workspace.Context);
        var missing = Path.Combine(workspace.DirectoryPath, "Other", "Other.csproj");

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GetDiagnosticsParams { ProjectPath = missing }));

        Assert.Equal(ErrorCodes.SourceNotInWorkspace, ex.ErrorCode);
        Assert.Equal($"Project not found in workspace: {missing}", ex.Message);
    }

    [SkippableFact]
    public async Task GetDiagnostics_ProjectPathBlank_MatchesOmitted()
    {
        await using var workspace = await TwoProjectWorkspace.CreateAsync();
        var operation = new GetDiagnosticsOperation(workspace.Context);

        var omitted = await operation.ExecuteAsync(new GetDiagnosticsParams { DiagnosticIds = new[] { "CS1030" } });
        var blank = await operation.ExecuteAsync(new GetDiagnosticsParams { DiagnosticIds = new[] { "CS1030" }, ProjectPath = "  " });

        Assert.True(omitted.Success);
        Assert.True(blank.Success);
        Assert.Equal(
            omitted.Data!.Diagnostics.Select(d => d.File + d.Message),
            blank.Data!.Diagnostics.Select(d => d.File + d.Message));
    }

    [SkippableFact]
    public async Task GetDiagnostics_ProjectPathOmitted_ReturnsEveryProject()
    {
        await using var workspace = await TwoProjectWorkspace.CreateAsync();
        var operation = new GetDiagnosticsOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GetDiagnosticsParams { DiagnosticIds = new[] { "CS1030" } });

        Assert.True(result.Success);
        Assert.Contains(result.Data!.Diagnostics, d => d.Message.Contains("LibWarning", StringComparison.Ordinal));
        Assert.Contains(result.Data.Diagnostics, d => d.Message.Contains("AppWarning", StringComparison.Ordinal));
    }

    [SkippableFact]
    public async Task GetDiagnostics_ProjectPath_RestrictsToThatProject()
    {
        await using var workspace = await TwoProjectWorkspace.CreateAsync();
        var operation = new GetDiagnosticsOperation(workspace.Context);

        var lib = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            DiagnosticIds = new[] { "CS1030" },
            ProjectPath = workspace.LibProjectPath
        });

        Assert.True(lib.Success);
        Assert.NotEmpty(lib.Data!.Diagnostics);
        Assert.Equal(lib.Data.Diagnostics.Count, lib.Data.TotalCount);
        Assert.All(lib.Data.Diagnostics, d => Assert.Equal(workspace.LibSourcePath, d.File));
        Assert.All(lib.Data.Diagnostics, d => Assert.Contains("LibWarning", d.Message, StringComparison.Ordinal));

        var app = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            DiagnosticIds = new[] { "CS1030" },
            ProjectPath = workspace.AppProjectPath
        });

        Assert.True(app.Success);
        Assert.NotEmpty(app.Data!.Diagnostics);
        Assert.All(app.Data.Diagnostics, d => Assert.Equal(workspace.AppSourcePath, d.File));
        Assert.All(app.Data.Diagnostics, d => Assert.Contains("AppWarning", d.Message, StringComparison.Ordinal));
    }

    [SkippableFact]
    public async Task GetDiagnostics_ProjectPathDifferentCasing_MatchesOnlyOnCaseInsensitiveVolume()
    {
        await using var workspace = await TwoProjectWorkspace.CreateAsync();
        var operation = new GetDiagnosticsOperation(workspace.Context);
        var wrongCased = Path.Combine(Path.GetDirectoryName(workspace.LibProjectPath)!, "LIB.CSPROJ");
        var caseInsensitiveVolume = File.Exists(wrongCased);

        var @params = new GetDiagnosticsParams
        {
            DiagnosticIds = new[] { "CS1030" },
            ProjectPath = wrongCased
        };

        if (caseInsensitiveVolume)
        {
            // Windows / default macOS: the wrong-cased alias is the same physical project.
            var result = await operation.ExecuteAsync(@params);
            Assert.True(result.Success);
            Assert.NotEmpty(result.Data!.Diagnostics);
            Assert.All(result.Data.Diagnostics, d => Assert.Equal(workspace.LibSourcePath, d.File));
        }
        else
        {
            // Case-sensitive volume (Linux): LIB.CSPROJ is a different, nonexistent project.
            var ex = await Assert.ThrowsAsync<RefactoringException>(() => operation.ExecuteAsync(@params));
            Assert.Equal(ErrorCodes.SourceNotInWorkspace, ex.ErrorCode);
        }
    }

    [SkippableFact]
    public async Task GetDiagnostics_ProjectPathWithMaxResults_CapsAfterProjectFilter()
    {
        await using var workspace = await TwoProjectWorkspace.CreateAsync(
            libSource: "#warning LibWarning1\n#warning LibWarning2\n#warning LibWarning3\nnamespace Lib { public class Animal {} }\n");
        var operation = new GetDiagnosticsOperation(workspace.Context);

        var filtered = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            DiagnosticIds = new[] { "CS1030" },
            ProjectPath = workspace.LibProjectPath
        });
        Assert.True(filtered.Success);
        Assert.Equal(3, filtered.Data!.TotalCount);

        var capped = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            DiagnosticIds = new[] { "CS1030" },
            ProjectPath = workspace.LibProjectPath,
            MaxResults = 2
        });

        Assert.True(capped.Success);
        Assert.Equal(3, capped.Data!.TotalCount);
        Assert.Equal(2, capped.Data.Diagnostics.Count);
        Assert.True(capped.Data.Truncated);
        Assert.All(capped.Data.Diagnostics, d => Assert.Equal(workspace.LibSourcePath, d.File));
        Assert.Equal(
            filtered.Data.Diagnostics.Take(2).Select(d => d.Message),
            capped.Data.Diagnostics.Select(d => d.Message));
    }

    [SkippableFact]
    public async Task GetDiagnostics_ProjectPathWithSourceFileFromOtherProject_ReturnsEmpty()
    {
        await using var workspace = await TwoProjectWorkspace.CreateAsync();
        var operation = new GetDiagnosticsOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            ProjectPath = workspace.LibProjectPath,
            SourceFile = workspace.AppSourcePath,
            SeverityFilter = "All"
        });

        Assert.True(result.Success);
        Assert.Empty(result.Data!.Diagnostics);
        Assert.Equal(0, result.Data.TotalCount);
        Assert.False(result.Data.Truncated);

        var sameProject = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            DiagnosticIds = new[] { "CS1030" },
            ProjectPath = workspace.AppProjectPath,
            SourceFile = workspace.AppSourcePath
        });

        Assert.True(sameProject.Success);
        Assert.NotEmpty(sameProject.Data!.Diagnostics);
        Assert.All(sameProject.Data.Diagnostics, d => Assert.Equal(workspace.AppSourcePath, d.File));
    }

    [SkippableFact]
    public async Task GetDiagnostics_ProjectPathSingleProject_MatchesOmitted()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MixedSource);
        var operation = new GetDiagnosticsOperation(workspace.Context);

        var omitted = await operation.ExecuteAsync(new GetDiagnosticsParams { SeverityFilter = "Warning" });
        var scoped = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            SeverityFilter = "Warning",
            ProjectPath = workspace.ProjectPath
        });

        Assert.True(omitted.Success);
        Assert.True(scoped.Success);
        Assert.True(scoped.Data!.TotalCount >= 3);
        Assert.Equal(omitted.Data!.TotalCount, scoped.Data.TotalCount);
        Assert.Equal(
            omitted.Data.Diagnostics.Select(d => d.Id + d.Message),
            scoped.Data.Diagnostics.Select(d => d.Id + d.Message));
    }

    #endregion

    #region namespaceFilter

    // One CS0168 ("declared but never used") per namespace; the variable name identifies the site.
    private const string NamespacedSource = """
        namespace App.Services
        {
            public class S { public void M() { int inServices; } }
        }

        namespace App.Services.Orders
        {
            public class O { public void M() { int inOrders; } }
        }

        namespace App.ServicesExtra
        {
            public class E { public void M() { int inExtra; } }
        }

        namespace Other
        {
            public class X { public void M() { int inOther; } }
        }

        public class G { public void M() { int inGlobal; } }
        """;

    private static readonly string[] Cs0168 = { "CS0168" };

    private static IEnumerable<string> VariableNames(GetDiagnosticsResult result) =>
        result.Diagnostics.Select(d =>
        {
            var start = d.Message.IndexOf('\'');
            var end = d.Message.IndexOf('\'', start + 1);
            return d.Message[(start + 1)..end];
        });

    [Fact]
    public void NamespaceFilter_DefaultsToNull()
    {
        var @params = new GetDiagnosticsParams();
        Assert.Null(@params.NamespaceFilter);
    }

    [SkippableTheory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("global::")]
    [InlineData("  global::  ")]
    public async Task GetDiagnostics_NamespaceFilterBlankOrBareGlobal_MatchesOmitted(string namespaceFilter)
    {
        await using var workspace = await TempWorkspace.CreateAsync(NamespacedSource);
        var operation = new GetDiagnosticsOperation(workspace.Context);

        var omitted = await operation.ExecuteAsync(new GetDiagnosticsParams { DiagnosticIds = Cs0168 });
        var filtered = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            DiagnosticIds = Cs0168,
            NamespaceFilter = namespaceFilter
        });

        Assert.True(omitted.Success);
        Assert.True(filtered.Success);
        Assert.Equal(5, omitted.Data!.TotalCount);
        Assert.Equal(omitted.Data.TotalCount, filtered.Data!.TotalCount);
        // Method-body diagnostics are bound concurrently, so compare as sets.
        Assert.Equal(
            omitted.Data.Diagnostics.Select(d => d.Id + d.Message + d.Line).OrderBy(x => x, StringComparer.Ordinal),
            filtered.Data.Diagnostics.Select(d => d.Id + d.Message + d.Line).OrderBy(x => x, StringComparer.Ordinal));
    }

    [SkippableFact]
    public async Task GetDiagnostics_NamespaceFilter_KeepsEqualAndNestedSites()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NamespacedSource);
        var operation = new GetDiagnosticsOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            DiagnosticIds = Cs0168,
            NamespaceFilter = "App.Services"
        });

        Assert.True(result.Success);
        Assert.Equal(new[] { "inOrders", "inServices" }, VariableNames(result.Data!).OrderBy(n => n, StringComparer.Ordinal));
        Assert.Equal(2, result.Data!.TotalCount);
        Assert.False(result.Data.Truncated);
        Assert.All(result.Data.Diagnostics, d => Assert.Equal(workspace.SourcePath, d.File));
    }

    [SkippableFact]
    public async Task GetDiagnostics_NamespaceFilterNested_ExcludesParentNamespace()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NamespacedSource);
        var operation = new GetDiagnosticsOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            DiagnosticIds = Cs0168,
            NamespaceFilter = "App.Services.Orders"
        });

        Assert.True(result.Success);
        Assert.Equal(new[] { "inOrders" }, VariableNames(result.Data!));
        Assert.Equal(1, result.Data!.TotalCount);
    }

    [SkippableFact]
    public async Task GetDiagnostics_NamespaceFilterOtherNamespace_DropsPrefixLookalikeAndGlobal()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NamespacedSource);
        var operation = new GetDiagnosticsOperation(workspace.Context);

        var other = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            DiagnosticIds = Cs0168,
            NamespaceFilter = "Other"
        });
        Assert.True(other.Success);
        Assert.Equal(new[] { "inOther" }, VariableNames(other.Data!));

        // App matches App.Services, App.Services.Orders, and App.ServicesExtra but never the global G.
        var app = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            DiagnosticIds = Cs0168,
            NamespaceFilter = "App"
        });
        Assert.True(app.Success);
        Assert.Equal(
            new[] { "inExtra", "inOrders", "inServices" },
            VariableNames(app.Data!).OrderBy(n => n, StringComparer.Ordinal));
        Assert.DoesNotContain("inGlobal", VariableNames(app.Data!));
    }

    [SkippableFact]
    public async Task GetDiagnostics_NamespaceFilterTrimmedWithGlobalPrefix_MatchesPlainFilter()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NamespacedSource);
        var operation = new GetDiagnosticsOperation(workspace.Context);

        var plain = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            DiagnosticIds = Cs0168,
            NamespaceFilter = "App.Services"
        });
        var prefixed = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            DiagnosticIds = Cs0168,
            NamespaceFilter = "  global::App.Services  "
        });

        Assert.True(plain.Success);
        Assert.True(prefixed.Success);
        Assert.Equal(2, prefixed.Data!.TotalCount);
        Assert.Equal(
            plain.Data!.Diagnostics.Select(d => d.Message + d.Line).OrderBy(x => x, StringComparer.Ordinal),
            prefixed.Data.Diagnostics.Select(d => d.Message + d.Line).OrderBy(x => x, StringComparer.Ordinal));
    }

    [SkippableFact]
    public async Task GetDiagnostics_NamespaceFilterIsOrdinal_DifferentCaseReturnsEmptySuccess()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NamespacedSource);
        var operation = new GetDiagnosticsOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            DiagnosticIds = Cs0168,
            NamespaceFilter = "app.services"
        });

        Assert.True(result.Success);
        Assert.Empty(result.Data!.Diagnostics);
        Assert.Equal(0, result.Data.TotalCount);
        Assert.False(result.Data.Truncated);
    }

    [SkippableFact]
    public async Task GetDiagnostics_NamespaceFilterNoMatch_ReturnsEmptySuccess()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NamespacedSource);
        var operation = new GetDiagnosticsOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            SeverityFilter = "All",
            NamespaceFilter = "Nowhere.At.All",
            MaxResults = 1
        });

        Assert.True(result.Success);
        Assert.Empty(result.Data!.Diagnostics);
        Assert.Equal(0, result.Data.TotalCount);
        Assert.False(result.Data.Truncated);
    }

    [SkippableFact]
    public async Task GetDiagnostics_NamespaceFilter_FileScopedNamespace()
    {
        await using var workspace = await TempWorkspace.CreateAsync("""
            namespace App.Services.Orders;

            public class O { public void M() { int inFileScoped; } }
            """);
        var operation = new GetDiagnosticsOperation(workspace.Context);

        var kept = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            DiagnosticIds = Cs0168,
            NamespaceFilter = "App.Services"
        });
        Assert.True(kept.Success);
        Assert.Equal(new[] { "inFileScoped" }, VariableNames(kept.Data!));

        var dropped = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            DiagnosticIds = Cs0168,
            NamespaceFilter = "App.ServicesExtra"
        });
        Assert.True(dropped.Success);
        Assert.Empty(dropped.Data!.Diagnostics);
        Assert.Equal(0, dropped.Data.TotalCount);
    }

    [SkippableFact]
    public async Task GetDiagnostics_NamespaceFilter_NamespaceLevelSiteUsesThatNamespace()
    {
        // A using directive inside a namespace declaration encloses to the namespace itself
        // (same rule as find_references); the one outside any namespace is a global-namespace site.
        await using var workspace = await TempWorkspace.CreateAsync("""
            using System.Text;

            namespace App.Services
            {
                using System.Collections.Generic;

                public class S { }
            }
            """);
        var operation = new GetDiagnosticsOperation(workspace.Context);

        // sourceFile scopes away the SDK-generated AssemblyInfo usings (also CS8019).
        var omitted = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            SeverityFilter = "All",
            DiagnosticIds = new[] { "CS8019" },
            SourceFile = workspace.SourcePath
        });
        Assert.True(omitted.Success);
        Assert.Equal(2, omitted.Data!.TotalCount);

        var filtered = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            SeverityFilter = "All",
            DiagnosticIds = new[] { "CS8019" },
            NamespaceFilter = "App.Services"
        });

        Assert.True(filtered.Success);
        var only = Assert.Single(filtered.Data!.Diagnostics);
        Assert.Equal(5, only.Line);
        Assert.Equal(1, filtered.Data.TotalCount);
    }

    [SkippableFact]
    public async Task GetDiagnostics_NamespaceFilter_DropsDiagnosticsWithoutSourceLocation()
    {
        // An Exe with no Main reports CS5001 with Location.None; it has no site, so it never
        // matches a named filter (but is still returned when the filter is omitted).
        await using var workspace = await TempWorkspace.CreateAsync(
            "namespace App { public class C { public void M() { int inApp; } } }\n",
            outputType: "Exe");
        var operation = new GetDiagnosticsOperation(workspace.Context);

        var omitted = await operation.ExecuteAsync(new GetDiagnosticsParams { SeverityFilter = "Warning" });
        Assert.True(omitted.Success);
        Assert.Contains(omitted.Data!.Diagnostics, d => d.Id == "CS5001" && d.File == null);

        var filtered = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            SeverityFilter = "Warning",
            NamespaceFilter = "App"
        });

        Assert.True(filtered.Success);
        Assert.NotEmpty(filtered.Data!.Diagnostics);
        Assert.All(filtered.Data.Diagnostics, d => Assert.NotNull(d.File));
        Assert.DoesNotContain(filtered.Data.Diagnostics, d => d.Id == "CS5001");
        Assert.Contains(filtered.Data.Diagnostics, d => d.Id == "CS0168");
    }

    [SkippableFact]
    public async Task GetDiagnostics_NamespaceFilterWithMaxResults_CapsAfterNamespaceFilter()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NamespacedSource);
        var operation = new GetDiagnosticsOperation(workspace.Context);

        var filtered = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            DiagnosticIds = Cs0168,
            NamespaceFilter = "App"
        });
        Assert.True(filtered.Success);
        Assert.Equal(3, filtered.Data!.TotalCount);

        var capped = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            DiagnosticIds = Cs0168,
            NamespaceFilter = "App",
            MaxResults = 2
        });

        Assert.True(capped.Success);
        Assert.Equal(3, capped.Data!.TotalCount);
        Assert.Equal(2, capped.Data.Diagnostics.Count);
        Assert.True(capped.Data.Truncated);
        // Every returned diagnostic comes from the namespace-filtered set (method-body diagnostics are
        // bound concurrently, so which two survive the cap is not asserted).
        Assert.All(VariableNames(capped.Data), n => Assert.Contains(n, VariableNames(filtered.Data)));
        Assert.Equal(2, VariableNames(capped.Data).Distinct(StringComparer.Ordinal).Count());
    }

    [SkippableFact]
    public async Task GetDiagnostics_NamespaceFilterWithSourceFile_MustSatisfyBoth()
    {
        await using var workspace = await TempWorkspace.CreateAsync(
            NamespacedSource,
            extraFiles: new Dictionary<string, string>
            {
                ["Bar.cs"] = "namespace App.Services { public class B { public void M() { int inBar; } } }\n"
            });
        var operation = new GetDiagnosticsOperation(workspace.Context);

        var both = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            DiagnosticIds = Cs0168,
            NamespaceFilter = "App.Services"
        });
        Assert.True(both.Success);
        Assert.Equal(
            new[] { "inBar", "inOrders", "inServices" },
            VariableNames(both.Data!).OrderBy(n => n, StringComparer.Ordinal));

        var barPath = Path.Combine(workspace.DirectoryPath, "Bar.cs");
        var scoped = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            DiagnosticIds = Cs0168,
            NamespaceFilter = "App.Services",
            SourceFile = barPath
        });
        Assert.True(scoped.Success);
        Assert.Equal(new[] { "inBar" }, VariableNames(scoped.Data!));
        Assert.Equal(1, scoped.Data!.TotalCount);

        var none = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            DiagnosticIds = Cs0168,
            NamespaceFilter = "Other",
            SourceFile = barPath
        });
        Assert.True(none.Success);
        Assert.Empty(none.Data!.Diagnostics);
        Assert.Equal(0, none.Data.TotalCount);
        Assert.False(none.Data.Truncated);
    }

    [SkippableFact]
    public async Task GetDiagnostics_NamespaceFilterWithProjectPath_MustSatisfyBoth()
    {
        await using var workspace = await TwoProjectWorkspace.CreateAsync(
            libSource: "namespace Lib { public class Animal { public void M() { int inLib; } } }\n",
            appSource: "namespace App { public class Dog : Lib.Animal { public void N() { int inApp; } } }\n");
        var operation = new GetDiagnosticsOperation(workspace.Context);

        var app = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            DiagnosticIds = Cs0168,
            NamespaceFilter = "App"
        });
        Assert.True(app.Success);
        Assert.Equal(new[] { "inApp" }, VariableNames(app.Data!));
        Assert.All(app.Data!.Diagnostics, d => Assert.Equal(workspace.AppSourcePath, d.File));

        var mismatched = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            DiagnosticIds = Cs0168,
            NamespaceFilter = "App",
            ProjectPath = workspace.LibProjectPath
        });
        Assert.True(mismatched.Success);
        Assert.Empty(mismatched.Data!.Diagnostics);
        Assert.Equal(0, mismatched.Data.TotalCount);

        var lib = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            DiagnosticIds = Cs0168,
            NamespaceFilter = "Lib",
            ProjectPath = workspace.LibProjectPath
        });
        Assert.True(lib.Success);
        Assert.Equal(new[] { "inLib" }, VariableNames(lib.Data!));
    }

    #endregion

    #region accessibilityFilter

    // One CS0168 ("declared but never used") per accessibility site; the variable name identifies the site.
    // Lambdas / local functions are looked through to the declaring member (Lam → public, Loc → internal).
    private const string AccessibilitySource = """
        namespace App;
        public class Host
        {
            public void Pub() { int inPublic; }
            internal void Int() { int inInternal; }
            protected void Prot() { int inProtected; }
            private void Priv() { int inPrivate; }
            protected internal void ProtInt() { int inProtInt; }
            private protected void PrivProt() { int inPrivProt; }
            void DefaultPriv() { int inDefaultPrivate; }
            public void Lam() { System.Action a = () => { int inLambda; }; a(); }
            internal void Loc() { void Inner() { int inLocalFunc; } Inner(); }
        }
        """;

    [Fact]
    public void AccessibilityFilter_DefaultsToNull()
    {
        var @params = new GetDiagnosticsParams();
        Assert.Null(@params.AccessibilityFilter);
    }

    [SkippableTheory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task GetDiagnostics_BlankAccessibilityFilter_MatchesOmitted(string accessibilityFilter)
    {
        await using var workspace = await TempWorkspace.CreateAsync(AccessibilitySource);
        var operation = new GetDiagnosticsOperation(workspace.Context);

        var omitted = await operation.ExecuteAsync(new GetDiagnosticsParams { DiagnosticIds = Cs0168 });
        var filtered = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            DiagnosticIds = Cs0168,
            AccessibilityFilter = accessibilityFilter
        });

        Assert.True(omitted.Success);
        Assert.True(filtered.Success);
        Assert.Equal(9, omitted.Data!.TotalCount);
        Assert.Equal(omitted.Data.TotalCount, filtered.Data!.TotalCount);
        Assert.Equal(
            omitted.Data.Diagnostics.Select(d => d.Id + d.Message + d.Line).OrderBy(x => x, StringComparer.Ordinal),
            filtered.Data.Diagnostics.Select(d => d.Id + d.Message + d.Line).OrderBy(x => x, StringComparer.Ordinal));
    }

    [SkippableTheory]
    [InlineData("public", new[] { "inLambda", "inPublic" })]
    [InlineData("internal", new[] { "inInternal", "inLocalFunc" })]
    [InlineData("protected", new[] { "inProtected" })]
    [InlineData("private", new[] { "inDefaultPrivate", "inPrivate" })]
    [InlineData("protected internal", new[] { "inProtInt" })]
    [InlineData("private protected", new[] { "inPrivProt" })]
    public async Task GetDiagnostics_AccessibilityFilter_SelectsSitesByEnclosingSymbolDeclaredAccessibility(
        string accessibilityFilter, string[] expected)
    {
        await using var workspace = await TempWorkspace.CreateAsync(AccessibilitySource);
        var operation = new GetDiagnosticsOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            DiagnosticIds = Cs0168,
            AccessibilityFilter = accessibilityFilter
        });

        Assert.True(result.Success, accessibilityFilter);
        Assert.Equal(expected, VariableNames(result.Data!).OrderBy(n => n, StringComparer.Ordinal).ToArray());
        Assert.Equal(expected.Length, result.Data!.TotalCount);
        Assert.False(result.Data.Truncated);
    }

    [SkippableFact]
    public async Task GetDiagnostics_AccessibilityFilter_ImplicitPrivateDefault()
    {
        await using var workspace = await TempWorkspace.CreateAsync(AccessibilitySource);
        var operation = new GetDiagnosticsOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            DiagnosticIds = Cs0168,
            AccessibilityFilter = "private"
        });

        Assert.True(result.Success);
        Assert.Contains("inDefaultPrivate", VariableNames(result.Data!));
        Assert.DoesNotContain("inPublic", VariableNames(result.Data!));
    }

    [SkippableTheory]
    [InlineData("PUBLIC", "public")]
    [InlineData("  Private  ", "private")]
    [InlineData("Protected   Internal", "protected internal")]
    [InlineData("internal protected", "protected internal")]
    [InlineData("protected private", "private protected")]
    public async Task GetDiagnostics_AccessibilityFilter_IsCaseInsensitiveAndAcceptsEitherModifierOrder(
        string accessibilityFilter, string canonical)
    {
        await using var workspace = await TempWorkspace.CreateAsync(AccessibilitySource);
        var operation = new GetDiagnosticsOperation(workspace.Context);

        var filtered = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            DiagnosticIds = Cs0168,
            AccessibilityFilter = accessibilityFilter
        });
        var expected = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            DiagnosticIds = Cs0168,
            AccessibilityFilter = canonical
        });

        Assert.True(filtered.Success);
        Assert.True(expected.Success);
        Assert.NotEmpty(expected.Data!.Diagnostics);
        Assert.Equal(
            VariableNames(expected.Data).OrderBy(n => n, StringComparer.Ordinal),
            VariableNames(filtered.Data!).OrderBy(n => n, StringComparer.Ordinal));
    }

    [SkippableTheory]
    [InlineData("friend")]
    [InlineData("ProtectedOrInternal")]
    [InlineData("public, internal")]
    public async Task GetDiagnostics_UnknownAccessibilityFilter_ThrowsInvalidVisibility(string accessibilityFilter)
    {
        await using var workspace = await TempWorkspace.CreateAsync(AccessibilitySource);
        var operation = new GetDiagnosticsOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GetDiagnosticsParams
            {
                DiagnosticIds = Cs0168,
                AccessibilityFilter = accessibilityFilter
            }));

        Assert.Equal(ErrorCodes.InvalidVisibility, ex.ErrorCode);
        Assert.StartsWith($"Invalid accessibilityFilter '{accessibilityFilter}'. Valid values: ", ex.Message);
    }

    [SkippableFact]
    public async Task GetDiagnostics_AccessibilityFilter_ProtectedInternalNotMatchedByProtectedOrInternal()
    {
        await using var workspace = await TempWorkspace.CreateAsync(AccessibilitySource);
        var operation = new GetDiagnosticsOperation(workspace.Context);

        var protInt = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            DiagnosticIds = Cs0168,
            AccessibilityFilter = "protected internal"
        });
        var prot = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            DiagnosticIds = Cs0168,
            AccessibilityFilter = "protected"
        });
        var intern = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            DiagnosticIds = Cs0168,
            AccessibilityFilter = "internal"
        });

        Assert.True(protInt.Success);
        Assert.Equal(new[] { "inProtInt" }, VariableNames(protInt.Data!));
        Assert.DoesNotContain("inProtInt", VariableNames(prot.Data!));
        Assert.DoesNotContain("inProtInt", VariableNames(intern.Data!));
    }

    [SkippableFact]
    public async Task GetDiagnostics_AccessibilityFilter_LambdaAndLocalFunctionJudgedByDeclaringMember()
    {
        await using var workspace = await TempWorkspace.CreateAsync(AccessibilitySource);
        var operation = new GetDiagnosticsOperation(workspace.Context);

        var pub = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            DiagnosticIds = Cs0168,
            AccessibilityFilter = "public"
        });
        var intern = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            DiagnosticIds = Cs0168,
            AccessibilityFilter = "internal"
        });

        Assert.True(pub.Success);
        Assert.Contains("inLambda", VariableNames(pub.Data!));
        Assert.DoesNotContain("inLocalFunc", VariableNames(pub.Data!));

        Assert.True(intern.Success);
        Assert.Contains("inLocalFunc", VariableNames(intern.Data!));
        Assert.DoesNotContain("inLambda", VariableNames(intern.Data!));
    }

    [SkippableFact]
    public async Task GetDiagnostics_AccessibilityFilter_NamespaceLevelSitesNeverMatch()
    {
        // Roslyn reports INamespaceSymbol.DeclaredAccessibility as Public, so an unused using inside
        // a namespace declaration must still be excluded when filtering for public.
        await using var workspace = await TempWorkspace.CreateAsync("""
            using System.Text;

            namespace App.Services
            {
                using System.Collections.Generic;

                public class S { public void M() { int inPublic; } }
            }
            """);
        var operation = new GetDiagnosticsOperation(workspace.Context);

        var omitted = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            SeverityFilter = "All",
            DiagnosticIds = new[] { "CS8019", "CS0168" },
            SourceFile = workspace.SourcePath
        });
        Assert.True(omitted.Success);
        Assert.Equal(3, omitted.Data!.TotalCount);

        var publicSites = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            SeverityFilter = "All",
            DiagnosticIds = new[] { "CS8019", "CS0168" },
            SourceFile = workspace.SourcePath,
            AccessibilityFilter = "public"
        });

        Assert.True(publicSites.Success);
        Assert.Equal(new[] { "inPublic" }, VariableNames(publicSites.Data!));
        Assert.DoesNotContain(publicSites.Data!.Diagnostics, d => d.Id == "CS8019");
        Assert.Equal(1, publicSites.Data.TotalCount);
    }

    [SkippableFact]
    public async Task GetDiagnostics_AccessibilityFilter_DropsDiagnosticsWithoutSourceLocation()
    {
        await using var workspace = await TempWorkspace.CreateAsync(
            "namespace App { public class C { public void M() { int inPublic; } } }\n",
            outputType: "Exe");
        var operation = new GetDiagnosticsOperation(workspace.Context);

        var omitted = await operation.ExecuteAsync(new GetDiagnosticsParams { SeverityFilter = "Warning" });
        Assert.True(omitted.Success);
        Assert.Contains(omitted.Data!.Diagnostics, d => d.Id == "CS5001" && d.File == null);

        var filtered = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            SeverityFilter = "Warning",
            AccessibilityFilter = "public"
        });

        Assert.True(filtered.Success);
        Assert.NotEmpty(filtered.Data!.Diagnostics);
        Assert.All(filtered.Data.Diagnostics, d => Assert.NotNull(d.File));
        Assert.DoesNotContain(filtered.Data.Diagnostics, d => d.Id == "CS5001");
        Assert.Contains(filtered.Data.Diagnostics, d => d.Id == "CS0168");
    }

    [SkippableFact]
    public async Task GetDiagnostics_AccessibilityFilterWithMaxResults_CapsAfterAccessibilityFilter()
    {
        await using var workspace = await TempWorkspace.CreateAsync(AccessibilitySource);
        var operation = new GetDiagnosticsOperation(workspace.Context);

        var filtered = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            DiagnosticIds = Cs0168,
            AccessibilityFilter = "public"
        });
        Assert.True(filtered.Success);
        Assert.Equal(2, filtered.Data!.TotalCount);

        var capped = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            DiagnosticIds = Cs0168,
            AccessibilityFilter = "public",
            MaxResults = 1
        });

        Assert.True(capped.Success);
        Assert.Equal(2, capped.Data!.TotalCount);
        Assert.Single(capped.Data.Diagnostics);
        Assert.True(capped.Data.Truncated);
        Assert.All(VariableNames(capped.Data), n => Assert.Contains(n, VariableNames(filtered.Data)));
    }

    [SkippableFact]
    public async Task GetDiagnostics_AccessibilityFilterNoMatch_ReturnsEmptySuccess()
    {
        await using var workspace = await TempWorkspace.CreateAsync("""
            public class OnlyPub { public void Go() { int inPublic; } }
            """);
        var operation = new GetDiagnosticsOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            DiagnosticIds = Cs0168,
            AccessibilityFilter = "private",
            MaxResults = 1
        });

        Assert.True(result.Success);
        Assert.Empty(result.Data!.Diagnostics);
        Assert.Equal(0, result.Data.TotalCount);
        Assert.False(result.Data.Truncated);
    }

    [SkippableFact]
    public async Task GetDiagnostics_AccessibilityFilterWithNamespaceFilter_RequiresBoth()
    {
        await using var workspace = await TempWorkspace.CreateAsync("""
            namespace App.Services
            {
                public class S
                {
                    public void Pub() { int inServicesPublic; }
                    private void Priv() { int inServicesPrivate; }
                }
            }

            namespace Other
            {
                public class O
                {
                    public void Pub() { int inOtherPublic; }
                }
            }
            """);
        var operation = new GetDiagnosticsOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            DiagnosticIds = Cs0168,
            AccessibilityFilter = "public",
            NamespaceFilter = "App.Services"
        });

        Assert.True(result.Success);
        Assert.Equal(new[] { "inServicesPublic" }, VariableNames(result.Data!));
        Assert.Equal(1, result.Data!.TotalCount);
        Assert.False(result.Data.Truncated);
    }

    [SkippableFact]
    public async Task GetDiagnostics_AccessibilityFilterWithSourceFile_MustSatisfyBoth()
    {
        await using var workspace = await TempWorkspace.CreateAsync(
            AccessibilitySource,
            extraFiles: new Dictionary<string, string>
            {
                ["Bar.cs"] = "namespace App; public class Bar { public void M() { int inBarPublic; } private void H() { int inBarPrivate; } }\n"
            });
        var operation = new GetDiagnosticsOperation(workspace.Context);

        var barPath = Path.Combine(workspace.DirectoryPath, "Bar.cs");
        var scoped = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            DiagnosticIds = Cs0168,
            AccessibilityFilter = "public",
            SourceFile = barPath
        });
        Assert.True(scoped.Success);
        Assert.Equal(new[] { "inBarPublic" }, VariableNames(scoped.Data!));
        Assert.Equal(1, scoped.Data!.TotalCount);

        var none = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            DiagnosticIds = Cs0168,
            AccessibilityFilter = "private",
            SourceFile = barPath
        });
        Assert.True(none.Success);
        Assert.Equal(new[] { "inBarPrivate" }, VariableNames(none.Data!));
    }

    [SkippableFact]
    public async Task GetDiagnostics_AccessibilityFilter_DeclarationAttachedFieldDiagnosticsUseFieldAccessibility()
    {
        // CS0169 / CS0414 attach to the field identifier. GetEnclosingSymbol is the enclosing public
        // type (a field without an initializer does not establish a member semantic scope), so without
        // resolving the declared field first, accessibilityFilter=public would incorrectly include
        // these private-field diagnostics. Compiler only emits CS0169/CS0414 for non-public fields.
        await using var workspace = await TempWorkspace.CreateAsync("""
            public class C
            {
                private int privateUnused;
                private int privateAssignedUnused = 1;
                public void Pub() { int inPublic; }
            }
            """);
        var operation = new GetDiagnosticsOperation(workspace.Context);

        var pub = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            DiagnosticIds = new[] { "CS0169", "CS0414", "CS0168" },
            AccessibilityFilter = "public"
        });
        var priv = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            DiagnosticIds = new[] { "CS0169", "CS0414", "CS0168" },
            AccessibilityFilter = "private"
        });

        Assert.True(pub.Success);
        Assert.True(priv.Success);
        // Public: only the method-body CS0168 site — not the private-field declaration diagnostics.
        Assert.Equal(new[] { "inPublic" }, VariableNames(pub.Data!).OrderBy(n => n, StringComparer.Ordinal).ToArray());
        Assert.DoesNotContain("C.privateUnused", VariableNames(pub.Data!));
        Assert.DoesNotContain("C.privateAssignedUnused", VariableNames(pub.Data!));
        // Private: both field diagnostics; not the public method's unused local.
        Assert.Equal(
            new[] { "C.privateAssignedUnused", "C.privateUnused" },
            VariableNames(priv.Data!).OrderBy(n => n, StringComparer.Ordinal).ToArray());
        Assert.DoesNotContain("inPublic", VariableNames(priv.Data!));
    }

    [SkippableFact]
    public async Task GetDiagnostics_AccessibilityFilter_FieldTypeAndModifierDiagnosticsUseFieldAccessibility()
    {
        // CS0246 (unknown field type) / CS0106 (invalid modifier) attach to the field declaration's
        // type / modifier, whose ancestors are VariableDeclarationSyntax / FieldDeclarationSyntax but not
        // the declarator. GetDeclaredSymbol yields no field for those container nodes, so without
        // resolving through a declarator the site would fall out to the public containing type.
        await using var workspace = await TempWorkspace.CreateAsync("""
            public class C
            {
                private MissingPrivate privateField;
                public MissingPublic publicField;
                private event MissingHandler privateEvent;
                private virtual int virtualField;
            }
            """);
        var operation = new GetDiagnosticsOperation(workspace.Context);
        var ids = new[] { "CS0246", "CS0106" };

        var omitted = await operation.ExecuteAsync(new GetDiagnosticsParams { DiagnosticIds = ids });
        var pub = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            DiagnosticIds = ids,
            AccessibilityFilter = "public"
        });
        var priv = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            DiagnosticIds = ids,
            AccessibilityFilter = "private"
        });

        Assert.True(omitted.Success);
        Assert.True(pub.Success);
        Assert.True(priv.Success);
        Assert.Equal(4, omitted.Data!.TotalCount);
        Assert.Equal(new[] { "MissingPublic" }, VariableNames(pub.Data!));
        Assert.Equal(
            new[] { "MissingHandler", "MissingPrivate", "virtual" },
            VariableNames(priv.Data!).OrderBy(n => n, StringComparer.Ordinal).ToArray());
    }

    [SkippableFact]
    public async Task GetDiagnostics_AccessibilityFilter_DirectiveTriviaBeforeDeclarationDoesNotInheritIt()
    {
        // FindToken attributes a position in leading trivia to the following token, so a #warning
        // directive immediately before `public class` must not resolve to that class: namespace-level
        // (and compilation-unit-level) sites never match a named filter.
        await using var workspace = await TempWorkspace.CreateAsync("""
            #warning TopLevel
            public class C { public void M() { int inPublic; } }

            namespace App
            {
                #warning InNamespace
                public class D { }
            }
            """);
        var operation = new GetDiagnosticsOperation(workspace.Context);
        var ids = new[] { "CS1030", "CS0168" };

        var omitted = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            SeverityFilter = "All",
            DiagnosticIds = ids
        });
        var pub = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            SeverityFilter = "All",
            DiagnosticIds = ids,
            AccessibilityFilter = "public"
        });

        Assert.True(omitted.Success);
        Assert.Equal(2, omitted.Data!.Diagnostics.Count(d => d.Id == "CS1030"));
        Assert.True(pub.Success);
        Assert.DoesNotContain(pub.Data!.Diagnostics, d => d.Id == "CS1030");
        Assert.Equal(new[] { "inPublic" }, VariableNames(pub.Data!));
        Assert.Equal(1, pub.Data!.TotalCount);
    }

    [SkippableFact]
    public async Task GetDiagnostics_AccessibilityFilter_DocumentationCommentDiagnosticsUseDocumentedMember()
    {
        // Documentation comment trivia belongs to the declaration it documents, so CS1574 on a doc
        // comment cref is judged by that member's accessibility (not the enclosing public type).
        await using var workspace = await TempWorkspace.CreateAsync(
            """
            /// <summary>C</summary>
            public class C
            {
                /// <see cref="NopePublic"/>
                public void Pub() { }

                /// <see cref="NopePrivate"/>
                private void Priv() { }
            }
            """,
            extraProperties: "<GenerateDocumentationFile>true</GenerateDocumentationFile>");
        var operation = new GetDiagnosticsOperation(workspace.Context);
        var ids = new[] { "CS1574" };

        var pub = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            DiagnosticIds = ids,
            AccessibilityFilter = "public"
        });
        var priv = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            DiagnosticIds = ids,
            AccessibilityFilter = "private"
        });

        Assert.True(pub.Success);
        Assert.True(priv.Success);
        Assert.Equal(new[] { "NopePublic" }, VariableNames(pub.Data!));
        Assert.Equal(new[] { "NopePrivate" }, VariableNames(priv.Data!));
    }

    #endregion


    private sealed class TempWorkspace : IAsyncDisposable
    {
        public required string DirectoryPath { get; init; }
        public required string SourcePath { get; init; }
        public required WorkspaceContext Context { get; init; }

        public string ProjectPath => Path.Combine(DirectoryPath, "TestApp.csproj");

        public static async Task<TempWorkspace> CreateAsync(
            string source,
            string fileName = "Foo.cs",
            string? outputType = null,
            IReadOnlyDictionary<string, string>? extraFiles = null,
            string extraProperties = "")
        {
            Skip.IfNot(ModuleInitializer.MsBuildAvailable, ModuleInitializer.MsBuildError ?? "MSBuild not available");

            var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpGetDiagnostics_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);

            var projectPath = Path.Combine(directory, "TestApp.csproj");
            var outputTypeElement = outputType == null ? "" : $"<OutputType>{outputType}</OutputType>";
            await File.WriteAllTextAsync(projectPath, $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net9.0</TargetFramework>
                    <Nullable>enable</Nullable>
                    {outputTypeElement}
                    {extraProperties}
                  </PropertyGroup>
                </Project>
                """);

            var path = Path.Combine(directory, fileName);
            await File.WriteAllTextAsync(path, source);

            if (extraFiles != null)
            {
                foreach (var (extraName, extraSource) in extraFiles)
                    await File.WriteAllTextAsync(Path.Combine(directory, extraName), extraSource);
            }

            try
            {
                var provider = new MSBuildWorkspaceProvider();
                var context = await provider.CreateContextAsync(projectPath);
                if (context.GetDocumentByPath(path) == null)
                {
                    context.Dispose();
                    throw new InvalidOperationException($"Workspace loaded but did not include {path}.");
                }

                return new TempWorkspace
                {
                    DirectoryPath = directory,
                    SourcePath = path,
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

    private sealed class TwoProjectWorkspace : IAsyncDisposable
    {
        public required string DirectoryPath { get; init; }
        public required string LibProjectPath { get; init; }
        public required string AppProjectPath { get; init; }
        public required string LibSourcePath { get; init; }
        public required string AppSourcePath { get; init; }
        public required WorkspaceContext Context { get; init; }

        public static async Task<TwoProjectWorkspace> CreateAsync(
            string libSource = "#warning LibWarning\nnamespace Lib { public class Animal {} }\n",
            string appSource = "#warning AppWarning\nnamespace App { public class Dog : Lib.Animal {} }\n")
        {
            Skip.IfNot(ModuleInitializer.MsBuildAvailable, ModuleInitializer.MsBuildError ?? "MSBuild not available");

            var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpGetDiagnosticsXP_" + Guid.NewGuid().ToString("N"));
            var libDir = Path.Combine(directory, "Lib");
            var appDir = Path.Combine(directory, "App");
            Directory.CreateDirectory(libDir);
            Directory.CreateDirectory(appDir);

            var libProject = Path.Combine(libDir, "Lib.csproj");
            var appProject = Path.Combine(appDir, "App.csproj");
            var libSourcePath = Path.Combine(libDir, "Animal.cs");
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
            await File.WriteAllTextAsync(libSourcePath, libSource);
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

            try
            {
                var provider = new MSBuildWorkspaceProvider();
                var context = await provider.CreateContextAsync(solutionPath);
                if (context.GetDocumentByPath(libSourcePath) == null || context.GetDocumentByPath(appSourcePath) == null)
                {
                    context.Dispose();
                    throw new InvalidOperationException("Workspace loaded but did not include Lib/App sources.");
                }

                return new TwoProjectWorkspace
                {
                    DirectoryPath = directory,
                    LibProjectPath = libProject,
                    AppProjectPath = appProject,
                    LibSourcePath = libSourcePath,
                    AppSourcePath = appSourcePath,
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

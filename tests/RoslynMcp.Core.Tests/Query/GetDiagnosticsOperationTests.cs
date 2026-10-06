using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Query;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Workspace;
using Xunit;

namespace RoslynMcp.Core.Tests.Query;

/// <summary>
/// Operation-level tests for <see cref="GetDiagnosticsOperation"/> optional <c>maxResults</c>, <c>diagnosticIds</c>, <c>excludeDiagnosticIds</c>, and <c>projectPath</c>
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

    private sealed class TempWorkspace : IAsyncDisposable
    {
        public required string DirectoryPath { get; init; }
        public required string SourcePath { get; init; }
        public required WorkspaceContext Context { get; init; }

        public string ProjectPath => Path.Combine(DirectoryPath, "TestApp.csproj");

        public static async Task<TempWorkspace> CreateAsync(string source, string fileName = "Foo.cs")
        {
            Skip.IfNot(ModuleInitializer.MsBuildAvailable, ModuleInitializer.MsBuildError ?? "MSBuild not available");

            var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpGetDiagnostics_" + Guid.NewGuid().ToString("N"));
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

            var path = Path.Combine(directory, fileName);
            await File.WriteAllTextAsync(path, source);

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

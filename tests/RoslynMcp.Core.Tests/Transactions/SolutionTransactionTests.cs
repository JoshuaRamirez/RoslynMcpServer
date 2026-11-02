using System.Diagnostics;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using RoslynMcp.Contracts.Coordinates;
using RoslynMcp.Contracts.Predicates;
using RoslynMcp.Contracts.Transactions;
using RoslynMcp.Core.Query;
using RoslynMcp.Core.Transactions;
using RoslynMcp.Core.Workspace;
using Xunit;

namespace RoslynMcp.Core.Tests.Transactions;

/// <summary>
/// Unit tests for Layer 3 <see cref="SolutionSessionManager"/> and <see cref="SolutionTransactionEngine"/>.
/// </summary>
public sealed class SolutionTransactionTests
{
    private const string InitialAccountCode = """
        namespace Acme.Ledger;

        public sealed class AccountCalculator
        {
            public int _taxBasisPoints = 500;

            public int CalculateNet(int grossAmount)
            {
                var taxDeduction = grossAmount * _taxBasisPoints / 10000;
                return grossAmount - taxDeduction;
            }

            private void ObsoleteHelper()
            {
            }
        }

        public sealed class AuditEntry
        {
            public int EntryId { get; init; }
        }
        """;

    private static Solution CreateLedgerSolution()
    {
        var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId("Acme.Ledger");
        var metadataRefs = new[]
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(Enumerable).Assembly.Location)
        };

        var solution = workspace.CurrentSolution
            .AddProject(ProjectInfo.Create(
                projectId,
                VersionStamp.Create(),
                "Acme.Ledger",
                "Acme.Ledger",
                LanguageNames.CSharp,
                metadataReferences: metadataRefs));

        var docId = DocumentId.CreateNewId(projectId, "AccountCalculator.cs");
        solution = solution.AddDocument(DocumentInfo.Create(
            docId,
            "AccountCalculator.cs",
            loader: TextLoader.From(TextAndVersion.Create(SourceText.From(InitialAccountCode), VersionStamp.Create())),
            filePath: "/repo/src/Acme.Ledger/AccountCalculator.cs"));

        return solution;
    }

    [Fact]
    public async Task ExecuteAsync_MultiStepFoldAcrossOperations_CommitsAtomically()
    {
        using var manager = new SolutionSessionManager();
        var session = manager.RegisterInMemorySession("/repo/Acme.sln", CreateLedgerSolution());

        var request = new SolutionTransactionRequest
        {
            SolutionPath = "/repo/Acme.sln",
            PreviewDiffOnly = false,
            RollbackOnCompilationErrors = true,
            Operations =
            [
                // Step 1: Rename CalculateNet -> ComputeNetBalance
                new RenameSymbolOperation
                {
                    Target = new TargetCoordinate
                    {
                        Scope = DocumentScope.FromFiles("AccountCalculator.cs"),
                        Anchor = Anchor.FromSymbolPath("Acme.Ledger.AccountCalculator.CalculateNet")
                    },
                    NewName = "ComputeNetBalance"
                },
                // Step 2: Encapsulate field _taxBasisPoints -> TaxBasisPoints
                new EncapsulateFieldOperation
                {
                    Target = new TargetCoordinate
                    {
                        Scope = DocumentScope.FromFiles("AccountCalculator.cs"),
                        Anchor = Anchor.FromSymbolPath("Acme.Ledger.AccountCalculator._taxBasisPoints")
                    },
                    PropertyName = "TaxBasisPoints"
                },
                // Step 3: Delete ObsoleteHelper method
                new DeleteSymbolOperation
                {
                    Target = new TargetCoordinate
                    {
                        Scope = DocumentScope.FromFiles("AccountCalculator.cs"),
                        Anchor = Anchor.FromSymbolPath("Acme.Ledger.AccountCalculator.ObsoleteHelper")
                    }
                },
                // Step 4: Move AuditEntry to its own file
                new MoveSymbolToFileOperation
                {
                    Target = new TargetCoordinate
                    {
                        Scope = DocumentScope.FromFiles("AccountCalculator.cs"),
                        Anchor = Anchor.FromSymbolPath("Acme.Ledger.AuditEntry")
                    },
                    TargetFilePath = "/repo/src/Acme.Ledger/AuditEntry.cs"
                }
            ]
        };

        var result = await SolutionTransactionEngine.ExecuteAsync(session, request, persistToDisk: false);

        Assert.True(result.Success, result.Error);
        Assert.False(result.RolledBack);
        Assert.False(result.Preview);
        Assert.Equal(4, result.OperationsExecuted);
        Assert.Equal(2, result.FileDiffs.Count);
        Assert.Empty(result.CompilationErrors);

        var updatedDoc = session.CurrentSolution.Projects.Single().Documents.Single(d => d.Name == "AccountCalculator.cs");
        var updatedText = (await updatedDoc.GetTextAsync()).ToString();
        Assert.Contains("ComputeNetBalance", updatedText, StringComparison.Ordinal);
        Assert.Contains("TaxBasisPoints", updatedText, StringComparison.Ordinal);
        Assert.DoesNotContain("ObsoleteHelper", updatedText, StringComparison.Ordinal);
        Assert.DoesNotContain("class AuditEntry", updatedText, StringComparison.Ordinal);

        var movedDoc = session.CurrentSolution.Projects.Single().Documents.Single(d => d.Name == "AuditEntry.cs");
        var movedText = (await movedDoc.GetTextAsync()).ToString();
        Assert.Contains("class AuditEntry", movedText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteAsync_PreviewDiffOnly_ReturnsUnifiedDiffsWithoutMutatingSession()
    {
        using var manager = new SolutionSessionManager();
        var initialSolution = CreateLedgerSolution();
        var session = manager.RegisterInMemorySession("/repo/Acme.sln", initialSolution);
        var initialRevision = session.Revision;

        var request = new SolutionTransactionRequest
        {
            SolutionPath = "/repo/Acme.sln",
            PreviewDiffOnly = true,
            RollbackOnCompilationErrors = true,
            Operations =
            [
                new RenameSymbolOperation
                {
                    Target = new TargetCoordinate
                    {
                        Scope = DocumentScope.FromFiles("AccountCalculator.cs"),
                        Anchor = Anchor.FromSymbolPath("Acme.Ledger.AccountCalculator.CalculateNet")
                    },
                    NewName = "PreviewRenamedMethod"
                }
            ]
        };

        var result = await SolutionTransactionEngine.ExecuteAsync(session, request, persistToDisk: false);

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.False(result.RolledBack);
        var diff = Assert.Single(result.FileDiffs);
        Assert.Equal("Modified", diff.ChangeKind);
        Assert.Contains("--- a/", diff.UnifiedDiff, StringComparison.Ordinal);
        Assert.Contains("+++ b/", diff.UnifiedDiff, StringComparison.Ordinal);
        Assert.Contains("+            public int PreviewRenamedMethod(int grossAmount)", diff.UnifiedDiff, StringComparison.Ordinal);

        // Verify warm session state was NOT mutated
        Assert.Equal(initialRevision, session.Revision);
        var docText = (await session.CurrentSolution.Projects.Single().Documents.Single().GetTextAsync()).ToString();
        Assert.Contains("CalculateNet", docText, StringComparison.Ordinal);
        Assert.DoesNotContain("PreviewRenamedMethod", docText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteAsync_RollbackOnCompilationErrors_RejectsInvalidMutationAndPreservesBaseline()
    {
        using var manager = new SolutionSessionManager();
        var session = manager.RegisterInMemorySession("/repo/Acme.sln", CreateLedgerSolution());
        var baselineRevision = session.Revision;

        // Line 10: "return grossAmount - taxDeduction;" -> replace with undefined symbol causing CS0103
        var phrase = "grossAmount - taxDeduction";
        var offset = InitialAccountCode.IndexOf(phrase, StringComparison.Ordinal);

        var request = new SolutionTransactionRequest
        {
            SolutionPath = "/repo/Acme.sln",
            PreviewDiffOnly = false,
            RollbackOnCompilationErrors = true,
            Operations =
            [
                new EditSpanOperation
                {
                    Target = new TargetCoordinate
                    {
                        Scope = DocumentScope.FromFiles("AccountCalculator.cs"),
                        Anchor = Anchor.FromTextSpan(offset, phrase.Length)
                    },
                    ReplacementText = "nonExistentIdentifier + \"incompatibleString\""
                }
            ]
        };

        var result = await SolutionTransactionEngine.ExecuteAsync(session, request, persistToDisk: false);

        Assert.False(result.Success);
        Assert.True(result.RolledBack);
        Assert.NotEmpty(result.CompilationErrors);
        Assert.Contains(result.CompilationErrors, e => e.Contains("CS0103", StringComparison.Ordinal) || e.Contains("CS0029", StringComparison.Ordinal));

        // Session state must remain untouched
        Assert.Equal(baselineRevision, session.Revision);
        var currentText = (await session.CurrentSolution.Projects.Single().Documents.Single().GetTextAsync()).ToString();
        Assert.Equal(InitialAccountCode, currentText);
    }

    [Fact]
    public async Task WarmIncrementalEditAndQueryTurnaround_CompletesUnder50Milliseconds()
    {
        using var manager = new SolutionSessionManager();
        var session = manager.RegisterInMemorySession("/repo/Acme.sln", CreateLedgerSolution());

        // Warm up compilation and evaluator caches once
        _ = await SymbolPredicateEvaluator.EvaluateAsync(
            session.CurrentSolution,
            SymbolPredicate.WithName("CalculateNet"));

        var updatedSource = InitialAccountCode.Replace("CalculateNet", "CalculateNetFast", StringComparison.Ordinal);

        var stopwatch = Stopwatch.StartNew();
        var updated = manager.InvalidateOrUpdateDocument("/repo/Acme.sln", "AccountCalculator.cs", updatedSource);
        var queryResult = await SymbolPredicateEvaluator.EvaluateAsync(
            session.CurrentSolution,
            SymbolPredicate.WithName("CalculateNetFast"));
        stopwatch.Stop();

        Assert.True(updated);
        Assert.True(queryResult.Success);
        Assert.Single(queryResult.Symbols);
        Assert.True(
            stopwatch.ElapsedMilliseconds < 50,
            $"Expected warm incremental update + query turnaround < 50ms, actual was {stopwatch.ElapsedMilliseconds}ms.");
    }

    [Fact]
    public async Task ExecuteAsync_InlineVariableAndExtractMethod_FoldCleanly()
    {
        using var manager = new SolutionSessionManager();
        var session = manager.RegisterInMemorySession("/repo/Acme.sln", CreateLedgerSolution());

        var request = new SolutionTransactionRequest
        {
            SolutionPath = "/repo/Acme.sln",
            Operations =
            [
                new InlineVariableOperation
                {
                    Target = new TargetCoordinate
                    {
                        Scope = DocumentScope.FromFiles("AccountCalculator.cs"),
                        Anchor = Anchor.FromLineSpan(9, 17),
                        Normalization = EnclosingNormalization.EnclosingStatement
                    }
                },
                new ChangeSignatureOperation
                {
                    Target = new TargetCoordinate
                    {
                        Scope = DocumentScope.FromFiles("AccountCalculator.cs"),
                        Anchor = Anchor.FromSymbolPath("Acme.Ledger.AccountCalculator.CalculateNet")
                    },
                    Parameters =
                    [
                        new ParameterSpec("grossAmount", "int", "1000")
                    ],
                    ReturnType = "long"
                }
            ]
        };

        var result = await SolutionTransactionEngine.ExecuteAsync(session, request, persistToDisk: false);

        Assert.True(result.Success, result.Error);
        var text = (await session.CurrentSolution.Projects.Single().Documents.Single().GetTextAsync()).ToString();
        Assert.DoesNotContain("var taxDeduction =", text, StringComparison.Ordinal);
        Assert.Contains("public long CalculateNet(int grossAmount = 1000)", text, StringComparison.Ordinal);
    }

    [Fact]
    public void SolutionOperation_PolymorphicJsonRoundTrip_PreservesDerivedOperationTypes()
    {
        var request = new SolutionTransactionRequest
        {
            SolutionPath = "/repo/Acme.sln",
            PreviewDiffOnly = true,
            Operations =
            [
                new RenameSymbolOperation
                {
                    Target = new TargetCoordinate { Anchor = Anchor.FromSymbolPath("Acme.Ledger.AccountCalculator") },
                    NewName = "LedgerEngine"
                },
                new SyntaxGeneratorOperation
                {
                    Target = new TargetCoordinate { Anchor = Anchor.FromSymbolPath("Acme.Ledger.LedgerEngine") },
                    Declaration = new DeclarationPayload
                    {
                        Action = SyntaxGeneratorActionKind.AddProperty,
                        Name = "Version",
                        TypeName = "int",
                        Accessibility = "Public"
                    }
                }
            ]
        };

        var json = JsonSerializer.Serialize(request);
        var roundTripped = JsonSerializer.Deserialize<SolutionTransactionRequest>(json);

        Assert.NotNull(roundTripped);
        Assert.Equal(2, roundTripped.Operations.Count);
        Assert.IsType<RenameSymbolOperation>(roundTripped.Operations[0]);
        Assert.IsType<SyntaxGeneratorOperation>(roundTripped.Operations[1]);
    }
}

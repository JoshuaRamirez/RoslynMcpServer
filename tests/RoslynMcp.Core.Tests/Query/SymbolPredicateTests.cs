using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using RoslynMcp.Contracts.Coordinates;
using RoslynMcp.Contracts.Predicates;
using RoslynMcp.Core.Query;
using Xunit;
using SymbolKind = RoslynMcp.Contracts.Enums.SymbolKind;

namespace RoslynMcp.Core.Tests.Query;

/// <summary>
/// Unit tests for Layer 2 <see cref="SymbolPredicate"/> AST and <see cref="SymbolPredicateEvaluator"/>.
/// </summary>
public sealed class SymbolPredicateTests
{
    private const string ContractsSource = """
        using System;

        namespace Acme.Contracts;

        [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method | AttributeTargets.Property)]
        public sealed class AuditedAttribute : Attribute
        {
        }

        public interface IEntity
        {
            Guid Id { get; }
        }

        public interface IOrderRepository : IEntity
        {
            void SaveOrder(int orderId);
        }

        public abstract class RepositoryBase : IOrderRepository
        {
            public Guid Id { get; } = Guid.NewGuid();
            public abstract void SaveOrder(int orderId);
        }
        """;

    private const string ServicesSource = """
        using System;
        using Acme.Contracts;

        namespace Acme.Services.Orders;

        [Audited]
        public sealed class SqlOrderRepository : RepositoryBase
        {
            public const int MaxRetryCount = 3;
            private readonly string _connectionString = "Server=localhost";

            [Audited]
            public override void SaveOrder(int orderId)
            {
                ValidateOrderId(orderId);
            }

            internal void ArchiveOrders()
            {
                ValidateOrderId(1);
                ValidateOrderId(2);
            }

            private static void ValidateOrderId(int id)
            {
                if (id <= 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(id));
                }
            }
        }

        internal record OrderSummary(int OrderId, decimal Total);
        """;

    private const string BillingSource = """
        using Acme.Contracts;
        using Acme.Services.Orders;

        namespace Acme.Services.Billing;

        public sealed class BillingCoordinator
        {
            public void Process(SqlOrderRepository repo)
            {
                repo.SaveOrder(42);
            }
        }
        """;

    private static Solution CreateTestSolution()
    {
        var workspace = new AdhocWorkspace();
        var solution = workspace.CurrentSolution;

        var contractsProjectId = ProjectId.CreateNewId("Acme.Contracts");
        var servicesProjectId = ProjectId.CreateNewId("Acme.Services");

        var metadataRefs = new[]
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(Attribute).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(Enumerable).Assembly.Location)
        };

        solution = solution
            .AddProject(ProjectInfo.Create(
                contractsProjectId,
                VersionStamp.Create(),
                "Acme.Contracts",
                "Acme.Contracts",
                LanguageNames.CSharp,
                metadataReferences: metadataRefs))
            .AddProject(ProjectInfo.Create(
                servicesProjectId,
                VersionStamp.Create(),
                "Acme.Services",
                "Acme.Services",
                LanguageNames.CSharp,
                metadataReferences: metadataRefs,
                projectReferences: [new ProjectReference(contractsProjectId)]));

        var contractsDocId = DocumentId.CreateNewId(contractsProjectId, "Contracts.cs");
        var ordersDocId = DocumentId.CreateNewId(servicesProjectId, "SqlOrderRepository.cs");
        var billingDocId = DocumentId.CreateNewId(servicesProjectId, "BillingCoordinator.cs");

        solution = solution
            .AddDocument(DocumentInfo.Create(
                contractsDocId,
                "Contracts.cs",
                loader: TextLoader.From(TextAndVersion.Create(SourceText.From(ContractsSource), VersionStamp.Create())),
                filePath: "/repo/src/Acme.Contracts/Contracts.cs"))
            .AddDocument(DocumentInfo.Create(
                ordersDocId,
                "SqlOrderRepository.cs",
                loader: TextLoader.From(TextAndVersion.Create(SourceText.From(ServicesSource), VersionStamp.Create())),
                filePath: "/repo/src/Acme.Services/Orders/SqlOrderRepository.cs"))
            .AddDocument(DocumentInfo.Create(
                billingDocId,
                "BillingCoordinator.cs",
                loader: TextLoader.From(TextAndVersion.Create(SourceText.From(BillingSource), VersionStamp.Create())),
                filePath: "/repo/src/Acme.Services/Billing/BillingCoordinator.cs"));

        return solution;
    }

    [Fact]
    public async Task EvaluateAsync_SymbolKindAndAccessibility_FiltersCorrectly()
    {
        var solution = CreateTestSolution();
        var predicate = SymbolPredicate.And(
            SymbolPredicate.WithKinds(SymbolKind.Class, SymbolKind.Record),
            SymbolPredicate.WithAccessibilities("Internal"));

        var result = await SymbolPredicateEvaluator.EvaluateAsync(solution, predicate);

        Assert.True(result.Success);
        var match = Assert.Single(result.Symbols);
        Assert.Equal("OrderSummary", match.Name);
        Assert.Equal("Record", match.Kind);
        Assert.Equal("Internal", match.Accessibility);
    }

    [Fact]
    public async Task EvaluateAsync_BooleanCombinators_AllAnyNot_ComposeCorrectly()
    {
        var solution = CreateTestSolution();

        var predicate = SymbolPredicate.And(
            SymbolPredicate.WithKinds(SymbolKind.Method),
            SymbolPredicate.Or(
                SymbolPredicate.WithName("Save*"),
                SymbolPredicate.WithName("Validate*")),
            SymbolPredicate.Negate(SymbolPredicate.WithAccessibilities("Private")));

        var result = await SymbolPredicateEvaluator.EvaluateAsync(solution, predicate);

        Assert.True(result.Success);
        Assert.All(result.Symbols, s => Assert.Equal("SaveOrder", s.Name));
        Assert.DoesNotContain(result.Symbols, s => s.Name == "ValidateOrderId");
    }

    [Fact]
    public async Task EvaluateAsync_NameAndNamespaceRegexAndGlob_MatchExpectedSymbols()
    {
        var solution = CreateTestSolution();

        // Regex name match + hierarchical namespace prefix
        var regexPredicate = new SymbolPredicate
        {
            SymbolKindIn = ["Class"],
            NameMatches = "/^(Sql|Billing).*$/",
            NamespaceMatches = "Acme.Services"
        };

        var regexResult = await SymbolPredicateEvaluator.EvaluateAsync(solution, regexPredicate);
        Assert.Equal(
            ["BillingCoordinator", "SqlOrderRepository"],
            regexResult.Symbols.Select(s => s.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());

        // Glob namespace match
        var globPredicate = new SymbolPredicate
        {
            SymbolKindIn = ["Class", "Record"],
            NamespaceMatches = "Acme.Services.Orders"
        };

        var globResult = await SymbolPredicateEvaluator.EvaluateAsync(solution, globPredicate);
        Assert.Equal(
            ["OrderSummary", "SqlOrderRepository"],
            globResult.Symbols.Select(s => s.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task EvaluateAsync_HasAttribute_MatchesShortAndFullAttributeName()
    {
        var solution = CreateTestSolution();

        var shortAttrResult = await SymbolPredicateEvaluator.EvaluateAsync(
            solution,
            SymbolPredicate.WithAttribute("Audited"));
        Assert.Equal(
            ["SaveOrder", "SqlOrderRepository"],
            shortAttrResult.Symbols.Select(s => s.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());

        var fullAttrResult = await SymbolPredicateEvaluator.EvaluateAsync(
            solution,
            SymbolPredicate.WithAttribute("Acme.Contracts.AuditedAttribute"));
        Assert.Equal(
            ["SaveOrder", "SqlOrderRepository"],
            fullAttrResult.Symbols.Select(s => s.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task EvaluateAsync_ImplementsOrInherits_MatchesTransitiveInterfacesAndBaseClasses()
    {
        var solution = CreateTestSolution();

        // Transitive interface IEntity is implemented by IOrderRepository, RepositoryBase, and SqlOrderRepository
        var entityImplementors = await SymbolPredicateEvaluator.EvaluateAsync(
            solution,
            SymbolPredicate.ImplementingOrInheriting("IEntity"));
        Assert.Equal(
            ["IOrderRepository", "RepositoryBase", "SqlOrderRepository"],
            entityImplementors.Symbols.Select(s => s.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());

        // Base class RepositoryBase is inherited only by SqlOrderRepository
        var baseSubclasses = await SymbolPredicateEvaluator.EvaluateAsync(
            solution,
            SymbolPredicate.ImplementingOrInheriting("Acme.Contracts.RepositoryBase"));
        var single = Assert.Single(baseSubclasses.Symbols);
        Assert.Equal("SqlOrderRepository", single.Name);
    }

    [Fact]
    public async Task EvaluateAsync_CallerAndReferenceCountRanges_FilterAccurately()
    {
        var solution = CreateTestSolution();

        // ValidateOrderId is called 3 times in SqlOrderRepository (1 in SaveOrder, 2 in ArchiveOrders)
        var calledThreeTimes = await SymbolPredicateEvaluator.EvaluateAsync(
            solution,
            new SymbolPredicate
            {
                SymbolKindIn = ["Method"],
                InDocumentScope = DocumentScope.FromFiles("SqlOrderRepository.cs"),
                CallerCountRange = new IntRange(Min: 3, Max: 3)
            });

        var validateMethod = Assert.Single(calledThreeTimes.Symbols);
        Assert.Equal("ValidateOrderId", validateMethod.Name);

        // MaxRetryCount has 0 references
        var unusedConstants = await SymbolPredicateEvaluator.EvaluateAsync(
            solution,
            new SymbolPredicate
            {
                SymbolKindIn = ["Constant"],
                ReferencesCountRange = new IntRange(Min: 0, Max: 0)
            });

        var maxRetry = Assert.Single(unusedConstants.Symbols);
        Assert.Equal("MaxRetryCount", maxRetry.Name);
    }

    [Fact]
    public async Task EvaluateAsync_InDocumentScope_RestrictsToMatchingProjectAndGlob()
    {
        var solution = CreateTestSolution();

        var scopedPredicate = new SymbolPredicate
        {
            SymbolKindIn = ["Class"],
            InDocumentScope = new DocumentScope
            {
                ProjectNames = ["Acme.Services"],
                DirectoryGlobs = ["**/Billing/**"]
            }
        };

        var result = await SymbolPredicateEvaluator.EvaluateAsync(solution, scopedPredicate);
        var match = Assert.Single(result.Symbols);
        Assert.Equal("BillingCoordinator", match.Name);
    }

    [Fact]
    public void SymbolPredicate_JsonRoundTrip_PreservesNestedTree()
    {
        var predicate = SymbolPredicate.And(
            SymbolPredicate.WithKinds(SymbolKind.Class, SymbolKind.Method),
            SymbolPredicate.Or(
                SymbolPredicate.WithAttribute("Audited"),
                SymbolPredicate.ImplementingOrInheriting("IOrderRepository")),
            SymbolPredicate.Negate(SymbolPredicate.WithCallerCount(max: 0)));

        var json = JsonSerializer.Serialize(predicate);
        var roundTripped = JsonSerializer.Deserialize<SymbolPredicate>(json);

        Assert.NotNull(roundTripped);
        Assert.Equal(3, roundTripped.All?.Count);
    }
}

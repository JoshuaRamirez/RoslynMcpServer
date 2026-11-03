using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using RoslynMcp.Contracts.Coordinates;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Core.Coordinates;
using RoslynMcp.Core.Refactoring;
using Xunit;

namespace RoslynMcp.Core.Tests.Coordinates;

/// <summary>
/// Comprehensive unit tests for Layer 1 <see cref="TargetCoordinate"/> and <see cref="TargetCoordinateResolver"/>.
/// </summary>
public sealed class TargetCoordinateTests
{
    private const string SampleServiceCode = """
        using System;

        namespace Acme.Services;

        public sealed class OrderProcessor
        {
            private readonly int _threshold = 10;

            public int ComputeTotal(int quantity, int unitPrice)
            {
                var subtotal = quantity * unitPrice;
                if (subtotal > _threshold)
                {
                    return subtotal - 2;
                }

                return subtotal;
            }

            public int ComputeTotal(int flatRate)
            {
                return flatRate;
            }
        }
        """;

    private const string SampleDomainCode = """
        namespace Acme.Domain;

        public record OrderRecord(int Id, string Customer);
        """;

    private const string SampleTestsCode = """
        namespace Acme.Tests;

        public sealed class OrderTests
        {
            public void VerifyOrder()
            {
                var x = 42;
            }
        }
        """;

    private static Solution CreateMultiProjectSolution()
    {
        var workspace = new AdhocWorkspace();
        var solution = workspace.CurrentSolution;

        var coreProjectId = ProjectId.CreateNewId("Acme.Core");
        var testProjectId = ProjectId.CreateNewId("Acme.Tests");

        var metadataRefs = new[]
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(Enumerable).Assembly.Location)
        };

        solution = solution
            .AddProject(ProjectInfo.Create(
                coreProjectId,
                VersionStamp.Create(),
                "Acme.Core",
                "Acme.Core",
                LanguageNames.CSharp,
                metadataReferences: metadataRefs))
            .AddProject(ProjectInfo.Create(
                testProjectId,
                VersionStamp.Create(),
                "Acme.Tests",
                "Acme.Tests",
                LanguageNames.CSharp,
                metadataReferences: metadataRefs,
                projectReferences: [new ProjectReference(coreProjectId)]));

        var serviceDocId = DocumentId.CreateNewId(coreProjectId, "OrderProcessor.cs");
        var domainDocId = DocumentId.CreateNewId(coreProjectId, "OrderRecord.cs");
        var testDocId = DocumentId.CreateNewId(testProjectId, "OrderTests.cs");

        solution = solution
            .AddDocument(DocumentInfo.Create(
                serviceDocId,
                "OrderProcessor.cs",
                loader: TextLoader.From(TextAndVersion.Create(SourceText.From(SampleServiceCode), VersionStamp.Create())),
                filePath: "/repo/src/Acme.Core/Services/OrderProcessor.cs"))
            .AddDocument(DocumentInfo.Create(
                domainDocId,
                "OrderRecord.cs",
                loader: TextLoader.From(TextAndVersion.Create(SourceText.From(SampleDomainCode), VersionStamp.Create())),
                filePath: "/repo/src/Acme.Core/Domain/OrderRecord.cs"))
            .AddDocument(DocumentInfo.Create(
                testDocId,
                "OrderTests.cs",
                loader: TextLoader.From(TextAndVersion.Create(SourceText.From(SampleTestsCode), VersionStamp.Create())),
                filePath: "/repo/tests/Acme.Tests/OrderTests.cs"));

        return solution;
    }

    [Fact]
    public void ResolveDocuments_AllScope_ReturnsAllDocumentsInDeterministicOrder()
    {
        var solution = CreateMultiProjectSolution();
        var docs = TargetCoordinateResolver.ResolveDocuments(solution, DocumentScope.All);

        Assert.Equal(3, docs.Count);
        Assert.Equal(
            ["OrderRecord.cs", "OrderProcessor.cs", "OrderTests.cs"],
            docs.Select(d => d.Name).ToArray());
    }

    [Fact]
    public void ResolveDocuments_FilePathsScope_FiltersByExactAndRelativePaths()
    {
        var solution = CreateMultiProjectSolution();

        var exactDocs = TargetCoordinateResolver.ResolveDocuments(
            solution,
            DocumentScope.FromFiles("/repo/src/Acme.Core/Services/OrderProcessor.cs"));
        Assert.Single(exactDocs);
        Assert.Equal("OrderProcessor.cs", exactDocs[0].Name);

        var relativeDocs = TargetCoordinateResolver.ResolveDocuments(
            solution,
            DocumentScope.FromFiles("Domain/OrderRecord.cs", "OrderTests.cs"));
        Assert.Equal(2, relativeDocs.Count);
        Assert.Equal(["OrderRecord.cs", "OrderTests.cs"], relativeDocs.Select(d => d.Name).ToArray());
    }

    [Fact]
    public void ResolveDocuments_ProjectNamesScope_FiltersByExactAndWildcardProjectNames()
    {
        var solution = CreateMultiProjectSolution();

        var coreDocs = TargetCoordinateResolver.ResolveDocuments(
            solution,
            DocumentScope.FromProjects("Acme.Core"));
        Assert.Equal(2, coreDocs.Count);
        Assert.All(coreDocs, d => Assert.Equal("Acme.Core", d.Project.Name));

        var wildcardDocs = TargetCoordinateResolver.ResolveDocuments(
            solution,
            DocumentScope.FromProjects("*.Tests"));
        Assert.Single(wildcardDocs);
        Assert.Equal("OrderTests.cs", wildcardDocs[0].Name);
    }

    [Fact]
    public void ResolveDocuments_DirectoryGlobsScope_FiltersByGlobPatterns()
    {
        var solution = CreateMultiProjectSolution();

        var serviceDocs = TargetCoordinateResolver.ResolveDocuments(
            solution,
            DocumentScope.FromGlobs("**/Services/**"));
        Assert.Single(serviceDocs);
        Assert.Equal("OrderProcessor.cs", serviceDocs[0].Name);

        var srcDocs = TargetCoordinateResolver.ResolveDocuments(
            solution,
            DocumentScope.FromGlobs("/repo/src/**/*.cs"));
        Assert.Equal(2, srcDocs.Count);

        var combinedScope = new DocumentScope
        {
            ProjectNames = ["Acme.Core"],
            DirectoryGlobs = ["**/Domain/*.cs"]
        };
        var combinedDocs = TargetCoordinateResolver.ResolveDocuments(solution, combinedScope);
        Assert.Single(combinedDocs);
        Assert.Equal("OrderRecord.cs", combinedDocs[0].Name);
    }

    [Fact]
    public async Task ResolveSingleAsync_LineSpanAnchor_ResolvesExactNodeAndSpan()
    {
        var solution = CreateMultiProjectSolution();
        // Line 11: "        var subtotal = quantity * unitPrice;"
        var coordinate = new TargetCoordinate
        {
            Scope = DocumentScope.FromFiles("OrderProcessor.cs"),
            Anchor = Anchor.FromLineSpan(startLine: 11, startColumn: 24, endLine: 11, endColumn: 44),
            Normalization = EnclosingNormalization.Exact
        };

        var target = await TargetCoordinateResolver.ResolveSingleAsync(solution, coordinate);
        var (docId, span, node) = target;

        Assert.NotEqual(default, docId);
        Assert.Equal("quantity * unitPrice", node.ToString());
        Assert.IsAssignableFrom<BinaryExpressionSyntax>(node);
        Assert.Equal(node.Span, span);
    }

    [Fact]
    public async Task ResolveSingleAsync_TextSpanAnchor_ResolvesExactSubstringNode()
    {
        var solution = CreateMultiProjectSolution();
        var phrase = "subtotal - 2";
        var startOffset = SampleServiceCode.IndexOf(phrase, StringComparison.Ordinal);
        Assert.True(startOffset > 0);

        var coordinate = new TargetCoordinate
        {
            Scope = DocumentScope.FromFiles("OrderProcessor.cs"),
            Anchor = Anchor.FromTextSpan(startOffset, phrase.Length),
            Normalization = EnclosingNormalization.Exact
        };

        var target = await TargetCoordinateResolver.ResolveSingleAsync(solution, coordinate);
        Assert.Equal(phrase, target.Node.ToString());
        Assert.Equal(new TextSpan(startOffset, phrase.Length), target.Span);
    }

    [Fact]
    public async Task ResolveSingleAsync_SymbolPathAnchor_ResolvesTypeMemberFieldAndOverload()
    {
        var solution = CreateMultiProjectSolution();

        // 1. Full path to overloaded method with parameter types
        var methodCoord = new TargetCoordinate
        {
            Scope = DocumentScope.FromProjects("Acme.Core"),
            Anchor = Anchor.FromSymbolPath("Acme.Services.OrderProcessor.ComputeTotal(int, int)"),
            Normalization = EnclosingNormalization.Exact
        };
        var methodTarget = await TargetCoordinateResolver.ResolveSingleAsync(solution, methodCoord);
        var methodDecl = Assert.IsType<MethodDeclarationSyntax>(methodTarget.Node);
        Assert.Equal("ComputeTotal", methodDecl.Identifier.Text);
        Assert.Equal(2, methodDecl.ParameterList.Parameters.Count);
        Assert.NotNull(methodTarget.Symbol);

        // 2. Field path
        var fieldCoord = new TargetCoordinate
        {
            Scope = DocumentScope.FromProjects("Acme.Core"),
            Anchor = Anchor.FromSymbolPath("global::Acme.Services.OrderProcessor._threshold"),
            Normalization = EnclosingNormalization.Exact
        };
        var fieldTarget = await TargetCoordinateResolver.ResolveSingleAsync(solution, fieldCoord);
        Assert.IsType<FieldDeclarationSyntax>(fieldTarget.Node);

        // 3. Record type path
        var typeCoord = new TargetCoordinate
        {
            Scope = DocumentScope.All,
            Anchor = Anchor.FromSymbolPath("Acme.Domain.OrderRecord"),
            Normalization = EnclosingNormalization.Exact
        };
        var typeTarget = await TargetCoordinateResolver.ResolveSingleAsync(solution, typeCoord);
        Assert.IsType<RecordDeclarationSyntax>(typeTarget.Node);
    }

    [Fact]
    public async Task ResolveSingleAsync_SyntaxIndexAnchor_ResolvesByPreorderAndSyntaxKind()
    {
        var solution = CreateMultiProjectSolution();

        // First ReturnStatement in OrderProcessor.cs is "return subtotal - 2;"
        var firstReturnCoord = new TargetCoordinate
        {
            Scope = DocumentScope.FromFiles("OrderProcessor.cs"),
            Anchor = Anchor.FromSyntaxIndex(0, "ReturnStatement"),
            Normalization = EnclosingNormalization.Exact
        };
        var firstReturn = await TargetCoordinateResolver.ResolveSingleAsync(solution, firstReturnCoord);
        Assert.IsType<ReturnStatementSyntax>(firstReturn.Node);
        Assert.Equal("return subtotal - 2;", firstReturn.Node.ToString());

        // Second ReturnStatement in OrderProcessor.cs is "return subtotal;"
        var secondReturnCoord = new TargetCoordinate
        {
            Scope = DocumentScope.FromFiles("OrderProcessor.cs"),
            Anchor = Anchor.FromSyntaxIndex(1, "ReturnStatement"),
            Normalization = EnclosingNormalization.Exact
        };
        var secondReturn = await TargetCoordinateResolver.ResolveSingleAsync(solution, secondReturnCoord);
        Assert.IsType<ReturnStatementSyntax>(secondReturn.Node);
        Assert.Equal("return subtotal;", secondReturn.Node.ToString());
    }

    [Fact]
    public async Task ResolveSingleAsync_AllFiveEnclosingNormalizationModes_PromoteCorrectly()
    {
        var solution = CreateMultiProjectSolution();
        // Anchor on the binary expression "subtotal - 2" inside:
        // if (subtotal > _threshold) { return subtotal - 2; }
        var phrase = "subtotal - 2";
        var startOffset = SampleServiceCode.IndexOf(phrase, StringComparison.Ordinal);
        var anchor = Anchor.FromTextSpan(startOffset, phrase.Length);
        var scope = DocumentScope.FromFiles("OrderProcessor.cs");

        // 1. Exact
        var exactTarget = await TargetCoordinateResolver.ResolveSingleAsync(
            solution,
            new TargetCoordinate { Scope = scope, Anchor = anchor, Normalization = EnclosingNormalization.Exact });
        Assert.IsAssignableFrom<BinaryExpressionSyntax>(exactTarget.Node);
        Assert.Equal("subtotal - 2", exactTarget.Node.ToString());

        // 2. EnclosingStatement
        var stmtTarget = await TargetCoordinateResolver.ResolveSingleAsync(
            solution,
            new TargetCoordinate { Scope = scope, Anchor = anchor, Normalization = EnclosingNormalization.EnclosingStatement });
        Assert.IsType<ReturnStatementSyntax>(stmtTarget.Node);
        Assert.Equal("return subtotal - 2;", stmtTarget.Node.ToString());
        Assert.Equal(stmtTarget.Node.Span, stmtTarget.Span);

        // 3. EnclosingBlock
        var blockTarget = await TargetCoordinateResolver.ResolveSingleAsync(
            solution,
            new TargetCoordinate { Scope = scope, Anchor = anchor, Normalization = EnclosingNormalization.EnclosingBlock });
        var blockNode = Assert.IsType<BlockSyntax>(blockTarget.Node);
        Assert.Contains("return subtotal - 2;", blockNode.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("if (subtotal > _threshold)", blockNode.ToString(), StringComparison.Ordinal);

        // 4. EnclosingMember
        var memberTarget = await TargetCoordinateResolver.ResolveSingleAsync(
            solution,
            new TargetCoordinate { Scope = scope, Anchor = anchor, Normalization = EnclosingNormalization.EnclosingMember });
        var memberNode = Assert.IsType<MethodDeclarationSyntax>(memberTarget.Node);
        Assert.Equal("ComputeTotal", memberNode.Identifier.Text);
        Assert.Equal(2, memberNode.ParameterList.Parameters.Count);
        Assert.NotNull(memberTarget.Symbol);

        // 5. EnclosingType
        var typeTarget = await TargetCoordinateResolver.ResolveSingleAsync(
            solution,
            new TargetCoordinate { Scope = scope, Anchor = anchor, Normalization = EnclosingNormalization.EnclosingType });
        var typeNode = Assert.IsType<ClassDeclarationSyntax>(typeTarget.Node);
        Assert.Equal("OrderProcessor", typeNode.Identifier.Text);
        Assert.NotNull(typeTarget.Symbol);
    }

    [Fact]
    public async Task ResolveSingleAsync_InvalidAnchors_ThrowRefactoringException()
    {
        var solution = CreateMultiProjectSolution();
        var scope = DocumentScope.FromFiles("OrderProcessor.cs");

        await Assert.ThrowsAsync<RefactoringException>(() =>
            TargetCoordinateResolver.ResolveSingleAsync(
                solution,
                new TargetCoordinate { Scope = scope, Anchor = Anchor.FromLineSpan(0, 1) }));

        await Assert.ThrowsAsync<RefactoringException>(() =>
            TargetCoordinateResolver.ResolveSingleAsync(
                solution,
                new TargetCoordinate { Scope = scope, Anchor = Anchor.FromLineSpan(999, 1) }));

        await Assert.ThrowsAsync<RefactoringException>(() =>
            TargetCoordinateResolver.ResolveSingleAsync(
                solution,
                new TargetCoordinate { Scope = scope, Anchor = Anchor.FromTextSpan(0, 99999) }));

        await Assert.ThrowsAsync<RefactoringException>(() =>
            TargetCoordinateResolver.ResolveSingleAsync(
                solution,
                new TargetCoordinate { Scope = scope, Anchor = Anchor.FromSyntaxIndex(9999, "MethodDeclaration") }));

        // Ambiguous symbol path (ComputeTotal has 2 overloads)
        var ambiguousEx = await Assert.ThrowsAsync<RefactoringException>(() =>
            TargetCoordinateResolver.ResolveSingleAsync(
                solution,
                new TargetCoordinate { Scope = scope, Anchor = Anchor.FromSymbolPath("Acme.Services.OrderProcessor.ComputeTotal") }));
        Assert.Equal(ErrorCodes.SymbolAmbiguous, ambiguousEx.ErrorCode);
    }

    [Fact]
    public void TargetCoordinate_JsonRoundTrip_PreservesAllFields()
    {
        var original = new TargetCoordinate
        {
            Scope = new DocumentScope
            {
                FilePaths = ["/repo/src/File.cs"],
                ProjectNames = ["Acme.Core"],
                DirectoryGlobs = ["**/Services/**"]
            },
            Anchor = Anchor.FromLineSpan(10, 5, 12, 20),
            Normalization = EnclosingNormalization.EnclosingMember
        };

        var json = JsonSerializer.Serialize(original);
        var roundTripped = JsonSerializer.Deserialize<TargetCoordinate>(json);

        Assert.NotNull(roundTripped);
        Assert.Equal(EnclosingNormalization.EnclosingMember, roundTripped.Normalization);
        Assert.Equal(new LineSpanAnchor(10, 5, 12, 20), roundTripped.Anchor.LineSpan);
        Assert.Equal(["/repo/src/File.cs"], roundTripped.Scope.FilePaths);
        Assert.Equal(["Acme.Core"], roundTripped.Scope.ProjectNames);
        Assert.Equal(["**/Services/**"], roundTripped.Scope.DirectoryGlobs);
    }
}

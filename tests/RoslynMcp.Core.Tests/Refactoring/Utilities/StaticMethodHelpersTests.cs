using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Utilities;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Utilities;

public class StaticMethodHelpersTests
{
    [Fact]
    public void PlanConflictsWithClaimedSpans_False_WhenClaimedEmpty()
    {
        var tree = CSharpSyntaxTree.ParseText("class C { void M() { } }");
        var method = tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().Single();

        var claimed = new HashSet<(SyntaxTree Tree, TextSpan Span)>();
        var declarations = new[] { (tree, method.Span) };
        var callSites = Array.Empty<(SyntaxTree, TextSpan)>();

        Assert.False(StaticMethodHelpers.PlanConflictsWithClaimedSpans(declarations, callSites, claimed));
    }

    [Fact]
    public void PlanConflictsWithClaimedSpans_True_WhenDeclarationAlreadyClaimed()
    {
        var tree = CSharpSyntaxTree.ParseText("class C { void M() { } void N() { } }");
        var methods = tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().ToList();
        var m = methods.Single(x => x.Identifier.Text == "M");
        var n = methods.Single(x => x.Identifier.Text == "N");

        var claimed = new HashSet<(SyntaxTree Tree, TextSpan Span)> { (tree, m.Span) };
        var declarations = new[] { (tree, m.Span), (tree, n.Span) };
        var callSites = Array.Empty<(SyntaxTree, TextSpan)>();

        Assert.True(StaticMethodHelpers.PlanConflictsWithClaimedSpans(declarations, callSites, claimed));
    }

    [Fact]
    public void PlanConflictsWithClaimedSpans_True_WhenCallSiteAlreadyClaimed()
    {
        var tree = CSharpSyntaxTree.ParseText("class C { void M() { N(); } void N() { } }");
        var root = tree.GetRoot();
        var m = root.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Single(x => x.Identifier.Text == "M");
        var invocation = root.DescendantNodes().OfType<InvocationExpressionSyntax>().Single();

        var claimed = new HashSet<(SyntaxTree Tree, TextSpan Span)> { (tree, invocation.Span) };
        var declarations = new[] { (tree, m.Span) };
        var callSites = new[] { (tree, invocation.Span) };

        Assert.True(StaticMethodHelpers.PlanConflictsWithClaimedSpans(declarations, callSites, claimed));
    }

    [Fact]
    public void PlanConflictsWithClaimedSpans_False_WhenSpansDistinct()
    {
        var tree = CSharpSyntaxTree.ParseText("class C { void M() { N(); } void N() { } }");
        var root = tree.GetRoot();
        var methods = root.DescendantNodes().OfType<MethodDeclarationSyntax>().ToList();
        var m = methods.Single(x => x.Identifier.Text == "M");
        var n = methods.Single(x => x.Identifier.Text == "N");
        var invocation = root.DescendantNodes().OfType<InvocationExpressionSyntax>().Single();

        var claimed = new HashSet<(SyntaxTree Tree, TextSpan Span)> { (tree, n.Span) };
        var declarations = new[] { (tree, m.Span) };
        var callSites = new[] { (tree, invocation.Span) };

        Assert.False(StaticMethodHelpers.PlanConflictsWithClaimedSpans(declarations, callSites, claimed));
    }

    [Fact]
    public void PlanConflictsWithClaimedSpans_False_WhenSameSpanDifferentTree()
    {
        var left = CSharpSyntaxTree.ParseText("class C { void M() { } }", path: "A.cs");
        var right = CSharpSyntaxTree.ParseText("class C { void M() { } }", path: "B.cs");
        var leftMethod = left.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().Single();
        var rightMethod = right.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().Single();

        // Same TextSpan offsets, different SyntaxTree — must not conflict.
        Assert.Equal(leftMethod.Span, rightMethod.Span);

        var claimed = new HashSet<(SyntaxTree Tree, TextSpan Span)> { (left, leftMethod.Span) };
        var declarations = new[] { (right, rightMethod.Span) };
        var callSites = Array.Empty<(SyntaxTree, TextSpan)>();

        Assert.False(StaticMethodHelpers.PlanConflictsWithClaimedSpans(declarations, callSites, claimed));
    }

    [Fact]
    public async Task GetDeclarationDocumentsAsync_ReturnsSingleDocument_ForOrdinaryMethod()
    {
        using var workspace = new AdhocWorkspace();
        var project = workspace.AddProject("P", LanguageNames.CSharp);
        var document = workspace.AddDocument(project.Id, "C.cs", SourceText.From("class C { void M() { } }"));
        var method = await GetMethodAsync(workspace.CurrentSolution, "C", "M");

        var documents = await StaticMethodHelpers.GetDeclarationDocumentsAsync(
            method, workspace.CurrentSolution, CancellationToken.None);

        Assert.Equal([document.Id], documents.Select(d => d.Id));
    }

    [Fact]
    public async Task GetDeclarationDocumentsAsync_ReturnsBothParts_ForPartialMethod()
    {
        using var workspace = new AdhocWorkspace();
        var project = workspace.AddProject("P", LanguageNames.CSharp);
        var definition = workspace.AddDocument(
            project.Id, "Def.cs", SourceText.From("partial class C { partial void M(); }"));
        var implementation = workspace.AddDocument(
            definition.Project.Id, "Impl.cs", SourceText.From("partial class C { partial void M() { } }"));
        var solution = workspace.CurrentSolution;
        var method = await GetMethodAsync(solution, "C", "M");
        Assert.NotNull(method.PartialImplementationPart);

        var fromDefinition = await StaticMethodHelpers.GetDeclarationDocumentsAsync(
            method, solution, CancellationToken.None);
        var fromImplementation = await StaticMethodHelpers.GetDeclarationDocumentsAsync(
            method.PartialImplementationPart!, solution, CancellationToken.None);

        // Declaring-reference order: the passed part first, then the other part.
        Assert.Equal([definition.Id, implementation.Id], fromDefinition.Select(d => d.Id));
        Assert.Equal([implementation.Id, definition.Id], fromImplementation.Select(d => d.Id));
    }

    [Fact]
    public async Task GetDeclarationDocumentsAsync_Throws_WhenDeclarationTreeNotInSolution()
    {
        using var workspace = new AdhocWorkspace();
        var project = workspace.AddProject("P", LanguageNames.CSharp);
        workspace.AddDocument(project.Id, "C.cs", SourceText.From("class C { void M() { } }"));
        var method = await GetMethodAsync(workspace.CurrentSolution, "C", "M");

        using var otherWorkspace = new AdhocWorkspace();
        otherWorkspace.AddProject("Other", LanguageNames.CSharp);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            StaticMethodHelpers.GetDeclarationDocumentsAsync(
                method, otherWorkspace.CurrentSolution, CancellationToken.None));

        Assert.Equal(ErrorCodes.DocumentNotEditable, ex.ErrorCode);
        Assert.Equal("Declaration of 'M' is not in an editable document.", ex.Message);
    }

    [Fact]
    public async Task GetDeclarationDocumentsAsync_Throws_WhenMethodHasNoSourceDeclarations()
    {
        using var workspace = new AdhocWorkspace();
        var project = workspace.AddProject("P", LanguageNames.CSharp)
            .AddMetadataReference(MetadataReference.CreateFromFile(typeof(object).Assembly.Location));
        Assert.True(workspace.TryApplyChanges(project.Solution));
        var compilation = await workspace.CurrentSolution.GetProject(project.Id)!.GetCompilationAsync();
        var toString = compilation!.GetSpecialType(SpecialType.System_Object)
            .GetMembers(nameof(ToString)).OfType<IMethodSymbol>().Single();
        Assert.Empty(toString.DeclaringSyntaxReferences);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            StaticMethodHelpers.GetDeclarationDocumentsAsync(
                toString, workspace.CurrentSolution, CancellationToken.None));

        Assert.Equal(ErrorCodes.DocumentNotEditable, ex.ErrorCode);
        Assert.Equal("Method 'ToString' is not in an editable document.", ex.Message);
    }

    [Fact]
    public async Task GetDeclarationDocumentsAsync_ThrowsOperationCanceled_WhenTokenCancelled()
    {
        using var workspace = new AdhocWorkspace();
        var project = workspace.AddProject("P", LanguageNames.CSharp);
        workspace.AddDocument(project.Id, "C.cs", SourceText.From("class C { void M() { } }"));
        var method = await GetMethodAsync(workspace.CurrentSolution, "C", "M");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            StaticMethodHelpers.GetDeclarationDocumentsAsync(method, workspace.CurrentSolution, cts.Token));
    }

    private static async Task<IMethodSymbol> GetMethodAsync(Solution solution, string typeName, string methodName)
    {
        var compilation = await solution.Projects.Single().GetCompilationAsync();
        var type = compilation!.GetTypeByMetadataName(typeName);
        Assert.NotNull(type);
        return type!.GetMembers(methodName).OfType<IMethodSymbol>().Single();
    }
}

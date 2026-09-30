using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Signature;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Signature;

public class SignatureCallSiteHelpersTests
{
    private sealed record TestCallSite(Document Document, TextSpan Span);

    [Fact]
    public async Task CollectInvocationCallSitesAsync_CollectsInvocation_SkipsDeclarationAndNameOf()
    {
        const string source = """
            public class C
            {
                public void M(int x) { }

                public void Run()
                {
                    M(1);
                    var n = nameof(M);
                }
            }
            """;

        using var workspace = CreateWorkspace(source, out var document, out var method);
        var sites = await SignatureCallSiteHelpers.CollectInvocationCallSitesAsync(
            new[] { method },
            document.Project.Solution,
            (doc, invocation, _, _) => Task.FromResult(new TestCallSite(doc, invocation.Span)),
            CancellationToken.None);

        Assert.Single(sites);
        var root = (await document.GetSyntaxRootAsync())!;
        var invocation = root.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Single(i => i.Expression is IdentifierNameSyntax id && id.Identifier.Text == "M");
        Assert.Equal(document.Id, sites[0].Document.Id);
        Assert.Equal(invocation.Span, sites[0].Span);
    }

    [Fact]
    public async Task CollectInvocationCallSitesAsync_MethodGroup_ThrowsUnsupportedCallSite()
    {
        const string source = """
            public class C
            {
                public int M(int x) => x;

                public void Run()
                {
                    System.Func<int, int> handler = M;
                }
            }
            """;

        using var workspace = CreateWorkspace(source, out var document, out var method);
        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            SignatureCallSiteHelpers.CollectInvocationCallSitesAsync(
                new[] { method },
                document.Project.Solution,
                (doc, invocation, _, _) => Task.FromResult(new TestCallSite(doc, invocation.Span)),
                CancellationToken.None));

        Assert.Equal(ErrorCodes.UnsupportedCallSite, ex.ErrorCode);
        Assert.Contains("method group", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CollectInvocationCallSitesAsync_DedupesSameInvocationSpan()
    {
        // Two methods in the list that share the same call site should not
        // double-add when FindReferences hits the same invocation twice for
        // the same document+span (seen set is across the whole walk).
        const string source = """
            public class C
            {
                public void M(int x) { }

                public void Run()
                {
                    M(1);
                    M(2);
                }
            }
            """;

        using var workspace = CreateWorkspace(source, out var document, out var method);
        var sites = await SignatureCallSiteHelpers.CollectInvocationCallSitesAsync(
            new[] { method, method },
            document.Project.Solution,
            (doc, invocation, _, _) => Task.FromResult(new TestCallSite(doc, invocation.Span)),
            CancellationToken.None);

        Assert.Equal(2, sites.Count);
        Assert.Equal(2, sites.Select(s => s.Span).Distinct().Count());
    }

    private static AdhocWorkspace CreateWorkspace(
        string source,
        out Document document,
        out IMethodSymbol method)
    {
        var workspace = new AdhocWorkspace();
        var project = workspace.AddProject("P", LanguageNames.CSharp)
            .WithMetadataReferences(new[]
            {
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location)
            });
        document = workspace.AddDocument(project.Id, "C.cs", SourceText.From(source));
        // Refresh project after AddDocument
        project = document.Project;
        var compilation = project.GetCompilationAsync().GetAwaiter().GetResult()
            ?? throw new InvalidOperationException("No compilation.");
        var type = compilation.GetTypeByMetadataName("C")
            ?? throw new InvalidOperationException("Type C missing.");
        method = type.GetMembers("M").OfType<IMethodSymbol>().Single();
        return workspace;
    }
}

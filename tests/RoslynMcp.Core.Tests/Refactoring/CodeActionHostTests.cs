using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CodeRefactorings;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using RoslynMcp.Contracts.Coordinates;
using RoslynMcp.Contracts.Transactions;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Transactions;
using RoslynMcp.Core.Workspace;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring;

/// <summary>
/// Unit tests for Layer 4 <see cref="CodeActionHostRunner"/>, <see cref="SyntaxGeneratorRunner"/>,
/// and <see cref="IOperationSemanticRewriter"/> inside <see cref="SolutionTransactionEngine"/>.
/// </summary>
public sealed class CodeActionHostTests
{
    private const string InitialProcessorCode = """
        using System.Text;
        using System;
        using System.Collections.Generic;
        using System.Linq;

        namespace Acme.Pipeline;

        public interface IHealthProbe
        {
            bool IsHealthy(string subsystem);
        }

        public partial class TelemetryProcessor
        {
            public List<int> FilterPositiveDoubled(IEnumerable<int> values)
            {
                var results = new List<int>();
                foreach (var v in values)
                {
                    if (v > 0)
                    {
                        results.Add(v * 2);
                    }
                }

                return results;
            }

            public string DescribeCode(int status)
            {
                if (status == 200)
                    return "OK";
                else if (status == 404)
                    return "NotFound";
                else
                    return "Unknown";
            }

            public string FormatGreeting(string user) => "Hello, " + user + "!";

            public int UnwrapLength(object candidate)
            {
                if (candidate is string)
                {
                    var text = (string)candidate;
                    return text.Length;
                }

                return 0;
            }
        }
        """;

    private static Solution CreatePipelineSolution()
    {
        var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId("Acme.Pipeline");
        var metadataRefs = new[]
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(Enumerable).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(List<>).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(System.Text.StringBuilder).Assembly.Location)
        };

        var solution = workspace.CurrentSolution.AddProject(ProjectInfo.Create(
            projectId,
            VersionStamp.Create(),
            "Acme.Pipeline",
            "Acme.Pipeline",
            LanguageNames.CSharp,
            metadataReferences: metadataRefs));

        var docId = DocumentId.CreateNewId(projectId, "TelemetryProcessor.cs");
        solution = solution.AddDocument(DocumentInfo.Create(
            docId,
            "TelemetryProcessor.cs",
            loader: TextLoader.From(TextAndVersion.Create(SourceText.From(InitialProcessorCode), VersionStamp.Create())),
            filePath: "/repo/src/Acme.Pipeline/TelemetryProcessor.cs"));

        return solution;
    }

    [Fact]
    public async Task CodeActionHostRunner_RemoveUnusedUsingsAndSortUsings_CleansDocument()
    {
        var solution = CreatePipelineSolution();

        var afterRemoveUnused = await CodeActionHostRunner.ExecuteAsync(
            solution,
            new CodeActionHostOperation
            {
                Target = new TargetCoordinate { Scope = DocumentScope.FromFiles("TelemetryProcessor.cs") },
                DiagnosticId = "CS8019",
                Scope = "Document"
            });

        var afterSort = await CodeActionHostRunner.ExecuteAsync(
            afterRemoveUnused,
            new CodeActionHostOperation
            {
                Target = new TargetCoordinate { Scope = DocumentScope.FromFiles("TelemetryProcessor.cs") },
                CodeActionTitle = "SortUsings",
                Scope = "Document"
            });

        var doc = afterSort.Projects.Single().Documents.Single();
        var text = (await doc.GetTextAsync()).ToString();

        // System.Text is unused in InitialProcessorCode and must be removed
        Assert.DoesNotContain("using System.Text;", text, StringComparison.Ordinal);
        Assert.Contains("using System.Collections.Generic;", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CodeActionHostRunner_CustomCodeFixAndRefactoringProviders_ExecuteThroughHost()
    {
        CodeActionHostRunner.RegisterCodeFixProvider(new SampleBannerCodeFixProvider());
        CodeActionHostRunner.RegisterCodeRefactoringProvider(new SampleSealClassRefactoringProvider());

        var solution = CreatePipelineSolution();

        var afterCustomFix = await CodeActionHostRunner.ExecuteAsync(
            solution,
            new CodeActionHostOperation
            {
                Target = new TargetCoordinate
                {
                    Scope = DocumentScope.FromFiles("TelemetryProcessor.cs"),
                    Anchor = Anchor.FromSymbolPath("Acme.Pipeline.TelemetryProcessor")
                },
                DiagnosticId = "ACME001",
                CodeActionTitle = "Add telemetry tag constant"
            });

        var afterCustomRefactoring = await CodeActionHostRunner.ExecuteAsync(
            afterCustomFix,
            new CodeActionHostOperation
            {
                Target = new TargetCoordinate
                {
                    Scope = DocumentScope.FromFiles("TelemetryProcessor.cs"),
                    Anchor = Anchor.FromSymbolPath("Acme.Pipeline.TelemetryProcessor")
                },
                CodeActionTitle = "Add health ping method"
            });

        var text = (await afterCustomRefactoring.Projects.Single().Documents.Single().GetTextAsync()).ToString();
        Assert.Contains("TelemetryTag", text, StringComparison.Ordinal);
        Assert.Contains("Ping()", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SyntaxGeneratorRunner_AddFieldPropertyMethodAttributeAndInterface_GeneratesValidDeclarations()
    {
        var solution = CreatePipelineSolution();
        var typeTarget = new TargetCoordinate
        {
            Scope = DocumentScope.FromFiles("TelemetryProcessor.cs"),
            Anchor = Anchor.FromSymbolPath("Acme.Pipeline.TelemetryProcessor")
        };

        // 1. Add Field
        solution = await SyntaxGeneratorRunner.ExecuteAsync(
            solution,
            new SyntaxGeneratorOperation
            {
                Target = typeTarget,
                Declaration = new DeclarationPayload
                {
                    Action = SyntaxGeneratorActionKind.AddField,
                    Name = "_batchLimit",
                    TypeName = "int",
                    Accessibility = "Private",
                    Modifiers = ["readonly"],
                    InitializerExpression = "100"
                }
            });

        // 2. Add Property
        solution = await SyntaxGeneratorRunner.ExecuteAsync(
            solution,
            new SyntaxGeneratorOperation
            {
                Target = typeTarget,
                Declaration = new DeclarationPayload
                {
                    Action = SyntaxGeneratorActionKind.AddProperty,
                    Name = "ServiceName",
                    TypeName = "string",
                    Accessibility = "Public",
                    InitializerExpression = "\"Telemetry\""
                }
            });

        // 3. Add Method
        solution = await SyntaxGeneratorRunner.ExecuteAsync(
            solution,
            new SyntaxGeneratorOperation
            {
                Target = typeTarget,
                Declaration = new DeclarationPayload
                {
                    Action = SyntaxGeneratorActionKind.AddMethod,
                    Name = "ComputeBatchSize",
                    TypeName = "int",
                    Accessibility = "Public",
                    Parameters = [new ParameterSpec("multiplier", "int")],
                    Statements = ["return _batchLimit * multiplier;"]
                }
            });

        // 4. Add Attribute
        solution = await SyntaxGeneratorRunner.ExecuteAsync(
            solution,
            new SyntaxGeneratorOperation
            {
                Target = typeTarget,
                Declaration = new DeclarationPayload
                {
                    Action = SyntaxGeneratorActionKind.AddAttribute,
                    Name = "Obsolete",
                    AttributeName = "Obsolete",
                    AttributeArguments = ["\"Use v2 processor\""]
                }
            });

        // 5. Add Interface Implementation (IHealthProbe)
        solution = await SyntaxGeneratorRunner.ExecuteAsync(
            solution,
            new SyntaxGeneratorOperation
            {
                Target = typeTarget,
                Declaration = new DeclarationPayload
                {
                    Action = SyntaxGeneratorActionKind.AddInterfaceImplementation,
                    Name = "IHealthProbe"
                }
            });

        var doc = solution.Projects.Single().Documents.Single();
        var text = (await doc.GetTextAsync()).ToString();

        Assert.Contains("private readonly int _batchLimit = 100;", text, StringComparison.Ordinal);
        Assert.Contains("public string ServiceName { get; set; } = \"Telemetry\";", text, StringComparison.Ordinal);
        Assert.Contains("public int ComputeBatchSize(int multiplier)", text, StringComparison.Ordinal);
        Assert.Contains("[Obsolete(\"Use v2 processor\")]", text, StringComparison.Ordinal);
        Assert.Contains("IHealthProbe", text, StringComparison.Ordinal);
        Assert.Contains("public bool IsHealthy(string subsystem)", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IOperationSemanticRewriter_AllPatterns_RewriteAccurately()
    {
        var solution = CreatePipelineSolution();
        var scope = DocumentScope.FromFiles("TelemetryProcessor.cs");

        // 1. ConvertForeachToLinq
        solution = await IOperationSemanticRewriter.ExecuteAsync(
            solution,
            new SemanticPatternRewriteOperation
            {
                Scope = scope,
                Pattern = SemanticPatternKind.ConvertForeachToLinq
            });

        // 2. ConvertIfToSwitchExpression
        solution = await IOperationSemanticRewriter.ExecuteAsync(
            solution,
            new SemanticPatternRewriteOperation
            {
                Scope = scope,
                Pattern = SemanticPatternKind.ConvertIfToSwitchExpression
            });

        // 3. ConvertToInterpolatedString
        solution = await IOperationSemanticRewriter.ExecuteAsync(
            solution,
            new SemanticPatternRewriteOperation
            {
                Scope = scope,
                Pattern = SemanticPatternKind.ConvertToInterpolatedString
            });

        // 4. ConvertToPatternMatching
        solution = await IOperationSemanticRewriter.ExecuteAsync(
            solution,
            new SemanticPatternRewriteOperation
            {
                Scope = scope,
                Pattern = SemanticPatternKind.ConvertToPatternMatching
            });

        // 5. ConvertExpressionToBlockBody on FormatGreeting
        solution = await IOperationSemanticRewriter.ExecuteAsync(
            solution,
            new SemanticPatternRewriteOperation
            {
                Scope = scope,
                Pattern = SemanticPatternKind.ConvertExpressionToBlockBody,
                Target = new TargetCoordinate
                {
                    Scope = scope,
                    Anchor = Anchor.FromSymbolPath("Acme.Pipeline.TelemetryProcessor.FormatGreeting")
                }
            });

        // 6. AddNullChecks on FormatGreeting
        solution = await IOperationSemanticRewriter.ExecuteAsync(
            solution,
            new SemanticPatternRewriteOperation
            {
                Scope = scope,
                Pattern = SemanticPatternKind.AddNullChecks,
                Target = new TargetCoordinate
                {
                    Scope = scope,
                    Anchor = Anchor.FromSymbolPath("Acme.Pipeline.TelemetryProcessor.FormatGreeting")
                }
            });

        var doc = solution.Projects.Single().Documents.Single();
        var text = (await doc.GetTextAsync()).ToString();

        Assert.Contains("results.AddRange(values.Where(v => v > 0).Select(v => v * 2));", text, StringComparison.Ordinal);
        Assert.Contains("return status switch", text, StringComparison.Ordinal);
        Assert.Contains("$\"Hello, {user}!\"", text, StringComparison.Ordinal);
        Assert.Contains("if (candidate is string text)", text, StringComparison.Ordinal);
        Assert.Contains("ArgumentNullException.ThrowIfNull(user);", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SolutionTransactionEngine_ExecutesAllThreeLayer4EnginesAtomically()
    {
        using var manager = new SolutionSessionManager();
        var session = manager.RegisterInMemorySession("/repo/Pipeline.sln", CreatePipelineSolution());

        var request = new SolutionTransactionRequest
        {
            SolutionPath = "/repo/Pipeline.sln",
            RollbackOnCompilationErrors = true,
            Operations =
            [
                new SyntaxGeneratorOperation
                {
                    Target = new TargetCoordinate
                    {
                        Scope = DocumentScope.FromFiles("TelemetryProcessor.cs"),
                        Anchor = Anchor.FromSymbolPath("Acme.Pipeline.TelemetryProcessor")
                    },
                    Declaration = new DeclarationPayload
                    {
                        Action = SyntaxGeneratorActionKind.AddInterfaceImplementation,
                        Name = "IHealthProbe"
                    }
                },
                new SemanticPatternRewriteOperation
                {
                    Scope = DocumentScope.FromFiles("TelemetryProcessor.cs"),
                    Pattern = SemanticPatternKind.ConvertForeachToLinq
                },
                new CodeActionHostOperation
                {
                    Target = new TargetCoordinate
                    {
                        Scope = DocumentScope.FromFiles("TelemetryProcessor.cs")
                    },
                    DiagnosticId = "CS8019",
                    Scope = "Document"
                }
            ]
        };

        var result = await SolutionTransactionEngine.ExecuteAsync(session, request, persistToDisk: false);

        Assert.True(result.Success, result.Error);
        Assert.False(result.RolledBack);
        Assert.Equal(3, result.OperationsExecuted);
        Assert.Empty(result.CompilationErrors);
        Assert.Single(result.FileDiffs);
    }

    private sealed class SampleBannerCodeFixProvider : CodeFixProvider
    {
        public override ImmutableArray<string> FixableDiagnosticIds => ["ACME001"];

        public override Task RegisterCodeFixesAsync(CodeFixContext context)
        {
            context.RegisterCodeFix(
                CodeAction.Create(
                    "Add telemetry tag constant",
                    async ct =>
                    {
                        var root = await context.Document.GetSyntaxRootAsync(ct);
                        var typeDecl = root!.DescendantNodes().OfType<ClassDeclarationSyntax>().First();
                        var constMember = SyntaxFactory.ParseMemberDeclaration("public const string TelemetryTag = \"v1\";")!;
                        var updatedType = typeDecl.AddMembers(constMember);
                        return context.Document.WithSyntaxRoot(root.ReplaceNode(typeDecl, updatedType));
                    },
                    equivalenceKey: "AddTelemetryTag"),
                context.Diagnostics);
            return Task.CompletedTask;
        }
    }

    private sealed class SampleSealClassRefactoringProvider : CodeRefactoringProvider
    {
        public override Task ComputeRefactoringsAsync(CodeRefactoringContext context)
        {
            context.RegisterRefactoring(
                CodeAction.Create(
                    "Add health ping method",
                    async ct =>
                    {
                        var root = await context.Document.GetSyntaxRootAsync(ct);
                        var typeDecl = root!.DescendantNodes().OfType<ClassDeclarationSyntax>().First();
                        var pingMethod = SyntaxFactory.ParseMemberDeclaration("public bool Ping() => true;")!;
                        var updatedType = typeDecl.AddMembers(pingMethod);
                        return context.Document.WithSyntaxRoot(root.ReplaceNode(typeDecl, updatedType));
                    },
                    equivalenceKey: "AddHealthPing"));
            return Task.CompletedTask;
        }
    }
}

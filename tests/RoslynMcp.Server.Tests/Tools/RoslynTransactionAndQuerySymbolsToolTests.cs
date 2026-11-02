using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using RoslynMcp.Core.Workspace;
using RoslynMcp.Server.Tests.TestHelpers;
using RoslynMcp.Server.Tools;
using Xunit;

namespace RoslynMcp.Server.Tests.Tools;

/// <summary>
/// Unit tests for Layer 5 compact <c>$defs</c> MCP tools (<see cref="RoslynTransactionTool"/> and <see cref="RoslynQuerySymbolsTool"/>).
/// </summary>
public class RoslynTransactionAndQuerySymbolsToolTests
{
    [Fact]
    public void RoslynTransactionTool_SchemaContainsCompactDefsAndRequiredProperties()
    {
        var tool = new RoslynTransactionTool(new ThrowingWorkspaceProvider());

        Assert.Equal("roslyn_transaction", tool.Name);
        Assert.NotEmpty(tool.Description);

        var json = JsonSerializer.Serialize(tool.InputSchema);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("object", root.GetProperty("type").GetString());
        var props = root.GetProperty("properties");
        Assert.True(props.TryGetProperty("solutionPath", out _));
        Assert.True(props.TryGetProperty("operations", out _));
        Assert.True(props.TryGetProperty("previewDiffOnly", out _));
        Assert.True(props.TryGetProperty("rollbackOnCompilationErrors", out _));

        var defs = root.GetProperty("$defs");
        Assert.True(defs.TryGetProperty("TargetCoordinate", out _));
        Assert.True(defs.TryGetProperty("DocumentScope", out _));
        Assert.True(defs.TryGetProperty("Anchor", out _));
        Assert.True(defs.TryGetProperty("SymbolPredicate", out _));
        Assert.True(defs.TryGetProperty("SolutionOperation", out _));
    }

    [Fact]
    public void RoslynQuerySymbolsTool_SchemaContainsCompactDefsAndRequiredProperties()
    {
        var tool = new RoslynQuerySymbolsTool(new ThrowingWorkspaceProvider());

        Assert.Equal("roslyn_query_symbols", tool.Name);
        Assert.NotEmpty(tool.Description);

        var json = JsonSerializer.Serialize(tool.InputSchema);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("object", root.GetProperty("type").GetString());
        var props = root.GetProperty("properties");
        Assert.True(props.TryGetProperty("solutionPath", out _));
        Assert.True(props.TryGetProperty("predicate", out _));
        Assert.True(props.TryGetProperty("maxResults", out _));

        var defs = root.GetProperty("$defs");
        Assert.True(defs.TryGetProperty("SymbolPredicate", out _));
        Assert.True(defs.TryGetProperty("DocumentScope", out _));
        Assert.True(defs.TryGetProperty("IntRange", out _));
    }

    [Fact]
    public async Task RoslynQuerySymbolsTool_ExecutesAgainstWarmSession()
    {
        using var workspace = CreateInMemoryWorkspace(
            "namespace App; public sealed class OrderService { public void Execute() { } }");
        using var sessionManager = new SolutionSessionManager();
        sessionManager.RegisterInMemorySession("/virtual/App.sln", workspace.CurrentSolution);

        var tool = new RoslynQuerySymbolsTool(sessionManager);
        using var argsDoc = JsonDocument.Parse("""
            {
              "solutionPath": "/virtual/App.sln",
              "predicate": {
                "symbolKindIn": ["Class"],
                "nameMatches": "Order*"
              },
              "maxResults": 10
            }
            """);

        var result = await tool.ExecuteAsync(argsDoc.RootElement);
        Assert.False(result.IsError);

        using var responseDoc = JsonDocument.Parse(result.Content[0].Text!);
        Assert.True(responseDoc.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal(1, responseDoc.RootElement.GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task RoslynTransactionTool_ExecutesPreviewAndCommitAgainstWarmSession()
    {
        using var workspace = CreateInMemoryWorkspace(
            "namespace App;\npublic class Calculator\n{\n    public int Add(int a, int b) => a + b;\n}\n");
        using var sessionManager = new SolutionSessionManager();
        sessionManager.RegisterInMemorySession("/virtual/App.sln", workspace.CurrentSolution);

        var tool = new RoslynTransactionTool(sessionManager, persistToDisk: false);
        using var argsDoc = JsonDocument.Parse("""
            {
              "solutionPath": "/virtual/App.sln",
              "previewDiffOnly": true,
              "rollbackOnCompilationErrors": true,
              "operations": [
                {
                  "op": "rename_symbol",
                  "target": {
                    "anchor": {
                      "kind": "SymbolPath",
                      "path": "App.Calculator.Add"
                    }
                  },
                  "newName": "Sum"
                }
              ]
            }
            """);

        var result = await tool.ExecuteAsync(argsDoc.RootElement);
        Assert.False(result.IsError);

        using var responseDoc = JsonDocument.Parse(result.Content[0].Text!);
        Assert.True(responseDoc.RootElement.GetProperty("success").GetBoolean());
        Assert.True(responseDoc.RootElement.GetProperty("preview").GetBoolean());
        Assert.Equal(1, responseDoc.RootElement.GetProperty("fileDiffs").GetArrayLength());
    }

    private static AdhocWorkspace CreateInMemoryWorkspace(string sourceCode)
    {
        var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();
        var refs = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrWhiteSpace(a.Location))
            .Select(a => MetadataReference.CreateFromFile(a.Location))
            .Cast<MetadataReference>()
            .ToList();

        var projectInfo = ProjectInfo.Create(
            projectId,
            VersionStamp.Create(),
            "App",
            "App",
            LanguageNames.CSharp,
            compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            metadataReferences: refs);

        workspace.AddProject(projectInfo);
        workspace.AddDocument(DocumentInfo.Create(
            DocumentId.CreateNewId(projectId),
            "Service.cs",
            loader: TextLoader.From(TextAndVersion.Create(SourceText.From(sourceCode), VersionStamp.Create())),
            filePath: "/virtual/Service.cs"));

        return workspace;
    }
}

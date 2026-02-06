using System.Text.Json;
using RoslynMcp.Server.Tools;
using RoslynMcp.Server.Transport;
using Xunit;

namespace RoslynMcp.Server.Tests.Transport;

public class ToolRegistryTests
{
    [Fact]
    public void Register_AddsToolHandler()
    {
        // Arrange
        var registry = new ToolRegistry();
        var handler = new TestToolHandler("test_tool");

        // Act
        registry.Register(handler);
        var retrieved = registry.GetHandler("test_tool");

        // Assert
        Assert.Same(handler, retrieved);
    }

    [Fact]
    public void GetHandler_ReturnsNullForUnknownTool()
    {
        // Arrange
        var registry = new ToolRegistry();

        // Act
        var handler = registry.GetHandler("unknown");

        // Assert
        Assert.Null(handler);
    }

    [Fact]
    public void GetHandler_IsCaseInsensitive()
    {
        // Arrange
        var registry = new ToolRegistry();
        var handler = new TestToolHandler("test_tool");
        registry.Register(handler);

        // Act
        var retrieved = registry.GetHandler("TEST_TOOL");

        // Assert
        Assert.Same(handler, retrieved);
    }

    [Fact]
    public void GetToolDefinitions_ReturnsAllTools()
    {
        // Arrange
        var registry = new ToolRegistry();
        registry.Register(new TestToolHandler("tool1"));
        registry.Register(new TestToolHandler("tool2"));

        // Act
        var definitions = registry.GetToolDefinitions();

        // Assert
        Assert.Equal(2, definitions.Count);
        Assert.Contains(definitions, d => d.Name == "tool1");
        Assert.Contains(definitions, d => d.Name == "tool2");
    }

    private sealed class TestToolHandler : IToolHandler
    {
        public string Name { get; }
        public string Description => "Test tool";
        public object InputSchema => new { type = "object" };

        public TestToolHandler(string name) => Name = name;

        public Task<ToolResult> ExecuteAsync(JsonElement? arguments, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(ToolResult.Success("test"));
        }
    }
}

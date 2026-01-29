using RoslynMcp.Server.Transport;

namespace RoslynMcp.Server.Tools;

/// <summary>
/// Registry for MCP tool handlers.
/// </summary>
public sealed class ToolRegistry
{
    private readonly Dictionary<string, IToolHandler> _handlers = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Registers a tool handler.
    /// </summary>
    /// <param name="handler">Handler to register.</param>
    public void Register(IToolHandler handler)
    {
        _handlers[handler.Name] = handler;
    }

    /// <summary>
    /// Gets a handler by tool name.
    /// </summary>
    /// <param name="name">Tool name.</param>
    /// <returns>Handler if found, null otherwise.</returns>
    public IToolHandler? GetHandler(string name)
    {
        return _handlers.TryGetValue(name, out var handler) ? handler : null;
    }

    /// <summary>
    /// Gets all registered tool definitions.
    /// </summary>
    public IReadOnlyList<ToolDefinition> GetToolDefinitions()
    {
        return _handlers.Values.Select(h => new ToolDefinition
        {
            Name = h.Name,
            Description = h.Description,
            InputSchema = h.InputSchema
        }).ToList();
    }

    /// <summary>
    /// Gets all registered tool names.
    /// </summary>
    public IReadOnlyList<string> GetToolNames()
    {
        return _handlers.Keys.ToList();
    }
}

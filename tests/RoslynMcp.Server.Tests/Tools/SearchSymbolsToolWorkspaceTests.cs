using System.Text.Json;
using RoslynMcp.Core.Workspace;
using RoslynMcp.Server.Tools;
using RoslynMcp.Server.Transport;
using Xunit;

namespace RoslynMcp.Server.Tests.Tools;

/// <summary>
/// Workspace-backed tests for <see cref="SearchSymbolsTool"/> that prove JSON arguments
/// are mapped onto <c>SearchSymbolsParams</c> and actually change the returned symbol set.
/// </summary>
public class SearchSymbolsToolWorkspaceTests
{
    private const string Source = """
        namespace Demo;

        public class Widget
        {
            public static int WidgetCount;
            public int WidgetSize;
            public static void WidgetReset() { }
            public void WidgetDraw() { }
        }

        public abstract class WidgetBase
        {
            public abstract void WidgetPaint();
            public virtual void WidgetLayout() { }
        }
        """;

    [Theory]
    [InlineData(true, new[] { "WidgetCount", "WidgetReset" }, new[] { "WidgetSize", "WidgetDraw" })]
    [InlineData(false, new[] { "WidgetSize", "WidgetDraw" }, new[] { "WidgetCount", "WidgetReset" })]
    public async Task ExecuteAsync_StaticFilter_IsMappedToOperation(bool staticFilter, string[] expected, string[] excluded)
    {
        var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpSearchSymbolsTool_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var projectPath = Path.Combine(directory, "TestApp.csproj");
            await File.WriteAllTextAsync(projectPath, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net9.0</TargetFramework>
                    <Nullable>enable</Nullable>
                  </PropertyGroup>
                </Project>
                """);
            await File.WriteAllTextAsync(Path.Combine(directory, "Widget.cs"), Source);

            var tool = new SearchSymbolsTool(new MSBuildWorkspaceProvider());
            var args = JsonSerializer.SerializeToElement(new
            {
                solutionPath = projectPath,
                query = "Widget",
                staticFilter
            });

            var result = await tool.ExecuteAsync(args);
            var text = result.Content.FirstOrDefault()?.Text ?? string.Empty;
            Assert.False(result.IsError, text);

            var names = CollectSymbolNames(JsonDocument.Parse(text).RootElement);
            foreach (var name in expected)
                Assert.Contains(name, names);
            foreach (var name in excluded)
                Assert.DoesNotContain(name, names);
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { /* best effort */ }
        }
    }

    [Theory]
    [InlineData(true, new[] { "WidgetBase", "WidgetPaint" }, new[] { "Widget", "WidgetLayout", "WidgetDraw", "WidgetCount" })]
    [InlineData(false, new[] { "Widget", "WidgetLayout", "WidgetDraw", "WidgetCount" }, new[] { "WidgetBase", "WidgetPaint" })]
    public async Task ExecuteAsync_AbstractFilter_IsMappedToOperation(bool abstractFilter, string[] expected, string[] excluded)
    {
        var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpSearchSymbolsTool_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var projectPath = Path.Combine(directory, "TestApp.csproj");
            await File.WriteAllTextAsync(projectPath, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net9.0</TargetFramework>
                    <Nullable>enable</Nullable>
                  </PropertyGroup>
                </Project>
                """);
            await File.WriteAllTextAsync(Path.Combine(directory, "Widget.cs"), Source);

            var tool = new SearchSymbolsTool(new MSBuildWorkspaceProvider());
            var args = JsonSerializer.SerializeToElement(new
            {
                solutionPath = projectPath,
                query = "Widget",
                abstractFilter
            });

            var result = await tool.ExecuteAsync(args);
            var text = result.Content.FirstOrDefault()?.Text ?? string.Empty;
            Assert.False(result.IsError, text);

            var names = CollectSymbolNames(JsonDocument.Parse(text).RootElement);
            foreach (var name in expected)
                Assert.Contains(name, names);
            foreach (var name in excluded)
                Assert.DoesNotContain(name, names);
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { /* best effort */ }
        }
    }

    private static HashSet<string> CollectSymbolNames(JsonElement element)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        Walk(element);
        return names;

        void Walk(JsonElement e)
        {
            switch (e.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var property in e.EnumerateObject())
                    {
                        if (property.NameEquals("symbols") && property.Value.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var entry in property.Value.EnumerateArray())
                            {
                                if (entry.TryGetProperty("name", out var n) && n.GetString() is { } s)
                                    names.Add(s);
                            }
                        }
                        else
                        {
                            Walk(property.Value);
                        }
                    }
                    break;
                case JsonValueKind.Array:
                    foreach (var item in e.EnumerateArray())
                        Walk(item);
                    break;
            }
        }
    }
}

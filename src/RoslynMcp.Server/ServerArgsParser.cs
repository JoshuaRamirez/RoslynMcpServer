using RoslynMcp.Core.Workspace;

namespace RoslynMcp.Server;

internal sealed class ServerStartupOptions
{
    public WorkspaceLoadOptions WorkspaceLoadOptions { get; init; } = WorkspaceLoadOptions.Default;
}

internal static class ServerArgsParser
{
    public static ServerStartupOptions Parse(string[] args)
    {
        var skipUnrecognizedProjects = false;

        foreach (var arg in args)
        {
            if (arg.Equals("--skip-unrecognized-projects", StringComparison.OrdinalIgnoreCase))
            {
                skipUnrecognizedProjects = true;
                continue;
            }

            throw new ArgumentException($"Unknown startup argument: {arg}", nameof(args));
        }

        return new ServerStartupOptions
        {
            WorkspaceLoadOptions = new WorkspaceLoadOptions
            {
                SkipUnrecognizedProjects = skipUnrecognizedProjects
            }
        };
    }
}

using Microsoft.CodeAnalysis;
using RoslynMcp.Core.FileSystem;

namespace RoslynMcp.Core.Refactoring.Utilities;

/// <summary>
/// Shared type-walk de-dupe key used by Generate AllFiles paths that collapse
/// rematches and partials within one project (3-overload form with file-local
/// identity). Same body as the ImplementAbstract / ImplementInterface /
/// GenerateToString / GenerateOverrides / GenerateEqualsHashCode copies.
/// </summary>
internal static class TypeWalkKeyHelpers
{
    /// <summary>
    /// De-dupes types within one project across rematches and partials.
    /// Includes <paramref name="projectId"/> so two projects that both
    /// declare <c>TestApp.Widget</c> are not collapsed onto one walk key.
    /// File-local types (<see cref="INamedTypeSymbol.IsFileLocal"/>) also
    /// include a file-local marker and declaring file so two
    /// <c>file class Worker</c> hosts that share
    /// <see cref="SymbolDisplayFormat.FullyQualifiedFormat"/> are not
    /// skipped as if they were one partial. Genuine partials
    /// (<c>IsFileLocal</c> false, multiple declaring syntax refs) still
    /// collapse to one walk.
    /// </summary>
    internal static string TypeWalkKey(ProjectId projectId, INamedTypeSymbol typeSymbol)
    {
        var fqn = typeSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        if (!typeSymbol.IsFileLocal)
            return TypeWalkKey(projectId, fqn);

        var declaringFile = typeSymbol.DeclaringSyntaxReferences
            .Select(reference => reference.SyntaxTree.FilePath)
            .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path));

        return TypeWalkKey(projectId, fqn, declaringFile);
    }

    /// <summary>
    /// Same key shape as a non-file-local
    /// <see cref="TypeWalkKey(ProjectId, INamedTypeSymbol)"/> for tests
    /// that do not have a compilation symbol.
    /// </summary>
    internal static string TypeWalkKey(ProjectId projectId, string fullyQualifiedTypeName) =>
        $"{projectId.Id:D}\0{fullyQualifiedTypeName}";

    /// <summary>
    /// File-local walk key: project id + FQN plus a <c>file</c> marker and
    /// declaring path so same-named file-local types in different files
    /// stay distinct. Ordinary (non-file-local) callers should use
    /// <see cref="TypeWalkKey(ProjectId, string)"/>.
    /// </summary>
    internal static string TypeWalkKey(ProjectId projectId, string fullyQualifiedTypeName, string? fileLocalDeclaringPath)
    {
        var key = TypeWalkKey(projectId, fullyQualifiedTypeName);
        if (string.IsNullOrWhiteSpace(fileLocalDeclaringPath))
            return $"{key}\0file";

        string normalized;
        try
        {
            normalized = PathResolver.NormalizePath(fileLocalDeclaringPath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            normalized = fileLocalDeclaringPath;
        }

        return $"{key}\0file\0{normalized}";
    }
}

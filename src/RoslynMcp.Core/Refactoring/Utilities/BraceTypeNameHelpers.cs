using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynMcp.Contracts.Errors;

namespace RoslynMcp.Core.Refactoring.Utilities;

/// <summary>
/// Shared type-name / qualified-path / scope-normalize / param-validation
/// helpers used by add_braces / remove_braces. Same bodies as the two
/// identical Convert brace copies. Named BraceTypeNameHelpers (not TypeDeclarationHelpers)
/// because <see cref="TypeDeclarationHelpers.FindTypeDeclaration"/> already
/// exists with a different signature (nullable + line/column).
/// </summary>
internal static class BraceTypeNameHelpers
{
    private const string ScopeStatement = "statement";
    private const string ScopeType = "type";

    /// <summary>
    /// Normalizes brace scope to statement, file, or type (default statement).
    /// Same body as the two Convert brace copies.
    /// </summary>
    internal static string NormalizeScope(string? scope)
    {
        if (string.IsNullOrWhiteSpace(scope))
            return "statement";

        var normalized = scope.Trim().ToLowerInvariant();
        if (normalized is "statement" or "file" or "type")
            return normalized;

        throw new RefactoringException(
            ErrorCodes.MissingRequiredParam,
            "scope must be statement, file, or type.");
    }

    /// <summary>
    /// Validates add_braces / remove_braces parameters. Same body as the two
    /// prior identical <c>AddBracesOperation.Validate</c> /
    /// <c>RemoveBracesOperation.Validate</c> copies: allFiles rejects an
    /// explicit statement scope and type scope, checks line/column &gt;= 1 and
    /// optional sourceFile path shape only; single-file requires sourceFile,
    /// line for statement scope, typeName for type scope, and an existing file.
    /// </summary>
    /// <exception cref="RefactoringException">
    /// <see cref="ErrorCodes.MissingRequiredParam"/>,
    /// <see cref="ErrorCodes.InvalidLineNumber"/>,
    /// <see cref="ErrorCodes.InvalidColumnNumber"/>, or
    /// <see cref="ErrorCodes.SourceFileNotFound"/> (plus path-shape errors from
    /// <see cref="SourceFilePathHelpers.ValidateSourceFilePath"/>).
    /// </exception>
    internal static void ValidateBraceParams(
        string? sourceFile,
        bool allFiles,
        int? line,
        int? column,
        string? scope,
        string? typeName)
    {
        var normalizedScope = NormalizeScope(scope);

        // AllFiles cannot be combined with a location/name scope. statement
        // and type stay single-file only. Omitted scope (null/whitespace) is
        // not an explicit statement pick — AllFiles treats that as a
        // file-scope walk so CLI --all-files and sibling-style AllFiles=true
        // succeed. Default single-file scope remains statement via
        // NormalizeScope.
        if (allFiles && !string.IsNullOrWhiteSpace(scope) && normalizedScope == ScopeStatement)
        {
            throw new RefactoringException(
                ErrorCodes.MissingRequiredParam,
                "allFiles cannot be combined with scope=statement.");
        }

        if (allFiles && normalizedScope == ScopeType)
        {
            throw new RefactoringException(
                ErrorCodes.MissingRequiredParam,
                "allFiles cannot be combined with scope=type.");
        }

        if (allFiles)
        {
            if (line.HasValue && line.Value < 1)
                throw new RefactoringException(ErrorCodes.InvalidLineNumber, "line must be >= 1.");

            if (column.HasValue && column.Value < 1)
                throw new RefactoringException(ErrorCodes.InvalidColumnNumber, "column must be >= 1.");

            // When processing all files, sourceFile is optional; validate path
            // shape only (filter throws SourceFileNotFound / SourceNotInWorkspace).
            if (!string.IsNullOrWhiteSpace(sourceFile))
                SourceFilePathHelpers.ValidateSourceFilePath(sourceFile!);

            return;
        }

        if (string.IsNullOrWhiteSpace(sourceFile))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "sourceFile is required when allFiles is false.");

        SourceFilePathHelpers.ValidateSourceFilePath(sourceFile);

        if (normalizedScope == ScopeStatement)
        {
            if (!line.HasValue)
                throw new RefactoringException(ErrorCodes.InvalidLineNumber, "line is required when scope is statement.");

            if (line.Value < 1)
                throw new RefactoringException(ErrorCodes.InvalidLineNumber, "line must be >= 1.");
        }
        else if (line.HasValue && line.Value < 1)
        {
            throw new RefactoringException(ErrorCodes.InvalidLineNumber, "line must be >= 1.");
        }

        if (column.HasValue && column.Value < 1)
            throw new RefactoringException(ErrorCodes.InvalidColumnNumber, "column must be >= 1.");

        if (normalizedScope == ScopeType && string.IsNullOrWhiteSpace(typeName))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "typeName is required when scope is type.");

        if (!File.Exists(sourceFile))
            throw new RefactoringException(ErrorCodes.SourceFileNotFound, $"Source file not found: {sourceFile}");
    }

    /// <summary>
    /// Finds the unique <see cref="TypeDeclarationSyntax"/> under
    /// <paramref name="root"/> whose simple or qualified name matches
    /// <paramref name="typeName"/>. Same body as the two Convert brace copies.
    /// </summary>
    /// <exception cref="RefactoringException">
    /// When zero matches (<see cref="ErrorCodes.TypeNotFound"/>) or more than
    /// one (<see cref="ErrorCodes.SymbolAmbiguous"/>).
    /// </exception>
    internal static TypeDeclarationSyntax FindTypeDeclaration(SyntaxNode root, string typeName)
    {
        var matches = root.DescendantNodes()
            .OfType<TypeDeclarationSyntax>()
            .Where(type => TypeNameMatches(type, typeName))
            .ToList();

        if (matches.Count == 0)
        {
            throw new RefactoringException(
                ErrorCodes.TypeNotFound,
                $"Type '{typeName}' not found.");
        }

        if (matches.Count > 1)
        {
            throw new RefactoringException(
                ErrorCodes.SymbolAmbiguous,
                $"Multiple types named '{typeName}' found. Provide a namespace-qualified typeName to disambiguate.");
        }

        return matches[0];
    }

    /// <summary>
    /// True when <paramref name="type"/>'s qualified name, simple identifier,
    /// or qualified suffix equals <paramref name="typeName"/>. Same body as
    /// the two Convert brace copies.
    /// </summary>
    internal static bool TypeNameMatches(TypeDeclarationSyntax type, string typeName)
    {
        var qualified = GetQualifiedTypeName(type);
        if (qualified.Equals(typeName, StringComparison.Ordinal))
            return true;

        if (type.Identifier.Text.Equals(typeName, StringComparison.Ordinal))
            return true;

        return qualified.EndsWith("." + typeName, StringComparison.Ordinal);
    }

    /// <summary>
    /// Builds a dot-joined namespace + nested-type path for
    /// <paramref name="type"/>. Same body as the two Convert brace copies.
    /// </summary>
    internal static string GetQualifiedTypeName(TypeDeclarationSyntax type)
    {
        var parts = new List<string>();
        for (var current = (SyntaxNode)type; current != null; current = current.Parent)
        {
            switch (current)
            {
                case TypeDeclarationSyntax declared:
                    parts.Insert(0, declared.Identifier.Text);
                    break;
                case BaseNamespaceDeclarationSyntax ns:
                    parts.InsertRange(0, ns.Name.ToString().Split('.'));
                    break;
            }
        }

        return string.Join(".", parts);
    }
}

namespace RoslynMcp.Contracts.Errors;

/// <summary>
/// C# compiler and analyzer diagnostic IDs used for code analysis.
/// </summary>
public static class DiagnosticIds
{
    // ============================================
    // Missing/Unresolved Type Diagnostics
    // ============================================

    /// <summary>
    /// CS0246: The type or namespace name could not be found.
    /// Indicates a missing using directive or assembly reference.
    /// </summary>
    public const string TypeOrNamespaceNotFound = "CS0246";

    /// <summary>
    /// CS0103: The name does not exist in the current context.
    /// Indicates an undeclared variable or type name.
    /// </summary>
    public const string NameDoesNotExist = "CS0103";

    /// <summary>
    /// CS0234: The type or namespace does not exist in the namespace.
    /// Indicates a missing nested namespace or type.
    /// </summary>
    public const string TypeOrNamespaceDoesNotExistInNamespace = "CS0234";

    // ============================================
    // Unused Code Diagnostics
    // ============================================

    /// <summary>
    /// CS8019: Unnecessary using directive.
    /// Compiler diagnostic for unused imports.
    /// </summary>
    public const string UnnecessaryUsing = "CS8019";

    /// <summary>
    /// IDE0005: Using directive is unnecessary.
    /// IDE analyzer diagnostic for unused imports.
    /// </summary>
    public const string UnnecessaryUsingIde = "IDE0005";
}

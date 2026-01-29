namespace RoslynMcp.Contracts.Enums;

/// <summary>
/// Represents symbol types supported by the refactoring operations.
/// </summary>
public enum SymbolKind
{
    // Type declarations
    /// <summary>Class declaration.</summary>
    Class,

    /// <summary>Struct declaration.</summary>
    Struct,

    /// <summary>Interface declaration.</summary>
    Interface,

    /// <summary>Enum declaration.</summary>
    Enum,

    /// <summary>Record declaration.</summary>
    Record,

    /// <summary>Delegate declaration.</summary>
    Delegate,

    // Members
    /// <summary>Method declaration.</summary>
    Method,

    /// <summary>Property declaration.</summary>
    Property,

    /// <summary>Field declaration.</summary>
    Field,

    /// <summary>Event declaration.</summary>
    Event,

    // Other
    /// <summary>Local variable.</summary>
    Local,

    /// <summary>Parameter.</summary>
    Parameter,

    /// <summary>Namespace.</summary>
    Namespace
}

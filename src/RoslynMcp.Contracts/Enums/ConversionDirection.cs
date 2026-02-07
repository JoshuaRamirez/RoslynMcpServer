namespace RoslynMcp.Contracts.Enums;

/// <summary>
/// Direction for expression body / property conversions.
/// </summary>
public enum ConversionDirection
{
    /// <summary>Convert block body to expression body.</summary>
    ToExpressionBody,

    /// <summary>Convert expression body to block body.</summary>
    ToBlockBody,

    /// <summary>Convert full property to auto-property.</summary>
    ToAutoProperty,

    /// <summary>Convert auto-property to full property with backing field.</summary>
    ToFullProperty
}

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynMcp.Core.Query.Utilities;

/// <summary>
/// Calculates code metrics: cyclomatic complexity, LOC, maintainability index,
/// class coupling, and depth of inheritance.
/// </summary>
public static class MetricsCalculator
{
    /// <summary>
    /// Calculates cyclomatic complexity for a syntax node.
    /// Counts decision points: if, while, for, foreach, case, catch, &amp;&amp;, ||, ??, ?:
    /// </summary>
    public static int CalculateCyclomaticComplexity(SyntaxNode node)
    {
        // Start with 1 for the method/type itself
        var complexity = 1;

        foreach (var descendant in node.DescendantNodesAndTokens())
        {
            if (descendant.IsNode)
            {
                complexity += descendant.AsNode() switch
                {
                    IfStatementSyntax => 1,
                    WhileStatementSyntax => 1,
                    ForStatementSyntax => 1,
                    ForEachStatementSyntax => 1,
                    CaseSwitchLabelSyntax => 1,
                    CasePatternSwitchLabelSyntax => 1,
                    SwitchExpressionArmSyntax => 1,
                    CatchClauseSyntax => 1,
                    ConditionalExpressionSyntax => 1,
                    _ => 0
                };
            }
            else if (descendant.IsToken)
            {
                var kind = descendant.AsToken().Kind();
                if (kind == SyntaxKind.AmpersandAmpersandToken ||
                    kind == SyntaxKind.BarBarToken ||
                    kind == SyntaxKind.QuestionQuestionToken)
                {
                    complexity++;
                }
            }
        }

        return complexity;
    }

    /// <summary>
    /// Counts logical lines of code (excluding blank lines and comment-only lines).
    /// </summary>
    public static int CalculateLinesOfCode(SyntaxNode node)
    {
        var text = node.GetText();
        var loc = 0;

        foreach (var line in text.Lines)
        {
            var lineText = line.ToString().Trim();
            if (string.IsNullOrEmpty(lineText))
                continue;
            if (lineText.StartsWith("//"))
                continue;

            loc++;
        }

        return loc;
    }

    /// <summary>
    /// Calculates maintainability index using the Visual Studio formula.
    /// MI = MAX(0, (171 - 5.2 * ln(HV) - 0.23 * CC - 16.2 * ln(LOC)) * 100 / 171)
    /// Simplified: uses LOC as a proxy for Halstead Volume.
    /// </summary>
    public static int CalculateMaintainabilityIndex(int cyclomaticComplexity, int linesOfCode)
    {
        if (linesOfCode <= 0)
            return 100;

        var logLoc = Math.Log(linesOfCode);
        // Simplified Halstead volume approximation using LOC
        var logHv = Math.Log(linesOfCode * 2.0);

        var mi = (171.0 - 5.2 * logHv - 0.23 * cyclomaticComplexity - 16.2 * logLoc) * 100.0 / 171.0;
        return Math.Max(0, (int)Math.Round(mi));
    }

    /// <summary>
    /// Counts distinct types referenced in the given node (class coupling).
    /// </summary>
    public static int CalculateClassCoupling(SemanticModel semanticModel, SyntaxNode node)
    {
        var referencedTypes = new HashSet<string>();

        foreach (var identifier in node.DescendantNodes().OfType<IdentifierNameSyntax>())
        {
            var symbolInfo = semanticModel.GetSymbolInfo(identifier);
            var symbol = symbolInfo.Symbol;
            if (symbol == null) continue;

            ITypeSymbol? typeSymbol = symbol switch
            {
                ITypeSymbol t => t,
                IMethodSymbol m => m.ContainingType,
                IPropertySymbol p => p.Type,
                IFieldSymbol f => f.Type,
                ILocalSymbol l => l.Type,
                IParameterSymbol par => par.Type,
                _ => null
            };

            if (typeSymbol != null &&
                typeSymbol.SpecialType == SpecialType.None &&
                !typeSymbol.ToDisplayString().StartsWith("System.") &&
                typeSymbol.TypeKind != TypeKind.TypeParameter)
            {
                referencedTypes.Add(typeSymbol.ToDisplayString());
            }
        }

        return referencedTypes.Count;
    }

    /// <summary>
    /// Calculates depth of inheritance for a type symbol.
    /// </summary>
    public static int CalculateDepthOfInheritance(INamedTypeSymbol? typeSymbol)
    {
        if (typeSymbol == null) return 0;

        var depth = 0;
        var current = typeSymbol.BaseType;
        while (current != null && current.SpecialType != SpecialType.System_Object)
        {
            depth++;
            current = current.BaseType;
        }

        return depth;
    }
}

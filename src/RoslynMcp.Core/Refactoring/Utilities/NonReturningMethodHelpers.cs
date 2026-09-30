using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynMcp.Core.Refactoring.Utilities;

/// <summary>
/// Shared void / async non-generic Task-or-ValueTask predicates used by
/// <c>ConvertToBlockBodyOperation</c> and <c>ConvertExpressionBodyOperation</c>
/// when deciding whether an expression-bodied member should emit a bare
/// expression statement vs a <c>return</c>. Same bodies as the prior private
/// copies.
/// </summary>
internal static class NonReturningMethodHelpers
{
    /// <summary>
    /// True when <paramref name="method"/> is void or async non-generic
    /// <c>Task</c>/<c>ValueTask</c> (no value to return). Convenience entry
    /// matching the prior ConvertExpressionBody private copy; delegates to
    /// <see cref="IsNonReturning(TypeSyntax, SyntaxTokenList, IMethodSymbol?)"/>.
    /// </summary>
    internal static bool IsNonReturning(MethodDeclarationSyntax method, SemanticModel? model) =>
        IsNonReturning(
            method.ReturnType,
            method.Modifiers,
            model?.GetDeclaredSymbol(method) as IMethodSymbol);

    /// <summary>
    /// True when the member is void or async non-generic <c>Task</c>/<c>ValueTask</c>
    /// (no value to return). Prefers <paramref name="symbol"/> when its return
    /// type is non-error; otherwise falls back to syntax. Same body as the prior
    /// ConvertToBlockBody private copy.
    /// </summary>
    internal static bool IsNonReturning(
        TypeSyntax returnType,
        SyntaxTokenList modifiers,
        IMethodSymbol? symbol = null)
    {
        if (symbol is { ReturnType.TypeKind: not TypeKind.Error })
        {
            if (symbol.ReturnsVoid)
                return true;

            return modifiers.Any(SyntaxKind.AsyncKeyword) &&
                   IsNonGenericTaskLikeSymbol(symbol.ReturnType);
        }

        return IsVoidReturn(returnType) ||
               (modifiers.Any(SyntaxKind.AsyncKeyword) && IsNonGenericTaskLike(returnType));
    }

    /// <summary>
    /// True for the <c>void</c> predefined type. Same body as the prior private copies.
    /// </summary>
    internal static bool IsVoidReturn(TypeSyntax returnType) =>
        returnType is PredefinedTypeSyntax predefined && predefined.Keyword.IsKind(SyntaxKind.VoidKeyword);

    /// <summary>
    /// True for non-generic <c>System.Threading.Tasks.Task</c> /
    /// <c>ValueTask</c>. Same body as the prior private copies.
    /// </summary>
    internal static bool IsNonGenericTaskLikeSymbol(ITypeSymbol type)
    {
        if (type is INamedTypeSymbol { IsGenericType: true })
            return false;

        return type.Name is "Task" or "ValueTask" &&
               type.ContainingNamespace?.ToDisplayString() == "System.Threading.Tasks";
    }

    /// <summary>
    /// Syntax-only Task/ValueTask name check (unwraps qualified / alias names;
    /// rejects generics). Same body as the prior private copies.
    /// </summary>
    internal static bool IsNonGenericTaskLike(TypeSyntax returnType) => returnType switch
    {
        GenericNameSyntax => false,
        QualifiedNameSyntax qualified => IsNonGenericTaskLike(qualified.Right),
        AliasQualifiedNameSyntax alias => IsNonGenericTaskLike(alias.Name),
        IdentifierNameSyntax identifier => IsTaskLikeName(identifier.Identifier.Text),
        _ => false
    };

    /// <summary>
    /// True when <paramref name="name"/> is <c>Task</c> or <c>ValueTask</c>.
    /// Same body as the prior ConvertToBlockBody private copy.
    /// </summary>
    internal static bool IsTaskLikeName(string name) => name is "Task" or "ValueTask";

    /// <summary>
    /// True for BCL <c>System.Threading.Tasks.Task</c> / <c>ValueTask</c>
    /// (non-generic or single type argument). Same body as the prior
    /// ConvertToBlockBody private copy used by
    /// <c>EnsureAsyncReturnTypeSafeToConvert</c>.
    /// </summary>
    internal static bool IsBclTaskLikeSymbol(ITypeSymbol type)
    {
        if (type is not INamedTypeSymbol { Name: "Task" or "ValueTask" } named)
            return false;
        if (named.ContainingNamespace?.ToDisplayString() != "System.Threading.Tasks")
            return false;
        return !named.IsGenericType || named.TypeArguments.Length == 1;
    }
}

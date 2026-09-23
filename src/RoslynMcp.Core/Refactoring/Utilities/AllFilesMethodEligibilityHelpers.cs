using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynMcp.Core.Refactoring.Utilities;

/// <summary>
/// Shared allFiles method-eligibility gates used by AddParameter /
/// RemoveParameter / ReorderParameters (and ImplementsAnyInterfaceMember
/// also by ChangeSignature). Same bodies as the identical copies on those
/// operations. Named AllFilesMethodEligibilityHelpers because this cluster
/// is the IsEligibleForAllFiles + attribute / interface gates used together
/// by those signature operations.
/// </summary>
internal static class AllFilesMethodEligibilityHelpers
{
    /// <summary>
    /// True when <paramref name="method"/> / <paramref name="methodDecl"/>
    /// may be rewritten under allFiles: not extension / partial / extern /
    /// override / interface member / explicit interface implementation, and
    /// not decorated with <c>UnmanagedCallersOnly</c> / <c>ModuleInitializer</c>
    /// so allFiles cannot rewrite a signature whose metadata or sibling
    /// contract cannot be updated (AddParameter / RemoveParameter /
    /// ReorderParameters / Codex).
    /// </summary>
    internal static bool IsEligibleForAllFiles(IMethodSymbol method, MethodDeclarationSyntax methodDecl)
    {
        if (method.IsExtensionMethod)
            return false;

        if (method.PartialDefinitionPart != null || method.PartialImplementationPart != null)
            return false;

        if (method.IsExtern)
            return false;

        if (HasUnmanagedCallersOnlyAttribute(method, methodDecl))
            return false;

        if (HasModuleInitializerAttribute(method, methodDecl))
            return false;

        if (method.IsOverride)
            return false;

        if (method.ContainingType?.TypeKind == TypeKind.Interface)
            return false;

        if (!method.ExplicitInterfaceImplementations.IsDefaultOrEmpty &&
            method.ExplicitInterfaceImplementations.Length > 0)
        {
            return false;
        }

        if (ImplementsAnyInterfaceMember(method))
            return false;

        return true;
    }

    /// <summary>
    /// True when <paramref name="method"/> is the implementation of any
    /// interface member on its containing type. Same body as the
    /// AddParameter / RemoveParameter / ReorderParameters / ChangeSignature
    /// copies.
    /// </summary>
    internal static bool ImplementsAnyInterfaceMember(IMethodSymbol method)
    {
        var containingType = method.ContainingType;
        if (containingType == null)
            return false;

        foreach (var iface in containingType.AllInterfaces)
        {
            foreach (var member in iface.GetMembers().OfType<IMethodSymbol>())
            {
                var impl = containingType.FindImplementationForInterfaceMember(member) as IMethodSymbol;
                if (impl != null && SymbolEqualityComparer.Default.Equals(impl, method))
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True when <paramref name="method"/> / <paramref name="methodDecl"/>
    /// carries <c>ModuleInitializer</c> (symbol attributes or syntactic
    /// attribute lists). Same body as the AddParameter / RemoveParameter /
    /// ReorderParameters copies.
    /// </summary>
    internal static bool HasModuleInitializerAttribute(IMethodSymbol method, MethodDeclarationSyntax methodDecl)
    {
        if (method.GetAttributes().Any(attr =>
        {
            var type = attr.AttributeClass;
            if (type == null)
                return false;
            if (type.Name is not ("ModuleInitializerAttribute" or "ModuleInitializer"))
                return false;
            return type.ContainingNamespace?.ToDisplayString() == "System.Runtime.CompilerServices";
        }))
        {
            return true;
        }

        return methodDecl.AttributeLists
            .SelectMany(list => list.Attributes)
            .Any(attr => attr.Name.ToString().Contains("ModuleInitializer", StringComparison.Ordinal));
    }

    /// <summary>
    /// True when <paramref name="method"/> / <paramref name="methodDecl"/>
    /// carries <c>UnmanagedCallersOnly</c> (symbol attributes or syntactic
    /// attribute lists). Same body as the AddParameter / RemoveParameter /
    /// ReorderParameters copies.
    /// </summary>
    internal static bool HasUnmanagedCallersOnlyAttribute(IMethodSymbol method, MethodDeclarationSyntax methodDecl)
    {
        if (method.GetAttributes().Any(attr =>
        {
            var type = attr.AttributeClass;
            if (type == null)
                return false;
            if (type.Name is not ("UnmanagedCallersOnlyAttribute" or "UnmanagedCallersOnly"))
                return false;
            return type.ContainingNamespace?.ToDisplayString() == "System.Runtime.InteropServices";
        }))
        {
            return true;
        }

        return methodDecl.AttributeLists
            .SelectMany(list => list.Attributes)
            .Any(attr => attr.Name.ToString().Contains("UnmanagedCallersOnly", StringComparison.Ordinal));
    }
}

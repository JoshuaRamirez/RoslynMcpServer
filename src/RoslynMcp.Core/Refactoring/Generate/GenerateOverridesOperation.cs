using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynMcp.Contracts.Enums;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.FileSystem;
using RoslynMcp.Core.Refactoring.Base;
using RoslynMcp.Core.Refactoring.Utilities;
using RoslynMcp.Core.Resolution;
using RoslynMcp.Core.Workspace;

namespace RoslynMcp.Core.Refactoring.Generate;

/// <summary>
/// Generates override methods for base class virtual/abstract members.
/// </summary>
public sealed class GenerateOverridesOperation : RefactoringOperationBase<GenerateOverridesParams>
{
    /// <summary>
    /// Creates a new generate overrides operation.
    /// </summary>
    public GenerateOverridesOperation(WorkspaceContext context) : base(context)
    {
    }

    /// <inheritdoc />
    protected override void ValidateParams(GenerateOverridesParams @params)
    {
        if (string.IsNullOrWhiteSpace(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "sourceFile is required.");

        if (string.IsNullOrWhiteSpace(@params.TypeName))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "typeName is required.");

        if (!PathResolver.IsAbsolutePath(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be an absolute path.");

        if (!PathResolver.IsValidCSharpFilePath(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be a .cs file.");

        if (!File.Exists(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.SourceFileNotFound, $"Source file not found: {@params.SourceFile}");
    }

    /// <inheritdoc />
    protected override async Task<RefactoringResult> ExecuteCoreAsync(
        Guid operationId,
        GenerateOverridesParams @params,
        CancellationToken cancellationToken)
    {
        var document = GetDocumentOrThrow(@params.SourceFile);
        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);

        if (root == null || semanticModel == null)
        {
            throw new RefactoringException(ErrorCodes.RoslynError, "Could not parse file.");
        }

        // Find the type declaration
        var typeDeclaration = root.DescendantNodes()
            .OfType<TypeDeclarationSyntax>()
            .FirstOrDefault(t => t.Identifier.Text == @params.TypeName);

        if (typeDeclaration == null)
        {
            throw new RefactoringException(
                ErrorCodes.TypeNotFound,
                $"Type '{@params.TypeName}' not found in file.");
        }

        // Get the type symbol
        var typeSymbol = semanticModel.GetDeclaredSymbol(typeDeclaration, cancellationToken) as INamedTypeSymbol;
        if (typeSymbol == null)
        {
            throw new RefactoringException(ErrorCodes.RoslynError, "Could not resolve type symbol.");
        }

        // Check for sealed class
        if (typeSymbol.IsSealed && typeSymbol.TypeKind != TypeKind.Struct)
        {
            // Sealed classes can still override, just can't be inherited from
        }

        // Get overridable members from base classes
        var overridableMembers = MemberAnalyzer.GetOverridableMembers(typeSymbol).ToList();

        // Add Object methods (ToString, Equals, GetHashCode)
        var objectOverrides = GetObjectMethodsToOverride(typeSymbol);
        overridableMembers.AddRange(objectOverrides);

        // Filter to requested members if specified
        List<ISymbol> membersToOverride;
        if (@params.Members != null && @params.Members.Count > 0)
        {
            var requestedSet = new HashSet<string>(@params.Members, StringComparer.OrdinalIgnoreCase);
            membersToOverride = overridableMembers.Where(m => requestedSet.Contains(m.Name)).ToList();

            // Check for not found
            var foundNames = membersToOverride.Select(m => m.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var notFound = @params.Members.Where(n => !foundNames.Contains(n)).ToList();
            if (notFound.Count > 0)
            {
                throw new RefactoringException(
                    ErrorCodes.OverrideTargetNotFound,
                    $"Members not found or not overridable: {string.Join(", ", notFound)}. " +
                    $"Available: {string.Join(", ", overridableMembers.Select(m => m.Name))}");
            }
        }
        else
        {
            membersToOverride = overridableMembers;
        }

        if (membersToOverride.Count == 0)
        {
            throw new RefactoringException(
                ErrorCodes.NoOverridableMembers,
                "No overridable members found in base classes.");
        }

        // Generate overrides
        var overrides = GenerateOverrideMembers(membersToOverride, @params.CallBase);

        // If preview mode, return without applying
        if (@params.Preview)
        {
            return CreatePreviewResult(operationId, @params, membersToOverride, overrides);
        }

        // Add overrides to type
        var newTypeDeclaration = AddMembers(typeDeclaration, overrides);
        var newRoot = root.ReplaceNode(typeDeclaration, newTypeDeclaration);

        var newDocument = document.WithSyntaxRoot(newRoot);
        var newSolution = newDocument.Project.Solution;

        // Commit changes
        var commitResult = await CommitChangesAsync(newSolution, cancellationToken);

        return RefactoringResult.Succeeded(
            operationId,
            new FileChanges
            {
                FilesModified = commitResult.FilesModified,
                FilesCreated = commitResult.FilesCreated,
                FilesDeleted = commitResult.FilesDeleted
            },
            new Contracts.Models.SymbolInfo
            {
                Name = @params.TypeName,
                FullyQualifiedName = typeSymbol.ToDisplayString(),
                Kind = Contracts.Enums.SymbolKind.Class
            },
            0,
            0);
    }

    private static List<ISymbol> GetObjectMethodsToOverride(INamedTypeSymbol typeSymbol)
    {
        var result = new List<ISymbol>();
        var existingOverrides = typeSymbol.GetMembers()
            .OfType<IMethodSymbol>()
            .Where(m => m.IsOverride)
            .Select(m => m.Name)
            .ToHashSet();

        // Find Object type
        var objectType = typeSymbol.BaseType;
        while (objectType != null && objectType.SpecialType != SpecialType.System_Object)
        {
            objectType = objectType.BaseType;
        }

        if (objectType == null) return result;

        // Get ToString, Equals, GetHashCode from Object
        foreach (var member in objectType.GetMembers())
        {
            if (member is IMethodSymbol method &&
                (method.Name == "ToString" || method.Name == "Equals" || method.Name == "GetHashCode") &&
                method.IsVirtual &&
                !existingOverrides.Contains(method.Name))
            {
                // Skip Equals(object, object) static method
                if (method.Name == "Equals" && method.Parameters.Length != 1)
                    continue;

                result.Add(method);
            }
        }

        return result;
    }

    private static List<MemberDeclarationSyntax> GenerateOverrideMembers(
        List<ISymbol> members,
        bool callBase)
    {
        var overrides = new List<MemberDeclarationSyntax>();

        foreach (var member in members)
        {
            MemberDeclarationSyntax? impl = member switch
            {
                IMethodSymbol method => SyntaxGenerationHelper.CreateMethodStub(
                    method,
                    explicitInterface: false,
                    callBase: callBase && !method.IsAbstract,
                    throwNotImplemented: method.IsAbstract),
                IPropertySymbol property => SyntaxGenerationHelper.CreatePropertyStub(
                    property,
                    explicitInterface: false,
                    throwNotImplemented: property.IsAbstract),
                _ => null
            };

            if (impl != null)
            {
                overrides.Add(impl);
            }
        }

        return overrides;
    }

    private static TypeDeclarationSyntax AddMembers(
        TypeDeclarationSyntax typeDeclaration,
        List<MemberDeclarationSyntax> newMembers)
    {
        var members = typeDeclaration.Members.ToList();

        foreach (var member in newMembers)
        {
            members.Add(member
                .WithLeadingTrivia(SyntaxFactory.CarriageReturnLineFeed, SyntaxFactory.CarriageReturnLineFeed)
                .WithTrailingTrivia(SyntaxFactory.CarriageReturnLineFeed));
        }

        return typeDeclaration.WithMembers(SyntaxFactory.List(members));
    }

    private static RefactoringResult CreatePreviewResult(
        Guid operationId,
        GenerateOverridesParams @params,
        List<ISymbol> members,
        List<MemberDeclarationSyntax> overrides)
    {
        var memberNames = string.Join(", ", members.Select(m => m.Name));
        var overrideCode = string.Join("\n\n",
            overrides.Select(o => o.NormalizeWhitespace().ToFullString()));

        var pendingChanges = new List<PendingChange>
        {
            new()
            {
                File = @params.SourceFile,
                ChangeType = ChangeKind.Modify,
                Description = $"Generate overrides for: {memberNames}",
                BeforeSnippet = $"// End of type '{@params.TypeName}'",
                AfterSnippet = overrideCode
            }
        };

        return RefactoringResult.PreviewResult(operationId, pendingChanges);
    }
}

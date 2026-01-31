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

namespace RoslynMcp.Core.Refactoring.Extract;

/// <summary>
/// Extracts an interface from a class's public members.
/// </summary>
public sealed class ExtractInterfaceOperation : RefactoringOperationBase<ExtractInterfaceParams>
{
    /// <summary>
    /// Creates a new extract interface operation.
    /// </summary>
    public ExtractInterfaceOperation(WorkspaceContext context) : base(context)
    {
    }

    /// <inheritdoc />
    protected override void ValidateParams(ExtractInterfaceParams @params)
    {
        if (string.IsNullOrWhiteSpace(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "sourceFile is required.");

        if (string.IsNullOrWhiteSpace(@params.TypeName))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "typeName is required.");

        if (string.IsNullOrWhiteSpace(@params.InterfaceName))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "interfaceName is required.");

        if (!PathResolver.IsAbsolutePath(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be an absolute path.");

        if (!PathResolver.IsValidCSharpFilePath(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be a .cs file.");

        if (!File.Exists(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.SourceFileNotFound, $"Source file not found: {@params.SourceFile}");

        if (!IsValidIdentifier(@params.InterfaceName))
            throw new RefactoringException(ErrorCodes.InvalidSymbolName, $"Invalid interface name: {@params.InterfaceName}");

        if (@params.TargetFile != null)
        {
            if (!PathResolver.IsAbsolutePath(@params.TargetFile))
                throw new RefactoringException(ErrorCodes.InvalidTargetPath, "targetFile must be an absolute path.");

            if (!PathResolver.IsValidCSharpFilePath(@params.TargetFile))
                throw new RefactoringException(ErrorCodes.InvalidTargetPath, "targetFile must be a .cs file.");
        }
    }

    /// <inheritdoc />
    protected override async Task<RefactoringResult> ExecuteCoreAsync(
        Guid operationId,
        ExtractInterfaceParams @params,
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

        // Check for static class
        if (typeSymbol.IsStatic)
        {
            throw new RefactoringException(
                ErrorCodes.CannotExtractFromStatic,
                "Cannot extract interface from static class.");
        }

        // Check if interface name already exists
        var existingInterface = await TypeResolver.FindTypeByNameAsync(
            $"{typeSymbol.ContainingNamespace}.{@params.InterfaceName}",
            cancellationToken);

        if (existingInterface != null)
        {
            throw new RefactoringException(
                ErrorCodes.InterfaceAlreadyExists,
                $"Interface '{@params.InterfaceName}' already exists.");
        }

        // Get members to extract
        var allExtractable = MemberAnalyzer.GetExtractableMembers(typeSymbol).ToList();
        var membersToExtract = FilterMembers(allExtractable, @params.Members);

        if (membersToExtract.Count == 0)
        {
            throw new RefactoringException(
                ErrorCodes.NoExtractableMembers,
                "No extractable public members found.");
        }

        // Generate interface declaration
        var interfaceDecl = SyntaxGenerationHelper.CreateInterfaceDeclaration(
            @params.InterfaceName,
            membersToExtract);

        // Get namespace
        var namespaceName = typeSymbol.ContainingNamespace?.ToDisplayString();

        // If preview mode, return without applying
        if (@params.Preview)
        {
            return CreatePreviewResult(operationId, @params, membersToExtract, interfaceDecl, namespaceName);
        }

        // Apply changes
        Solution newSolution;
        if (@params.TargetFile != null && @params.TargetFile != @params.SourceFile)
        {
            // Create new file with interface
            newSolution = await CreateInterfaceInNewFileAsync(
                document.Project.Solution,
                document.Project,
                @params.TargetFile,
                interfaceDecl,
                namespaceName,
                root,
                cancellationToken);
        }
        else
        {
            // Add interface to same file
            newSolution = AddInterfaceToSameFile(
                document,
                root,
                typeDeclaration,
                interfaceDecl);
        }

        // Add interface to type's base list if requested
        if (@params.AddInterfaceToType)
        {
            var updatedDoc = newSolution.GetDocument(document.Id);
            if (updatedDoc != null)
            {
                newSolution = await AddInterfaceToBaseListAsync(
                    newSolution,
                    updatedDoc,
                    @params.TypeName,
                    @params.InterfaceName,
                    cancellationToken);
            }
        }

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
                Name = @params.InterfaceName,
                FullyQualifiedName = string.IsNullOrEmpty(namespaceName)
                    ? @params.InterfaceName
                    : $"{namespaceName}.{@params.InterfaceName}",
                Kind = Contracts.Enums.SymbolKind.Interface
            },
            0,
            0);
    }

    private static List<ISymbol> FilterMembers(
        List<ISymbol> allMembers,
        IReadOnlyList<string>? requestedMembers)
    {
        if (requestedMembers == null || requestedMembers.Count == 0)
        {
            return allMembers;
        }

        var requestedSet = new HashSet<string>(requestedMembers);
        var filtered = allMembers.Where(m => requestedSet.Contains(m.Name)).ToList();

        // Validate all requested members were found
        var foundNames = filtered.Select(m => m.Name).ToHashSet();
        var notFound = requestedMembers.Where(n => !foundNames.Contains(n)).ToList();

        if (notFound.Count > 0)
        {
            throw new RefactoringException(
                ErrorCodes.MemberNotFound,
                $"Members not found or not extractable: {string.Join(", ", notFound)}");
        }

        return filtered;
    }

    private async Task<Solution> CreateInterfaceInNewFileAsync(
        Solution solution,
        Project project,
        string targetFile,
        InterfaceDeclarationSyntax interfaceDecl,
        string? namespaceName,
        SyntaxNode sourceRoot,
        CancellationToken cancellationToken)
    {
        // Build compilation unit with usings from source
        var usings = sourceRoot.DescendantNodes()
            .OfType<UsingDirectiveSyntax>()
            .ToList();

        MemberDeclarationSyntax wrappedInterface;
        if (!string.IsNullOrEmpty(namespaceName))
        {
            wrappedInterface = SyntaxFactory.FileScopedNamespaceDeclaration(
                    SyntaxFactory.ParseName(namespaceName))
                .WithMembers(SyntaxFactory.SingletonList<MemberDeclarationSyntax>(interfaceDecl));
        }
        else
        {
            wrappedInterface = interfaceDecl;
        }

        var compilationUnit = SyntaxFactory.CompilationUnit()
            .WithUsings(SyntaxFactory.List(usings))
            .WithMembers(SyntaxFactory.SingletonList(wrappedInterface))
            .NormalizeWhitespace();

        // Create new document
        var newDoc = project.AddDocument(
            Path.GetFileName(targetFile),
            compilationUnit,
            filePath: targetFile);

        return newDoc.Project.Solution;
    }

    private static Solution AddInterfaceToSameFile(
        Document document,
        SyntaxNode root,
        TypeDeclarationSyntax typeDeclaration,
        InterfaceDeclarationSyntax interfaceDecl)
    {
        // Find insertion point - before the class
        var newInterface = interfaceDecl
            .WithLeadingTrivia(SyntaxFactory.CarriageReturnLineFeed)
            .WithTrailingTrivia(SyntaxFactory.CarriageReturnLineFeed, SyntaxFactory.CarriageReturnLineFeed);

        var newRoot = root.InsertNodesBefore(typeDeclaration, new[] { newInterface });
        var newDoc = document.WithSyntaxRoot(newRoot);

        return newDoc.Project.Solution;
    }

    private static async Task<Solution> AddInterfaceToBaseListAsync(
        Solution solution,
        Document document,
        string typeName,
        string interfaceName,
        CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken);
        if (root == null) return solution;

        var typeDeclaration = root.DescendantNodes()
            .OfType<TypeDeclarationSyntax>()
            .FirstOrDefault(t => t.Identifier.Text == typeName);

        if (typeDeclaration == null) return solution;

        var baseType = SyntaxFactory.SimpleBaseType(SyntaxFactory.ParseTypeName(interfaceName));

        TypeDeclarationSyntax newTypeDeclaration;
        if (typeDeclaration.BaseList == null)
        {
            newTypeDeclaration = typeDeclaration.WithBaseList(
                SyntaxFactory.BaseList(SyntaxFactory.SingletonSeparatedList<BaseTypeSyntax>(baseType)));
        }
        else
        {
            var newBaseList = typeDeclaration.BaseList.AddTypes(baseType);
            newTypeDeclaration = typeDeclaration.WithBaseList(newBaseList);
        }

        var newRoot = root.ReplaceNode(typeDeclaration, newTypeDeclaration);
        var newDoc = document.WithSyntaxRoot(newRoot);

        return newDoc.Project.Solution;
    }

    private static RefactoringResult CreatePreviewResult(
        Guid operationId,
        ExtractInterfaceParams @params,
        List<ISymbol> members,
        InterfaceDeclarationSyntax interfaceDecl,
        string? namespaceName)
    {
        var memberNames = string.Join(", ", members.Select(m => m.Name));
        var interfaceCode = interfaceDecl.NormalizeWhitespace().ToFullString();

        var targetFile = @params.TargetFile ?? @params.SourceFile;
        var isNewFile = @params.TargetFile != null && @params.TargetFile != @params.SourceFile;

        var pendingChanges = new List<PendingChange>
        {
            new()
            {
                File = targetFile,
                ChangeType = isNewFile ? ChangeKind.Create : ChangeKind.Modify,
                Description = $"Extract interface {@params.InterfaceName} with members: {memberNames}",
                BeforeSnippet = isNewFile ? "// (new file)" : $"// Before type '{@params.TypeName}'",
                AfterSnippet = interfaceCode
            }
        };

        if (@params.AddInterfaceToType)
        {
            pendingChanges.Add(new PendingChange
            {
                File = @params.SourceFile,
                ChangeType = ChangeKind.Modify,
                Description = $"Add {@params.InterfaceName} to base list of {@params.TypeName}",
                BeforeSnippet = $"class {@params.TypeName}",
                AfterSnippet = $"class {@params.TypeName} : {@params.InterfaceName}"
            });
        }

        return RefactoringResult.PreviewResult(operationId, pendingChanges);
    }

    private static bool IsValidIdentifier(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        if (!char.IsLetter(name[0]) && name[0] != '_') return false;
        return name.All(c => char.IsLetterOrDigit(c) || c == '_');
    }
}

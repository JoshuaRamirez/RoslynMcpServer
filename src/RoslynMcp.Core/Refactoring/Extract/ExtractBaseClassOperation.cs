using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynMcp.Contracts.Enums;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.FileSystem;
using RoslynMcp.Core.Refactoring.Base;
using RoslynMcp.Core.Workspace;

namespace RoslynMcp.Core.Refactoring.Extract;

/// <summary>
/// Extracts members to a new base class.
/// </summary>
public sealed class ExtractBaseClassOperation : RefactoringOperationBase<ExtractBaseClassParams>
{
    /// <summary>
    /// Creates a new extract base class operation.
    /// </summary>
    public ExtractBaseClassOperation(WorkspaceContext context) : base(context)
    {
    }

    /// <inheritdoc />
    protected override void ValidateParams(ExtractBaseClassParams @params)
    {
        if (string.IsNullOrWhiteSpace(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "sourceFile is required.");

        if (string.IsNullOrWhiteSpace(@params.TypeName))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "typeName is required.");

        if (string.IsNullOrWhiteSpace(@params.BaseClassName))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "baseClassName is required.");

        if (@params.Members == null || @params.Members.Count == 0)
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "members is required.");

        if (!PathResolver.IsAbsolutePath(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be an absolute path.");

        if (!PathResolver.IsValidCSharpFilePath(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be a .cs file.");

        if (!File.Exists(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.SourceFileNotFound, $"Source file not found: {@params.SourceFile}");

        if (!IsValidIdentifier(@params.BaseClassName))
            throw new RefactoringException(ErrorCodes.InvalidSymbolName, $"Invalid base class name: {@params.BaseClassName}");

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
        ExtractBaseClassParams @params,
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
            .OfType<ClassDeclarationSyntax>()
            .FirstOrDefault(t => t.Identifier.Text == @params.TypeName);

        if (typeDeclaration == null)
        {
            throw new RefactoringException(
                ErrorCodes.TypeNotFound,
                $"Class '{@params.TypeName}' not found in file.");
        }

        // Get the type symbol
        var typeSymbol = semanticModel.GetDeclaredSymbol(typeDeclaration, cancellationToken) as INamedTypeSymbol;
        if (typeSymbol == null)
        {
            throw new RefactoringException(ErrorCodes.RoslynError, "Could not resolve type symbol.");
        }

        // Check if type already has a base class other than Object
        if (typeSymbol.BaseType != null &&
            typeSymbol.BaseType.SpecialType != SpecialType.System_Object)
        {
            throw new RefactoringException(
                ErrorCodes.TypeAlreadyHasBase,
                $"Type '{@params.TypeName}' already has base class '{typeSymbol.BaseType.Name}'.");
        }

        // Check if base class name already exists
        var existingType = await TypeResolver.FindTypeByNameAsync(
            $"{typeSymbol.ContainingNamespace}.{@params.BaseClassName}",
            cancellationToken);

        if (existingType != null)
        {
            throw new RefactoringException(
                ErrorCodes.NameCollision,
                $"Type '{@params.BaseClassName}' already exists.");
        }

        // Find members to extract
        var membersToExtract = FindMembersToExtract(typeDeclaration, @params.Members, semanticModel);

        // Generate base class
        var baseClass = GenerateBaseClass(
            @params.BaseClassName,
            membersToExtract,
            @params.MakeAbstract);

        // Get namespace
        var namespaceName = typeSymbol.ContainingNamespace?.ToDisplayString();

        // If preview mode, return without applying
        if (@params.Preview)
        {
            return CreatePreviewResult(operationId, @params, membersToExtract, baseClass, namespaceName);
        }

        // Apply changes
        Solution newSolution;
        if (@params.TargetFile != null && @params.TargetFile != @params.SourceFile)
        {
            // Create new file with base class
            newSolution = await CreateBaseClassInNewFileAsync(
                document.Project.Solution,
                document.Project,
                @params.TargetFile,
                baseClass,
                namespaceName,
                root,
                cancellationToken);
        }
        else
        {
            // Add base class to same file
            newSolution = AddBaseClassToSameFile(
                document,
                root,
                typeDeclaration,
                baseClass);
        }

        // Update derived class: remove extracted members and add base class
        var updatedDoc = newSolution.GetDocument(document.Id);
        if (updatedDoc != null)
        {
            var updatedRoot = await updatedDoc.GetSyntaxRootAsync(cancellationToken);
            if (updatedRoot != null)
            {
                var updatedTypeDecl = updatedRoot.DescendantNodes()
                    .OfType<ClassDeclarationSyntax>()
                    .First(t => t.Identifier.Text == @params.TypeName);

                // Add base class to type
                var baseType = SyntaxFactory.SimpleBaseType(SyntaxFactory.ParseTypeName(@params.BaseClassName));

                ClassDeclarationSyntax newTypeDecl;
                if (updatedTypeDecl.BaseList == null)
                {
                    newTypeDecl = updatedTypeDecl.WithBaseList(
                        SyntaxFactory.BaseList(SyntaxFactory.SingletonSeparatedList<BaseTypeSyntax>(baseType)));
                }
                else
                {
                    // Insert base class before interfaces
                    var newBaseList = SyntaxFactory.BaseList(
                        SyntaxFactory.SeparatedList(
                            new[] { baseType }.Concat(updatedTypeDecl.BaseList.Types)));
                    newTypeDecl = updatedTypeDecl.WithBaseList(newBaseList);
                }

                // Remove extracted members from derived class
                var memberNames = @params.Members.ToHashSet();
                var newMembers = newTypeDecl.Members
                    .Where(m => !ShouldRemoveMember(m, memberNames))
                    .ToList();

                newTypeDecl = newTypeDecl.WithMembers(SyntaxFactory.List(newMembers));

                updatedRoot = updatedRoot.ReplaceNode(updatedTypeDecl, newTypeDecl);
                newSolution = updatedDoc.WithSyntaxRoot(updatedRoot).Project.Solution;
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
                Name = @params.BaseClassName,
                FullyQualifiedName = string.IsNullOrEmpty(namespaceName)
                    ? @params.BaseClassName
                    : $"{namespaceName}.{@params.BaseClassName}",
                Kind = Contracts.Enums.SymbolKind.Class
            },
            0,
            0);
    }

    private static List<MemberDeclarationSyntax> FindMembersToExtract(
        ClassDeclarationSyntax typeDeclaration,
        IReadOnlyList<string> memberNames,
        SemanticModel semanticModel)
    {
        var nameSet = new HashSet<string>(memberNames);
        var result = new List<MemberDeclarationSyntax>();

        foreach (var member in typeDeclaration.Members)
        {
            var name = GetMemberName(member);
            if (name != null && nameSet.Contains(name))
            {
                result.Add(member);
                nameSet.Remove(name);
            }
        }

        if (nameSet.Count > 0)
        {
            throw new RefactoringException(
                ErrorCodes.MemberNotFound,
                $"Members not found: {string.Join(", ", nameSet)}");
        }

        return result;
    }

    private static string? GetMemberName(MemberDeclarationSyntax member)
    {
        return member switch
        {
            MethodDeclarationSyntax m => m.Identifier.Text,
            PropertyDeclarationSyntax p => p.Identifier.Text,
            FieldDeclarationSyntax f => f.Declaration.Variables.FirstOrDefault()?.Identifier.Text,
            EventDeclarationSyntax e => e.Identifier.Text,
            _ => null
        };
    }

    private static bool ShouldRemoveMember(MemberDeclarationSyntax member, HashSet<string> memberNames)
    {
        var name = GetMemberName(member);
        return name != null && memberNames.Contains(name);
    }

    private static ClassDeclarationSyntax GenerateBaseClass(
        string className,
        List<MemberDeclarationSyntax> members,
        bool makeAbstract)
    {
        var modifiers = new List<SyntaxToken> { SyntaxFactory.Token(SyntaxKind.PublicKeyword) };
        if (makeAbstract)
        {
            modifiers.Add(SyntaxFactory.Token(SyntaxKind.AbstractKeyword));
        }

        // Make members protected if they're private
        var adjustedMembers = members.Select(m => AdjustMemberAccessibility(m)).ToList();

        return SyntaxFactory.ClassDeclaration(className)
            .WithModifiers(SyntaxFactory.TokenList(modifiers))
            .WithMembers(SyntaxFactory.List(adjustedMembers))
            .NormalizeWhitespace();
    }

    private static MemberDeclarationSyntax AdjustMemberAccessibility(MemberDeclarationSyntax member)
    {
        // If private, make protected
        var modifiers = member switch
        {
            MethodDeclarationSyntax m => m.Modifiers,
            PropertyDeclarationSyntax p => p.Modifiers,
            FieldDeclarationSyntax f => f.Modifiers,
            _ => default
        };

        if (modifiers.Any(SyntaxKind.PrivateKeyword))
        {
            var newModifiers = SyntaxFactory.TokenList(
                modifiers.Where(m => !m.IsKind(SyntaxKind.PrivateKeyword))
                         .Prepend(SyntaxFactory.Token(SyntaxKind.ProtectedKeyword)));

            return member switch
            {
                MethodDeclarationSyntax m => m.WithModifiers(newModifiers),
                PropertyDeclarationSyntax p => p.WithModifiers(newModifiers),
                FieldDeclarationSyntax f => f.WithModifiers(newModifiers),
                _ => member
            };
        }

        return member;
    }

    private async Task<Solution> CreateBaseClassInNewFileAsync(
        Solution solution,
        Project project,
        string targetFile,
        ClassDeclarationSyntax baseClass,
        string? namespaceName,
        SyntaxNode sourceRoot,
        CancellationToken cancellationToken)
    {
        // Build compilation unit with usings from source
        var usings = sourceRoot.DescendantNodes()
            .OfType<UsingDirectiveSyntax>()
            .ToList();

        MemberDeclarationSyntax wrappedClass;
        if (!string.IsNullOrEmpty(namespaceName))
        {
            wrappedClass = SyntaxFactory.FileScopedNamespaceDeclaration(
                    SyntaxFactory.ParseName(namespaceName))
                .WithMembers(SyntaxFactory.SingletonList<MemberDeclarationSyntax>(baseClass));
        }
        else
        {
            wrappedClass = baseClass;
        }

        var compilationUnit = SyntaxFactory.CompilationUnit()
            .WithUsings(SyntaxFactory.List(usings))
            .WithMembers(SyntaxFactory.SingletonList(wrappedClass))
            .NormalizeWhitespace();

        // Create new document
        var newDoc = project.AddDocument(
            Path.GetFileName(targetFile),
            compilationUnit,
            filePath: targetFile);

        return newDoc.Project.Solution;
    }

    private static Solution AddBaseClassToSameFile(
        Document document,
        SyntaxNode root,
        ClassDeclarationSyntax derivedClass,
        ClassDeclarationSyntax baseClass)
    {
        // Insert base class before derived class
        var newBaseClass = baseClass
            .WithLeadingTrivia(SyntaxFactory.CarriageReturnLineFeed)
            .WithTrailingTrivia(SyntaxFactory.CarriageReturnLineFeed, SyntaxFactory.CarriageReturnLineFeed);

        var newRoot = root.InsertNodesBefore(derivedClass, new[] { newBaseClass });
        var newDoc = document.WithSyntaxRoot(newRoot);

        return newDoc.Project.Solution;
    }

    private static RefactoringResult CreatePreviewResult(
        Guid operationId,
        ExtractBaseClassParams @params,
        List<MemberDeclarationSyntax> members,
        ClassDeclarationSyntax baseClass,
        string? namespaceName)
    {
        var memberNames = string.Join(", ", members.Select(m => GetMemberName(m)));
        var baseClassCode = baseClass.NormalizeWhitespace().ToFullString();

        var targetFile = @params.TargetFile ?? @params.SourceFile;
        var isNewFile = @params.TargetFile != null && @params.TargetFile != @params.SourceFile;

        var pendingChanges = new List<PendingChange>
        {
            new()
            {
                File = targetFile,
                ChangeType = isNewFile ? ChangeKind.Create : ChangeKind.Modify,
                Description = $"Extract base class {@params.BaseClassName} with members: {memberNames}",
                BeforeSnippet = isNewFile ? "// (new file)" : $"// Before class '{@params.TypeName}'",
                AfterSnippet = baseClassCode
            },
            new()
            {
                File = @params.SourceFile,
                ChangeType = ChangeKind.Modify,
                Description = $"Update {@params.TypeName} to inherit from {@params.BaseClassName}",
                BeforeSnippet = $"class {@params.TypeName}",
                AfterSnippet = $"class {@params.TypeName} : {@params.BaseClassName}"
            }
        };

        return RefactoringResult.PreviewResult(operationId, pendingChanges);
    }

    private static bool IsValidIdentifier(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        if (!char.IsLetter(name[0]) && name[0] != '_') return false;
        return name.All(c => char.IsLetterOrDigit(c) || c == '_');
    }
}

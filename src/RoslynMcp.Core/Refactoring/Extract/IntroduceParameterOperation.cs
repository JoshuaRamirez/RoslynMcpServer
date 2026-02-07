using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using RoslynMcp.Contracts.Enums;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.FileSystem;
using RoslynMcp.Core.Refactoring.Base;
using RoslynMcp.Core.Workspace;

namespace RoslynMcp.Core.Refactoring.Extract;

/// <summary>
/// Promotes a local variable to a method parameter and updates call sites.
/// </summary>
public sealed class IntroduceParameterOperation : RefactoringOperationBase<IntroduceParameterParams>
{
    /// <inheritdoc />
    public IntroduceParameterOperation(WorkspaceContext context) : base(context)
    {
    }

    /// <inheritdoc />
    protected override void ValidateParams(IntroduceParameterParams @params)
    {
        if (string.IsNullOrWhiteSpace(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "sourceFile is required.");

        if (string.IsNullOrWhiteSpace(@params.VariableName))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "variableName is required.");

        if (!PathResolver.IsAbsolutePath(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be an absolute path.");

        if (!PathResolver.IsValidCSharpFilePath(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be a .cs file.");

        if (@params.Line < 1)
            throw new RefactoringException(ErrorCodes.InvalidLineNumber, "line must be >= 1.");

        if (!File.Exists(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.SourceFileNotFound, $"Source file not found: {@params.SourceFile}");
    }

    /// <inheritdoc />
    protected override async Task<RefactoringResult> ExecuteCoreAsync(
        Guid operationId,
        IntroduceParameterParams @params,
        CancellationToken cancellationToken)
    {
        var document = GetDocumentOrThrow(@params.SourceFile);
        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);

        if (root == null || semanticModel == null)
            throw new RefactoringException(ErrorCodes.RoslynError, "Could not parse file.");

        // Find the local variable declaration at the specified line
        var targetLine = @params.Line - 1; // 0-based
        var localDecl = root.DescendantNodes()
            .OfType<LocalDeclarationStatementSyntax>()
            .Where(l => l.GetLocation().GetLineSpan().StartLinePosition.Line == targetLine)
            .SelectMany(l => l.Declaration.Variables)
            .FirstOrDefault(v => v.Identifier.Text == @params.VariableName);

        if (localDecl == null)
        {
            throw new RefactoringException(ErrorCodes.SymbolNotFound,
                $"Local variable '{@params.VariableName}' not found at line {@params.Line}.");
        }

        // Get the containing method
        var containingMethod = localDecl.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();
        if (containingMethod == null)
        {
            throw new RefactoringException(ErrorCodes.CannotConvert,
                "Local variable must be inside a method to be promoted to a parameter.");
        }

        var methodSymbol = semanticModel.GetDeclaredSymbol(containingMethod, cancellationToken);
        if (methodSymbol == null)
            throw new RefactoringException(ErrorCodes.RoslynError, "Could not resolve method symbol.");

        // Get the variable's type
        var localDeclStatement = localDecl.Ancestors().OfType<LocalDeclarationStatementSyntax>().First();
        var declaredType = localDeclStatement.Declaration.Type;

        // If using 'var', resolve the actual type
        TypeSyntax paramType;
        if (declaredType.IsVar)
        {
            var typeInfo = semanticModel.GetTypeInfo(declaredType, cancellationToken);
            if (typeInfo.Type != null)
            {
                paramType = SyntaxFactory.ParseTypeName(typeInfo.Type.ToDisplayString());
            }
            else
            {
                paramType = declaredType;
            }
        }
        else
        {
            paramType = declaredType;
        }

        // Get the initializer expression (used as default value at call sites)
        var initializer = localDecl.Initializer?.Value;

        if (@params.Preview)
        {
            var beforeSig = containingMethod.Identifier.Text + "(" +
                            string.Join(", ", containingMethod.ParameterList.Parameters.Select(p => p.ToString())) + ")";
            var afterSig = containingMethod.Identifier.Text + "(" +
                           string.Join(", ", containingMethod.ParameterList.Parameters.Select(p => p.ToString())) +
                           (containingMethod.ParameterList.Parameters.Count > 0 ? ", " : "") +
                           $"{paramType.NormalizeWhitespace()} {@params.VariableName})";

            var pendingChanges = new List<PendingChange>
            {
                new()
                {
                    File = @params.SourceFile,
                    ChangeType = ChangeKind.Modify,
                    Description = $"Promote '{@params.VariableName}' to parameter of '{containingMethod.Identifier.Text}'",
                    BeforeSnippet = beforeSig,
                    AfterSnippet = afterSig
                }
            };
            return RefactoringResult.PreviewResult(operationId, pendingChanges);
        }

        // 1. Add parameter to method signature
        var newParam = SyntaxFactory.Parameter(SyntaxFactory.Identifier(@params.VariableName))
            .WithType(paramType.WithTrailingTrivia(SyntaxFactory.Space));

        var newParameterList = containingMethod.ParameterList.AddParameters(newParam);

        // 2. Remove the local variable declaration (or just the declarator if multi-variable)
        var declarationStatement = localDecl.Ancestors().OfType<LocalDeclarationStatementSyntax>().First();
        var newBody = containingMethod.Body!;

        if (declarationStatement.Declaration.Variables.Count == 1)
        {
            // Remove entire statement
            newBody = newBody.RemoveNode(declarationStatement, SyntaxRemoveOptions.KeepNoTrivia)!;
        }
        else
        {
            // Remove just this variable from the multi-variable declaration
            var newDeclaration = declarationStatement.Declaration.RemoveNode(localDecl, SyntaxRemoveOptions.KeepNoTrivia)!;
            var newDeclStatement = declarationStatement.WithDeclaration(newDeclaration);
            newBody = (BlockSyntax)newBody.ReplaceNode(declarationStatement, newDeclStatement);
        }

        var newMethod = containingMethod
            .WithParameterList(newParameterList)
            .WithBody(newBody);

        var newRoot = root.ReplaceNode(containingMethod, newMethod);
        var newSolution = document.WithSyntaxRoot(newRoot).Project.Solution;

        // 3. Update call sites: add the initializer value as argument
        if (initializer != null)
        {
            var references = await SymbolFinder.FindReferencesAsync(
                methodSymbol, newSolution, cancellationToken);

            foreach (var reference in references.SelectMany(r => r.Locations))
            {
                var refDoc = newSolution.GetDocument(reference.Document.Id);
                if (refDoc == null) continue;

                var refRoot = await refDoc.GetSyntaxRootAsync(cancellationToken);
                if (refRoot == null) continue;

                var refNode = refRoot.FindNode(reference.Location.SourceSpan);
                var invocation = refNode.Ancestors().OfType<InvocationExpressionSyntax>().FirstOrDefault();

                if (invocation != null)
                {
                    var newArgument = SyntaxFactory.Argument(initializer);
                    var newArgList = invocation.ArgumentList.AddArguments(newArgument);
                    var newInvocation = invocation.WithArgumentList(newArgList);
                    var newRefRoot = refRoot.ReplaceNode(invocation, newInvocation);
                    newSolution = refDoc.WithSyntaxRoot(newRefRoot).Project.Solution;
                }
            }
        }

        var commitResult = await CommitChangesAsync(newSolution, cancellationToken);

        return RefactoringResult.Succeeded(operationId,
            new FileChanges { FilesModified = commitResult.FilesModified, FilesCreated = commitResult.FilesCreated, FilesDeleted = commitResult.FilesDeleted },
            new Contracts.Models.SymbolInfo { Name = containingMethod.Identifier.Text, FullyQualifiedName = $"{methodSymbol.ContainingType.ToDisplayString()}.{containingMethod.Identifier.Text}", Kind = Contracts.Enums.SymbolKind.Method },
            0, 0);
    }
}

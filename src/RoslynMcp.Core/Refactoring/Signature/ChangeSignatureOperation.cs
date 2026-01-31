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

namespace RoslynMcp.Core.Refactoring.Signature;

/// <summary>
/// Changes a method's signature by adding, removing, or reordering parameters.
/// </summary>
public sealed class ChangeSignatureOperation : RefactoringOperationBase<ChangeSignatureParams>
{
    /// <summary>
    /// Creates a new change signature operation.
    /// </summary>
    public ChangeSignatureOperation(WorkspaceContext context) : base(context)
    {
    }

    /// <inheritdoc />
    protected override void ValidateParams(ChangeSignatureParams @params)
    {
        if (string.IsNullOrWhiteSpace(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "sourceFile is required.");

        if (string.IsNullOrWhiteSpace(@params.MethodName))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "methodName is required.");

        if (@params.Parameters == null || @params.Parameters.Count == 0)
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "parameters is required.");

        if (!PathResolver.IsAbsolutePath(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be an absolute path.");

        if (!PathResolver.IsValidCSharpFilePath(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be a .cs file.");

        if (!File.Exists(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.SourceFileNotFound, $"Source file not found: {@params.SourceFile}");

        // Validate parameter changes
        foreach (var change in @params.Parameters)
        {
            if (string.IsNullOrWhiteSpace(change.Name))
                throw new RefactoringException(ErrorCodes.MissingRequiredParam, "Each parameter change requires a name.");

            if (change.OriginalName == null && !change.Remove && string.IsNullOrWhiteSpace(change.Type))
                throw new RefactoringException(ErrorCodes.MissingRequiredParam, "New parameters require a type.");
        }
    }

    /// <inheritdoc />
    protected override async Task<RefactoringResult> ExecuteCoreAsync(
        Guid operationId,
        ChangeSignatureParams @params,
        CancellationToken cancellationToken)
    {
        var document = GetDocumentOrThrow(@params.SourceFile);
        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);

        if (root == null || semanticModel == null)
        {
            throw new RefactoringException(ErrorCodes.RoslynError, "Could not parse file.");
        }

        // Find method declaration
        var methodDeclarations = root.DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Where(m => m.Identifier.Text == @params.MethodName)
            .ToList();

        if (methodDeclarations.Count == 0)
        {
            throw new RefactoringException(
                ErrorCodes.MethodNotFound,
                $"Method '{@params.MethodName}' not found.");
        }

        MethodDeclarationSyntax methodDecl;
        if (methodDeclarations.Count > 1)
        {
            if (!@params.Line.HasValue)
            {
                var lines = methodDeclarations
                    .Select(m => m.GetLocation().GetLineSpan().StartLinePosition.Line + 1)
                    .ToList();
                throw new RefactoringException(
                    ErrorCodes.SymbolAmbiguous,
                    $"Multiple methods named '{@params.MethodName}' found. Provide line number. Options: {string.Join(", ", lines)}");
            }

            methodDecl = methodDeclarations.FirstOrDefault(m =>
                m.GetLocation().GetLineSpan().StartLinePosition.Line + 1 == @params.Line.Value)
                ?? throw new RefactoringException(
                    ErrorCodes.MethodNotFound,
                    $"Method '{@params.MethodName}' not found at line {@params.Line}.");
        }
        else
        {
            methodDecl = methodDeclarations[0];
        }

        var methodSymbol = semanticModel.GetDeclaredSymbol(methodDecl, cancellationToken);
        if (methodSymbol == null)
        {
            throw new RefactoringException(ErrorCodes.RoslynError, "Could not resolve method symbol.");
        }

        // Build new parameter list
        var newParameters = BuildNewParameterList(methodSymbol.Parameters.ToList(), @params.Parameters);

        // Find all call sites
        var references = await SymbolFinder.FindReferencesAsync(
            methodSymbol,
            Context.Solution,
            cancellationToken);

        var callSites = references
            .SelectMany(r => r.Locations)
            .Where(loc => loc.Document.Id != document.Id || !loc.Location.SourceSpan.IntersectsWith(methodDecl.Span))
            .ToList();

        // If preview mode, return without applying
        if (@params.Preview)
        {
            return CreatePreviewResult(operationId, @params, methodSymbol.Parameters.ToList(), newParameters, callSites.Count);
        }

        // Update method declaration
        var newParamSyntax = newParameters.Select(p => CreateParameterSyntax(p));
        var newParamList = SyntaxFactory.ParameterList(SyntaxFactory.SeparatedList(newParamSyntax));
        var newMethodDecl = methodDecl.WithParameterList(newParamList);

        var newRoot = root.ReplaceNode(methodDecl, newMethodDecl);
        var newSolution = document.WithSyntaxRoot(newRoot).Project.Solution;

        // Update call sites
        foreach (var callSite in callSites)
        {
            var callDoc = newSolution.GetDocument(callSite.Document.Id);
            if (callDoc == null) continue;

            var callRoot = await callDoc.GetSyntaxRootAsync(cancellationToken);
            if (callRoot == null) continue;

            var callNode = callRoot.FindNode(callSite.Location.SourceSpan);
            var invocation = callNode.AncestorsAndSelf().OfType<InvocationExpressionSyntax>().FirstOrDefault();

            if (invocation != null)
            {
                var newInvocation = UpdateInvocation(
                    invocation,
                    methodSymbol.Parameters.ToList(),
                    newParameters,
                    @params.Parameters);

                var newCallRoot = callRoot.ReplaceNode(invocation, newInvocation);
                newSolution = callDoc.WithSyntaxRoot(newCallRoot).Project.Solution;
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
                Name = @params.MethodName,
                FullyQualifiedName = methodSymbol.ToDisplayString(),
                Kind = Contracts.Enums.SymbolKind.Method
            },
            callSites.Count,
            0);
    }

    private static List<NewParameter> BuildNewParameterList(
        List<IParameterSymbol> originalParams,
        IReadOnlyList<ParameterChange> changes)
    {
        var result = new List<NewParameter>();
        var originalMap = originalParams.ToDictionary(p => p.Name);
        var usedPositions = new HashSet<int>();

        // First pass: handle existing parameters and removals
        foreach (var change in changes.Where(c => c.OriginalName != null))
        {
            if (change.Remove) continue;

            if (!originalMap.TryGetValue(change.OriginalName!, out var originalParam))
            {
                throw new RefactoringException(
                    ErrorCodes.ParameterNotFound,
                    $"Parameter '{change.OriginalName}' not found in method.");
            }

            result.Add(new NewParameter
            {
                Name = change.Name,
                Type = change.Type ?? originalParam.Type.ToDisplayString(),
                DefaultValue = change.DefaultValue,
                Position = change.NewPosition,
                OriginalName = change.OriginalName
            });
        }

        // Second pass: add new parameters
        foreach (var change in changes.Where(c => c.OriginalName == null && !c.Remove))
        {
            result.Add(new NewParameter
            {
                Name = change.Name,
                Type = change.Type!,
                DefaultValue = change.DefaultValue,
                Position = change.NewPosition,
                OriginalName = null
            });
        }

        // Sort by position
        if (result.Any(p => p.Position.HasValue))
        {
            result = result
                .OrderBy(p => p.Position ?? int.MaxValue)
                .ThenBy(p => result.IndexOf(p))
                .ToList();
        }

        return result;
    }

    private static ParameterSyntax CreateParameterSyntax(NewParameter param)
    {
        var paramSyntax = SyntaxFactory.Parameter(SyntaxFactory.Identifier(param.Name))
            .WithType(SyntaxFactory.ParseTypeName(param.Type).WithTrailingTrivia(SyntaxFactory.Space));

        if (!string.IsNullOrEmpty(param.DefaultValue))
        {
            paramSyntax = paramSyntax.WithDefault(
                SyntaxFactory.EqualsValueClause(
                    SyntaxFactory.ParseExpression(param.DefaultValue)));
        }

        return paramSyntax;
    }

    private static InvocationExpressionSyntax UpdateInvocation(
        InvocationExpressionSyntax invocation,
        List<IParameterSymbol> originalParams,
        List<NewParameter> newParams,
        IReadOnlyList<ParameterChange> changes)
    {
        var originalArgs = invocation.ArgumentList.Arguments.ToList();
        var newArgs = new List<ArgumentSyntax>();

        // Build a map of original param name to argument
        var argMap = new Dictionary<string, ArgumentSyntax>();
        for (int i = 0; i < originalArgs.Count && i < originalParams.Count; i++)
        {
            var arg = originalArgs[i];
            var paramName = arg.NameColon?.Name.Identifier.Text ?? originalParams[i].Name;
            argMap[paramName] = arg;
        }

        foreach (var newParam in newParams)
        {
            if (newParam.OriginalName != null && argMap.TryGetValue(newParam.OriginalName, out var existingArg))
            {
                // Rename the argument if needed
                if (newParam.Name != newParam.OriginalName && existingArg.NameColon != null)
                {
                    existingArg = existingArg.WithNameColon(
                        SyntaxFactory.NameColon(newParam.Name));
                }
                newArgs.Add(existingArg);
            }
            else if (!string.IsNullOrEmpty(newParam.DefaultValue))
            {
                // New parameter with default - use default
                newArgs.Add(SyntaxFactory.Argument(SyntaxFactory.ParseExpression(newParam.DefaultValue)));
            }
            else
            {
                // New parameter without default - add placeholder
                newArgs.Add(SyntaxFactory.Argument(
                    SyntaxFactory.ParseExpression($"default /* TODO: {newParam.Name} */")));
            }
        }

        return invocation.WithArgumentList(
            SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(newArgs)));
    }

    private static RefactoringResult CreatePreviewResult(
        Guid operationId,
        ChangeSignatureParams @params,
        List<IParameterSymbol> originalParams,
        List<NewParameter> newParams,
        int callSiteCount)
    {
        var oldSig = string.Join(", ", originalParams.Select(p => $"{p.Type.ToDisplayString()} {p.Name}"));
        var newSig = string.Join(", ", newParams.Select(p =>
            string.IsNullOrEmpty(p.DefaultValue)
                ? $"{p.Type} {p.Name}"
                : $"{p.Type} {p.Name} = {p.DefaultValue}"));

        var pendingChanges = new List<PendingChange>
        {
            new()
            {
                File = @params.SourceFile,
                ChangeType = ChangeKind.Modify,
                Description = $"Change signature of '{@params.MethodName}' ({callSiteCount} call sites to update)",
                BeforeSnippet = $"{@params.MethodName}({oldSig})",
                AfterSnippet = $"{@params.MethodName}({newSig})"
            }
        };

        return RefactoringResult.PreviewResult(operationId, pendingChanges);
    }

    private sealed class NewParameter
    {
        public required string Name { get; init; }
        public required string Type { get; init; }
        public string? DefaultValue { get; init; }
        public int? Position { get; init; }
        public string? OriginalName { get; init; }
    }
}

using System.Diagnostics;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Rename;
using Microsoft.CodeAnalysis.Text;
using RoslynMcp.Contracts.Coordinates;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Transactions;
using RoslynMcp.Core.Coordinates;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Workspace;

namespace RoslynMcp.Core.Transactions;

/// <summary>
/// Executes atomic multi-step <see cref="SolutionTransactionRequest"/> pipelines
/// (<c>Solution₀ → Op₁ → Solution₁ → ... → Solutionₖ</c>) over a warm <see cref="WarmSolutionSession"/>,
/// supporting unified diff previews and automatic rollback on post-transaction compilation errors.
/// </summary>
public static class SolutionTransactionEngine
{
    /// <summary>
    /// Handler delegate for Layer 4 <see cref="SyntaxGeneratorOperation"/> steps.
    /// </summary>
    public static Func<Solution, SyntaxGeneratorOperation, CancellationToken, Task<Solution>>? SyntaxGeneratorHandler { get; set; }
        = SyntaxGeneratorRunner.ExecuteAsync;

    /// <summary>
    /// Handler delegate for Layer 4 <see cref="CodeActionHostOperation"/> steps.
    /// </summary>
    public static Func<Solution, CodeActionHostOperation, CancellationToken, Task<Solution>>? CodeActionHostHandler { get; set; }
        = CodeActionHostRunner.ExecuteAsync;

    /// <summary>
    /// Handler delegate for Layer 4 <see cref="SemanticPatternRewriteOperation"/> steps.
    /// </summary>
    public static Func<Solution, SemanticPatternRewriteOperation, CancellationToken, Task<Solution>>? SemanticPatternRewriteHandler { get; set; }
        = IOperationSemanticRewriter.ExecuteAsync;

    /// <summary>
    /// Executes a <see cref="SolutionTransactionRequest"/> against a warm <see cref="WarmSolutionSession"/>.
    /// </summary>
    /// <param name="session">The warm solution session.</param>
    /// <param name="request">The transaction request containing sequential operations and flags.</param>
    /// <param name="persistToDisk">Whether committed transactions also write modified files to disk.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The <see cref="SolutionTransactionResult"/> describing the outcome and unified diffs.</returns>
    public static Task<SolutionTransactionResult> ExecuteAsync(
        WarmSolutionSession session,
        SolutionTransactionRequest request,
        bool persistToDisk = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(request);

        return session.ExecuteSerializedAsync(async (lockedSession, ct) =>
        {
            var stopwatch = Stopwatch.StartNew();
            var baselineSolution = lockedSession.CurrentSolution;
            var currentSolution = baselineSolution;
            var executedCount = 0;

            try
            {
                foreach (var operation in request.Operations)
                {
                    ct.ThrowIfCancellationRequested();
                    currentSolution = await ApplyOperationAsync(currentSolution, operation, ct).ConfigureAwait(false);
                    executedCount++;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                stopwatch.Stop();
                return new SolutionTransactionResult
                {
                    Success = false,
                    RolledBack = true,
                    Preview = request.PreviewDiffOnly,
                    OperationsExecuted = executedCount,
                    FileDiffs = [],
                    CompilationErrors = [],
                    ExecutionTimeMs = stopwatch.ElapsedMilliseconds,
                    Error = ex.Message
                };
            }

            var diffs = await ComputeUnifiedDiffsAsync(baselineSolution, currentSolution, ct).ConfigureAwait(false);

            if (request.RollbackOnCompilationErrors)
            {
                var introducedErrors = await CollectIntroducedCompilationErrorsAsync(baselineSolution, currentSolution, ct)
                    .ConfigureAwait(false);
                if (introducedErrors.Count > 0)
                {
                    stopwatch.Stop();
                    return new SolutionTransactionResult
                    {
                        Success = false,
                        RolledBack = true,
                        Preview = request.PreviewDiffOnly,
                        OperationsExecuted = executedCount,
                        FileDiffs = diffs,
                        CompilationErrors = introducedErrors,
                        ExecutionTimeMs = stopwatch.ElapsedMilliseconds,
                        Error = $"Transaction rolled back because post-transaction compilation produced {introducedErrors.Count} error(s)."
                    };
                }
            }

            if (!request.PreviewDiffOnly)
            {
                await lockedSession.CommitAuthorizedSolutionAsync(currentSolution, persistToDisk, ct).ConfigureAwait(false);
            }

            stopwatch.Stop();
            return new SolutionTransactionResult
            {
                Success = true,
                RolledBack = false,
                Preview = request.PreviewDiffOnly,
                OperationsExecuted = executedCount,
                FileDiffs = diffs,
                CompilationErrors = [],
                ExecutionTimeMs = stopwatch.ElapsedMilliseconds
            };
        }, cancellationToken);
    }

    /// <summary>
    /// Applies a single <see cref="SolutionOperation"/> to <paramref name="solution"/>, returning a new immutable <see cref="Solution"/>.
    /// </summary>
    /// <param name="solution">Input solution snapshot.</param>
    /// <param name="operation">Operation to apply.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Transformed solution snapshot.</returns>
    public static Task<Solution> ApplyOperationAsync(
        Solution solution,
        SolutionOperation operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(solution);
        ArgumentNullException.ThrowIfNull(operation);

        return operation switch
        {
            EditSpanOperation editOp => ApplyEditSpanAsync(solution, editOp, cancellationToken),
            DeleteSymbolOperation deleteOp => ApplyDeleteSymbolAsync(solution, deleteOp, cancellationToken),
            RenameSymbolOperation renameOp => ApplyRenameSymbolAsync(solution, renameOp, cancellationToken),
            MoveSymbolToFileOperation moveOp => ApplyMoveSymbolToFileAsync(solution, moveOp, cancellationToken),
            ExtractMethodOperation extractOp => ApplyExtractMethodAsync(solution, extractOp, cancellationToken),
            EncapsulateFieldOperation encapsulateOp => ApplyEncapsulateFieldAsync(solution, encapsulateOp, cancellationToken),
            ChangeSignatureOperation signatureOp => ApplyChangeSignatureAsync(solution, signatureOp, cancellationToken),
            InlineVariableOperation inlineOp => ApplyInlineVariableAsync(solution, inlineOp, cancellationToken),
            SyntaxGeneratorOperation syntaxGenOp => SyntaxGeneratorHandler is not null
                ? SyntaxGeneratorHandler(solution, syntaxGenOp, cancellationToken)
                : throw new NotSupportedException("SyntaxGeneratorHandler is not registered."),
            CodeActionHostOperation codeActionOp => CodeActionHostHandler is not null
                ? CodeActionHostHandler(solution, codeActionOp, cancellationToken)
                : throw new NotSupportedException("CodeActionHostHandler is not registered."),
            SemanticPatternRewriteOperation rewriteOp => SemanticPatternRewriteHandler is not null
                ? SemanticPatternRewriteHandler(solution, rewriteOp, cancellationToken)
                : throw new NotSupportedException("SemanticPatternRewriteHandler is not registered."),
            _ => throw new NotSupportedException($"Unsupported SolutionOperation type: {operation.GetType().Name}")
        };
    }

    private static async Task<Solution> ApplyEditSpanAsync(
        Solution solution,
        EditSpanOperation op,
        CancellationToken cancellationToken)
    {
        var targets = await TargetCoordinateResolver.ResolveAsync(solution, op.Target, cancellationToken).ConfigureAwait(false);
        if (targets.Count == 0)
        {
            throw new RefactoringException(ErrorCodes.SymbolNotFound, "EditSpanOperation target did not match any location.");
        }

        var updatedSolution = solution;
        foreach (var group in targets.GroupBy(t => t.DocumentId))
        {
            var doc = updatedSolution.GetDocument(group.Key);
            if (doc is null)
            {
                continue;
            }

            var text = await doc.GetTextAsync(cancellationToken).ConfigureAwait(false);
            var changes = group
                .OrderByDescending(t => t.Span.Start)
                .Select(t => new TextChange(t.Span, op.ReplacementText))
                .ToList();

            var newText = text.WithChanges(changes);
            updatedSolution = updatedSolution.WithDocumentText(group.Key, newText, PreservationMode.PreserveIdentity);
        }

        return updatedSolution;
    }

    private static async Task<Solution> ApplyDeleteSymbolAsync(
        Solution solution,
        DeleteSymbolOperation op,
        CancellationToken cancellationToken)
    {
        var targets = await TargetCoordinateResolver.ResolveAsync(solution, op.Target, cancellationToken).ConfigureAwait(false);
        if (targets.Count == 0)
        {
            throw new RefactoringException(ErrorCodes.SymbolNotFound, "DeleteSymbolOperation target did not match any syntax node.");
        }

        var updatedSolution = solution;
        foreach (var group in targets.GroupBy(t => t.DocumentId))
        {
            var doc = updatedSolution.GetDocument(group.Key);
            if (doc is null)
            {
                continue;
            }

            var root = await doc.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
            if (root is null)
            {
                continue;
            }

            var nodesToRemove = group
                .Select(t => root.FindNode(t.Span, getInnermostNodeForTie: true))
                .Select(n => n is VariableDeclaratorSyntax { Parent: VariableDeclarationSyntax { Parent: BaseFieldDeclarationSyntax field } } && field.Declaration.Variables.Count == 1
                    ? field
                    : n)
                .Distinct()
                .ToList();

            var newRoot = root.RemoveNodes(nodesToRemove, SyntaxRemoveOptions.KeepNoTrivia);
            if (newRoot is not null)
            {
                updatedSolution = updatedSolution.WithDocumentSyntaxRoot(group.Key, newRoot);
            }
        }

        return updatedSolution;
    }

    private static async Task<Solution> ApplyRenameSymbolAsync(
        Solution solution,
        RenameSymbolOperation op,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(op.NewName))
        {
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "RenameSymbolOperation requires a non-empty NewName.");
        }

        var target = await TargetCoordinateResolver.ResolveSingleAsync(solution, op.Target, cancellationToken).ConfigureAwait(false);
        var semanticModel = await target.Document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new RefactoringException(ErrorCodes.RoslynError, "Could not obtain SemanticModel for RenameSymbolOperation.");

        var symbol = target.Symbol
            ?? semanticModel.GetDeclaredSymbol(target.Node, cancellationToken)
            ?? semanticModel.GetSymbolInfo(target.Node, cancellationToken).Symbol;

        if (symbol is null)
        {
            for (var current = target.Node; current is not null && symbol is null; current = current.Parent)
            {
                symbol = semanticModel.GetDeclaredSymbol(current, cancellationToken)
                    ?? semanticModel.GetSymbolInfo(current, cancellationToken).Symbol;
            }
        }

        if (symbol is null)
        {
            throw new RefactoringException(ErrorCodes.SymbolNotFound, "Could not resolve symbol at target coordinate for rename.");
        }

        return await Renamer.RenameSymbolAsync(
            solution,
            symbol,
            new SymbolRenameOptions(),
            op.NewName,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<Solution> ApplyMoveSymbolToFileAsync(
        Solution solution,
        MoveSymbolToFileOperation op,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(op.TargetFilePath))
        {
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "TargetFilePath is required.");
        }

        var target = await TargetCoordinateResolver.ResolveSingleAsync(solution, op.Target, cancellationToken).ConfigureAwait(false);
        var typeDecl = target.Node.AncestorsAndSelf().OfType<BaseTypeDeclarationSyntax>().FirstOrDefault()
            ?? throw new RefactoringException(ErrorCodes.SymbolNotFound, "MoveSymbolToFileOperation target must resolve to a type declaration.");

        var sourceDoc = target.Document;
        var sourceRoot = (CompilationUnitSyntax?)await sourceDoc.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new RefactoringException(ErrorCodes.RoslynError, "Could not obtain CompilationUnitSyntax.");

        var enclosingNs = typeDecl.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault();

        // Build the new compilation unit containing usings, optional namespace, and the moved type declaration
        CompilationUnitSyntax newUnit = SyntaxFactory.CompilationUnit()
            .WithUsings(sourceRoot.Usings);

        if (enclosingNs is FileScopedNamespaceDeclarationSyntax fileScopedNs)
        {
            newUnit = newUnit.WithMembers(SyntaxFactory.SingletonList<MemberDeclarationSyntax>(
                SyntaxFactory.FileScopedNamespaceDeclaration(fileScopedNs.Name)
                    .WithMembers(SyntaxFactory.SingletonList<MemberDeclarationSyntax>(typeDecl))));
        }
        else if (enclosingNs is NamespaceDeclarationSyntax blockNs)
        {
            newUnit = newUnit.WithMembers(SyntaxFactory.SingletonList<MemberDeclarationSyntax>(
                SyntaxFactory.NamespaceDeclaration(blockNs.Name)
                    .WithMembers(SyntaxFactory.SingletonList<MemberDeclarationSyntax>(typeDecl))));
        }
        else
        {
            newUnit = newUnit.WithMembers(SyntaxFactory.SingletonList<MemberDeclarationSyntax>(typeDecl));
        }

        var newFileText = newUnit.NormalizeWhitespace().ToFullString() + Environment.NewLine;

        // Remove the type from the original document
        var updatedSourceRoot = sourceRoot.RemoveNode(typeDecl, SyntaxRemoveOptions.KeepNoTrivia)
            ?? throw new RefactoringException(ErrorCodes.RoslynError, "Failed to remove moved type from source document.");

        var updatedSolution = solution.WithDocumentSyntaxRoot(sourceDoc.Id, updatedSourceRoot);

        var existingDestDoc = updatedSolution.Projects
            .SelectMany(p => p.Documents)
            .FirstOrDefault(d => string.Equals(d.FilePath, op.TargetFilePath, StringComparison.OrdinalIgnoreCase));

        if (existingDestDoc is not null)
        {
            updatedSolution = updatedSolution.WithDocumentText(existingDestDoc.Id, SourceText.From(newFileText, Encoding.UTF8));
        }
        else
        {
            var newDocId = DocumentId.CreateNewId(sourceDoc.Project.Id, Path.GetFileName(op.TargetFilePath));
            var docInfo = DocumentInfo.Create(
                newDocId,
                Path.GetFileName(op.TargetFilePath),
                loader: TextLoader.From(TextAndVersion.Create(SourceText.From(newFileText, Encoding.UTF8), VersionStamp.Create())),
                filePath: op.TargetFilePath);
            updatedSolution = updatedSolution.AddDocument(docInfo);
        }

        return updatedSolution;
    }

    private static async Task<Solution> ApplyExtractMethodAsync(
        Solution solution,
        ExtractMethodOperation op,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(op.MethodName))
        {
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "MethodName is required.");
        }

        var target = await TargetCoordinateResolver.ResolveSingleAsync(solution, op.Target, cancellationToken).ConfigureAwait(false);
        var doc = target.Document;
        var root = await doc.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new RefactoringException(ErrorCodes.RoslynError, "Could not obtain syntax root.");
        var semanticModel = await doc.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new RefactoringException(ErrorCodes.RoslynError, "Could not obtain semantic model.");

        var enclosingMethod = target.Node.AncestorsAndSelf().OfType<MethodDeclarationSyntax>().FirstOrDefault()
            ?? throw new RefactoringException(ErrorCodes.InvalidSelection, "ExtractMethodOperation target must be inside a method.");
        var enclosingType = enclosingMethod.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault()
            ?? throw new RefactoringException(ErrorCodes.InvalidSelection, "ExtractMethodOperation requires an enclosing type.");

        var isStatic = enclosingMethod.Modifiers.Any(SyntaxKind.StaticKeyword);
        var staticMod = isStatic ? "static " : string.Empty;

        if (target.Node is ExpressionSyntax expr)
        {
            var typeInfo = semanticModel.GetTypeInfo(expr, cancellationToken);
            var returnType = (typeInfo.Type ?? typeInfo.ConvertedType)?.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat) ?? "var";
            var dataFlow = semanticModel.AnalyzeDataFlow(expr);
            var inputSymbols = dataFlow?.Succeeded == true
                ? dataFlow.DataFlowsIn.Where(s => s is IParameterSymbol or ILocalSymbol).ToList()
                : [];

            var paramDeclText = string.Join(", ", inputSymbols.Select(s => $"{GetSymbolTypeDisplay(s)} {s.Name}"));
            var argCallText = string.Join(", ", inputSymbols.Select(s => s.Name));

            var callExpr = SyntaxFactory.ParseExpression($"{op.MethodName}({argCallText})").WithTriviaFrom(expr);
            var newMethodText = $"private {staticMod}{returnType} {op.MethodName}({paramDeclText}) => {expr};";
            var newMethodDecl = (MethodDeclarationSyntax)SyntaxFactory.ParseMemberDeclaration(newMethodText)!
                .WithAdditionalAnnotations(Microsoft.CodeAnalysis.Formatting.Formatter.Annotation);

            var updatedMethod = enclosingMethod.ReplaceNode(expr, callExpr);
            var updatedType = enclosingType.ReplaceNode(enclosingMethod, updatedMethod).AddMembers(newMethodDecl);
            var newRoot = root.ReplaceNode(enclosingType, updatedType);
            return solution.WithDocumentSyntaxRoot(doc.Id, newRoot);
        }

        var statement = target.Node.AncestorsAndSelf().OfType<StatementSyntax>().FirstOrDefault(s => s is not BlockSyntax)
            ?? throw new RefactoringException(ErrorCodes.InvalidSelection, "Could not locate expression or statement to extract.");

        var stmtFlow = semanticModel.AnalyzeDataFlow(statement);
        var stmtInputs = stmtFlow?.Succeeded == true
            ? stmtFlow.DataFlowsIn.Where(s => s is IParameterSymbol or ILocalSymbol).ToList()
            : [];

        var stmtParams = string.Join(", ", stmtInputs.Select(s => $"{GetSymbolTypeDisplay(s)} {s.Name}"));
        var stmtArgs = string.Join(", ", stmtInputs.Select(s => s.Name));

        var callStatement = SyntaxFactory.ParseStatement($"{op.MethodName}({stmtArgs});").WithTriviaFrom(statement);
        var extractedMethodDecl = (MethodDeclarationSyntax)SyntaxFactory.ParseMemberDeclaration(
            $"private {staticMod}void {op.MethodName}({stmtParams})\n{{\n    {statement}\n}}")!;

        var newEnclosingMethod = enclosingMethod.ReplaceNode(statement, callStatement);
        var newEnclosingType = enclosingType.ReplaceNode(enclosingMethod, newEnclosingMethod).AddMembers(extractedMethodDecl);
        var updatedRoot = root.ReplaceNode(enclosingType, newEnclosingType);
        return solution.WithDocumentSyntaxRoot(doc.Id, updatedRoot);
    }

    private static string GetSymbolTypeDisplay(ISymbol symbol) => symbol switch
    {
        ILocalSymbol local => local.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
        IParameterSymbol param => param.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
        _ => "object"
    };

    private static async Task<Solution> ApplyEncapsulateFieldAsync(
        Solution solution,
        EncapsulateFieldOperation op,
        CancellationToken cancellationToken)
    {
        var target = await TargetCoordinateResolver.ResolveSingleAsync(solution, op.Target, cancellationToken).ConfigureAwait(false);
        var doc = target.Document;
        var root = await doc.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new RefactoringException(ErrorCodes.RoslynError, "Could not obtain syntax root.");

        var fieldDecl = target.Node.AncestorsAndSelf().OfType<FieldDeclarationSyntax>().FirstOrDefault()
            ?? throw new RefactoringException(ErrorCodes.SymbolNotFound, "EncapsulateFieldOperation target must resolve to a field declaration.");
        var enclosingType = fieldDecl.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault()
            ?? throw new RefactoringException(ErrorCodes.SymbolNotFound, "Field must be inside a type declaration.");

        var variable = fieldDecl.Declaration.Variables.First();
        var rawFieldName = variable.Identifier.Text;
        var cleanBaseName = rawFieldName.TrimStart('_');
        var propName = !string.IsNullOrWhiteSpace(op.PropertyName)
            ? op.PropertyName!
            : char.ToUpperInvariant(cleanBaseName[0]) + cleanBaseName[1..];

        var backingFieldName = string.Equals(rawFieldName, propName, StringComparison.Ordinal)
            ? "_" + char.ToLowerInvariant(propName[0]) + propName[1..]
            : rawFieldName;

        var currentSolution = solution;
        if (!string.Equals(rawFieldName, backingFieldName, StringComparison.Ordinal))
        {
            var semanticModel = await doc.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
            var fieldSymbol = semanticModel?.GetDeclaredSymbol(variable, cancellationToken);
            if (fieldSymbol is not null)
            {
                currentSolution = await Renamer.RenameSymbolAsync(
                    currentSolution,
                    fieldSymbol,
                    new SymbolRenameOptions(),
                    backingFieldName,
                    cancellationToken).ConfigureAwait(false);
            }
        }

        // Re-fetch document after potential rename
        var updatedDoc = currentSolution.GetDocument(doc.Id)!;
        var updatedRoot = (await updatedDoc.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false))!;
        var updatedField = updatedRoot.DescendantNodes().OfType<FieldDeclarationSyntax>()
            .First(f => f.Declaration.Variables.Any(v => v.Identifier.Text == backingFieldName));
        var updatedType = updatedField.Ancestors().OfType<TypeDeclarationSyntax>().First();

        var isReadOnly = updatedField.Modifiers.Any(SyntaxKind.ReadOnlyKeyword);
        var isStatic = updatedField.Modifiers.Any(SyntaxKind.StaticKeyword);
        var fieldType = updatedField.Declaration.Type.ToString();
        var staticMod = isStatic ? "static " : string.Empty;

        // Make backing field private
        var nonAccessModifiers = updatedField.Modifiers
            .Where(m => !m.IsKind(SyntaxKind.PublicKeyword) &&
                        !m.IsKind(SyntaxKind.InternalKeyword) &&
                        !m.IsKind(SyntaxKind.ProtectedKeyword) &&
                        !m.IsKind(SyntaxKind.PrivateKeyword))
            .Prepend(SyntaxFactory.Token(SyntaxKind.PrivateKeyword).WithTrailingTrivia(SyntaxFactory.Space));
        var privateField = updatedField.WithModifiers(SyntaxFactory.TokenList(nonAccessModifiers));

        var propertyText = isReadOnly
            ? $"public {staticMod}{fieldType} {propName} => {backingFieldName};"
            : $"public {staticMod}{fieldType} {propName} {{ get => {backingFieldName}; set => {backingFieldName} = value; }}";
        var propertyDecl = (PropertyDeclarationSyntax)SyntaxFactory.ParseMemberDeclaration(propertyText)!;

        var newType = updatedType.ReplaceNode(updatedField, privateField).AddMembers(propertyDecl);
        var finalRoot = updatedRoot.ReplaceNode(updatedType, newType).NormalizeWhitespace();
        return currentSolution.WithDocumentSyntaxRoot(doc.Id, finalRoot);
    }

    private static async Task<Solution> ApplyChangeSignatureAsync(
        Solution solution,
        ChangeSignatureOperation op,
        CancellationToken cancellationToken)
    {
        var target = await TargetCoordinateResolver.ResolveSingleAsync(solution, op.Target, cancellationToken).ConfigureAwait(false);
        var doc = target.Document;
        var root = await doc.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new RefactoringException(ErrorCodes.RoslynError, "Could not obtain syntax root.");

        var methodDecl = target.Node.AncestorsAndSelf().OfType<MethodDeclarationSyntax>().FirstOrDefault()
            ?? throw new RefactoringException(ErrorCodes.SymbolNotFound, "ChangeSignatureOperation target must resolve to a method declaration.");

        var paramStrings = op.Parameters.Select(p =>
            string.IsNullOrWhiteSpace(p.DefaultValue)
                ? $"{p.TypeName} {p.Name}"
                : $"{p.TypeName} {p.Name} = {p.DefaultValue}");
        var newParamList = SyntaxFactory.ParseParameterList($"({string.Join(", ", paramStrings)})");

        var updatedMethod = methodDecl.WithParameterList(newParamList);
        if (!string.IsNullOrWhiteSpace(op.ReturnType))
        {
            updatedMethod = updatedMethod.WithReturnType(
                SyntaxFactory.ParseTypeName(op.ReturnType!).WithTrailingTrivia(SyntaxFactory.Space));
        }

        var newRoot = root.ReplaceNode(methodDecl, updatedMethod);
        return solution.WithDocumentSyntaxRoot(doc.Id, newRoot);
    }

    private static async Task<Solution> ApplyInlineVariableAsync(
        Solution solution,
        InlineVariableOperation op,
        CancellationToken cancellationToken)
    {
        var target = await TargetCoordinateResolver.ResolveSingleAsync(solution, op.Target, cancellationToken).ConfigureAwait(false);
        var doc = target.Document;
        var root = await doc.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new RefactoringException(ErrorCodes.RoslynError, "Could not obtain syntax root.");
        var semanticModel = await doc.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new RefactoringException(ErrorCodes.RoslynError, "Could not obtain semantic model.");

        var localDeclStmt = target.Node.AncestorsAndSelf().OfType<LocalDeclarationStatementSyntax>().FirstOrDefault()
            ?? throw new RefactoringException(ErrorCodes.SymbolNotFound, "InlineVariableOperation target must resolve to a local variable declaration.");

        var declarator = localDeclStmt.Declaration.Variables.First();
        var initializerValue = declarator.Initializer?.Value
            ?? throw new RefactoringException(ErrorCodes.InvalidSelection, "Local variable has no initializer to inline.");

        var localSymbol = semanticModel.GetDeclaredSymbol(declarator, cancellationToken)
            ?? throw new RefactoringException(ErrorCodes.SymbolNotFound, "Could not resolve local variable symbol.");

        var enclosingBlock = localDeclStmt.Parent as BlockSyntax
            ?? throw new RefactoringException(ErrorCodes.InvalidSelection, "Local variable declaration must reside in a block.");

        var replacementExpr = initializerValue is BinaryExpressionSyntax or ConditionalExpressionSyntax
            ? SyntaxFactory.ParenthesizedExpression(initializerValue.WithoutTrivia())
            : initializerValue.WithoutTrivia();

        var identifiersToReplace = enclosingBlock
            .DescendantNodes()
            .OfType<IdentifierNameSyntax>()
            .Where(id => !localDeclStmt.Span.Contains(id.Span) &&
                         SymbolEqualityComparer.Default.Equals(semanticModel.GetSymbolInfo(id, cancellationToken).Symbol, localSymbol))
            .ToList();

        var blockWithInlinedRefs = enclosingBlock.ReplaceNodes(
            identifiersToReplace,
            (orig, _) => replacementExpr.WithTriviaFrom(orig));

        var stmtToRemove = blockWithInlinedRefs.Statements
            .OfType<LocalDeclarationStatementSyntax>()
            .First(s => s.Declaration.Variables.Any(v => v.Identifier.Text == declarator.Identifier.Text));

        var finalBlock = blockWithInlinedRefs.RemoveNode(stmtToRemove, SyntaxRemoveOptions.KeepNoTrivia)!;
        var newRoot = root.ReplaceNode(enclosingBlock, finalBlock);
        return solution.WithDocumentSyntaxRoot(doc.Id, newRoot);
    }

    private static async Task<IReadOnlyList<string>> CollectIntroducedCompilationErrorsAsync(
        Solution baselineSolution,
        Solution candidateSolution,
        CancellationToken cancellationToken)
    {
        var baselineErrors = new HashSet<string>(StringComparer.Ordinal);
        foreach (var project in baselineSolution.Projects)
        {
            var comp = await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
            if (comp is null)
            {
                continue;
            }

            foreach (var diag in comp.GetDiagnostics(cancellationToken).Where(d => d.Severity == DiagnosticSeverity.Error))
            {
                baselineErrors.Add($"{project.Name}:{diag.Id}:{diag.GetMessage()}");
            }
        }

        var newErrors = new List<string>();
        foreach (var project in candidateSolution.Projects)
        {
            var comp = await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
            if (comp is null)
            {
                continue;
            }

            foreach (var diag in comp.GetDiagnostics(cancellationToken).Where(d => d.Severity == DiagnosticSeverity.Error))
            {
                var key = $"{project.Name}:{diag.Id}:{diag.GetMessage()}";
                if (!baselineErrors.Contains(key))
                {
                    var lineSpan = diag.Location.GetLineSpan();
                    var file = Path.GetFileName(lineSpan.Path);
                    var line = lineSpan.StartLinePosition.Line + 1;
                    newErrors.Add($"{diag.Id} ({file}:{line}): {diag.GetMessage()}");
                }
            }
        }

        return newErrors;
    }

    /// <summary>
    /// Computes unified diffs across all added, modified, and deleted documents between <paramref name="oldSolution"/> and <paramref name="newSolution"/>.
    /// </summary>
    /// <param name="oldSolution">Baseline solution snapshot.</param>
    /// <param name="newSolution">Candidate solution snapshot.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Ordered list of per-file unified diff summaries.</returns>
    public static async Task<IReadOnlyList<FileDiffSummary>> ComputeUnifiedDiffsAsync(
        Solution oldSolution,
        Solution newSolution,
        CancellationToken cancellationToken = default)
    {
        var summaries = new List<FileDiffSummary>();
        var changes = newSolution.GetChanges(oldSolution);

        foreach (var projectChanges in changes.GetProjectChanges())
        {
            foreach (var addedId in projectChanges.GetAddedDocuments())
            {
                var addedDoc = newSolution.GetDocument(addedId);
                if (addedDoc is null)
                {
                    continue;
                }

                var newText = (await addedDoc.GetTextAsync(cancellationToken).ConfigureAwait(false)).ToString();
                var path = addedDoc.FilePath ?? addedDoc.Name;
                summaries.Add(new FileDiffSummary
                {
                    FilePath = path,
                    ChangeKind = "Added",
                    UnifiedDiff = FormatUnifiedDiff(path, string.Empty, newText)
                });
            }

            foreach (var changedId in projectChanges.GetChangedDocuments())
            {
                var oldDoc = oldSolution.GetDocument(changedId);
                var newDoc = newSolution.GetDocument(changedId);
                if (oldDoc is null || newDoc is null)
                {
                    continue;
                }

                var oldText = (await oldDoc.GetTextAsync(cancellationToken).ConfigureAwait(false)).ToString();
                var newText = (await newDoc.GetTextAsync(cancellationToken).ConfigureAwait(false)).ToString();
                if (string.Equals(oldText, newText, StringComparison.Ordinal))
                {
                    continue;
                }

                var path = newDoc.FilePath ?? newDoc.Name;
                summaries.Add(new FileDiffSummary
                {
                    FilePath = path,
                    ChangeKind = "Modified",
                    UnifiedDiff = FormatUnifiedDiff(path, oldText, newText)
                });
            }

            foreach (var removedId in projectChanges.GetRemovedDocuments())
            {
                var oldDoc = oldSolution.GetDocument(removedId);
                if (oldDoc is null)
                {
                    continue;
                }

                var oldText = (await oldDoc.GetTextAsync(cancellationToken).ConfigureAwait(false)).ToString();
                var path = oldDoc.FilePath ?? oldDoc.Name;
                summaries.Add(new FileDiffSummary
                {
                    FilePath = path,
                    ChangeKind = "Deleted",
                    UnifiedDiff = FormatUnifiedDiff(path, oldText, string.Empty)
                });
            }
        }

        return summaries.OrderBy(s => s.FilePath, StringComparer.Ordinal).ToList();
    }

    private static string FormatUnifiedDiff(string filePath, string oldText, string newText)
    {
        var oldLines = SplitLines(oldText);
        var newLines = SplitLines(newText);

        var sb = new StringBuilder();
        var displayPath = filePath.Replace('\\', '/').TrimStart('/');
        sb.AppendLine($"--- a/{displayPath}");
        sb.AppendLine($"+++ b/{displayPath}");
        sb.AppendLine($"@@ -1,{oldLines.Length} +1,{newLines.Length} @@");

        var prefixLen = 0;
        while (prefixLen < oldLines.Length &&
               prefixLen < newLines.Length &&
               string.Equals(oldLines[prefixLen], newLines[prefixLen], StringComparison.Ordinal))
        {
            prefixLen++;
        }

        var suffixLen = 0;
        while (suffixLen < (oldLines.Length - prefixLen) &&
               suffixLen < (newLines.Length - prefixLen) &&
               string.Equals(
                   oldLines[oldLines.Length - 1 - suffixLen],
                   newLines[newLines.Length - 1 - suffixLen],
                   StringComparison.Ordinal))
        {
            suffixLen++;
        }

        var contextStart = Math.Max(0, prefixLen - 2);
        for (var i = contextStart; i < prefixLen; i++)
        {
            sb.AppendLine(" " + oldLines[i]);
        }

        for (var i = prefixLen; i < oldLines.Length - suffixLen; i++)
        {
            sb.AppendLine("-" + oldLines[i]);
        }

        for (var i = prefixLen; i < newLines.Length - suffixLen; i++)
        {
            sb.AppendLine("+" + newLines[i]);
        }

        var suffixStart = oldLines.Length - suffixLen;
        var contextEnd = Math.Min(oldLines.Length, suffixStart + 2);
        for (var i = suffixStart; i < contextEnd; i++)
        {
            sb.AppendLine(" " + oldLines[i]);
        }

        return sb.ToString();
    }

    private static string[] SplitLines(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        return text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
    }
}

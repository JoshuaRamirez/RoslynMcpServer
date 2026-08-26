using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;
using RoslynMcp.Contracts.Enums;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.FileSystem;
using RoslynMcp.Core.Refactoring.Base;
using RoslynMcp.Core.Resolution;
using RoslynMcp.Core.Workspace;

namespace RoslynMcp.Core.Refactoring.Inline;

/// <summary>
/// Inlines a const (or static readonly compile-time constant) field by replacing
/// references with a formatted literal and optionally removing the declaration.
/// </summary>
public sealed class InlineConstantOperation : RefactoringOperationBase<InlineConstantParams>
{
    private static readonly SyntaxAnnotation TargetConstantAnnotation = new("RoslynMcp.InlineConstant.Target");

    /// <summary>
    /// Creates a new inline constant operation.
    /// </summary>
    public InlineConstantOperation(WorkspaceContext context) : base(context)
    {
    }

    /// <inheritdoc />
    protected override void ValidateParams(InlineConstantParams @params) => Validate(@params);

    /// <summary>
    /// Validates inline-constant parameters. Internal so tests can exercise
    /// input rules without loading a workspace.
    /// </summary>
    internal static void Validate(InlineConstantParams @params)
    {
        if (string.IsNullOrWhiteSpace(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "sourceFile is required.");

        if (string.IsNullOrWhiteSpace(@params.ConstantName))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "constantName is required.");

        if (!PathResolver.IsAbsolutePath(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be an absolute path.");

        if (!PathResolver.IsValidCSharpFilePath(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be a .cs file.");

        if (!File.Exists(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.SourceFileNotFound, $"Source file not found: {@params.SourceFile}");

        if (!IsValidIdentifier(@params.ConstantName))
            throw new RefactoringException(ErrorCodes.InvalidSymbolName, $"'{@params.ConstantName}' is not a valid constant name.");

        if (@params.TypeName != null && string.IsNullOrWhiteSpace(@params.TypeName))
            throw new RefactoringException(ErrorCodes.InvalidSymbolName, "typeName must not be empty when provided.");
    }

    /// <summary>
    /// Rejects documents that cannot receive source edits.
    /// </summary>
    internal static void ValidateDocumentIsEditable(Document document, Microsoft.CodeAnalysis.Workspace workspace)
    {
        if (document is SourceGeneratedDocument)
        {
            throw new RefactoringException(
                ErrorCodes.DocumentNotEditable,
                $"Document '{document.Name}' is not editable (source-generated).");
        }

        if (string.IsNullOrWhiteSpace(document.FilePath) || !File.Exists(document.FilePath))
        {
            throw new RefactoringException(
                ErrorCodes.DocumentNotEditable,
                $"Document '{document.Name}' is not editable.");
        }

        if (!workspace.CanApplyChange(ApplyChangesKind.ChangeDocument))
        {
            throw new RefactoringException(
                ErrorCodes.DocumentNotEditable,
                $"Document '{document.Name}' is not editable (workspace cannot apply changes).");
        }
    }

    /// <inheritdoc />
    protected override async Task<RefactoringResult> ExecuteCoreAsync(
        Guid operationId,
        InlineConstantParams @params,
        CancellationToken cancellationToken)
    {
        var document = GetDocumentOrThrow(@params.SourceFile);
        ValidateDocumentIsEditable(document, Context.Workspace);

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
        if (root == null || semanticModel == null)
            throw new RefactoringException(ErrorCodes.RoslynError, "Could not parse file.");

        var declarator = FindConstantDeclarator(root, semanticModel, @params, cancellationToken);
        var fieldSymbol = semanticModel.GetDeclaredSymbol(declarator, cancellationToken) as IFieldSymbol
            ?? throw new RefactoringException(ErrorCodes.RoslynError, "Could not resolve constant symbol.");

        ValidateIsConstant(fieldSymbol, declarator, semanticModel, cancellationToken);

        if (@params.RemoveConstant && IsPublicApiConstant(fieldSymbol))
        {
            throw new RefactoringException(
                ErrorCodes.PublicApiConstant,
                $"Removing public API constant '{fieldSymbol.Name}' would break ABI. Set removeConstant to false to inline references and keep the declaration.");
        }

        var literal = GetLiteralRepresentation(fieldSymbol, declarator, semanticModel, cancellationToken);
        var references = await FindReferenceLocationsAsync(fieldSymbol, declarator, document, cancellationToken);

        if (references.Any(r => r.InAttribute))
        {
            throw new RefactoringException(
                ErrorCodes.ConstantInAttribute,
                $"Constant '{fieldSymbol.Name}' is used in an attribute argument and cannot be inlined.");
        }

        foreach (var reference in references)
            ValidateDocumentIsEditable(reference.Document, Context.Workspace);

        var replaceable = references.Where(r => r.CanReplace).ToList();
        var remainingNonDeclaration = references.Count(r => !r.CanReplace);

        var removeConstant = @params.RemoveConstant && remainingNonDeclaration == 0;
        var newSolution = await ApplyInliningAsync(
            document,
            declarator,
            replaceable,
            literal,
            removeConstant,
            cancellationToken);

        if (@params.Preview)
        {
            return await CreatePreviewResultAsync(
                operationId,
                @params,
                document,
                newSolution,
                replaceable.Count,
                removeConstant,
                cancellationToken);
        }

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
                Name = @params.ConstantName,
                FullyQualifiedName = fieldSymbol.ToDisplayString(),
                Kind = SymbolKindMapper.Map(fieldSymbol)
            },
            replaceable.Count,
            0);
    }

    private static VariableDeclaratorSyntax FindConstantDeclarator(
        SyntaxNode root,
        SemanticModel semanticModel,
        InlineConstantParams @params,
        CancellationToken cancellationToken)
    {
        var candidates = root.DescendantNodes()
            .OfType<VariableDeclaratorSyntax>()
            .Where(v => NamesMatch(v.Identifier, @params.ConstantName) && v.Parent?.Parent is FieldDeclarationSyntax)
            .ToList();

        if (!string.IsNullOrWhiteSpace(@params.TypeName))
        {
            candidates = candidates
                .Where(v => MatchesTypeName(semanticModel.GetDeclaredSymbol(v, cancellationToken) as IFieldSymbol, @params.TypeName))
                .ToList();
        }

        if (candidates.Count == 0)
        {
            throw new RefactoringException(
                ErrorCodes.FieldNotFound,
                string.IsNullOrWhiteSpace(@params.TypeName)
                    ? $"Constant '{@params.ConstantName}' not found."
                    : $"Constant '{@params.ConstantName}' not found on type '{@params.TypeName}'.");
        }

        if (candidates.Count > 1)
        {
            var types = candidates
                .Select(v => (semanticModel.GetDeclaredSymbol(v, cancellationToken) as IFieldSymbol)?.ContainingType.Name)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct()
                .ToList();
            throw new RefactoringException(
                ErrorCodes.SymbolAmbiguous,
                $"Multiple constants named '{@params.ConstantName}' found. Provide typeName. Options: {string.Join(", ", types)}");
        }

        return candidates[0];
    }

    private static bool MatchesTypeName(IFieldSymbol? field, string typeName)
    {
        if (field?.ContainingType == null)
            return false;

        var type = field.ContainingType;
        return type.Name == typeName
               || type.ToDisplayString() == typeName
               || type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) == typeName
               || type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) == "global::" + typeName;
    }

    private static void ValidateIsConstant(
        IFieldSymbol field,
        VariableDeclaratorSyntax declarator,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        if (field.IsConst)
            return;

        if (field.IsStatic && field.IsReadOnly && TryGetConstantValue(field, declarator, semanticModel, cancellationToken, out _))
            return;

        throw new RefactoringException(
            ErrorCodes.NotAConstant,
            $"Field '{field.Name}' is not a const or compile-time constant.");
    }

    private static bool IsPublicApiConstant(IFieldSymbol field)
    {
        for (ISymbol? current = field; current != null && current is not INamespaceSymbol; current = current.ContainingSymbol)
        {
            if (current.DeclaredAccessibility is not (Accessibility.Public or Accessibility.Protected or Accessibility.ProtectedOrInternal))
                return false;
        }

        return field.DeclaredAccessibility is Accessibility.Public or Accessibility.Protected or Accessibility.ProtectedOrInternal;
    }

    internal static ExpressionSyntax GetLiteralRepresentation(
        IFieldSymbol constant,
        VariableDeclaratorSyntax declarator,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        if (!TryGetConstantValue(constant, declarator, semanticModel, cancellationToken, out var value))
        {
            throw new RefactoringException(
                ErrorCodes.NotAConstant,
                $"Field '{constant.Name}' is not a const or compile-time constant.");
        }

        var type = constant.Type;
        if (value == null)
            return SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression);

        return type.SpecialType switch
        {
            SpecialType.System_Int32 => SyntaxFactory.LiteralExpression(
                SyntaxKind.NumericLiteralExpression,
                SyntaxFactory.Literal(System.Convert.ToInt32(value))),

            SpecialType.System_Int64 => SyntaxFactory.LiteralExpression(
                SyntaxKind.NumericLiteralExpression,
                SyntaxFactory.Literal(System.Convert.ToInt64(value))),

            SpecialType.System_Single => SyntaxFactory.LiteralExpression(
                SyntaxKind.NumericLiteralExpression,
                SyntaxFactory.Literal(System.Convert.ToSingle(value))),

            SpecialType.System_Double => SyntaxFactory.LiteralExpression(
                SyntaxKind.NumericLiteralExpression,
                SyntaxFactory.Literal(System.Convert.ToDouble(value))),

            SpecialType.System_Decimal => SyntaxFactory.LiteralExpression(
                SyntaxKind.NumericLiteralExpression,
                SyntaxFactory.Literal(System.Convert.ToDecimal(value))),

            SpecialType.System_String => SyntaxFactory.LiteralExpression(
                SyntaxKind.StringLiteralExpression,
                SyntaxFactory.Literal((string)value)),

            SpecialType.System_Boolean => SyntaxFactory.LiteralExpression(
                System.Convert.ToBoolean(value) ? SyntaxKind.TrueLiteralExpression : SyntaxKind.FalseLiteralExpression),

            SpecialType.System_Char => SyntaxFactory.LiteralExpression(
                SyntaxKind.CharacterLiteralExpression,
                SyntaxFactory.Literal(System.Convert.ToChar(value))),

            _ => throw new RefactoringException(
                ErrorCodes.NotCompileTimeConstant,
                $"Cannot inline constant of type {type}")
        };
    }

    private static bool TryGetConstantValue(
        IFieldSymbol field,
        VariableDeclaratorSyntax declarator,
        SemanticModel semanticModel,
        CancellationToken cancellationToken,
        out object? value)
    {
        if (field.HasConstantValue)
        {
            value = field.ConstantValue;
            return true;
        }

        if (declarator.Initializer != null)
        {
            var constant = semanticModel.GetConstantValue(declarator.Initializer.Value, cancellationToken);
            if (constant.HasValue)
            {
                value = constant.Value;
                return true;
            }
        }

        value = null;
        return false;
    }

    private async Task<List<ConstantReference>> FindReferenceLocationsAsync(
        IFieldSymbol fieldSymbol,
        VariableDeclaratorSyntax declarator,
        Document declaringDocument,
        CancellationToken cancellationToken)
    {
        var references = await SymbolFinder.FindReferencesAsync(
            fieldSymbol,
            Context.Solution,
            cancellationToken);

        var results = new List<ConstantReference>();
        var declarationSpan = declarator.Identifier.Span;

        foreach (var referencedSymbol in references)
        {
            foreach (var location in referencedSymbol.Locations)
            {
                if (location.Location.Kind != LocationKind.SourceFile)
                    continue;

                var document = location.Document;
                var root = await document.GetSyntaxRootAsync(cancellationToken);
                if (root == null)
                    continue;

                var node = root.FindNode(location.Location.SourceSpan, getInnermostNodeForTie: true);
                if (document.Id == declaringDocument.Id &&
                    (declarationSpan.Contains(location.Location.SourceSpan) ||
                     location.Location.SourceSpan.Contains(declarationSpan) ||
                     node.AncestorsAndSelf().OfType<VariableDeclaratorSyntax>()
                         .Any(v => v.Identifier.Span == declarationSpan)))
                {
                    continue;
                }

                if (IsInAttribute(node))
                {
                    results.Add(new ConstantReference(document, location.Location.SourceSpan, CanReplace: false, InAttribute: true));
                    continue;
                }

                if (IsInNameOf(node))
                {
                    results.Add(new ConstantReference(document, location.Location.SourceSpan, CanReplace: false, InAttribute: false));
                    continue;
                }

                var expression = FindReplaceableExpression(node, location.Location.SourceSpan);
                if (expression == null)
                {
                    throw new RefactoringException(
                        ErrorCodes.InvalidSelection,
                        $"Constant '{fieldSymbol.Name}' is used in an unsupported target and cannot be inlined.");
                }

                results.Add(new ConstantReference(document, expression.Span, CanReplace: true, InAttribute: false));
            }
        }

        return results;
    }

    private static bool IsInAttribute(SyntaxNode node) =>
        node.AncestorsAndSelf().Any(n => n is AttributeSyntax or AttributeArgumentSyntax);

    private static bool IsInNameOf(SyntaxNode node)
    {
        foreach (var invocation in node.AncestorsAndSelf().OfType<InvocationExpressionSyntax>())
        {
            if (invocation.Expression is IdentifierNameSyntax identifier &&
                identifier.Identifier.Text == "nameof")
            {
                return true;
            }
        }

        return false;
    }

    private static ExpressionSyntax? FindReplaceableExpression(SyntaxNode node, TextSpan referenceSpan)
    {
        var name = node.AncestorsAndSelf().FirstOrDefault(n =>
            n is IdentifierNameSyntax or GenericNameSyntax);
        if (name == null)
            return null;

        if (name.Parent is MemberAccessExpressionSyntax memberAccess && memberAccess.Name == name)
            return memberAccess;

        if (name.Parent is QualifiedNameSyntax qualified && qualified.Right == name)
            return qualified;

        if (name.Parent is MemberBindingExpressionSyntax)
            return null;

        if (name is ExpressionSyntax expression &&
            (name.Span.Contains(referenceSpan) || referenceSpan.Contains(name.Span)))
        {
            return expression;
        }

        return null;
    }

    private static async Task<Solution> ApplyInliningAsync(
        Document declaringDocument,
        VariableDeclaratorSyntax declarator,
        IReadOnlyList<ConstantReference> replaceable,
        ExpressionSyntax literal,
        bool removeConstant,
        CancellationToken cancellationToken)
    {
        var solution = declaringDocument.Project.Solution;

        var declaringRoot = await declaringDocument.GetSyntaxRootAsync(cancellationToken)
            ?? throw new RefactoringException(ErrorCodes.RoslynError, "Could not parse file.");
        var currentDeclarator = RematchDeclarator(declaringRoot, declarator)
            ?? throw new RefactoringException(ErrorCodes.RoslynError, "Constant declaration disappeared.");
        declaringRoot = declaringRoot.ReplaceNode(
            currentDeclarator,
            currentDeclarator.WithAdditionalAnnotations(TargetConstantAnnotation));
        solution = declaringDocument.WithSyntaxRoot(declaringRoot).Project.Solution;

        var documentsToRewrite = replaceable.Select(r => r.Document.Id).ToHashSet();
        if (removeConstant)
            documentsToRewrite.Add(declaringDocument.Id);

        foreach (var documentId in documentsToRewrite)
        {
            var document = solution.GetDocument(documentId)
                ?? throw new RefactoringException(ErrorCodes.RoslynError, "Document disappeared from solution.");
            var root = await document.GetSyntaxRootAsync(cancellationToken)
                ?? throw new RefactoringException(ErrorCodes.RoslynError, "Could not parse file.");

            var spans = replaceable
                .Where(r => r.Document.Id == documentId)
                .Select(r => r.Span)
                .ToHashSet();
            var targets = spans.Count == 0
                ? new List<ExpressionSyntax>()
                : root.DescendantNodes()
                    .OfType<ExpressionSyntax>()
                    .Where(expr => spans.Contains(expr.Span))
                    .ToList();

            if (targets.Count > 0)
            {
                var declaredType = (declarator.Parent as VariableDeclarationSyntax)?.Type;
                var rewriter = new InlineConstantRewriter(targets, literal, declaredType);
                root = rewriter.Visit(root)
                    ?? throw new RefactoringException(ErrorCodes.RoslynError, "Failed to rewrite constant references.");
            }

            if (removeConstant && documentId == declaringDocument.Id)
                root = RemoveAnnotatedConstant(root, declarator.Identifier.ValueText);

            solution = document.WithSyntaxRoot(root).Project.Solution;
        }

        return solution;
    }

    private static VariableDeclaratorSyntax? RematchDeclarator(SyntaxNode root, VariableDeclaratorSyntax original)
    {
        return root.DescendantNodes()
            .OfType<VariableDeclaratorSyntax>()
            .FirstOrDefault(v => v.Span == original.Span && v.Identifier.Text == original.Identifier.Text);
    }

    private static SyntaxNode RemoveAnnotatedConstant(SyntaxNode root, string constantName)
    {
        var annotated = root.GetAnnotatedNodes(TargetConstantAnnotation)
            .OfType<VariableDeclaratorSyntax>()
            .ToList();

        var declarator = annotated.Count == 1
            ? annotated[0]
            : root.DescendantNodes()
                .OfType<VariableDeclaratorSyntax>()
                .Where(v => NamesMatch(v.Identifier, constantName) && v.Parent?.Parent is FieldDeclarationSyntax)
                .ToList() switch
            {
                { Count: 1 } list => list[0],
                _ => throw new RefactoringException(
                    ErrorCodes.SymbolAmbiguous,
                    $"Could not uniquely identify constant '{constantName}' to remove after inlining. Provide typeName.")
            };

        if (declarator.Parent is VariableDeclarationSyntax declaration &&
            declaration.Parent is FieldDeclarationSyntax field)
        {
            if (declaration.Variables.Count == 1)
                return root.RemoveNode(field, SyntaxRemoveOptions.KeepDirectives) ?? root;

            return root.RemoveNode(declarator, SyntaxRemoveOptions.KeepNoTrivia) ?? root;
        }

        return root.RemoveNode(declarator, SyntaxRemoveOptions.KeepNoTrivia) ?? root;
    }

    private static async Task<RefactoringResult> CreatePreviewResultAsync(
        Guid operationId,
        InlineConstantParams @params,
        Document originalDocument,
        Solution newSolution,
        int usageCount,
        bool removeConstant,
        CancellationToken cancellationToken)
    {
        var pendingChanges = new List<PendingChange>();
        var originalSolution = originalDocument.Project.Solution;

        foreach (var projectChanges in newSolution.GetChanges(originalSolution).GetProjectChanges())
        {
            foreach (var docId in projectChanges.GetChangedDocuments())
            {
                var oldDoc = originalSolution.GetDocument(docId);
                var newDoc = newSolution.GetDocument(docId);
                if (oldDoc?.FilePath == null || newDoc == null)
                    continue;

                var before = await oldDoc.GetTextAsync(cancellationToken);
                var after = await newDoc.GetTextAsync(cancellationToken);
                pendingChanges.Add(new PendingChange
                {
                    File = oldDoc.FilePath,
                    ChangeType = ChangeKind.Modify,
                    Description = $"Inline constant '{@params.ConstantName}' ({usageCount} usage(s)" +
                                  (removeConstant ? ", constant removed" : "") + ")",
                    BeforeSnippet = before.ToString(),
                    AfterSnippet = after.ToString()
                });
            }
        }

        if (pendingChanges.Count == 0)
        {
            pendingChanges.Add(new PendingChange
            {
                File = @params.SourceFile,
                ChangeType = ChangeKind.Modify,
                Description = $"Inline constant '{@params.ConstantName}' ({usageCount} usage(s))",
                BeforeSnippet = null,
                AfterSnippet = null
            });
        }

        return RefactoringResult.PreviewResult(operationId, pendingChanges);
    }

    private sealed record ConstantReference(
        Document Document,
        TextSpan Span,
        bool CanReplace,
        bool InAttribute);

    internal static bool IsValidIdentifier(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;

        if (name.StartsWith('@') && name.Length > 1)
        {
            var bare = name[1..];
            return SyntaxFacts.IsValidIdentifier(bare) ||
                   SyntaxFacts.GetKeywordKind(bare) != SyntaxKind.None;
        }

        if (!SyntaxFacts.IsValidIdentifier(name))
            return false;

        var keywordKind = SyntaxFacts.GetKeywordKind(name);
        return keywordKind == SyntaxKind.None || !SyntaxFacts.IsReservedKeyword(keywordKind);
    }

    private static bool NamesMatch(SyntaxToken identifier, string constantName)
    {
        return identifier.ValueText == NormalizeIdentifier(constantName);
    }

    private static string NormalizeIdentifier(string name) =>
        name.StartsWith('@') && name.Length > 1 ? name[1..] : name;

    private sealed class InlineConstantRewriter : CSharpSyntaxRewriter
    {
        private readonly HashSet<ExpressionSyntax> _targets;
        private readonly ExpressionSyntax _literal;
        private readonly TypeSyntax? _declaredType;

        public InlineConstantRewriter(
            IReadOnlyList<ExpressionSyntax> targets,
            ExpressionSyntax literal,
            TypeSyntax? declaredType)
        {
            _targets = new HashSet<ExpressionSyntax>(targets);
            _literal = literal;
            _declaredType = declaredType;
        }

        public override SyntaxNode? Visit(SyntaxNode? node)
        {
            if (node is ExpressionSyntax expression && _targets.Contains(expression))
                return AdaptReplacement(_literal, expression, _declaredType).WithTriviaFrom(expression);

            return base.Visit(node);
        }
    }

    private static ExpressionSyntax AdaptReplacement(
        ExpressionSyntax literal,
        ExpressionSyntax original,
        TypeSyntax? declaredType)
    {
        var replacement = literal;

        if (literal.IsKind(SyntaxKind.NullLiteralExpression) &&
            declaredType != null &&
            NeedsTypedNull(original))
        {
            replacement = SyntaxFactory.CastExpression(
                declaredType.WithoutTrivia(),
                SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression));
        }

        if (NeedsReceiverParentheses(replacement, original))
            replacement = SyntaxFactory.ParenthesizedExpression(replacement);

        return replacement;
    }

    private static bool NeedsTypedNull(ExpressionSyntax original)
    {
        if (original.Parent is EqualsValueClauseSyntax &&
            original.Parent.Parent is VariableDeclaratorSyntax &&
            original.Parent.Parent.Parent is VariableDeclarationSyntax declaration &&
            declaration.Type.IsVar)
        {
            return true;
        }

        return original.Parent is ArgumentSyntax;
    }

    private static bool NeedsReceiverParentheses(ExpressionSyntax replacement, ExpressionSyntax original)
    {
        if (!IsNegativeNumeric(replacement))
            return false;

        return original.Parent switch
        {
            MemberAccessExpressionSyntax memberAccess => memberAccess.Expression == original,
            ElementAccessExpressionSyntax elementAccess => elementAccess.Expression == original,
            ConditionalAccessExpressionSyntax conditional => conditional.Expression == original,
            _ => false
        };
    }

    private static bool IsNegativeNumeric(ExpressionSyntax expression)
    {
        if (expression is PrefixUnaryExpressionSyntax prefix &&
            prefix.IsKind(SyntaxKind.UnaryMinusExpression))
        {
            return true;
        }

        if (expression is not LiteralExpressionSyntax literal)
            return false;

        return literal.Token.Value switch
        {
            int value => value < 0,
            long value => value < 0,
            float value => value < 0,
            double value => value < 0,
            decimal value => value < 0,
            _ => false
        };
    }
}

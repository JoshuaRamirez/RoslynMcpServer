using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using RoslynMcp.Contracts.Enums;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.FileSystem;
using RoslynMcp.Core.Refactoring.Base;
using RoslynMcp.Core.Workspace;

namespace RoslynMcp.Core.Refactoring.Extract;

/// <summary>
/// Promotes a local variable or expression to a class field, optionally
/// initializing the field in a constructor.
/// </summary>
public sealed class IntroduceFieldOperation : RefactoringOperationBase<IntroduceFieldParams>
{
    /// <summary>
    /// Creates a new introduce-field operation.
    /// </summary>
    public IntroduceFieldOperation(WorkspaceContext context) : base(context)
    {
    }

    /// <inheritdoc />
    protected override void ValidateParams(IntroduceFieldParams @params) => Validate(@params);

    /// <summary>
    /// Validates introduce-field parameters. Internal so tests can exercise
    /// input rules without loading a workspace.
    /// </summary>
    internal static void Validate(IntroduceFieldParams @params)
    {
        if (string.IsNullOrWhiteSpace(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "sourceFile is required.");

        if (string.IsNullOrWhiteSpace(@params.FieldName))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "fieldName is required.");

        if (!PathResolver.IsAbsolutePath(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be an absolute path.");

        if (!PathResolver.IsValidCSharpFilePath(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be a .cs file.");

        if (@params.StartLine < 1)
            throw new RefactoringException(ErrorCodes.InvalidLineNumber, "startLine must be >= 1.");

        if (@params.StartColumn < 1)
            throw new RefactoringException(ErrorCodes.InvalidColumnNumber, "startColumn must be >= 1.");

        if (@params.EndLine < 1)
            throw new RefactoringException(ErrorCodes.InvalidLineNumber, "endLine must be >= 1.");

        if (@params.EndColumn < 1)
            throw new RefactoringException(ErrorCodes.InvalidColumnNumber, "endColumn must be >= 1.");

        if (@params.EndLine < @params.StartLine ||
            (@params.EndLine == @params.StartLine && @params.EndColumn < @params.StartColumn))
            throw new RefactoringException(ErrorCodes.InvalidSelectionRange, "End must be after start.");

        if (!IsValidIdentifier(@params.FieldName))
            throw new RefactoringException(ErrorCodes.InvalidSymbolName, $"Invalid field name: {@params.FieldName}");

        if (!File.Exists(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.SourceFileNotFound, $"Source file not found: {@params.SourceFile}");
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
        IntroduceFieldParams @params,
        CancellationToken cancellationToken)
    {
        var document = GetDocumentOrThrow(@params.SourceFile);
        ValidateDocumentIsEditable(document, Context.Workspace);

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
        if (root == null || semanticModel == null)
            throw new RefactoringException(ErrorCodes.RoslynError, "Could not parse file.");

        var sourceText = await document.GetTextAsync(cancellationToken);
        var span = GetSelectionSpan(sourceText, @params);
        var node = root.FindNode(span);

        var plan = BuildPlan(node, span, semanticModel, @params, cancellationToken);

        if (@params.Preview)
            return CreatePreviewResult(operationId, @params, plan);

        var newRoot = ApplyPlan((CompilationUnitSyntax)root, plan, @params);
        var newDocument = document.WithSyntaxRoot(newRoot);
        var commitResult = await CommitChangesAsync(newDocument.Project.Solution, cancellationToken);

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
                Name = @params.FieldName,
                FullyQualifiedName = $"{plan.ContainingTypeName}.{@params.FieldName}",
                Kind = Contracts.Enums.SymbolKind.Field
            },
            plan.ReplacementCount,
            0);
    }

    private static TextSpan GetSelectionSpan(SourceText sourceText, IntroduceFieldParams @params)
    {
        if (@params.StartLine > sourceText.Lines.Count || @params.EndLine > sourceText.Lines.Count)
            throw new RefactoringException(ErrorCodes.InvalidLineNumber, "Selection is outside the file.");

        var startLine = sourceText.Lines[@params.StartLine - 1];
        var endLine = sourceText.Lines[@params.EndLine - 1];
        if (@params.StartColumn - 1 > startLine.Span.Length || @params.EndColumn - 1 > endLine.SpanIncludingLineBreak.Length)
            throw new RefactoringException(ErrorCodes.InvalidColumnNumber, "Selection column is outside the line.");

        var startPosition = startLine.Start + @params.StartColumn - 1;
        var endPosition = endLine.Start + @params.EndColumn - 1;
        if (endPosition < startPosition)
            throw new RefactoringException(ErrorCodes.InvalidSelectionRange, "End must be after start.");

        return TextSpan.FromBounds(startPosition, endPosition);
    }

    private static FieldPlan BuildPlan(
        SyntaxNode node,
        TextSpan span,
        SemanticModel semanticModel,
        IntroduceFieldParams @params,
        CancellationToken cancellationToken)
    {
        var local = FindPromotableLocal(node, span, semanticModel, cancellationToken);
        ExpressionSyntax? expression = null;
        ExpressionSyntax? initializer;
        ITypeSymbol fieldType;
        IReadOnlyList<SyntaxNode> replacements;
        LocalDeclarationStatementSyntax? declarationToRemove = null;
        VariableDeclaratorSyntax? declaratorToRemove = null;

        if (local != null)
        {
            var declarator = local.DeclaringSyntaxReferences
                .Select(r => r.GetSyntax(cancellationToken))
                .OfType<VariableDeclaratorSyntax>()
                .FirstOrDefault(v => v.Parent?.Parent is LocalDeclarationStatementSyntax);

            if (declarator == null)
            {
                throw new RefactoringException(
                    ErrorCodes.ExpressionNotFieldInitializable,
                    $"Local variable '{local.Name}' cannot be promoted to a field.");
            }

            var declaration = (LocalDeclarationStatementSyntax)declarator.Parent!.Parent!;
            initializer = declarator.Initializer?.Value;
            fieldType = local.Type;
            ValidateFieldType(fieldType);

            if (initializer != null)
                ValidateExpressionCaptures(initializer, semanticModel, local, @params.IsStatic, cancellationToken);

            var references = FindLocalReferences(declaration.SyntaxTree.GetRoot(), semanticModel, local, cancellationToken)
                .Where(id => id.Span != declarator.Identifier.Span)
                .Cast<SyntaxNode>()
                .ToList();

            replacements = references;
            if (declaration.Declaration.Variables.Count == 1)
                declarationToRemove = declaration;
            else
                declaratorToRemove = declarator;
        }
        else
        {
            expression = FindEnclosingExpression(node, span);
            if (expression == null || IsTypeContext(expression))
            {
                throw new RefactoringException(
                    ErrorCodes.ExpressionNotFound,
                    "No valid expression found at the specified location.");
            }

            var typeInfo = semanticModel.GetTypeInfo(expression, cancellationToken);
            fieldType = typeInfo.ConvertedType ?? typeInfo.Type
                ?? throw new RefactoringException(ErrorCodes.RoslynError, "Could not determine expression type.");

            ValidateFieldType(fieldType);
            ValidateExpressionCaptures(expression, semanticModel, excludedLocal: null, @params.IsStatic, cancellationToken);

            if (ContainsAwait(expression))
            {
                throw new RefactoringException(
                    ErrorCodes.ExpressionNotFieldInitializable,
                    "Expression cannot be used as a field initializer.");
            }

            if (expression is AssignmentExpressionSyntax)
            {
                throw new RefactoringException(
                    ErrorCodes.ExpressionNotFieldInitializable,
                    "Expression cannot be used as a field initializer.");
            }

            initializer = expression;
            replacements = @params.ReplaceAll
                ? FindMatchingExpressions(GetContainingTypeOrThrow(expression), expression)
                : new List<SyntaxNode> { expression };
        }

        var containingType = GetContainingTypeOrThrow(local?.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() ?? expression!);
        ValidateContainingType(containingType, @params.IsStatic, semanticModel, cancellationToken);
        ValidateNameAvailable(containingType, semanticModel, @params.FieldName, cancellationToken);
        ValidateStaticUsage(local?.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() ?? expression!, @params.IsStatic);

        if (!@params.InitializeInConstructor && initializer != null)
            ValidateInlineInitializer(initializer, semanticModel, @params.IsStatic, cancellationToken);

        if (@params.InitializeInConstructor && initializer == null)
        {
            throw new RefactoringException(
                ErrorCodes.ExpressionNotFieldInitializable,
                "Cannot initialize in a constructor without an initializer expression.");
        }

        var field = CreateFieldDeclaration(@params, fieldType, @params.InitializeInConstructor ? null : initializer);

        return new FieldPlan(
            containingType.Identifier.Text,
            fieldType.ToDisplayString(),
            field,
            replacements,
            declarationToRemove,
            declaratorToRemove,
            initializer,
            replacements.Count + (declarationToRemove != null || declaratorToRemove != null ? 1 : 0));
    }

    private static CompilationUnitSyntax ApplyPlan(
        CompilationUnitSyntax root,
        FieldPlan plan,
        IntroduceFieldParams @params)
    {
        var replaceAnn = new SyntaxAnnotation("introduce-field-replace");
        var removeDeclAnn = new SyntaxAnnotation("introduce-field-remove-decl");
        var removeVarAnn = new SyntaxAnnotation("introduce-field-remove-var");

        var annotateTargets = plan.Replacements
            .Concat(plan.DeclarationToRemove != null ? new SyntaxNode[] { plan.DeclarationToRemove } : Array.Empty<SyntaxNode>())
            .Concat(plan.DeclaratorToRemove != null ? new SyntaxNode[] { plan.DeclaratorToRemove } : Array.Empty<SyntaxNode>())
            .Distinct()
            .ToList();

        var annotated = annotateTargets.Count == 0
            ? root
            : root.ReplaceNodes(annotateTargets, (original, _) =>
            {
                var node = original;
                if (plan.Replacements.Contains(original))
                    node = node.WithAdditionalAnnotations(replaceAnn);
                if (plan.DeclarationToRemove == original)
                    node = node.WithAdditionalAnnotations(removeDeclAnn);
                if (plan.DeclaratorToRemove == original)
                    node = node.WithAdditionalAnnotations(removeVarAnn);
                return node;
            });

        var fieldRef = SyntaxFactory.IdentifierName(@params.FieldName);
        var replacements = annotated.GetAnnotatedNodes(replaceAnn).ToList();
        SyntaxNode newRoot = replacements.Count == 0
            ? annotated
            : annotated.ReplaceNodes(replacements, (original, _) => fieldRef.WithTriviaFrom(original));

        var declToRemove = newRoot.GetAnnotatedNodes(removeDeclAnn).FirstOrDefault();
        if (declToRemove != null)
        {
            newRoot = newRoot.RemoveNode(declToRemove, SyntaxRemoveOptions.KeepNoTrivia)
                ?? throw new RefactoringException(ErrorCodes.RoslynError, "Failed to remove local declaration.");
        }

        var varToRemove = newRoot.GetAnnotatedNodes(removeVarAnn).FirstOrDefault();
        if (varToRemove != null)
        {
            var declaration = varToRemove.Parent as VariableDeclarationSyntax
                ?? throw new RefactoringException(ErrorCodes.RoslynError, "Failed to update local declaration.");
            var statement = declaration.Parent as LocalDeclarationStatementSyntax
                ?? throw new RefactoringException(ErrorCodes.RoslynError, "Failed to update local declaration.");
            var newDeclaration = declaration.RemoveNode(varToRemove, SyntaxRemoveOptions.KeepNoTrivia)!;
            newRoot = newRoot.ReplaceNode(statement, statement.WithDeclaration(newDeclaration));
        }

        var updatedType = newRoot.DescendantNodes()
            .OfType<TypeDeclarationSyntax>()
            .First(t => t.Identifier.Text == plan.ContainingTypeName);
        var typeWithField = InsertField(updatedType, plan.Field);
        if (@params.InitializeInConstructor && plan.Initializer != null)
            typeWithField = EnsureConstructorInitialization(typeWithField, @params, plan.Initializer);

        return (CompilationUnitSyntax)newRoot.ReplaceNode(updatedType, typeWithField);
    }

    private static ILocalSymbol? FindPromotableLocal(
        SyntaxNode node,
        TextSpan span,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        var declarator = node.AncestorsAndSelf()
            .OfType<VariableDeclaratorSyntax>()
            .FirstOrDefault(v => v.Parent?.Parent is LocalDeclarationStatementSyntax && v.Span.Contains(span));
        if (declarator != null)
            return semanticModel.GetDeclaredSymbol(declarator, cancellationToken) as ILocalSymbol;

        var localStatement = node.AncestorsAndSelf()
            .OfType<LocalDeclarationStatementSyntax>()
            .FirstOrDefault(s => s.Span.Contains(span) && s.Declaration.Variables.Count == 1);
        if (localStatement != null)
            return semanticModel.GetDeclaredSymbol(localStatement.Declaration.Variables[0], cancellationToken) as ILocalSymbol;

        IdentifierNameSyntax? identifier = node as IdentifierNameSyntax
            ?? node.AncestorsAndSelf()
                .OfType<IdentifierNameSyntax>()
                .FirstOrDefault(id => id.Span.Contains(span));

        if (identifier == null)
            return null;

        if (semanticModel.GetSymbolInfo(identifier, cancellationToken).Symbol is ILocalSymbol local)
        {
            var syntax = local.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(cancellationToken);
            if (syntax is VariableDeclaratorSyntax v && v.Parent?.Parent is LocalDeclarationStatementSyntax)
                return local;
        }

        return null;
    }

    private static IReadOnlyList<IdentifierNameSyntax> FindLocalReferences(
        SyntaxNode root,
        SemanticModel semanticModel,
        ILocalSymbol local,
        CancellationToken cancellationToken)
    {
        return root.DescendantNodes()
            .OfType<IdentifierNameSyntax>()
            .Where(id =>
            {
                var symbol = semanticModel.GetSymbolInfo(id, cancellationToken).Symbol;
                return symbol != null && SymbolEqualityComparer.Default.Equals(symbol, local);
            })
            .ToList();
    }

    private static ExpressionSyntax? FindEnclosingExpression(SyntaxNode node, TextSpan span)
    {
        ExpressionSyntax? bestMatch = null;
        var current = node;

        while (current != null)
        {
            if (current is ExpressionSyntax expr && current.Span.Contains(span))
            {
                if (bestMatch == null || current.Span.Length <= bestMatch.Span.Length)
                    bestMatch = expr;
            }

            current = current.Parent;
        }

        return bestMatch;
    }

    private static List<SyntaxNode> FindMatchingExpressions(TypeDeclarationSyntax containingType, ExpressionSyntax original)
    {
        var normalized = NormalizeExpression(original);
        return containingType.DescendantNodes()
            .OfType<ExpressionSyntax>()
            .Where(expr => expr != original && NormalizeExpression(expr) == normalized)
            .Cast<SyntaxNode>()
            .Prepend(original)
            .ToList();
    }

    private static string NormalizeExpression(ExpressionSyntax expression) =>
        expression.NormalizeWhitespace().ToFullString().Trim();

    private static TypeDeclarationSyntax GetContainingTypeOrThrow(SyntaxNode node)
    {
        var typeDecl = node.AncestorsAndSelf().OfType<TypeDeclarationSyntax>().FirstOrDefault();
        if (typeDecl != null)
            return typeDecl;

        if (node.AncestorsAndSelf().OfType<BaseTypeDeclarationSyntax>().Any())
        {
            throw new RefactoringException(
                ErrorCodes.InvalidTargetType,
                "Cannot introduce a field into this type.");
        }

        throw new RefactoringException(
            ErrorCodes.TypeNotFound,
            "Selection must be inside a type declaration.");
    }

    private static void ValidateContainingType(
        TypeDeclarationSyntax containingType,
        bool isStaticField,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        if (containingType is InterfaceDeclarationSyntax)
        {
            throw new RefactoringException(
                ErrorCodes.InvalidTargetType,
                "Cannot introduce a field into an interface.");
        }

        var symbol = semanticModel.GetDeclaredSymbol(containingType, cancellationToken) as INamedTypeSymbol;
        if (symbol == null)
            throw new RefactoringException(ErrorCodes.RoslynError, "Could not resolve containing type.");

        if (symbol.TypeKind is TypeKind.Enum or TypeKind.Delegate or TypeKind.Interface)
        {
            throw new RefactoringException(
                ErrorCodes.InvalidTargetType,
                $"Cannot introduce a field into '{symbol.Name}'.");
        }

        if (symbol.IsStatic && !isStaticField)
        {
            throw new RefactoringException(
                ErrorCodes.InvalidTargetType,
                "Cannot introduce an instance field into a static type.");
        }
    }

    private static void ValidateNameAvailable(
        TypeDeclarationSyntax containingType,
        SemanticModel semanticModel,
        string fieldName,
        CancellationToken cancellationToken)
    {
        var symbol = semanticModel.GetDeclaredSymbol(containingType, cancellationToken) as INamedTypeSymbol;
        if (symbol?.GetMembers(fieldName).Any() == true)
        {
            throw new RefactoringException(
                ErrorCodes.NameCollision,
                $"Member '{fieldName}' already exists in type.");
        }

        var existingField = containingType.Members
            .OfType<FieldDeclarationSyntax>()
            .SelectMany(f => f.Declaration.Variables)
            .FirstOrDefault(v => v.Identifier.Text == fieldName);

        if (existingField != null)
        {
            throw new RefactoringException(
                ErrorCodes.NameCollision,
                $"Field '{fieldName}' already exists in type.");
        }
    }

    private static void ValidateFieldType(ITypeSymbol fieldType)
    {
        if (fieldType.SpecialType == SpecialType.System_Void)
        {
            throw new RefactoringException(
                ErrorCodes.ExpressionIsVoid,
                "Cannot introduce a field from a void expression.");
        }

        if (fieldType.TypeKind == TypeKind.Error || fieldType.IsAnonymousType)
        {
            throw new RefactoringException(
                ErrorCodes.InvalidTargetType,
                "Expression type is not a valid field type.");
        }
    }

    private static void ValidateExpressionCaptures(
        ExpressionSyntax expression,
        SemanticModel semanticModel,
        ILocalSymbol? excludedLocal,
        bool isStaticField,
        CancellationToken cancellationToken)
    {
        foreach (var ident in expression.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>())
        {
            var symbol = semanticModel.GetSymbolInfo(ident, cancellationToken).Symbol;
            if (symbol == null)
                continue;

            if (symbol is ILocalSymbol local &&
                (excludedLocal == null || !SymbolEqualityComparer.Default.Equals(local, excludedLocal)))
            {
                throw new RefactoringException(
                    ErrorCodes.ExpressionCapturesLocal,
                    $"Expression captures local variable '{local.Name}'.");
            }

            if (symbol is IParameterSymbol parameter && parameter.ContainingSymbol is IMethodSymbol)
            {
                throw new RefactoringException(
                    ErrorCodes.ExpressionCapturesLocal,
                    $"Expression captures parameter '{parameter.Name}'.");
            }

            if (isStaticField && symbol is ISymbol { IsStatic: false, Kind: not Microsoft.CodeAnalysis.SymbolKind.Namespace and not Microsoft.CodeAnalysis.SymbolKind.NamedType })
            {
                throw new RefactoringException(
                    ErrorCodes.ExpressionNotFieldInitializable,
                    "A static field initializer cannot reference instance members.");
            }
        }

        if (isStaticField && expression.DescendantNodesAndSelf().OfType<ThisExpressionSyntax>().Any())
        {
            throw new RefactoringException(
                ErrorCodes.ExpressionNotFieldInitializable,
                "A static field initializer cannot reference instance members.");
        }
    }

    private static void ValidateInlineInitializer(
        ExpressionSyntax initializer,
        SemanticModel semanticModel,
        bool isStaticField,
        CancellationToken cancellationToken)
    {
        if (ContainsAwait(initializer))
        {
            throw new RefactoringException(
                ErrorCodes.ExpressionNotFieldInitializable,
                "Expression cannot be used as a field initializer.");
        }

        ValidateExpressionCaptures(initializer, semanticModel, excludedLocal: null, isStaticField, cancellationToken);
    }

    private static void ValidateStaticUsage(SyntaxNode node, bool isStaticField)
    {
        if (isStaticField)
            return;

        if (IsInStaticMember(node))
        {
            throw new RefactoringException(
                ErrorCodes.InvalidTargetType,
                "Cannot introduce an instance field from a static member.");
        }
    }

    private static bool IsInStaticMember(SyntaxNode node)
    {
        foreach (var ancestor in node.AncestorsAndSelf())
        {
            switch (ancestor)
            {
                case MethodDeclarationSyntax method:
                    return method.Modifiers.Any(SyntaxKind.StaticKeyword);
                case PropertyDeclarationSyntax property:
                    return property.Modifiers.Any(SyntaxKind.StaticKeyword);
                case ConstructorDeclarationSyntax ctor:
                    return ctor.Modifiers.Any(SyntaxKind.StaticKeyword);
                case LocalFunctionStatementSyntax localFunction:
                    return localFunction.Modifiers.Any(SyntaxKind.StaticKeyword);
                case FieldDeclarationSyntax field:
                    return field.Modifiers.Any(SyntaxKind.StaticKeyword);
                case VariableDeclarationSyntax:
                    continue;
            }
        }

        return false;
    }

    private static bool ContainsAwait(SyntaxNode node) =>
        node.DescendantNodesAndSelf().Any(n => n.IsKind(SyntaxKind.AwaitExpression));

    private static bool IsTypeContext(ExpressionSyntax expression)
    {
        return expression.Parent switch
        {
            VariableDeclarationSyntax vd when vd.Type == expression => true,
            ParameterSyntax p when p.Type == expression => true,
            MethodDeclarationSyntax m when m.ReturnType == expression => true,
            PropertyDeclarationSyntax p when p.Type == expression => true,
            TypeArgumentListSyntax => true,
            QualifiedNameSyntax => true,
            AliasQualifiedNameSyntax => true,
            CastExpressionSyntax c when c.Type == expression => true,
            BinaryExpressionSyntax b when b.IsKind(SyntaxKind.AsExpression) && b.Right == expression => true,
            TypeConstraintSyntax => true,
            BaseListSyntax => true,
            SimpleBaseTypeSyntax => true,
            ArrayTypeSyntax => true,
            NullableTypeSyntax => true,
            ForEachStatementSyntax f when f.Type == expression => true,
            _ => false
        };
    }

    private static FieldDeclarationSyntax CreateFieldDeclaration(
        IntroduceFieldParams @params,
        ITypeSymbol type,
        ExpressionSyntax? initializer)
    {
        var modifiers = new List<SyntaxToken>
        {
            SyntaxFactory.Token(SyntaxKind.PrivateKeyword).WithTrailingTrivia(SyntaxFactory.Space)
        };

        if (@params.IsStatic)
            modifiers.Add(SyntaxFactory.Token(SyntaxKind.StaticKeyword).WithTrailingTrivia(SyntaxFactory.Space));

        if (@params.IsReadonly)
            modifiers.Add(SyntaxFactory.Token(SyntaxKind.ReadOnlyKeyword).WithTrailingTrivia(SyntaxFactory.Space));

        var declarator = SyntaxFactory.VariableDeclarator(@params.FieldName);
        if (initializer != null)
        {
            declarator = declarator.WithInitializer(
                SyntaxFactory.EqualsValueClause(initializer.WithoutTrivia()));
        }

        return SyntaxFactory.FieldDeclaration(
                SyntaxFactory.VariableDeclaration(
                        SyntaxFactory.ParseTypeName(type.ToDisplayString()).WithTrailingTrivia(SyntaxFactory.Space))
                    .WithVariables(SyntaxFactory.SingletonSeparatedList(declarator)))
            .WithModifiers(SyntaxFactory.TokenList(modifiers))
            .NormalizeWhitespace();
    }

    private static TypeDeclarationSyntax InsertField(
        TypeDeclarationSyntax typeDeclaration,
        FieldDeclarationSyntax field)
    {
        var members = typeDeclaration.Members.ToList();
        var insertIndex = 0;
        for (var i = 0; i < members.Count; i++)
        {
            if (members[i] is FieldDeclarationSyntax)
                insertIndex = i + 1;
            else if (insertIndex > 0)
                break;
        }

        members.Insert(insertIndex, field
            .WithLeadingTrivia(SyntaxFactory.CarriageReturnLineFeed)
            .WithTrailingTrivia(SyntaxFactory.CarriageReturnLineFeed));

        return typeDeclaration.WithMembers(SyntaxFactory.List(members));
    }

    private static TypeDeclarationSyntax EnsureConstructorInitialization(
        TypeDeclarationSyntax typeDeclaration,
        IntroduceFieldParams @params,
        ExpressionSyntax initializer)
    {
        if (!@params.IsStatic && typeDeclaration.ParameterList != null)
        {
            var hasInstanceCtor = typeDeclaration.Members
                .OfType<ConstructorDeclarationSyntax>()
                .Any(c => !c.Modifiers.Any(SyntaxKind.StaticKeyword));
            if (!hasInstanceCtor)
            {
                throw new RefactoringException(
                    ErrorCodes.ConstructorNotFound,
                    "Cannot initialize in a constructor on a type that only has a primary constructor.");
            }
        }

        var assignment = CreateAssignment(@params.FieldName, initializer);
        var constructors = typeDeclaration.Members
            .OfType<ConstructorDeclarationSyntax>()
            .Where(c => @params.IsStatic
                ? c.Modifiers.Any(SyntaxKind.StaticKeyword)
                : !c.Modifiers.Any(SyntaxKind.StaticKeyword) && !ChainsToThis(c))
            .ToList();

        if (constructors.Count == 0)
        {
            var created = CreateConstructor(typeDeclaration.Identifier.Text, @params.IsStatic, assignment);
            var members = typeDeclaration.Members.ToList();
            var insertIndex = 0;
            for (var i = 0; i < members.Count; i++)
            {
                if (members[i] is FieldDeclarationSyntax)
                    insertIndex = i + 1;
            }

            members.Insert(insertIndex, created
                .WithLeadingTrivia(SyntaxFactory.CarriageReturnLineFeed)
                .WithTrailingTrivia(SyntaxFactory.CarriageReturnLineFeed));
            return typeDeclaration.WithMembers(SyntaxFactory.List(members));
        }

        return typeDeclaration.ReplaceNodes(constructors, (original, _) => AddAssignmentToConstructor(original, assignment));
    }

    private static bool ChainsToThis(ConstructorDeclarationSyntax constructor) =>
        constructor.Initializer?.ThisOrBaseKeyword.IsKind(SyntaxKind.ThisKeyword) == true;

    private static ConstructorDeclarationSyntax AddAssignmentToConstructor(
        ConstructorDeclarationSyntax constructor,
        ExpressionStatementSyntax assignment)
    {
        if (constructor.ExpressionBody != null)
        {
            var original = SyntaxFactory.ExpressionStatement(constructor.ExpressionBody.Expression);
            var body = SyntaxFactory.Block(assignment, original);
            return constructor
                .WithExpressionBody(null)
                .WithSemicolonToken(default)
                .WithBody(body.NormalizeWhitespace());
        }

        var bodyBlock = constructor.Body ?? SyntaxFactory.Block();
        return constructor.WithBody(
            bodyBlock.WithStatements(bodyBlock.Statements.Insert(0, assignment)));
    }

    private static ConstructorDeclarationSyntax CreateConstructor(
        string typeName,
        bool isStatic,
        ExpressionStatementSyntax assignment)
    {
        var modifiers = isStatic
            ? SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.StaticKeyword).WithTrailingTrivia(SyntaxFactory.Space))
            : SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PublicKeyword).WithTrailingTrivia(SyntaxFactory.Space));

        return SyntaxFactory.ConstructorDeclaration(typeName)
            .WithModifiers(modifiers)
            .WithParameterList(SyntaxFactory.ParameterList())
            .WithBody(SyntaxFactory.Block(assignment))
            .NormalizeWhitespace();
    }

    private static ExpressionStatementSyntax CreateAssignment(string fieldName, ExpressionSyntax initializer) =>
        SyntaxFactory.ExpressionStatement(
                SyntaxFactory.AssignmentExpression(
                    SyntaxKind.SimpleAssignmentExpression,
                    SyntaxFactory.IdentifierName(fieldName),
                    initializer.WithoutTrivia()))
            .NormalizeWhitespace()
            .WithTrailingTrivia(SyntaxFactory.CarriageReturnLineFeed);

    private static RefactoringResult CreatePreviewResult(
        Guid operationId,
        IntroduceFieldParams @params,
        FieldPlan plan)
    {
        var initNote = @params.InitializeInConstructor
            ? " initialized in constructor"
            : string.Empty;

        var pendingChanges = new List<PendingChange>
        {
            new()
            {
                File = @params.SourceFile,
                ChangeType = ChangeKind.Modify,
                Description = $"Introduce field '{@params.FieldName}' of type {plan.FieldType}{initNote}",
                BeforeSnippet = "// (selected expression or local)",
                AfterSnippet = plan.Field.NormalizeWhitespace().ToFullString()
            }
        };

        return RefactoringResult.PreviewResult(operationId, pendingChanges);
    }

    private static bool IsValidIdentifier(string name)
    {
        if (string.IsNullOrEmpty(name))
            return false;
        if (!SyntaxFacts.IsValidIdentifier(name))
            return false;
        return !SyntaxFacts.IsKeywordKind(SyntaxFacts.GetKeywordKind(name));
    }

    private sealed record FieldPlan(
        string ContainingTypeName,
        string FieldType,
        FieldDeclarationSyntax Field,
        IReadOnlyList<SyntaxNode> Replacements,
        LocalDeclarationStatementSyntax? DeclarationToRemove,
        VariableDeclaratorSyntax? DeclaratorToRemove,
        ExpressionSyntax? Initializer,
        int ReplacementCount);
}

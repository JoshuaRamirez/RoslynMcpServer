using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynMcp.Contracts.Enums;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.FileSystem;
using RoslynMcp.Core.Refactoring.Base;
using RoslynMcp.Core.Refactoring.Utilities;
using RoslynMcp.Core.Workspace;

namespace RoslynMcp.Core.Refactoring.Convert;

/// <summary>
/// Converts properties between auto-property and full-property forms.
/// </summary>
public sealed class ConvertPropertyOperation : RefactoringOperationBase<ConvertPropertyParams>
{
    /// <inheritdoc />
    public ConvertPropertyOperation(WorkspaceContext context) : base(context)
    {
    }

    /// <inheritdoc />
    protected override void ValidateParams(ConvertPropertyParams @params) => Validate(@params);

    /// <summary>
    /// Validates convert-property parameters. Internal so tests can
    /// exercise input rules without loading a workspace.
    /// </summary>
    internal static void Validate(ConvertPropertyParams @params)
    {
        if (string.IsNullOrWhiteSpace(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "sourceFile is required.");

        if (string.IsNullOrWhiteSpace(@params.Direction))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "direction is required.");

        if (!PathResolver.IsAbsolutePath(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be an absolute path.");

        if (!PathResolver.IsValidCSharpFilePath(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be a .cs file.");

        if (!@params.Line.HasValue && string.IsNullOrWhiteSpace(@params.PropertyName))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "Either propertyName or line must be provided.");

        if (@params.Line.HasValue && @params.Line.Value < 1)
            throw new RefactoringException(ErrorCodes.InvalidLineNumber, "Line number must be >= 1.");

        if (@params.Column.HasValue && @params.Column.Value < 1)
            throw new RefactoringException(ErrorCodes.InvalidColumnNumber, "column must be >= 1.");

        if (!Enum.TryParse<ConversionDirection>(@params.Direction, ignoreCase: true, out var dir) ||
            (dir != ConversionDirection.ToAutoProperty && dir != ConversionDirection.ToFullProperty))
        {
            throw new RefactoringException(ErrorCodes.CannotConvert,
                "direction must be 'ToAutoProperty' or 'ToFullProperty'.");
        }

        if (!File.Exists(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.SourceFileNotFound, $"Source file not found: {@params.SourceFile}");
    }

    /// <inheritdoc />
    protected override async Task<RefactoringResult> ExecuteCoreAsync(
        Guid operationId,
        ConvertPropertyParams @params,
        CancellationToken cancellationToken)
    {
        var document = GetDocumentOrThrow(@params.SourceFile);
        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);

        if (root == null || semanticModel == null)
            throw new RefactoringException(ErrorCodes.RoslynError, "Could not parse file.");

        var direction = Enum.Parse<ConversionDirection>(@params.Direction, ignoreCase: true);

        var property = FindProperty(root, @params.PropertyName, @params.Line, @params.Column);
        if (property == null)
        {
            var location = @params.Column.HasValue
                ? $"{@params.PropertyName ?? $"at line {@params.Line}"}, column {@params.Column.Value}"
                : @params.PropertyName ?? $"at line {@params.Line}";
            throw new RefactoringException(ErrorCodes.SymbolNotFound,
                $"Property '{location}' not found.");
        }

        SyntaxNode newRoot;
        string beforeSnippet;
        string afterSnippet;

        if (direction == ConversionDirection.ToFullProperty)
        {
            (newRoot, beforeSnippet, afterSnippet) = ConvertToFullProperty(root, property);
        }
        else
        {
            (newRoot, beforeSnippet, afterSnippet) = ConvertToAutoProperty(root, property);
        }

        if (@params.Preview)
        {
            var pendingChanges = new List<PendingChange>
            {
                new()
                {
                    File = @params.SourceFile,
                    ChangeType = ChangeKind.Modify,
                    Description = $"Convert property to {direction}",
                    BeforeSnippet = beforeSnippet,
                    AfterSnippet = afterSnippet
                }
            };
            return RefactoringResult.PreviewResult(operationId, pendingChanges);
        }

        var newDocument = document.WithSyntaxRoot(newRoot);
        var commitResult = await CommitChangesAsync(newDocument.Project.Solution, cancellationToken);

        return RefactoringResult.Succeeded(operationId,
            new FileChanges { FilesModified = commitResult.FilesModified, FilesCreated = commitResult.FilesCreated, FilesDeleted = commitResult.FilesDeleted },
            new Contracts.Models.SymbolInfo { Name = property.Identifier.Text, FullyQualifiedName = property.Identifier.Text, Kind = Contracts.Enums.SymbolKind.Property },
            0, 0);
    }

    private static (SyntaxNode newRoot, string before, string after) ConvertToFullProperty(
        SyntaxNode root, PropertyDeclarationSyntax property)
    {
        // Must be an auto-property
        if (property.AccessorList == null)
            throw new RefactoringException(ErrorCodes.CannotConvert, "Property does not have accessors.");

        var isAutoProperty = property.AccessorList.Accessors.All(a => a.Body == null && a.ExpressionBody == null);
        if (!isAutoProperty)
            throw new RefactoringException(ErrorCodes.CannotConvert, "Property is already a full property.");

        // Generate backing field name
        var fieldName = "_" + char.ToLower(property.Identifier.Text[0]) + property.Identifier.Text.Substring(1);

        // Create backing field
        var backingField = SyntaxFactory.FieldDeclaration(
            SyntaxFactory.VariableDeclaration(property.Type)
                .WithVariables(SyntaxFactory.SingletonSeparatedList(
                    SyntaxFactory.VariableDeclarator(fieldName))))
            .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PrivateKeyword)))
            .NormalizeWhitespace();

        // Create full property with getter and setter
        var accessors = new List<AccessorDeclarationSyntax>();

        var hasGetter = property.AccessorList.Accessors.Any(a => a.IsKind(SyntaxKind.GetAccessorDeclaration));
        var hasSetter = property.AccessorList.Accessors.Any(a =>
            a.IsKind(SyntaxKind.SetAccessorDeclaration) || a.IsKind(SyntaxKind.InitAccessorDeclaration));

        if (hasGetter)
        {
            var getter = SyntaxFactory.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration)
                .WithBody(SyntaxFactory.Block(
                    SyntaxFactory.ReturnStatement(SyntaxFactory.IdentifierName(fieldName))));
            accessors.Add(getter);
        }

        if (hasSetter)
        {
            var originalSetter = property.AccessorList.Accessors.First(a =>
                a.IsKind(SyntaxKind.SetAccessorDeclaration) || a.IsKind(SyntaxKind.InitAccessorDeclaration));

            var setterKind = originalSetter.IsKind(SyntaxKind.InitAccessorDeclaration)
                ? SyntaxKind.InitAccessorDeclaration
                : SyntaxKind.SetAccessorDeclaration;

            var setter = SyntaxFactory.AccessorDeclaration(setterKind)
                .WithModifiers(originalSetter.Modifiers)
                .WithBody(SyntaxFactory.Block(
                    SyntaxFactory.ExpressionStatement(
                        SyntaxFactory.AssignmentExpression(
                            SyntaxKind.SimpleAssignmentExpression,
                            SyntaxFactory.IdentifierName(fieldName),
                            SyntaxFactory.IdentifierName("value")))));
            accessors.Add(setter);
        }

        var newProperty = property
            .WithAccessorList(SyntaxFactory.AccessorList(SyntaxFactory.List(accessors)))
            .WithInitializer(null)
            .WithSemicolonToken(default)
            .NormalizeWhitespace();

        var before = property.NormalizeWhitespace().ToFullString();
        var after = backingField.NormalizeWhitespace().ToFullString() + "\n" + newProperty.NormalizeWhitespace().ToFullString();

        // Insert backing field before the property
        var parent = property.Parent;
        if (parent is TypeDeclarationSyntax typeDecl)
        {
            var propertyIndex = typeDecl.Members.IndexOf(property);
            var newMembers = typeDecl.Members
                .Insert(propertyIndex, backingField.WithLeadingTrivia(SyntaxFactory.CarriageReturnLineFeed));
            var newTypeDecl = typeDecl.WithMembers(newMembers);

            // Now replace the original property (which shifted by 1) with the new full property
            var shiftedProperty = newTypeDecl.Members[propertyIndex + 1] as PropertyDeclarationSyntax;
            if (shiftedProperty != null)
            {
                newTypeDecl = (TypeDeclarationSyntax)newTypeDecl.ReplaceNode(shiftedProperty, newProperty);
            }

            return (root.ReplaceNode(typeDecl, newTypeDecl), before, after);
        }

        // Fallback: just replace the property
        return (root.ReplaceNode(property, newProperty), before, after);
    }

    private static (SyntaxNode newRoot, string before, string after) ConvertToAutoProperty(
        SyntaxNode root, PropertyDeclarationSyntax property)
    {
        if (property.AccessorList == null)
            throw new RefactoringException(ErrorCodes.CannotConvert, "Property does not have accessors.");

        var isAutoProperty = property.AccessorList.Accessors.All(a => a.Body == null && a.ExpressionBody == null);
        if (isAutoProperty)
            throw new RefactoringException(ErrorCodes.CannotConvert, "Property is already an auto-property.");

        // Create auto-property accessors
        var accessors = new List<AccessorDeclarationSyntax>();

        foreach (var accessor in property.AccessorList.Accessors)
        {
            var autoAccessor = SyntaxFactory.AccessorDeclaration(accessor.Kind())
                .WithModifiers(accessor.Modifiers)
                .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));
            accessors.Add(autoAccessor);
        }

        var newProperty = property
            .WithAccessorList(SyntaxFactory.AccessorList(SyntaxFactory.List(accessors)))
            .WithExpressionBody(null)
            .NormalizeWhitespace();

        var before = property.NormalizeWhitespace().ToFullString();
        var after = newProperty.NormalizeWhitespace().ToFullString();

        return (root.ReplaceNode(property, newProperty), before, after);
    }

    /// <summary>
    /// Finds a property. When <paramref name="column"/> is omitted, keeps
    /// today's pick (propertyName and/or line start-line <c>FirstOrDefault</c>).
    /// When set with <paramref name="line"/>, picks the smallest property
    /// whose identifier or declaration span covers that 1-based column.
    /// Column without line cannot disambiguate same-indent same-name
    /// properties across lines — keep today's first-match rather than
    /// substituting each candidate's own start line. Do not require the
    /// declaration to start on <paramref name="line"/> when column is set
    /// — a split property may put the identifier on a continuation line.
    /// </summary>
    internal static PropertyDeclarationSyntax? FindProperty(
        SyntaxNode root,
        string? propertyName,
        int? line,
        int? column)
    {
        IEnumerable<PropertyDeclarationSyntax> properties = root.DescendantNodes()
            .OfType<PropertyDeclarationSyntax>();

        if (!string.IsNullOrWhiteSpace(propertyName))
            properties = properties.Where(p => p.Identifier.Text == propertyName);

        // Column without line is not a source position: substituting each
        // candidate's own start line would match every equally-aligned
        // same-name property and could silently pick the shortest. Keep
        // today's FirstOrDefault after the propertyName filter.
        if (column.HasValue && !line.HasValue)
            return properties.FirstOrDefault();

        if (column.HasValue)
        {
            // Do not require the declaration to start on `line` — a split
            // property's identifier may live on a continuation line whose
            // declaration span still covers that column.
            return properties
                .Where(p => PropertyCoversColumn(p, line!.Value, column.Value))
                .OrderBy(p => IdentifierCoversColumn(p, line!.Value, column.Value) ? 0 : 1)
                .ThenBy(p => p.Span.Length)
                .FirstOrDefault();
        }

        if (line.HasValue)
            properties = properties.Where(p => StartLine(p) == line.Value);

        return properties.FirstOrDefault();
    }

    private static int StartLine(PropertyDeclarationSyntax property) =>
        property.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

    private static bool PropertyCoversColumn(PropertyDeclarationSyntax property, int line, int column) =>
        IdentifierCoversColumn(property, line, column) ||
        SpanCoversColumn(property.GetLocation().GetLineSpan(), line, column);

    private static bool IdentifierCoversColumn(PropertyDeclarationSyntax property, int line, int column) =>
        SpanCoversColumn(property.Identifier.GetLocation().GetLineSpan(), line, column);

    /// <summary>
    /// 1-based line/column coverage. <see cref="FileLinePositionSpan.EndLinePosition"/>
    /// is exclusive, so <paramref name="column"/> must be strictly before the
    /// exclusive end (reject <c>column &gt;= endCol</c>). Treating the end as
    /// inclusive would let the first character of an adjacent property also
    /// match the previous declaration.
    /// </summary>
    internal static bool SpanCoversColumn(FileLinePositionSpan span, int line, int column)
    {
        var startLine = span.StartLinePosition.Line + 1;
        var endLine = span.EndLinePosition.Line + 1;
        var startCol = span.StartLinePosition.Character + 1;
        var endCol = span.EndLinePosition.Character + 1;

        if (line < startLine || line > endLine)
            return false;
        if (line == startLine && column < startCol)
            return false;
        if (line == endLine && column >= endCol)
            return false;
        return true;
    }
}

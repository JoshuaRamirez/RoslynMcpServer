using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.FileSystem;
using RoslynMcp.Core.Refactoring.Base;
using RoslynMcp.Core.Refactoring.Utilities;
using RoslynMcp.Core.Workspace;

namespace RoslynMcp.Core.Refactoring.Generate;

/// <summary>
/// Generates a ToString() override for a type.
/// Honors <c>format</c> for interpolated vs StringBuilder bodies,
/// <c>includeInheritedMembers</c> to append accessible base-type members,
/// and <c>replaceExisting</c> to remove an existing non-generic parameterless
/// ToString (instance or static) before generating a fresh override.
/// </summary>
public sealed class GenerateToStringOperation : RefactoringOperationBase<GenerateToStringParams>
{
    private const string InterpolatedFormat = "interpolated";
    private const string StringBuilderFormat = "stringbuilder";

    /// <inheritdoc />
    public GenerateToStringOperation(WorkspaceContext context) : base(context)
    {
    }

    /// <inheritdoc />
    protected override void ValidateParams(GenerateToStringParams @params) => Validate(@params);

    /// <summary>
    /// Validates generate-tostring parameters. Internal so tests can exercise
    /// input rules without loading a workspace.
    /// </summary>
    internal static void Validate(GenerateToStringParams @params)
    {
        if (string.IsNullOrWhiteSpace(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "sourceFile is required.");

        if (string.IsNullOrWhiteSpace(@params.TypeName))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "typeName is required.");

        if (!PathResolver.IsAbsolutePath(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be an absolute path.");

        if (!PathResolver.IsValidCSharpFilePath(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be a .cs file.");

        ValidateFormat(@params.Format);

        if (!File.Exists(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.SourceFileNotFound, $"Source file not found: {@params.SourceFile}");
    }

    /// <inheritdoc />
    protected override async Task<RefactoringResult> ExecuteCoreAsync(
        Guid operationId,
        GenerateToStringParams @params,
        CancellationToken cancellationToken)
    {
        var document = GetDocumentOrThrow(@params.SourceFile);
        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);

        if (root == null || semanticModel == null)
            throw new RefactoringException(ErrorCodes.RoslynError, "Could not parse file.");

        var typeDecl = root.DescendantNodes().OfType<TypeDeclarationSyntax>()
            .FirstOrDefault(t => t.Identifier.Text == @params.TypeName);

        if (typeDecl == null)
            throw new RefactoringException(ErrorCodes.TypeNotFound, $"Type '{@params.TypeName}' not found.");

        var typeSymbol = semanticModel.GetDeclaredSymbol(typeDecl, cancellationToken) as INamedTypeSymbol;
        if (typeSymbol == null)
            throw new RefactoringException(ErrorCodes.RoslynError, "Could not resolve type symbol.");

        if (!@params.ReplaceExisting && HasExistingToStringOverride(typeSymbol))
            throw new RefactoringException(ErrorCodes.AlreadyHasOverride, "Type already has a ToString override.");

        var members = CollectToStringMembers(typeSymbol, @params);
        if (members.Count == 0)
            throw new RefactoringException(ErrorCodes.NoMembersToGenerate, "No fields or properties available for ToString generation.");

        var toStringMethod = GenerateToString(@params.TypeName, members, @params.Format);

        if (@params.Preview)
        {
            var code = toStringMethod.NormalizeWhitespace().ToFullString();
            var pendingChanges = new List<PendingChange>
            {
                new()
                {
                    File = @params.SourceFile,
                    ChangeType = Contracts.Enums.ChangeKind.Modify,
                    Description = BuildDescription(
                        @params.TypeName,
                        members,
                        @params.Format,
                        @params.IncludeInheritedMembers,
                        @params.ReplaceExisting),
                    BeforeSnippet = @params.ReplaceExisting
                        ? $"// Type '{@params.TypeName}' (replacing existing ToString)"
                        : $"// Type '{@params.TypeName}' (no ToString)",
                    AfterSnippet = code
                }
            };
            return RefactoringResult.PreviewResult(operationId, pendingChanges);
        }

        var solution = document.Project.Solution;
        if (@params.ReplaceExisting)
        {
            solution = await RemoveExistingToStringOverridesAcrossPartialsAsync(
                solution, typeSymbol, cancellationToken);
            document = solution.GetDocument(document.Id)
                ?? throw new RefactoringException(
                    ErrorCodes.DocumentNotEditable,
                    $"Could not locate the document for type '{@params.TypeName}'.");
            root = await document.GetSyntaxRootAsync(cancellationToken)
                ?? throw new RefactoringException(ErrorCodes.RoslynError, "Could not parse file.");
            typeDecl = FindTypeDeclaration(root, @params.TypeName, typeDecl.SpanStart)
                ?? throw new RefactoringException(ErrorCodes.TypeNotFound, $"Type '{@params.TypeName}' not found.");
        }

        var newTypeDecl = typeDecl.AddMembers(
            toStringMethod.WithLeadingTrivia(SyntaxFactory.CarriageReturnLineFeed, SyntaxFactory.CarriageReturnLineFeed));

        var newRoot = root.ReplaceNode(typeDecl, newTypeDecl);
        var newDocument = document.WithSyntaxRoot(newRoot);
        var commitResult = await CommitChangesAsync(newDocument.Project.Solution, cancellationToken);

        return RefactoringResult.Succeeded(operationId,
            new FileChanges { FilesModified = commitResult.FilesModified, FilesCreated = commitResult.FilesCreated, FilesDeleted = commitResult.FilesDeleted },
            new Contracts.Models.SymbolInfo { Name = @params.TypeName, FullyQualifiedName = @params.TypeName, Kind = Contracts.Enums.SymbolKind.Class },
            0, 0);
    }

    internal static void ValidateFormat(string? format)
    {
        if (string.IsNullOrWhiteSpace(format))
            return;

        var normalized = format.Trim();
        if (normalized.Equals(InterpolatedFormat, StringComparison.OrdinalIgnoreCase)
            || normalized.Equals(StringBuilderFormat, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        throw new RefactoringException(
            ErrorCodes.InvalidToStringFormat,
            $"Invalid format: '{format}'. Expected \"interpolated\" or \"stringbuilder\".");
    }

    internal static bool IsStringBuilderFormat(string? format) =>
        !string.IsNullOrWhiteSpace(format)
        && format.Trim().Equals(StringBuilderFormat, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Collects ToString members, then drops any named <c>ToString</c> so the
    /// generated override cannot hide <c>this.ToString</c> (recursive / CS0119).
    /// </summary>
    internal static List<ISymbol> CollectToStringMembers(INamedTypeSymbol typeSymbol, GenerateToStringParams @params)
    {
        var members = @params.IncludeInheritedMembers
            ? EqualityMemberCollector.CollectMembers(
                typeSymbol, @params.Fields, includeProperties: true, includeInheritedMembers: true)
            : EqualityMemberCollector.CollectMembers(typeSymbol, @params.Fields);

        return members.Where(m => !string.Equals(m.Name, "ToString", StringComparison.Ordinal)).ToList();
    }

    internal static bool HasExistingToStringOverride(INamedTypeSymbol typeSymbol) =>
        typeSymbol.GetMembers("ToString").OfType<IMethodSymbol>().Any(IsParameterlessToString);

    internal static string BuildDescription(
        string typeName,
        IReadOnlyList<ISymbol> members,
        string? format,
        bool includeInheritedMembers,
        bool replaceExisting = false)
    {
        var verb = replaceExisting ? "Replace" : "Generate";
        var formatName = IsStringBuilderFormat(format) ? StringBuilderFormat : InterpolatedFormat;
        var inherited = includeInheritedMembers ? " including inherited members" : "";
        var memberList = string.Join(", ", members.Select(m => m.Name));
        return $"{verb} ToString ({formatName}){inherited} for {typeName}: {memberList}";
    }

    private static async Task<Solution> RemoveExistingToStringOverridesAcrossPartialsAsync(
        Solution solution,
        INamedTypeSymbol typeSymbol,
        CancellationToken cancellationToken)
    {
        // Match by span/kind, not SyntaxNode reference — same seam as Equals.
        var membersByTreeAndPart = new Dictionary<SyntaxTree, Dictionary<int, HashSet<(int Start, int End, SyntaxKind Kind)>>>();

        foreach (var method in CollectToStringOverridesToReplace(typeSymbol))
        {
            foreach (var reference in method.DeclaringSyntaxReferences)
            {
                var syntax = await reference.GetSyntaxAsync(cancellationToken);
                if (syntax.Parent is not TypeDeclarationSyntax part)
                    continue;

                if (!membersByTreeAndPart.TryGetValue(syntax.SyntaxTree, out var byPart))
                {
                    byPart = new Dictionary<int, HashSet<(int Start, int End, SyntaxKind Kind)>>();
                    membersByTreeAndPart[syntax.SyntaxTree] = byPart;
                }

                if (!byPart.TryGetValue(part.SpanStart, out var keys))
                {
                    keys = new HashSet<(int Start, int End, SyntaxKind Kind)>();
                    byPart[part.SpanStart] = keys;
                }

                keys.Add((syntax.SpanStart, syntax.Span.End, syntax.Kind()));
            }
        }

        foreach (var (tree, byPart) in membersByTreeAndPart)
        {
            var document = solution.GetDocument(tree)
                ?? throw new RefactoringException(
                    ErrorCodes.DocumentNotEditable,
                    $"Could not locate a declaring document for type '{typeSymbol.Name}'.");
            var root = await document.GetSyntaxRootAsync(cancellationToken)
                ?? throw new RefactoringException(ErrorCodes.RoslynError, "Could not parse file.");

            var replacements = new Dictionary<TypeDeclarationSyntax, TypeDeclarationSyntax>();
            foreach (var reference in typeSymbol.DeclaringSyntaxReferences)
            {
                if (reference.SyntaxTree != tree)
                    continue;
                if (await reference.GetSyntaxAsync(cancellationToken) is not TypeDeclarationSyntax part)
                    continue;
                if (!byPart.TryGetValue(part.SpanStart, out var keys) || keys.Count == 0)
                    continue;

                var remainingMembers = part.Members
                    .Where(m => !keys.Contains((m.SpanStart, m.Span.End, m.Kind())))
                    .ToArray();
                replacements[part] = part.WithMembers(SyntaxFactory.List(remainingMembers));
            }

            if (replacements.Count == 0)
                continue;

            var newRoot = root.ReplaceNodes(replacements.Keys, (original, _) => replacements[original]);
            solution = solution.WithDocumentSyntaxRoot(document.Id, newRoot);
        }

        return solution;
    }

    /// <summary>
    /// Non-generic parameterless ToString (instance or static). Generic
    /// <c>ToString&lt;T&gt;()</c> can coexist with the generated override and
    /// is not treated as existing / not replaced.
    /// </summary>
    private static bool IsParameterlessToString(IMethodSymbol method) =>
        !method.IsImplicitlyDeclared && method.Arity == 0 && method.Parameters.Length == 0;

    private static IEnumerable<IMethodSymbol> CollectToStringOverridesToReplace(INamedTypeSymbol typeSymbol) =>
        typeSymbol.GetMembers("ToString").OfType<IMethodSymbol>().Where(IsParameterlessToString);

    private static TypeDeclarationSyntax? FindTypeDeclaration(SyntaxNode root, string typeName, int preferredSpanStart)
    {
        var matches = root.DescendantNodes().OfType<TypeDeclarationSyntax>()
            .Where(t => t.Identifier.Text == typeName)
            .ToList();
        return matches.FirstOrDefault(t => t.SpanStart == preferredSpanStart) ?? matches.FirstOrDefault();
    }

    private static MethodDeclarationSyntax GenerateToString(string typeName, List<ISymbol> members, string? format)
    {
        return IsStringBuilderFormat(format)
            ? GenerateStringBuilderToString(typeName, members)
            : GenerateInterpolatedToString(typeName, members);
    }

    private static MethodDeclarationSyntax GenerateInterpolatedToString(string typeName, List<ISymbol> members)
    {
        // $"TypeName {{ Field1 = {Field1}, Field2 = {Field2} }}"
        var parts = new List<InterpolatedStringContentSyntax>();

        parts.Add(SyntaxFactory.InterpolatedStringText(
            SyntaxFactory.Token(
                SyntaxFactory.TriviaList(),
                SyntaxKind.InterpolatedStringTextToken,
                $"{typeName} {{ ",
                $"{typeName} {{ ",
                SyntaxFactory.TriviaList())));

        for (int i = 0; i < members.Count; i++)
        {
            var member = members[i];
            var prefix = i == 0 ? "" : ", ";

            parts.Add(SyntaxFactory.InterpolatedStringText(
                SyntaxFactory.Token(
                    SyntaxFactory.TriviaList(),
                    SyntaxKind.InterpolatedStringTextToken,
                    $"{prefix}{member.Name} = ",
                    $"{prefix}{member.Name} = ",
                    SyntaxFactory.TriviaList())));

            parts.Add(SyntaxFactory.Interpolation(SyntaxFactory.IdentifierName(member.Name)));
        }

        parts.Add(SyntaxFactory.InterpolatedStringText(
            SyntaxFactory.Token(
                SyntaxFactory.TriviaList(),
                SyntaxKind.InterpolatedStringTextToken,
                " }}",
                " }}",
                SyntaxFactory.TriviaList())));

        var interpolatedString = SyntaxFactory.InterpolatedStringExpression(
            SyntaxFactory.Token(SyntaxKind.InterpolatedStringStartToken),
            SyntaxFactory.List(parts));

        return CreateToStringMethod(SyntaxFactory.Block(SyntaxFactory.ReturnStatement(interpolatedString)));
    }

    private static MethodDeclarationSyntax GenerateStringBuilderToString(string typeName, List<ISymbol> members)
    {
        // Same display shape as interpolated: TypeName { Field1 = {Field1}, Field2 = {Field2} }
        var statements = new List<StatementSyntax>
        {
            SyntaxFactory.LocalDeclarationStatement(
                SyntaxFactory.VariableDeclaration(SyntaxFactory.IdentifierName("var"))
                    .WithVariables(SyntaxFactory.SingletonSeparatedList(
                        SyntaxFactory.VariableDeclarator(SyntaxFactory.Identifier("sb"))
                            .WithInitializer(SyntaxFactory.EqualsValueClause(
                                SyntaxFactory.ObjectCreationExpression(
                                        SyntaxFactory.ParseTypeName("global::System.Text.StringBuilder"))
                                    .WithArgumentList(SyntaxFactory.ArgumentList()))))))
        };

        statements.Add(AppendLiteral($"{typeName} {{ "));

        for (int i = 0; i < members.Count; i++)
        {
            var member = members[i];
            var prefix = i == 0 ? "" : ", ";
            statements.Add(AppendLiteral($"{prefix}{member.Name} = "));
            statements.Add(AppendExpression(MemberAccess(member.Name)));
        }

        statements.Add(AppendLiteral(" }"));
        statements.Add(SyntaxFactory.ReturnStatement(
            SyntaxFactory.InvocationExpression(
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.IdentifierName("sb"),
                        SyntaxFactory.IdentifierName("ToString")))
                .WithArgumentList(SyntaxFactory.ArgumentList())));

        return CreateToStringMethod(SyntaxFactory.Block(statements));
    }

    private static MethodDeclarationSyntax CreateToStringMethod(BlockSyntax body) =>
        SyntaxFactory.MethodDeclaration(
                SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.StringKeyword)),
                "ToString")
            .WithModifiers(SyntaxFactory.TokenList(
                SyntaxFactory.Token(SyntaxKind.PublicKeyword),
                SyntaxFactory.Token(SyntaxKind.OverrideKeyword)))
            .WithBody(body)
            .NormalizeWhitespace();

    private static MemberAccessExpressionSyntax MemberAccess(string memberName) =>
        SyntaxFactory.MemberAccessExpression(
            SyntaxKind.SimpleMemberAccessExpression,
            SyntaxFactory.ThisExpression(),
            SyntaxFactory.IdentifierName(memberName));

    private static ExpressionStatementSyntax AppendLiteral(string text) =>
        AppendExpression(SyntaxFactory.LiteralExpression(
            SyntaxKind.StringLiteralExpression,
            SyntaxFactory.Literal(text)));

    private static ExpressionStatementSyntax AppendExpression(ExpressionSyntax argument) =>
        SyntaxFactory.ExpressionStatement(
            SyntaxFactory.InvocationExpression(
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.IdentifierName("sb"),
                        SyntaxFactory.IdentifierName("Append")))
                .WithArgumentList(SyntaxFactory.ArgumentList(
                    SyntaxFactory.SingletonSeparatedList(SyntaxFactory.Argument(argument)))));
}

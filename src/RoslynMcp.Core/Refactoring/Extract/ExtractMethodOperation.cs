using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.FileSystem;
using RoslynMcp.Core.Refactoring.Base;
using RoslynMcp.Core.Workspace;

namespace RoslynMcp.Core.Refactoring.Extract;

/// <summary>
/// Extracts selected code into a new method.
/// </summary>
public sealed class ExtractMethodOperation : RefactoringOperationBase<ExtractMethodParams>
{
    private static readonly Regex IdentifierPattern = new(
        @"^[A-Za-z_][A-Za-z0-9_]*$",
        RegexOptions.Compiled);

    private static readonly HashSet<string> ValidVisibilities = new(StringComparer.OrdinalIgnoreCase)
    {
        "private", "internal", "protected", "public", "private protected", "protected internal"
    };

    /// <summary>
    /// Creates a new extract method operation.
    /// </summary>
    /// <param name="context">Workspace context.</param>
    public ExtractMethodOperation(WorkspaceContext context) : base(context)
    {
    }

    /// <inheritdoc />
    protected override void ValidateParams(ExtractMethodParams @params)
    {
        if (string.IsNullOrWhiteSpace(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "sourceFile is required.");

        if (string.IsNullOrWhiteSpace(@params.MethodName))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "methodName is required.");

        if (!PathResolver.IsAbsolutePath(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be an absolute path.");

        if (!PathResolver.IsValidCSharpFilePath(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be a .cs file.");

        if (!File.Exists(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.SourceFileNotFound, $"Source file not found: {@params.SourceFile}");

        if (!IdentifierPattern.IsMatch(@params.MethodName))
            throw new RefactoringException(ErrorCodes.InvalidNewName, $"'{@params.MethodName}' is not a valid method name.");

        if (SyntaxFacts.GetKeywordKind(@params.MethodName) != SyntaxKind.None)
            throw new RefactoringException(ErrorCodes.ReservedKeyword, $"'{@params.MethodName}' is a C# reserved keyword.");

        if (@params.StartLine < 1 || @params.EndLine < 1)
            throw new RefactoringException(ErrorCodes.InvalidLineNumber, "Line numbers must be >= 1.");

        if (@params.StartColumn < 1 || @params.EndColumn < 1)
            throw new RefactoringException(ErrorCodes.InvalidColumnNumber, "Column numbers must be >= 1.");

        if (@params.StartLine > @params.EndLine ||
            (@params.StartLine == @params.EndLine && @params.StartColumn >= @params.EndColumn))
            throw new RefactoringException(ErrorCodes.InvalidSelectionRange, "Selection start must be before end.");

        if (!ValidVisibilities.Contains(@params.Visibility))
            throw new RefactoringException(ErrorCodes.InvalidVisibility, $"'{@params.Visibility}' is not a valid visibility modifier.");
    }

    /// <inheritdoc />
    protected override async Task<RefactoringResult> ExecuteCoreAsync(
        Guid operationId,
        ExtractMethodParams @params,
        CancellationToken cancellationToken)
    {
        var document = GetDocumentOrThrow(@params.SourceFile);
        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);

        if (root == null || semanticModel == null)
        {
            throw new RefactoringException(ErrorCodes.RoslynError, "Could not parse file.");
        }

        // Get the selection span
        var text = await document.GetTextAsync(cancellationToken);
        var startPosition = GetPosition(text, @params.StartLine, @params.StartColumn);
        var endPosition = GetPosition(text, @params.EndLine, @params.EndColumn);
        var selectionSpan = TextSpan.FromBounds(startPosition, endPosition);

        // Find nodes in selection
        var selectedNodes = GetSelectedNodes(root, selectionSpan);
        if (selectedNodes.Count == 0)
        {
            throw new RefactoringException(
                ErrorCodes.EmptySelection,
                "No code selected for extraction.");
        }

        // Validate selection
        ValidateSelection(selectedNodes, semanticModel, cancellationToken);

        // Find containing method and type
        var containingMethod = selectedNodes[0].Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault()
            ?? selectedNodes[0].Ancestors().OfType<LocalFunctionStatementSyntax>().FirstOrDefault() as SyntaxNode
            ?? throw new RefactoringException(ErrorCodes.InvalidSelection, "Selection must be inside a method.");

        var containingType = containingMethod.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault()
            ?? throw new RefactoringException(ErrorCodes.InvalidSelection, "Selection must be inside a type.");

        // Analyze data flow
        var dataFlowAnalysis = AnalyzeDataFlow(selectedNodes, semanticModel, cancellationToken);

        // Build the extracted method
        var (extractedMethod, callExpression) = BuildExtractedMethod(
            @params,
            selectedNodes,
            dataFlowAnalysis,
            containingMethod,
            semanticModel,
            cancellationToken);

        // Create the new syntax tree
        var newRoot = CreateNewRoot(root, containingType, containingMethod, selectedNodes, extractedMethod, callExpression);

        // If preview mode, return without applying (but include before/after snippets)
        if (@params.Preview)
        {
            return CreatePreviewResult(
                operationId,
                @params,
                document.FilePath!,
                selectedNodes,
                extractedMethod,
                callExpression);
        }

        // Update document
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
                Name = @params.MethodName,
                FullyQualifiedName = @params.MethodName,
                Kind = Contracts.Enums.SymbolKind.Method
            },
            0,
            0);
    }

    /// <summary>
    /// Converts 1-based line/column to absolute position with bounds validation.
    /// </summary>
    /// <param name="text">Source text to navigate.</param>
    /// <param name="line">1-based line number.</param>
    /// <param name="column">1-based column number.</param>
    /// <returns>Absolute position in text.</returns>
    /// <exception cref="RefactoringException">Thrown if line/column is out of bounds.</exception>
    private static int GetPosition(SourceText text, int line, int column)
    {
        var lineIndex = line - 1; // Convert to 0-based

        if (lineIndex < 0 || lineIndex >= text.Lines.Count)
        {
            throw new RefactoringException(
                ErrorCodes.InvalidLineNumber,
                $"Line {line} is out of range. File has {text.Lines.Count} lines.");
        }

        var lineInfo = text.Lines[lineIndex];
        var columnIndex = column - 1; // Convert to 0-based
        var lineLength = lineInfo.End - lineInfo.Start;

        if (columnIndex < 0 || columnIndex > lineLength)
        {
            throw new RefactoringException(
                ErrorCodes.InvalidColumnNumber,
                $"Column {column} is out of range for line {line} (line has {lineLength} characters).");
        }

        return lineInfo.Start + columnIndex;
    }

    private static List<SyntaxNode> GetSelectedNodes(SyntaxNode root, TextSpan selection)
    {
        var nodes = new List<SyntaxNode>();

        // Find the innermost node that contains the selection
        var node = root.FindNode(selection, getInnermostNodeForTie: true);

        // If it's a statement, collect all statements in selection
        if (node is StatementSyntax)
        {
            var parent = node.Parent;
            if (parent != null)
            {
                foreach (var child in parent.ChildNodes())
                {
                    if (child.Span.IntersectsWith(selection) && child is StatementSyntax)
                    {
                        nodes.Add(child);
                    }
                }
            }
        }
        else if (node is ExpressionSyntax)
        {
            nodes.Add(node);
        }

        if (nodes.Count == 0 && node != null)
        {
            nodes.Add(node);
        }

        return nodes;
    }

    private static void ValidateSelection(
        List<SyntaxNode> nodes,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        foreach (var node in nodes)
        {
            // Check for yield statements
            if (node.DescendantNodes().Any(n => n is YieldStatementSyntax))
            {
                throw new RefactoringException(
                    ErrorCodes.ContainsYield,
                    "Cannot extract code containing yield statements.");
            }

            // Check for multiple returns (simple heuristic)
            var returns = node.DescendantNodes().OfType<ReturnStatementSyntax>().ToList();
            if (returns.Count > 1)
            {
                throw new RefactoringException(
                    ErrorCodes.MultipleExitPoints,
                    "Selection has multiple return statements. Simplify before extraction.");
            }
        }
    }

    /// <summary>
    /// Contains data flow analysis results for method extraction.
    /// </summary>
    /// <remarks>
    /// This class holds information about:
    /// <list type="bullet">
    ///   <item>Variables that need to be passed as parameters (DataFlowsIn)</item>
    ///   <item>Variables that are written and used after selection (DataFlowsOut)</item>
    ///   <item>Return type if the selection produces a value</item>
    ///   <item>Whether ref/out parameters are needed</item>
    /// </list>
    /// </remarks>
    private sealed class DataFlowInfo
    {
        /// <summary>Variables that flow into the selection and must become parameters.</summary>
        public List<ISymbol> Parameters { get; } = new();

        /// <summary>The return type of the extracted method, if any.</summary>
        public ITypeSymbol? ReturnType { get; set; }

        /// <summary>The variable that should be returned, if any.</summary>
        public ISymbol? ReturnVariable { get; set; }

        /// <summary>Variables declared in selection but used after - require out params or return.</summary>
        public List<ISymbol> LocalsToHoist { get; } = new();

        /// <summary>True if any variables need ref parameters (modified in selection, used after).</summary>
        public bool RequiresRef { get; set; }

        /// <summary>True if the selection contains await expressions, requiring async method.</summary>
        public bool ContainsAwait { get; set; }

        /// <summary>Variables written inside selection that flow out (may need ref/out).</summary>
        public List<ISymbol> VariablesWritten { get; } = new();
    }

    /// <summary>
    /// Analyzes data flow for the selected nodes using Roslyn's semantic analysis.
    /// </summary>
    /// <param name="nodes">Selected syntax nodes.</param>
    /// <param name="semanticModel">Semantic model for analysis.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Data flow analysis results.</returns>
    /// <remarks>
    /// Uses SemanticModel.AnalyzeDataFlow() for accurate analysis of:
    /// <list type="bullet">
    ///   <item>Variables flowing in (read before written in selection)</item>
    ///   <item>Variables flowing out (written in selection, read after)</item>
    ///   <item>Variables declared in selection</item>
    ///   <item>Complex scenarios like loops, conditional assignments, out params</item>
    /// </list>
    /// Falls back to manual analysis if Roslyn data flow analysis is not available.
    /// </remarks>
    private static DataFlowInfo AnalyzeDataFlow(
        List<SyntaxNode> nodes,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        var info = new DataFlowInfo();

        // Try to use Roslyn's built-in data flow analysis for accurate results
        var dataFlowResult = TryAnalyzeDataFlowWithRoslyn(nodes, semanticModel);

        if (dataFlowResult != null && dataFlowResult.Succeeded)
        {
            // Use Roslyn's accurate data flow analysis
            PopulateFromDataFlowAnalysis(info, dataFlowResult);
        }
        else
        {
            // Fall back to manual analysis for nodes that don't support data flow
            PopulateFromManualAnalysis(info, nodes, semanticModel, cancellationToken);
        }

        // Determine return type based on last node
        DetermineReturnType(info, nodes, semanticModel, cancellationToken);

        // Check for await expressions
        foreach (var node in nodes)
        {
            if (node.DescendantNodesAndSelf().Any(n => n is AwaitExpressionSyntax))
            {
                info.ContainsAwait = true;
                break;
            }
        }

        return info;
    }

    /// <summary>
    /// Attempts to use Roslyn's AnalyzeDataFlow for the given nodes.
    /// </summary>
    /// <param name="nodes">The selected nodes to analyze.</param>
    /// <param name="semanticModel">The semantic model.</param>
    /// <returns>DataFlowAnalysis result or null if analysis fails.</returns>
    private static DataFlowAnalysis? TryAnalyzeDataFlowWithRoslyn(
        List<SyntaxNode> nodes,
        SemanticModel semanticModel)
    {
        if (nodes.Count == 0) return null;

        // For single statement, analyze it directly
        if (nodes.Count == 1 && nodes[0] is StatementSyntax singleStatement)
        {
            try
            {
                return semanticModel.AnalyzeDataFlow(singleStatement);
            }
            catch
            {
                return null;
            }
        }

        // For multiple statements, try to analyze the range
        if (nodes.All(n => n is StatementSyntax))
        {
            var firstStatement = (StatementSyntax)nodes[0];
            var lastStatement = (StatementSyntax)nodes[^1];

            try
            {
                return semanticModel.AnalyzeDataFlow(firstStatement, lastStatement);
            }
            catch
            {
                return null;
            }
        }

        // For expression, try to analyze it
        if (nodes.Count == 1 && nodes[0] is ExpressionSyntax expression)
        {
            try
            {
                return semanticModel.AnalyzeDataFlow(expression);
            }
            catch
            {
                return null;
            }
        }

        return null;
    }

    /// <summary>
    /// Populates DataFlowInfo from Roslyn's DataFlowAnalysis results.
    /// </summary>
    /// <param name="info">The DataFlowInfo to populate.</param>
    /// <param name="dataFlow">Roslyn's data flow analysis result.</param>
    private static void PopulateFromDataFlowAnalysis(DataFlowInfo info, DataFlowAnalysis dataFlow)
    {
        // Variables that flow into the selection (read before written) become parameters
        foreach (var symbol in dataFlow.DataFlowsIn)
        {
            if (symbol is ILocalSymbol || symbol is IParameterSymbol)
            {
                info.Parameters.Add(symbol);
            }
        }

        // Variables that flow out (written in selection, read after) may need ref/out
        foreach (var symbol in dataFlow.DataFlowsOut)
        {
            if (symbol is ILocalSymbol || symbol is IParameterSymbol)
            {
                info.VariablesWritten.Add(symbol);

                // If variable flows in AND out, it needs ref
                if (dataFlow.DataFlowsIn.Contains(symbol))
                {
                    info.RequiresRef = true;
                }
                else
                {
                    // Variable only flows out - could be return value or out param
                    info.LocalsToHoist.Add(symbol);
                }
            }
        }
    }

    /// <summary>
    /// Fallback manual analysis when Roslyn data flow is not available.
    /// </summary>
    /// <param name="info">The DataFlowInfo to populate.</param>
    /// <param name="nodes">The selected nodes.</param>
    /// <param name="semanticModel">The semantic model.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    private static void PopulateFromManualAnalysis(
        DataFlowInfo info,
        List<SyntaxNode> nodes,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        var referencedSymbols = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        var declaredSymbols = new HashSet<ISymbol>(SymbolEqualityComparer.Default);

        foreach (var node in nodes)
        {
            foreach (var descendant in node.DescendantNodes())
            {
                var declared = semanticModel.GetDeclaredSymbol(descendant, cancellationToken);
                if (declared != null)
                {
                    declaredSymbols.Add(declared);
                }
            }

            foreach (var identifier in node.DescendantNodes().OfType<IdentifierNameSyntax>())
            {
                var symbolInfo = semanticModel.GetSymbolInfo(identifier, cancellationToken);
                if (symbolInfo.Symbol != null)
                {
                    referencedSymbols.Add(symbolInfo.Symbol);
                }
            }
        }

        foreach (var symbol in referencedSymbols)
        {
            if (!declaredSymbols.Contains(symbol) &&
                (symbol is ILocalSymbol || symbol is IParameterSymbol))
            {
                info.Parameters.Add(symbol);
            }
        }
    }

    /// <summary>
    /// Determines the return type based on the last node in the selection.
    /// </summary>
    /// <param name="info">The DataFlowInfo to update.</param>
    /// <param name="nodes">The selected nodes.</param>
    /// <param name="semanticModel">The semantic model.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    private static void DetermineReturnType(
        DataFlowInfo info,
        List<SyntaxNode> nodes,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        var lastNode = nodes[^1];

        if (lastNode is ReturnStatementSyntax returnStmt && returnStmt.Expression != null)
        {
            var typeInfo = semanticModel.GetTypeInfo(returnStmt.Expression, cancellationToken);
            info.ReturnType = typeInfo.Type;
        }
        else if (lastNode is ExpressionStatementSyntax exprStmt)
        {
            var typeInfo = semanticModel.GetTypeInfo(exprStmt.Expression, cancellationToken);
            if (typeInfo.Type != null && typeInfo.Type.SpecialType != SpecialType.System_Void)
            {
                info.ReturnType = typeInfo.Type;
            }
        }
        else if (info.LocalsToHoist.Count == 1)
        {
            // Single variable that flows out can be the return value
            var returnVar = info.LocalsToHoist[0];
            info.ReturnVariable = returnVar;
            info.ReturnType = returnVar switch
            {
                ILocalSymbol local => local.Type,
                IParameterSymbol param => param.Type,
                _ => null
            };
        }
    }

    private static (MethodDeclarationSyntax Method, ExpressionSyntax Call) BuildExtractedMethod(
        ExtractMethodParams @params,
        List<SyntaxNode> selectedNodes,
        DataFlowInfo dataFlow,
        SyntaxNode containingMethod,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        // Build parameters
        var parameters = new List<ParameterSyntax>();
        var arguments = new List<ArgumentSyntax>();

        foreach (var param in dataFlow.Parameters)
        {
            var type = param switch
            {
                ILocalSymbol local => local.Type,
                IParameterSymbol p => p.Type,
                _ => null
            };

            if (type != null)
            {
                parameters.Add(SyntaxFactory.Parameter(SyntaxFactory.Identifier(param.Name))
                    .WithType(SyntaxFactory.ParseTypeName(type.ToDisplayString())));
                arguments.Add(SyntaxFactory.Argument(SyntaxFactory.IdentifierName(param.Name)));
            }
        }

        // Determine return type - wrap in Task<T> if async
        TypeSyntax returnType;
        if (dataFlow.ContainsAwait)
        {
            if (dataFlow.ReturnType != null)
            {
                // async method with return value -> Task<T>
                returnType = SyntaxFactory.GenericName(
                    SyntaxFactory.Identifier("Task"),
                    SyntaxFactory.TypeArgumentList(
                        SyntaxFactory.SingletonSeparatedList<TypeSyntax>(
                            SyntaxFactory.ParseTypeName(dataFlow.ReturnType.ToDisplayString()))));
            }
            else
            {
                // async method returning void -> Task
                returnType = SyntaxFactory.ParseTypeName("Task");
            }
        }
        else
        {
            returnType = dataFlow.ReturnType != null
                ? SyntaxFactory.ParseTypeName(dataFlow.ReturnType.ToDisplayString())
                : SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.VoidKeyword));
        }

        // Build method body.
        // Preserve inner trivia (comments within statements) but strip only the outer
        // leading/trailing whitespace that would cause formatting issues in the new method.
        var statements = selectedNodes
            .OfType<StatementSyntax>()
            .Select(s => PreserveInnerTrivia(s))
            .ToList();

        if (statements.Count == 0 && selectedNodes.Count > 0 && selectedNodes[0] is ExpressionSyntax expr)
        {
            // Single expression - make it a return statement
            statements.Add(SyntaxFactory.ReturnStatement(expr));
        }

        var body = SyntaxFactory.Block(statements);

        // Build modifiers - handle multi-word visibility modifiers
        var modifiers = new List<SyntaxToken>();
        var visibility = @params.Visibility.ToLowerInvariant();

        switch (visibility)
        {
            case "private protected":
                modifiers.Add(SyntaxFactory.Token(SyntaxKind.PrivateKeyword));
                modifiers.Add(SyntaxFactory.Token(SyntaxKind.ProtectedKeyword));
                break;
            case "protected internal":
                modifiers.Add(SyntaxFactory.Token(SyntaxKind.ProtectedKeyword));
                modifiers.Add(SyntaxFactory.Token(SyntaxKind.InternalKeyword));
                break;
            case "internal":
                modifiers.Add(SyntaxFactory.Token(SyntaxKind.InternalKeyword));
                break;
            case "protected":
                modifiers.Add(SyntaxFactory.Token(SyntaxKind.ProtectedKeyword));
                break;
            case "public":
                modifiers.Add(SyntaxFactory.Token(SyntaxKind.PublicKeyword));
                break;
            case "private":
            default:
                modifiers.Add(SyntaxFactory.Token(SyntaxKind.PrivateKeyword));
                break;
        }

        // Check if static is needed
        bool makeStatic = @params.MakeStatic ?? false;
        if (!makeStatic && containingMethod is MethodDeclarationSyntax methodDecl)
        {
            makeStatic = methodDecl.Modifiers.Any(SyntaxKind.StaticKeyword);
        }

        if (makeStatic)
        {
            modifiers.Add(SyntaxFactory.Token(SyntaxKind.StaticKeyword));
        }

        // Add async modifier if method contains await expressions
        if (dataFlow.ContainsAwait)
        {
            modifiers.Add(SyntaxFactory.Token(SyntaxKind.AsyncKeyword));
        }

        var method = SyntaxFactory.MethodDeclaration(returnType, @params.MethodName)
            .WithModifiers(SyntaxFactory.TokenList(modifiers))
            .WithParameterList(SyntaxFactory.ParameterList(SyntaxFactory.SeparatedList(parameters)))
            .WithBody(body)
            .NormalizeWhitespace();

        // Build call expression - wrap in await if method is async
        ExpressionSyntax call = SyntaxFactory.InvocationExpression(
            SyntaxFactory.IdentifierName(@params.MethodName),
            SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(arguments)));

        if (dataFlow.ContainsAwait)
        {
            call = SyntaxFactory.AwaitExpression(call);
        }

        return (method, call);
    }

    private static SyntaxNode CreateNewRoot(
        SyntaxNode root,
        TypeDeclarationSyntax containingType,
        SyntaxNode containingMethod,
        List<SyntaxNode> selectedNodes,
        MethodDeclarationSyntax extractedMethod,
        ExpressionSyntax callExpression)
    {
        // Create the replacement statement
        StatementSyntax callStatement;
        if (extractedMethod.ReturnType is PredefinedTypeSyntax predefined &&
            predefined.Keyword.IsKind(SyntaxKind.VoidKeyword))
        {
            callStatement = SyntaxFactory.ExpressionStatement(callExpression);
        }
        else
        {
            // If there's a return type, we might need to assign or return
            callStatement = SyntaxFactory.ExpressionStatement(callExpression);
        }

        // Replace selected nodes with call
        var firstNode = selectedNodes[0];
        var lastNode = selectedNodes[^1];

        var newRoot = root;

        // Remove all selected nodes except first, replace first with call
        if (selectedNodes.Count == 1)
        {
            if (firstNode is StatementSyntax)
            {
                newRoot = root.ReplaceNode(firstNode, callStatement
                    .WithLeadingTrivia(firstNode.GetLeadingTrivia())
                    .WithTrailingTrivia(firstNode.GetTrailingTrivia()));
            }
            else if (firstNode is ExpressionSyntax)
            {
                newRoot = root.ReplaceNode(firstNode, callExpression);
            }
        }
        else
        {
            // Multiple statements - more complex replacement
            var parent = firstNode.Parent;
            if (parent is BlockSyntax block)
            {
                var newStatements = new List<StatementSyntax>();
                bool replaced = false;

                foreach (var stmt in block.Statements)
                {
                    if (selectedNodes.Contains(stmt))
                    {
                        if (!replaced)
                        {
                            newStatements.Add(callStatement
                                .WithLeadingTrivia(stmt.GetLeadingTrivia()));
                            replaced = true;
                        }
                        // Skip other selected statements
                    }
                    else
                    {
                        newStatements.Add(stmt);
                    }
                }

                var newBlock = block.WithStatements(SyntaxFactory.List(newStatements));
                newRoot = root.ReplaceNode(block, newBlock);
            }
        }

        // Add the extracted method to the type
        var currentType = newRoot.DescendantNodes().OfType<TypeDeclarationSyntax>()
            .First(t => t.Identifier.Text == containingType.Identifier.Text);

        var newType = currentType.AddMembers(extractedMethod
            .WithLeadingTrivia(SyntaxFactory.CarriageReturnLineFeed, SyntaxFactory.CarriageReturnLineFeed)
            .WithTrailingTrivia(SyntaxFactory.CarriageReturnLineFeed));

        newRoot = newRoot.ReplaceNode(currentType, newType);

        return newRoot;
    }

    /// <summary>
    /// Creates a preview result with before/after code snippets.
    /// </summary>
    private static RefactoringResult CreatePreviewResult(
        Guid operationId,
        ExtractMethodParams @params,
        string filePath,
        List<SyntaxNode> selectedNodes,
        MethodDeclarationSyntax extractedMethod,
        ExpressionSyntax callExpression)
    {
        // Build the "before" snippet from the selected nodes
        var beforeSnippet = string.Join(Environment.NewLine,
            selectedNodes.Select(n => n.ToFullString().Trim()));

        // Build the "after" snippet showing the method call and new method
        var callStatement = SyntaxFactory.ExpressionStatement(callExpression)
            .NormalizeWhitespace()
            .ToFullString();

        var afterSnippet = $"// Call site replacement:\r\n{callStatement}\r\n\r\n// New extracted method:\r\n{extractedMethod.ToFullString()}";

        var pendingChanges = new List<PendingChange>
        {
            new()
            {
                File = filePath,
                ChangeType = Contracts.Enums.ChangeKind.Modify,
                Description = $"Extract method '{@params.MethodName}' from lines {@params.StartLine}-{@params.EndLine}",
                StartLine = @params.StartLine,
                EndLine = @params.EndLine,
                BeforeSnippet = beforeSnippet,
                AfterSnippet = afterSnippet
            }
        };

        return RefactoringResult.PreviewResult(operationId, pendingChanges);
    }

    /// <summary>
    /// Preserves inner trivia (comments, etc.) within a statement while stripping only
    /// the outermost leading whitespace/newlines for formatting in the new method.
    /// </summary>
    /// <param name="statement">The statement to process.</param>
    /// <returns>The statement with preserved inner trivia and normalized outer formatting.</returns>
    /// <remarks>
    /// This method:
    /// <list type="bullet">
    ///   <item>Strips leading whitespace/newlines but preserves leading comments</item>
    ///   <item>Strips trailing whitespace/newlines but preserves trailing comments</item>
    ///   <item>Leaves all inner trivia (comments within the statement) intact</item>
    /// </list>
    /// </remarks>
    private static StatementSyntax PreserveInnerTrivia(StatementSyntax statement)
    {
        // Process leading trivia: preserve comments, strip pure whitespace at the start
        var leadingTrivia = statement.GetLeadingTrivia();
        var preservedLeading = new List<SyntaxTrivia>();
        var foundNonWhitespace = false;

        foreach (var trivia in leadingTrivia)
        {
            var kind = trivia.Kind();

            // Once we find a comment, preserve everything from there
            if (kind == SyntaxKind.SingleLineCommentTrivia ||
                kind == SyntaxKind.MultiLineCommentTrivia ||
                kind == SyntaxKind.SingleLineDocumentationCommentTrivia ||
                kind == SyntaxKind.MultiLineDocumentationCommentTrivia ||
                kind == SyntaxKind.RegionDirectiveTrivia ||
                kind == SyntaxKind.EndRegionDirectiveTrivia ||
                kind == SyntaxKind.PragmaWarningDirectiveTrivia)
            {
                foundNonWhitespace = true;
            }

            if (foundNonWhitespace)
            {
                preservedLeading.Add(trivia);
            }
            else if (kind != SyntaxKind.WhitespaceTrivia && kind != SyntaxKind.EndOfLineTrivia)
            {
                // Non-whitespace, non-comment: preserve it
                preservedLeading.Add(trivia);
                foundNonWhitespace = true;
            }
        }

        // Process trailing trivia: preserve comments, strip pure whitespace at the end
        var trailingTrivia = statement.GetTrailingTrivia();
        var preservedTrailing = new List<SyntaxTrivia>();

        foreach (var trivia in trailingTrivia)
        {
            var kind = trivia.Kind();

            if (kind == SyntaxKind.SingleLineCommentTrivia ||
                kind == SyntaxKind.MultiLineCommentTrivia)
            {
                preservedTrailing.Add(trivia);
            }
            else if (kind == SyntaxKind.EndOfLineTrivia && preservedTrailing.Count > 0)
            {
                // Keep newline after trailing comment
                preservedTrailing.Add(trivia);
            }
        }

        return statement
            .WithLeadingTrivia(preservedLeading)
            .WithTrailingTrivia(preservedTrailing);
    }
}

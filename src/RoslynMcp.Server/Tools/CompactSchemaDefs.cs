namespace RoslynMcp.Server.Tools;

/// <summary>
/// Shared JSON Schema <c>$defs</c> for Layer 5 compact schema tools (<c>roslyn_transaction</c> and <c>roslyn_query_symbols</c>).
/// </summary>
public static class CompactSchemaDefs
{
    /// <summary>
    /// Builds the shared <c>$defs</c> dictionary containing <c>TargetCoordinate</c>, <c>DocumentScope</c>,
    /// <c>Anchor</c>, <c>SymbolPredicate</c>, <c>IntRange</c>, <c>SolutionOperation</c>, <c>DeclarationPayload</c>, and <c>ParameterSpec</c>.
    /// </summary>
    /// <returns>A dictionary suitable for the <c>$defs</c> property of a JSON Schema object.</returns>
    public static IReadOnlyDictionary<string, object> BuildActionAlgebraDefs() => new Dictionary<string, object>
    {
        ["DocumentScope"] = new
        {
            type = "object",
            description = "Restricts resolution or evaluation to matching documents, projects, or directory globs.",
            properties = new
            {
                filePaths = new { type = "array", items = new { type = "string" } },
                projectNames = new { type = "array", items = new { type = "string" } },
                directoryGlobs = new { type = "array", items = new { type = "string" } }
            },
            additionalProperties = false
        },
        ["Anchor"] = new
        {
            type = "object",
            description = "Discriminated location anchor within a document (LineSpan, TextSpan, SymbolPath, or SyntaxIndex).",
            required = new[] { "kind" },
            properties = new
            {
                kind = new
                {
                    type = "string",
                    @enum = new[] { "LineSpan", "TextSpan", "SymbolPath", "SyntaxIndex" }
                },
                startLine = new { type = "integer", minimum = 1 },
                startColumn = new { type = "integer", minimum = 1 },
                endLine = new { type = "integer", minimum = 1 },
                endColumn = new { type = "integer", minimum = 1 },
                start = new { type = "integer", minimum = 0 },
                length = new { type = "integer", minimum = 0 },
                path = new { type = "string" },
                syntaxKind = new { type = "string" },
                index = new { type = "integer", minimum = 0 }
            },
            additionalProperties = false
        },
        ["TargetCoordinate"] = new
        {
            type = "object",
            description = "Universal 3-part coordinate (DocumentScope × Anchor × EnclosingNormalization) for targeting syntax nodes and spans.",
            required = new[] { "anchor" },
            properties = new
            {
                scope = new Dictionary<string, object> { ["$ref"] = "#/$defs/DocumentScope" },
                anchor = new Dictionary<string, object> { ["$ref"] = "#/$defs/Anchor" },
                normalization = new
                {
                    type = "string",
                    @enum = new[]
                    {
                        "Exact",
                        "EnclosingStatement",
                        "EnclosingBlock",
                        "EnclosingMember",
                        "EnclosingType"
                    },
                    @default = "Exact"
                }
            },
            additionalProperties = false
        },
        ["IntRange"] = new
        {
            type = "object",
            description = "Inclusive integer range [min, max].",
            properties = new
            {
                min = new { type = "integer", minimum = 0 },
                max = new { type = "integer", minimum = 0 }
            },
            additionalProperties = false
        },
        ["SymbolPredicate"] = new
        {
            type = "object",
            description = "Composable Layer 2 symbol query predicate AST with boolean combinators and structural/semantic filters.",
            properties = new
            {
                all = new
                {
                    type = "array",
                    items = new Dictionary<string, object> { ["$ref"] = "#/$defs/SymbolPredicate" }
                },
                any = new
                {
                    type = "array",
                    items = new Dictionary<string, object> { ["$ref"] = "#/$defs/SymbolPredicate" }
                },
                not = new Dictionary<string, object> { ["$ref"] = "#/$defs/SymbolPredicate" },
                symbolKindIn = new { type = "array", items = new { type = "string" } },
                accessibilityIn = new { type = "array", items = new { type = "string" } },
                nameMatches = new { type = "string" },
                namespaceMatches = new { type = "string" },
                hasAttribute = new { type = "string" },
                implementsOrInherits = new { type = "string" },
                callerCountRange = new Dictionary<string, object> { ["$ref"] = "#/$defs/IntRange" },
                referencesCountRange = new Dictionary<string, object> { ["$ref"] = "#/$defs/IntRange" },
                inDocumentScope = new Dictionary<string, object> { ["$ref"] = "#/$defs/DocumentScope" }
            },
            additionalProperties = false
        },
        ["ParameterSpec"] = new
        {
            type = "object",
            description = "Method or constructor parameter specification.",
            required = new[] { "name", "type" },
            properties = new
            {
                name = new { type = "string" },
                type = new { type = "string" },
                defaultValue = new { type = "string" }
            },
            additionalProperties = false
        },
        ["DeclarationPayload"] = new
        {
            type = "object",
            description = "Structured declaration payload for SyntaxGeneratorOperation.",
            properties = new
            {
                name = new { type = "string" },
                typeName = new { type = "string" },
                accessibility = new { type = "string" },
                modifiers = new { type = "array", items = new { type = "string" } },
                parameters = new
                {
                    type = "array",
                    items = new Dictionary<string, object> { ["$ref"] = "#/$defs/ParameterSpec" }
                },
                attributes = new { type = "array", items = new { type = "string" } },
                bodyStatements = new { type = "array", items = new { type = "string" } },
                initializerExpression = new { type = "string" },
                hasSetter = new { type = "boolean" }
            },
            additionalProperties = false
        },
        ["SolutionOperation"] = new
        {
            type = "object",
            description = "Polymorphic refactoring or code-generation operation step in a SolutionTransaction pipeline.",
            required = new[] { "op" },
            properties = new
            {
                op = new
                {
                    type = "string",
                    @enum = new[]
                    {
                        "edit_span",
                        "delete_symbol",
                        "rename_symbol",
                        "move_symbol_to_file",
                        "extract_method",
                        "encapsulate_field",
                        "change_signature",
                        "inline_variable",
                        "syntax_generator",
                        "code_action_host",
                        "semantic_pattern_rewrite"
                    }
                },
                target = new Dictionary<string, object> { ["$ref"] = "#/$defs/TargetCoordinate" },
                scope = new Dictionary<string, object> { ["$ref"] = "#/$defs/DocumentScope" },
                replacementText = new { type = "string" },
                failIfReferenced = new { type = "boolean" },
                newName = new { type = "string" },
                targetFilePath = new { type = "string" },
                methodName = new { type = "string" },
                makeStatic = new { type = "boolean" },
                propertyName = new { type = "string" },
                updateReferences = new { type = "boolean" },
                newReturnType = new { type = "string" },
                parameters = new
                {
                    type = "array",
                    items = new Dictionary<string, object> { ["$ref"] = "#/$defs/ParameterSpec" }
                },
                removeDeclaration = new { type = "boolean" },
                action = new { type = "string" },
                declaration = new Dictionary<string, object> { ["$ref"] = "#/$defs/DeclarationPayload" },
                providerOrEquivalenceKey = new { type = "string" },
                diagnosticIds = new { type = "array", items = new { type = "string" } },
                fixAllScope = new { type = "string", @enum = new[] { "Document", "Project", "Solution" } },
                pattern = new
                {
                    type = "string",
                    @enum = new[]
                    {
                        "ConvertForeachToLinq",
                        "ConvertIfToSwitchExpression",
                        "ConvertToPatternMatching",
                        "ConvertToInterpolatedString",
                        "ConvertExpressionToBlockBody",
                        "ConvertBlockToExpressionBody",
                        "AddNullChecks",
                        "InvertIf"
                    }
                }
            },
            additionalProperties = false
        }
    };
}

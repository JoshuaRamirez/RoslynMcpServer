using System.Text.Json.Serialization;
using RoslynMcp.Contracts.Coordinates;

namespace RoslynMcp.Contracts.Transactions;

/// <summary>
/// Kinds of structured declaration mutations supported by <see cref="SyntaxGeneratorOperation"/>.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SyntaxGeneratorActionKind
{
    /// <summary>Add a field declaration to the target type.</summary>
    AddField = 0,

    /// <summary>Add a property declaration to the target type.</summary>
    AddProperty = 1,

    /// <summary>Add a method declaration to the target type.</summary>
    AddMethod = 2,

    /// <summary>Add a parameter to the target method or constructor.</summary>
    AddParameter = 3,

    /// <summary>Attach an attribute to the target declaration.</summary>
    AddAttribute = 4,

    /// <summary>Update accessibility or declaration modifiers on the target symbol.</summary>
    SetModifiers = 5,

    /// <summary>Add an interface to a type's base list and synthesize stub members.</summary>
    AddInterfaceImplementation = 6,

    /// <summary>Replace the body or declaration of the target member.</summary>
    ReplaceMember = 7
}

/// <summary>
/// Semantic <c>IOperation</c>-guided rewrite patterns supported by <see cref="SemanticPatternRewriteOperation"/>.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SemanticPatternKind
{
    /// <summary>Rewrite eligible <c>foreach</c> accumulation loops into LINQ expressions.</summary>
    ConvertForeachToLinq = 0,

    /// <summary>Rewrite eligible <c>if</c>/<c>else</c> return/assignment chains into switch expressions.</summary>
    ConvertIfToSwitchExpression = 1,

    /// <summary>Rewrite type-test and cast patterns into C# pattern matching.</summary>
    ConvertToPatternMatching = 2,

    /// <summary>Rewrite <c>string.Format</c> or string concatenation into interpolated strings.</summary>
    ConvertToInterpolatedString = 3,

    /// <summary>Rewrite expression-bodied members into block-bodied members.</summary>
    ConvertExpressionToBlockBody = 4,

    /// <summary>Rewrite single-statement block-bodied members into expression-bodied members.</summary>
    ConvertBlockToExpressionBody = 5,

    /// <summary>Insert <c>ArgumentNullException.ThrowIfNull</c> guards for reference-type parameters.</summary>
    AddNullChecks = 6,

    /// <summary>Invert <c>if</c> conditions and swap branches.</summary>
    InvertIf = 7
}

/// <summary>
/// Parameter specification for method/constructor signature mutations and code generation.
/// </summary>
/// <param name="Name">Parameter identifier.</param>
/// <param name="TypeName">Parameter C# type name.</param>
/// <param name="DefaultValue">Optional default value expression.</param>
public sealed record ParameterSpec(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("typeName")] string TypeName,
    [property: JsonPropertyName("defaultValue")] string? DefaultValue = null);

/// <summary>
/// Structured payload describing a declaration mutation executed via <c>SyntaxGenerator</c> / <c>SymbolEditor</c>.
/// </summary>
public sealed record DeclarationPayload
{
    /// <summary>Kind of declaration mutation to perform.</summary>
    [JsonPropertyName("action")]
    public required SyntaxGeneratorActionKind Action { get; init; }

    /// <summary>Name of the member, parameter, attribute, or interface.</summary>
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    /// <summary>Optional type name (field type, property type, method return type, or parameter type).</summary>
    [JsonPropertyName("typeName")]
    public string? TypeName { get; init; }

    /// <summary>Optional accessibility (<c>Public</c>, <c>Internal</c>, <c>Protected</c>, <c>Private</c>).</summary>
    [JsonPropertyName("accessibility")]
    public string? Accessibility { get; init; }

    /// <summary>Optional declaration modifiers (<c>static</c>, <c>readonly</c>, <c>virtual</c>, <c>override</c>, <c>abstract</c>, <c>sealed</c>, <c>async</c>).</summary>
    [JsonPropertyName("modifiers")]
    public IReadOnlyList<string>? Modifiers { get; init; }

    /// <summary>Optional parameters for method generation or signature mutation.</summary>
    [JsonPropertyName("parameters")]
    public IReadOnlyList<ParameterSpec>? Parameters { get; init; }

    /// <summary>Optional C# statements for method/property body generation.</summary>
    [JsonPropertyName("statements")]
    public IReadOnlyList<string>? Statements { get; init; }

    /// <summary>Optional initializer expression for fields or properties.</summary>
    [JsonPropertyName("initializerExpression")]
    public string? InitializerExpression { get; init; }

    /// <summary>Optional attribute name when <see cref="Action"/> is <see cref="SyntaxGeneratorActionKind.AddAttribute"/>.</summary>
    [JsonPropertyName("attributeName")]
    public string? AttributeName { get; init; }

    /// <summary>Optional attribute argument expressions.</summary>
    [JsonPropertyName("attributeArguments")]
    public IReadOnlyList<string>? AttributeArguments { get; init; }
}

/// <summary>
/// Base polymorphic contract for a single step in a <see cref="SolutionTransactionRequest"/> fold pipeline
/// (<c>Solution₀ → Op₁ → Solution₁ → ... → Solutionₖ</c>).
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(EditSpanOperation), "edit_span")]
[JsonDerivedType(typeof(DeleteSymbolOperation), "delete_symbol")]
[JsonDerivedType(typeof(RenameSymbolOperation), "rename_symbol")]
[JsonDerivedType(typeof(MoveSymbolToFileOperation), "move_symbol_to_file")]
[JsonDerivedType(typeof(ExtractMethodOperation), "extract_method")]
[JsonDerivedType(typeof(EncapsulateFieldOperation), "encapsulate_field")]
[JsonDerivedType(typeof(ChangeSignatureOperation), "change_signature")]
[JsonDerivedType(typeof(InlineVariableOperation), "inline_variable")]
[JsonDerivedType(typeof(SyntaxGeneratorOperation), "syntax_generator")]
[JsonDerivedType(typeof(CodeActionHostOperation), "code_action_host")]
[JsonDerivedType(typeof(SemanticPatternRewriteOperation), "semantic_pattern_rewrite")]
public abstract record SolutionOperation;

/// <summary>
/// Replaces the resolved target span or syntax node with <see cref="ReplacementText"/>.
/// </summary>
public sealed record EditSpanOperation : SolutionOperation
{
    /// <summary>Target coordinate to replace.</summary>
    [JsonPropertyName("target")]
    public required TargetCoordinate Target { get; init; }

    /// <summary>Replacement C# source text.</summary>
    [JsonPropertyName("replacementText")]
    public required string ReplacementText { get; init; }
}

/// <summary>
/// Deletes the declaration identified by <see cref="Target"/> from its containing document.
/// </summary>
public sealed record DeleteSymbolOperation : SolutionOperation
{
    /// <summary>Target coordinate of the declaration to remove.</summary>
    [JsonPropertyName("target")]
    public required TargetCoordinate Target { get; init; }
}

/// <summary>
/// Renames the symbol at <see cref="Target"/> across the entire solution using <c>Renamer.RenameSymbolAsync</c>.
/// </summary>
public sealed record RenameSymbolOperation : SolutionOperation
{
    /// <summary>Target coordinate resolving to the symbol declaration or reference.</summary>
    [JsonPropertyName("target")]
    public required TargetCoordinate Target { get; init; }

    /// <summary>New identifier name for the symbol.</summary>
    [JsonPropertyName("newName")]
    public required string NewName { get; init; }
}

/// <summary>
/// Moves the type declaration at <see cref="Target"/> into a new or existing document at <see cref="TargetFilePath"/>.
/// </summary>
public sealed record MoveSymbolToFileOperation : SolutionOperation
{
    /// <summary>Target coordinate resolving to the type declaration.</summary>
    [JsonPropertyName("target")]
    public required TargetCoordinate Target { get; init; }

    /// <summary>Destination file path for the moved type declaration.</summary>
    [JsonPropertyName("targetFilePath")]
    public required string TargetFilePath { get; init; }
}

/// <summary>
/// Extracts the expression or statement block at <see cref="Target"/> into a new method named <see cref="MethodName"/>.
/// </summary>
public sealed record ExtractMethodOperation : SolutionOperation
{
    /// <summary>Target coordinate of the expression or statements to extract.</summary>
    [JsonPropertyName("target")]
    public required TargetCoordinate Target { get; init; }

    /// <summary>Name of the extracted method.</summary>
    [JsonPropertyName("methodName")]
    public required string MethodName { get; init; }
}

/// <summary>
/// Encapsulates the field at <see cref="Target"/> into a property and updates references.
/// </summary>
public sealed record EncapsulateFieldOperation : SolutionOperation
{
    /// <summary>Target coordinate of the field declaration.</summary>
    [JsonPropertyName("target")]
    public required TargetCoordinate Target { get; init; }

    /// <summary>Optional explicit property name (derived from field name when omitted).</summary>
    [JsonPropertyName("propertyName")]
    public string? PropertyName { get; init; }
}

/// <summary>
/// Updates the parameter list and optional return type of the method at <see cref="Target"/>.
/// </summary>
public sealed record ChangeSignatureOperation : SolutionOperation
{
    /// <summary>Target coordinate of the method declaration.</summary>
    [JsonPropertyName("target")]
    public required TargetCoordinate Target { get; init; }

    /// <summary>New parameter specifications.</summary>
    [JsonPropertyName("parameters")]
    public required IReadOnlyList<ParameterSpec> Parameters { get; init; }

    /// <summary>Optional new return type name.</summary>
    [JsonPropertyName("returnType")]
    public string? ReturnType { get; init; }
}

/// <summary>
/// Inlines the local variable declaration at <see cref="Target"/> across its enclosing method body.
/// </summary>
public sealed record InlineVariableOperation : SolutionOperation
{
    /// <summary>Target coordinate of the local variable declaration.</summary>
    [JsonPropertyName("target")]
    public required TargetCoordinate Target { get; init; }
}

/// <summary>
/// Executes a structured <c>SyntaxGenerator</c> / <c>SymbolEditor</c> declaration mutation at <see cref="Target"/>.
/// </summary>
public sealed record SyntaxGeneratorOperation : SolutionOperation
{
    /// <summary>Target coordinate identifying the type or member declaration to mutate.</summary>
    [JsonPropertyName("target")]
    public required TargetCoordinate Target { get; init; }

    /// <summary>Structured declaration mutation specification.</summary>
    [JsonPropertyName("declaration")]
    public required DeclarationPayload Declaration { get; init; }
}

/// <summary>
/// Executes a Roslyn <c>CodeFixProvider</c>, <c>CodeRefactoringProvider</c>, or diagnostic-driven fix across
/// <c>Document</c>, <c>Project</c>, or <c>Solution</c> scope.
/// </summary>
public sealed record CodeActionHostOperation : SolutionOperation
{
    /// <summary>Target coordinate where the code action or diagnostic fix is anchored.</summary>
    [JsonPropertyName("target")]
    public required TargetCoordinate Target { get; init; }

    /// <summary>Optional compiler/analyzer diagnostic ID (e.g. <c>CS8019</c>, <c>IDE0005</c>) to fix.</summary>
    [JsonPropertyName("diagnosticId")]
    public string? DiagnosticId { get; init; }

    /// <summary>Optional code action title or provider key to invoke.</summary>
    [JsonPropertyName("codeActionTitle")]
    public string? CodeActionTitle { get; init; }

    /// <summary>Fix-all scope: <c>Document</c> (default), <c>Project</c>, or <c>Solution</c>.</summary>
    [JsonPropertyName("scope")]
    public string Scope { get; init; } = "Document";
}

/// <summary>
/// Executes a semantic <c>IOperation</c>-guided pattern match and syntax rewrite across <see cref="Scope"/>.
/// </summary>
public sealed record SemanticPatternRewriteOperation : SolutionOperation
{
    /// <summary>Document scope restricting which files are rewritten.</summary>
    [JsonPropertyName("scope")]
    public DocumentScope Scope { get; init; } = DocumentScope.All;

    /// <summary>Semantic pattern rewrite kind to apply.</summary>
    [JsonPropertyName("pattern")]
    public required SemanticPatternKind Pattern { get; init; }

    /// <summary>Optional narrower target coordinate within <see cref="Scope"/>.</summary>
    [JsonPropertyName("target")]
    public TargetCoordinate? Target { get; init; }
}

/// <summary>
/// Unified diff summary for a single document modified, created, or deleted by a <see cref="SolutionTransactionRequest"/>.
/// </summary>
public sealed record FileDiffSummary
{
    /// <summary>Document file path.</summary>
    [JsonPropertyName("filePath")]
    public required string FilePath { get; init; }

    /// <summary>Kind of file change (<c>Modified</c>, <c>Added</c>, or <c>Deleted</c>).</summary>
    [JsonPropertyName("changeKind")]
    public required string ChangeKind { get; init; }

    /// <summary>Standard unified diff (<c>--- a/...</c>, <c>+++ b/...</c>, <c>@@ ... @@</c>).</summary>
    [JsonPropertyName("unifiedDiff")]
    public required string UnifiedDiff { get; init; }
}

/// <summary>
/// Request payload for executing an atomic multi-step <c>roslyn_transaction</c> over a warm Roslyn solution session.
/// </summary>
public sealed record SolutionTransactionRequest
{
    /// <summary>Path to the <c>.sln</c>, <c>.slnx</c>, or <c>.csproj</c> file.</summary>
    [JsonPropertyName("solutionPath")]
    public string? SolutionPath { get; init; }

    /// <summary>Ordered sequence of solution operations to fold in memory.</summary>
    [JsonPropertyName("operations")]
    public required IReadOnlyList<SolutionOperation> Operations { get; init; }

    /// <summary>
    /// When <c>true</c>, computes unified diffs across all steps in memory without mutating the warm session or writing to disk.
    /// </summary>
    [JsonPropertyName("previewDiffOnly")]
    public bool PreviewDiffOnly { get; init; }

    /// <summary>
    /// When <c>true</c> (default), automatically rolls back the entire transaction if post-transaction compilation introduces errors.
    /// </summary>
    [JsonPropertyName("rollbackOnCompilationErrors")]
    public bool RollbackOnCompilationErrors { get; init; } = true;
}

/// <summary>
/// Result returned from executing a <see cref="SolutionTransactionRequest"/>.
/// </summary>
public sealed record SolutionTransactionResult
{
    /// <summary>Whether the transaction succeeded (and was committed unless <see cref="Preview"/> is <c>true</c>).</summary>
    [JsonPropertyName("success")]
    public required bool Success { get; init; }

    /// <summary>Whether the transaction was rolled back due to compilation errors or an operation failure.</summary>
    [JsonPropertyName("rolledBack")]
    public bool RolledBack { get; init; }

    /// <summary>Whether the transaction ran in preview-only mode without modifying disk or session state.</summary>
    [JsonPropertyName("preview")]
    public bool Preview { get; init; }

    /// <summary>Number of operations folded during the transaction.</summary>
    [JsonPropertyName("operationsExecuted")]
    public int OperationsExecuted { get; init; }

    /// <summary>Per-file unified diffs computed between the initial and final solution snapshots.</summary>
    [JsonPropertyName("fileDiffs")]
    public required IReadOnlyList<FileDiffSummary> FileDiffs { get; init; }

    /// <summary>Post-transaction C# compiler error messages, if any.</summary>
    [JsonPropertyName("compilationErrors")]
    public IReadOnlyList<string> CompilationErrors { get; init; } = [];

    /// <summary>Total transaction turnaround time in milliseconds.</summary>
    [JsonPropertyName("executionTimeMs")]
    public long ExecutionTimeMs { get; init; }

    /// <summary>Error message when <see cref="Success"/> is <c>false</c>.</summary>
    [JsonPropertyName("error")]
    public string? Error { get; init; }
}

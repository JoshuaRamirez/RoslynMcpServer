namespace RoslynMcp.Contracts.Models;

/// <summary>
/// Parameters for the generate_method_stub tool.
/// </summary>
public sealed class GenerateMethodStubParams
{
    /// <summary>
    /// Absolute path to the file containing the call site.
    /// </summary>
    public required string SourceFile { get; init; }

    /// <summary>
    /// 1-based line number of the call site.
    /// </summary>
    public required int Line { get; init; }

    /// <summary>
    /// 1-based column number within the method name.
    /// </summary>
    public required int Column { get; init; }

    /// <summary>
    /// Method name override when the name is not inferable from the location.
    /// </summary>
    public string? MethodName { get; init; }

    /// <summary>
    /// Explicit return type override when usage does not constrain the type.
    /// </summary>
    public string? ReturnType { get; init; }

    /// <summary>
    /// Accessibility of the generated method. Default: private on the same type, public on another type.
    /// </summary>
    public string? Visibility { get; init; }

    /// <summary>
    /// Force async method generation (<c>Task</c> / <c>Task&lt;T&gt;</c>). Default: false.
    /// </summary>
    public bool GenerateAsync { get; init; }

    /// <summary>
    /// Throw NotImplementedException in the generated stub body. Default: true.
    /// When false, uses a default-return body (empty block for <c>void</c> /
    /// async <c>Task</c>; <c>return null;</c> for reference types;
    /// <c>return default(T);</c> for value types and type parameters).
    /// <c>ref</c> / <c>ref readonly</c> returns still throw (a default return
    /// is not a valid ref return).
    /// </summary>
    public bool ThrowNotImplemented { get; init; } = true;

    /// <summary>
    /// When true, remove an existing ordinary method declaration that matches
    /// the inferred/requested signature (same name, type-parameter arity,
    /// parameter count, parameter types in order, and <c>RefKind</c> when
    /// inferred) — including on other partials of the same type — before
    /// inserting a freshly generated stub. Body still follows
    /// <see cref="ThrowNotImplemented"/>, <see cref="GenerateAsync"/>,
    /// <see cref="Visibility"/>, and <see cref="ReturnType"/>.
    /// Constructors, operators, local functions, explicit interface
    /// implementations, accessors, and other non-ordinary methods are never
    /// replaced. Two compatible ordinary methods with no single target fail
    /// with <c>NameCollision</c> — this flag does not guess.
    /// Default: false (fail if a compatible ordinary method already exists).
    /// </summary>
    public bool ReplaceExisting { get; init; }

    /// <summary>
    /// Return computed changes without applying. Default: false.
    /// </summary>
    public bool Preview { get; init; }
}

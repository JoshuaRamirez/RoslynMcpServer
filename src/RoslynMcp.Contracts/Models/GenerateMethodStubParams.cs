namespace RoslynMcp.Contracts.Models;

/// <summary>
/// Parameters for the generate_method_stub tool.
/// </summary>
public sealed class GenerateMethodStubParams
{
    /// <summary>
    /// Absolute path to the file containing the call site.
    /// Required when <see cref="AllFiles"/> is false.
    /// When <see cref="AllFiles"/> is true, optional and limits the walk
    /// to that one file when set.
    /// </summary>
    public string? SourceFile { get; init; }

    /// <summary>
    /// When true, process every eligible undefined call site in every C# document
    /// (or the optional single <see cref="SourceFile"/>).
    /// When true, cannot be combined with <see cref="Line"/>,
    /// <see cref="Column"/>, or <see cref="MethodName"/>.
    /// </summary>
    public bool AllFiles { get; init; }

    /// <summary>
    /// 1-based line number of the call site.
    /// Single-site only. Required when <see cref="AllFiles"/> is false.
    /// </summary>
    public int? Line { get; init; }

    /// <summary>
    /// 1-based column number within the method name.
    /// Single-site only. Required when <see cref="AllFiles"/> is false.
    /// </summary>
    public int? Column { get; init; }

    /// <summary>
    /// Method name override when the name is not inferable from the location.
    /// Single-site only; cannot be combined with <see cref="AllFiles"/>.
    /// </summary>
    public string? MethodName { get; init; }

    /// <summary>
    /// Explicit return type override when usage does not constrain the type.
    /// Valid with <see cref="AllFiles"/>.
    /// </summary>
    public string? ReturnType { get; init; }

    /// <summary>
    /// Accessibility of the generated method. Default: private on the same type, public on another type.
    /// Valid with <see cref="AllFiles"/>.
    /// </summary>
    public string? Visibility { get; init; }

    /// <summary>
    /// Force async method generation (<c>Task</c> / <c>Task&lt;T&gt;</c>). Default: false.
    /// Valid with <see cref="AllFiles"/>.
    /// </summary>
    public bool GenerateAsync { get; init; }

    /// <summary>
    /// Throw NotImplementedException in the generated stub body. Default: true.
    /// When false, uses a default-return body (empty block for <c>void</c> /
    /// async <c>Task</c>; <c>return null;</c> for reference types;
    /// <c>return default(T);</c> for value types and type parameters).
    /// <c>ref</c> / <c>ref readonly</c> returns still throw (a default return
    /// is not a valid ref return).
    /// Valid with <see cref="AllFiles"/>.
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
    /// Valid with <see cref="AllFiles"/>.
    /// </summary>
    public bool ReplaceExisting { get; init; }

    /// <summary>
    /// Return computed changes without applying. Default: false.
    /// Valid with <see cref="AllFiles"/>.
    /// </summary>
    public bool Preview { get; init; }
}

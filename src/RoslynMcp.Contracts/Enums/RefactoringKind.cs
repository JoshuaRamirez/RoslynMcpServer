namespace RoslynMcp.Contracts.Enums;

/// <summary>
/// Represents the type of refactoring operation.
/// </summary>
public enum RefactoringKind
{
    // Move Operations
    /// <summary>Move a type to a different file.</summary>
    MoveTypeToFile,

    /// <summary>Move a type to a different namespace.</summary>
    MoveTypeToNamespace,

    // Rename Operations
    /// <summary>Rename any symbol with reference updates.</summary>
    RenameSymbol,

    /// <summary>Rename a file to match its primary type.</summary>
    RenameFile,

    /// <summary>Rename a namespace across all files.</summary>
    RenameNamespace,

    // Extract Operations
    /// <summary>Extract selection to new method.</summary>
    ExtractMethod,

    /// <summary>Extract interface from type.</summary>
    ExtractInterface,

    /// <summary>Extract base class from type.</summary>
    ExtractBaseClass,

    /// <summary>Move selected members from a derived type onto an existing base class or interface.</summary>
    PullMembersUp,

    /// <summary>Move selected members from a base type down onto derived types.</summary>
    PushMembersDown,

    /// <summary>Replace derived-type references with a compatible base type or interface.</summary>
    UseBaseType,

    /// <summary>Promote a local variable or expression to a class field.</summary>
    IntroduceField,

    /// <summary>Delete a symbol only when it has no remaining references.</summary>
    SafeDelete,

    /// <summary>Add the static modifier to an instance method that does not use instance state.</summary>
    MakeStatic,

    /// <summary>Remove the static modifier from a method and rewrite call sites to an instance receiver.</summary>
    MakeNonStatic,

    /// <summary>Extract expression to variable.</summary>
    ExtractVariable,

    /// <summary>Extract expression to constant.</summary>
    ExtractConstant,

    // Inline Operations
    /// <summary>Inline a method by replacing call sites with the method body.</summary>
    InlineMethod,

    /// <summary>Inline a constant by replacing references with its literal value.</summary>
    InlineConstant,

    // Signature Operations
    /// <summary>Add a parameter to a method and update call sites.</summary>
    AddParameter,

    /// <summary>Remove a parameter from a method and update call sites.</summary>
    RemoveParameter,

    /// <summary>Reorder a method's parameters and update call sites.</summary>
    ReorderParameters,

    /// <summary>Change a method's return type and update return statements.</summary>
    ChangeReturnType,

    // Generate Operations
    /// <summary>Generate constructor from fields/properties.</summary>
    GenerateConstructor,

    /// <summary>Generate a property on a type.</summary>
    GenerateProperty,

    /// <summary>Generate method stub.</summary>
    GenerateMethodStub,

    /// <summary>Generate override methods.</summary>
    GenerateOverrides,

    /// <summary>Implement interface members.</summary>
    ImplementInterface,

    /// <summary>Implement inherited abstract members.</summary>
    ImplementAbstract,

    // Organize Operations
    /// <summary>Sort using directives.</summary>
    SortUsings,

    /// <summary>Remove unused using directives.</summary>
    RemoveUnusedUsings,

    /// <summary>Add missing using directives.</summary>
    AddMissingUsings
}

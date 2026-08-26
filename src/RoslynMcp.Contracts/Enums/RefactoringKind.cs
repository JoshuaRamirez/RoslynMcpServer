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

    /// <summary>Extract expression to variable.</summary>
    ExtractVariable,

    /// <summary>Extract expression to constant.</summary>
    ExtractConstant,

    // Inline Operations
    /// <summary>Inline a method by replacing call sites with the method body.</summary>
    InlineMethod,

    // Generate Operations
    /// <summary>Generate constructor from fields/properties.</summary>
    GenerateConstructor,

    /// <summary>Generate method stub.</summary>
    GenerateMethodStub,

    /// <summary>Generate override methods.</summary>
    GenerateOverrides,

    /// <summary>Implement interface members.</summary>
    ImplementInterface,

    // Organize Operations
    /// <summary>Sort using directives.</summary>
    SortUsings,

    /// <summary>Remove unused using directives.</summary>
    RemoveUnusedUsings,

    /// <summary>Add missing using directives.</summary>
    AddMissingUsings
}

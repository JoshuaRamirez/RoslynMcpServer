# Glossary - Roslyn MCP Move Server

## A

**Accessibility**
The visibility scope of a symbol. Values: public, internal, protected, private, protected internal, private protected. Determines which code can reference the symbol.

**Aggregate Root**
In domain-driven design, the primary entity that controls access to a cluster of related objects. Workspace and RefactoringOperation are aggregate roots in this system.

**Atomic Operation**
An operation that either completes fully or has no effect. All refactoring operations are atomic to prevent partial changes.

## C

**Change Kind**
The type of modification to a document: Create, Modify, or Delete.

**Compilation**
The process of converting source code to executable form. Used to verify that refactoring does not break the code.

## D

**Document**
A source code file within a Roslyn workspace. Represents a .cs file with its syntax tree and semantic model.

**Document Change**
A modification to be applied to a document, including the change kind and new content.

## F

**File-Scoped Namespace**
C# 10 feature allowing namespace declaration without braces. Format: namespace MyApp.Services;

**Fully Qualified Name**
The complete name of a symbol including its namespace. Example: System.Collections.Generic.List

## I

**Invariant**
A condition that must always be true for the system to be in a valid state.

## M

**MCP (Model Context Protocol)**
An open protocol for communication between AI assistants and external tools/data sources. This server implements MCP to expose refactoring capabilities.

**MSBuild**
Microsoft Build Engine. Required to load .NET projects and solutions for analysis.

**Moveable Symbol**
A symbol type that can be relocated: Class, Struct, Interface, Enum, Record, Delegate.

## N

**Namespace**
A container for organizing code elements. Can be nested using dot notation.

**Nested Type**
A type declared inside another type. Cannot be moved independently from its containing type.

## O

**Operation State**
The current phase of a refactoring operation: Pending, Validating, Resolving, Computing, Applying, Committing, Completed, Failed, or Cancelled.

## P

**Preview Mode**
Operation mode where changes are computed and returned but not applied. Allows inspection before commitment.

**Project**
A compilation unit in a solution, corresponding to a .csproj file.

## R

**Reference**
A usage of a symbol from another location in code. References must be updated when a symbol moves.

**Refactoring**
Restructuring code without changing its external behavior. This server supports move refactorings.

**Roslyn**
The .NET Compiler Platform. Provides APIs for code analysis and transformation.

## S

**Semantic Model**
Roslyn component providing type information, symbol binding, and flow analysis for a document.

**Solution**
A container for one or more projects, represented by a .sln file.

**Symbol**
A named program element such as a type, method, property, or field.

**Symbol Key**
A stable identifier for a symbol that survives compilation changes.

**Syntax Tree**
The parsed representation of source code as a tree of syntax nodes.

## T

**Top-Level Type**
A type declared directly in a namespace, not nested inside another type.

**Type Declaration**
The syntax node representing a type definition: class, struct, interface, enum, record, or delegate.

## U

**Using Directive**
A statement that imports a namespace, making its types available without qualification.

## V

**Validation**
The process of checking inputs and state before performing an operation.

## W

**Workspace**
Roslyn component representing a solution with all its projects and documents. MSBuildWorkspace loads from .sln files.

**Workspace State**
The lifecycle phase of a workspace: Unloaded, Loading, Ready, Operating, Error, or Disposed.

---

## Technical Term Mapping

| Business Term | Technical Implementation |
|---------------|-------------------------|
| Type | INamedTypeSymbol |
| File | Document |
| Solution | MSBuildWorkspace.CurrentSolution |
| Move | Extract + Insert + Update References |
| Reference Update | Add using + Qualify name |
| Validation | Input + Semantic + Filesystem checks |
| Commit | Workspace.TryApplyChanges + File.WriteAllText |

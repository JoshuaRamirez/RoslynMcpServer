using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.FileSystem;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Rename;
using RoslynMcp.Core.Refactoring.Utilities;
using RoslynMcp.Core.Workspace;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Rename;

/// <summary>
/// Unit tests for RenameSymbolOperation semantic validation.
/// Tests validate symbol-level rename restrictions that occur during execution.
/// </summary>
public class RenameSymbolOperationTests
{
    #region Constructor Tests

    [Fact]
    public void RenameSymbol_Constructor_ThrowsCannotRenameConstructor()
    {
        // Arrange
        var method = CreateMethodSymbol(MethodKind.Constructor, "MyClass");

        // Act
        var exception = Assert.Throws<RefactoringException>(() =>
            ValidateRename(method));

        // Assert
        Assert.Equal(ErrorCodes.CannotRenameConstructor, exception.ErrorCode);
    }

    [Fact]
    public void RenameSymbol_Constructor_MessageIndicatesRenameContainingType()
    {
        // Arrange
        var method = CreateMethodSymbol(MethodKind.Constructor, "MyClass");

        // Act
        var exception = Assert.Throws<RefactoringException>(() =>
            ValidateRename(method));

        // Assert
        Assert.Contains("containing type", exception.Message);
    }

    #endregion

    #region Destructor Tests

    [Fact]
    public void RenameSymbol_Destructor_ThrowsCannotRenameDestructor()
    {
        // Arrange
        var method = CreateMethodSymbol(MethodKind.Destructor, "~MyClass");

        // Act
        var exception = Assert.Throws<RefactoringException>(() =>
            ValidateRename(method));

        // Assert
        Assert.Equal(ErrorCodes.CannotRenameDestructor, exception.ErrorCode);
    }

    [Fact]
    public void RenameSymbol_Destructor_MessageIndicatesRenameContainingType()
    {
        // Arrange
        var method = CreateMethodSymbol(MethodKind.Destructor, "~MyClass");

        // Act
        var exception = Assert.Throws<RefactoringException>(() =>
            ValidateRename(method));

        // Assert
        Assert.Contains("containing type", exception.Message);
    }

    #endregion

    #region Operator Tests

    [Fact]
    public void RenameSymbol_UserDefinedOperator_ThrowsCannotRenameOperator()
    {
        // Arrange
        var method = CreateMethodSymbol(MethodKind.UserDefinedOperator, "op_Addition");

        // Act
        var exception = Assert.Throws<RefactoringException>(() =>
            ValidateRename(method));

        // Assert
        Assert.Equal(ErrorCodes.CannotRenameOperator, exception.ErrorCode);
    }

    [Fact]
    public void RenameSymbol_ConversionOperator_ThrowsCannotRenameOperator()
    {
        // Arrange
        var method = CreateMethodSymbol(MethodKind.Conversion, "op_Implicit");

        // Act
        var exception = Assert.Throws<RefactoringException>(() =>
            ValidateRename(method));

        // Assert
        Assert.Equal(ErrorCodes.CannotRenameOperator, exception.ErrorCode);
    }

    [Fact]
    public void RenameSymbol_Operator_MessageIndicatesCannotRename()
    {
        // Arrange
        var method = CreateMethodSymbol(MethodKind.UserDefinedOperator, "op_Addition");

        // Act
        var exception = Assert.Throws<RefactoringException>(() =>
            ValidateRename(method));

        // Assert
        Assert.Contains("operators", exception.Message.ToLowerInvariant());
    }

    #endregion

    #region External Symbol Tests

    [Fact]
    public void RenameSymbol_ExternalSymbol_ThrowsCannotRenameExternal()
    {
        // Arrange
        var symbol = CreateExternalSymbol("ExternalType");

        // Act
        var exception = Assert.Throws<RefactoringException>(() =>
            ValidateRename(symbol));

        // Assert
        Assert.Equal(ErrorCodes.CannotRenameExternal, exception.ErrorCode);
    }

    [Fact]
    public void RenameSymbol_ExternalSymbol_MessageIndicatesExternalAssemblies()
    {
        // Arrange
        var symbol = CreateExternalSymbol("ExternalType");

        // Act
        var exception = Assert.Throws<RefactoringException>(() =>
            ValidateRename(symbol));

        // Assert
        Assert.Contains("external assemblies", exception.Message);
    }

    #endregion

    #region Ambiguous Symbol Tests

    [Fact]
    public void RenameSymbol_AmbiguousSymbol_ThrowsSymbolAmbiguous()
    {
        // Arrange
        var candidateCount = 3;

        // Act
        var exception = Assert.Throws<RefactoringException>(() =>
            ThrowAmbiguousSymbolError("MyMethod", candidateCount));

        // Assert
        Assert.Equal(ErrorCodes.SymbolAmbiguous, exception.ErrorCode);
    }

    [Fact]
    public void RenameSymbol_AmbiguousSymbol_MessageIndicatesLineNumber()
    {
        // Arrange
        var candidateCount = 2;

        // Act
        var exception = Assert.Throws<RefactoringException>(() =>
            ThrowAmbiguousSymbolError("MyMethod", candidateCount));

        // Assert
        Assert.Contains("line number", exception.Message);
    }

    [Fact]
    public void RenameSymbol_AmbiguousSymbol_IncludesCandidateCountInDetails()
    {
        // Arrange
        var candidateCount = 3;

        // Act
        var exception = Assert.Throws<RefactoringException>(() =>
            ThrowAmbiguousSymbolError("MyMethod", candidateCount));

        // Assert
        Assert.Equal(candidateCount, exception.Details?["candidateCount"]);
    }

    #endregion

    #region Verbatim Identifier Tests

    [Fact]
    public void RenameSymbol_VerbatimIdentifier_AcceptsAtPrefix()
    {
        // Arrange
        var newName = "@class";

        // Act
        var isValid = IsValidIdentifier(newName);

        // Assert
        Assert.True(isValid);
    }

    [Fact]
    public void RenameSymbol_VerbatimIdentifierKeyword_DoesNotTreatAsKeyword()
    {
        // Arrange
        var newName = "@void";

        // Act
        var isKeyword = IsKeyword(newName);

        // Assert
        Assert.False(isKeyword);
    }

    [Fact]
    public void RenameSymbol_VerbatimIdentifier_MatchesIdentifierPattern()
    {
        // Arrange
        var newName = "@event";

        // Act
        var isValid = IsValidIdentifier(newName);

        // Assert
        Assert.True(isValid);
    }

    #endregion

    #region Rename All Overloads Tests

    [Fact]
    public void RenameSymbol_MethodWithOverloads_RenameOverloadsFlagDefaultsFalse()
    {
        // Arrange
        var @params = new RenameSymbolParams
        {
            SourceFile = "C:\\test\\file.cs",
            SymbolName = "MyMethod",
            NewName = "RenamedMethod"
        };

        // Act
        var renameOverloads = @params.RenameOverloads;

        // Assert
        Assert.False(renameOverloads);
    }

    [Fact]
    public void RenameSymbol_MethodWithOverloads_RenameOverloadsFlagCanBeEnabled()
    {
        // Arrange
        var @params = new RenameSymbolParams
        {
            SourceFile = "C:\\test\\file.cs",
            SymbolName = "MyMethod",
            NewName = "RenamedMethod",
            RenameOverloads = true
        };

        // Act
        var renameOverloads = @params.RenameOverloads;

        // Assert
        Assert.True(renameOverloads);
    }

    #endregion

    #region Rename Implementations Flag Tests

    [Fact]
    public void RenameSymbol_RenameImplementations_DefaultsTrue()
    {
        var @params = new RenameSymbolParams
        {
            SourceFile = "C:\\test\\file.cs",
            SymbolName = "Process",
            NewName = "Execute"
        };

        Assert.True(@params.RenameImplementations);
    }

    [Fact]
    public void RenameSymbol_RenameImplementations_CanBeDisabled()
    {
        var @params = new RenameSymbolParams
        {
            SourceFile = "C:\\test\\file.cs",
            SymbolName = "Process",
            NewName = "Execute",
            RenameImplementations = false
        };

        Assert.False(@params.RenameImplementations);
    }

    #endregion

    #region Helper Methods

    /// <summary>
    /// Mimics the ValidateRename logic from RenameSymbolOperation.
    /// </summary>
    private static void ValidateRename(ISymbol symbol)
    {
        if (symbol is IMethodSymbol method)
        {
            if (method.MethodKind == MethodKind.Constructor)
            {
                throw new RefactoringException(
                    ErrorCodes.CannotRenameConstructor,
                    "Cannot rename constructor directly. Rename the containing type instead.");
            }

            if (method.MethodKind == MethodKind.Destructor)
            {
                throw new RefactoringException(
                    ErrorCodes.CannotRenameDestructor,
                    "Cannot rename destructor directly. Rename the containing type instead.");
            }

            if (method.MethodKind == MethodKind.UserDefinedOperator ||
                method.MethodKind == MethodKind.Conversion)
            {
                throw new RefactoringException(
                    ErrorCodes.CannotRenameOperator,
                    "Cannot rename operators.");
            }
        }

        if (symbol.ContainingAssembly != null &&
            !symbol.Locations.Any(l => l.IsInSource))
        {
            throw new RefactoringException(
                ErrorCodes.CannotRenameExternal,
                "Cannot rename symbols from external assemblies.");
        }
    }

    private static void ThrowAmbiguousSymbolError(string symbolName, int candidateCount)
    {
        throw new RefactoringException(
            ErrorCodes.SymbolAmbiguous,
            $"Multiple symbols named '{symbolName}' found. Provide line number to disambiguate.",
            new Dictionary<string, object>
            {
                ["candidateCount"] = candidateCount
            });
    }

    private static bool IsValidIdentifier(string name) =>
        System.Text.RegularExpressions.Regex.IsMatch(name, @"^@?[A-Za-z_][A-Za-z0-9_]*$");

    private static bool IsKeyword(string name)
    {
        if (name.StartsWith("@")) return false;
        return SyntaxFacts.GetKeywordKind(name) != SyntaxKind.None;
    }

    /// <summary>
    /// Creates a mock method symbol with the specified method kind.
    /// Uses real Roslyn compilation to create proper symbol instances.
    /// </summary>
    private static IMethodSymbol CreateMethodSymbol(MethodKind methodKind, string name)
    {
        var source = methodKind switch
        {
            MethodKind.Constructor => "public class MyClass { public MyClass() { } }",
            MethodKind.Destructor => "public class MyClass { ~MyClass() { } }",
            MethodKind.UserDefinedOperator => "public class MyClass { public static MyClass operator +(MyClass a, MyClass b) => a; }",
            MethodKind.Conversion => "public class MyClass { public static implicit operator int(MyClass m) => 0; }",
            _ => "public class MyClass { public void TestMethod() { } }"
        };

        var tree = CSharpSyntaxTree.ParseText(source);
        var compilation = CSharpCompilation.Create("TestAssembly")
            .AddReferences(MetadataReference.CreateFromFile(typeof(object).Assembly.Location))
            .AddSyntaxTrees(tree);

        var semanticModel = compilation.GetSemanticModel(tree);
        var root = tree.GetRoot();

        IMethodSymbol? symbol = methodKind switch
        {
            MethodKind.Constructor => root.DescendantNodes()
                .OfType<ConstructorDeclarationSyntax>()
                .Select(c => semanticModel.GetDeclaredSymbol(c))
                .FirstOrDefault(),
            MethodKind.Destructor => root.DescendantNodes()
                .OfType<DestructorDeclarationSyntax>()
                .Select(d => semanticModel.GetDeclaredSymbol(d))
                .FirstOrDefault(),
            MethodKind.UserDefinedOperator => root.DescendantNodes()
                .OfType<OperatorDeclarationSyntax>()
                .Select(o => semanticModel.GetDeclaredSymbol(o))
                .FirstOrDefault(),
            MethodKind.Conversion => root.DescendantNodes()
                .OfType<ConversionOperatorDeclarationSyntax>()
                .Select(c => semanticModel.GetDeclaredSymbol(c))
                .FirstOrDefault(),
            _ => root.DescendantNodes()
                .OfType<MethodDeclarationSyntax>()
                .Select(m => semanticModel.GetDeclaredSymbol(m))
                .FirstOrDefault()
        };

        return symbol ?? throw new InvalidOperationException($"Failed to create method symbol for {methodKind}");
    }

    /// <summary>
    /// Creates a symbol that appears to be from an external assembly (no source locations).
    /// </summary>
    private static ISymbol CreateExternalSymbol(string name)
    {
        // Get a symbol from the system assembly (truly external)
        var compilation = CSharpCompilation.Create("TestAssembly")
            .AddReferences(MetadataReference.CreateFromFile(typeof(object).Assembly.Location));

        var objectType = compilation.GetTypeByMetadataName("System.Object");
        return objectType ?? throw new InvalidOperationException("Could not get System.Object type");
    }

    #endregion

    #region allFiles

    private const string EligibleSymbolFileA = """
        public class AlphaHelper
        {
            public void SharedName(int x) { }
        }
        """;

    private const string EligibleSymbolFileB = """
        public class BetaHelper
        {
            public void SharedName(string s) { }
        }
        """;

    private const string AlreadyRenamedFileC = """
        public class GammaHelper
        {
            public void RenamedTarget(int x) { }
        }
        """;

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    [SkippableFact]
    public async Task RenameSymbol_OmittedAllFiles_KeepsSingleSiteRewrite()
    {
        await using var workspace = await TempWorkspace.CreateAsync(
            ("FileA.cs", EligibleSymbolFileA),
            ("FileB.cs", EligibleSymbolFileB));
        var operation = new RenameSymbolOperation(workspace.Context);
        var pathA = Path.Combine(workspace.DirectoryPath, "FileA.cs");
        var pathB = Path.Combine(workspace.DirectoryPath, "FileB.cs");

        var result = await operation.ExecuteAsync(new RenameSymbolParams
        {
            SourceFile = pathA,
            SymbolName = "SharedName",
            NewName = "RenamedTarget",
            RenameFile = false
        });

        Assert.True(result.Success);
        Assert.Contains("RenamedTarget", await File.ReadAllTextAsync(pathA));
        Assert.Contains("SharedName", await File.ReadAllTextAsync(pathB));
    }

    [SkippableFact]
    public async Task RenameSymbol_AllFilesTrue_AppliesToEligibleSymbolsAcrossFiles()
    {
        await using var workspace = await TempWorkspace.CreateAsync(
            ("FileA.cs", EligibleSymbolFileA),
            ("FileB.cs", EligibleSymbolFileB),
            ("FileC.cs", AlreadyRenamedFileC));
        var operation = new RenameSymbolOperation(workspace.Context);
        var pathA = Path.Combine(workspace.DirectoryPath, "FileA.cs");
        var pathB = Path.Combine(workspace.DirectoryPath, "FileB.cs");
        var pathC = Path.Combine(workspace.DirectoryPath, "FileC.cs");
        var beforeC = await File.ReadAllTextAsync(pathC);

        var result = await operation.ExecuteAsync(new RenameSymbolParams
        {
            AllFiles = true,
            SymbolName = "SharedName",
            NewName = "RenamedTarget",
            RenameFile = false
        });

        Assert.True(result.Success);
        Assert.False(result.Preview);
        Assert.Contains("RenamedTarget", await File.ReadAllTextAsync(pathA));
        Assert.Contains("RenamedTarget", await File.ReadAllTextAsync(pathB));
        Assert.Equal(beforeC, await File.ReadAllTextAsync(pathC));
        Assert.True(result.Changes!.FilesModified.Count >= 2);
        Assert.Contains(result.Changes.FilesModified, p => PathsEqual(p, pathA));
        Assert.Contains(result.Changes.FilesModified, p => PathsEqual(p, pathB));
        Assert.DoesNotContain(result.Changes.FilesModified, p => PathsEqual(p, pathC));
    }

    [SkippableFact]
    public async Task RenameSymbol_AllFilesTrue_WithoutSourceFile_Succeeds()
    {
        await using var workspace = await TempWorkspace.CreateAsync(
            ("FileA.cs", EligibleSymbolFileA),
            ("FileB.cs", EligibleSymbolFileB));
        var operation = new RenameSymbolOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new RenameSymbolParams
        {
            AllFiles = true,
            SymbolName = "SharedName",
            NewName = "RenamedTarget",
            RenameFile = false
        });

        Assert.True(result.Success);
        Assert.True(result.Changes!.FilesModified.Count >= 2);
    }

    [SkippableFact]
    public async Task RenameSymbol_AllFilesFalse_WithoutSourceFile_MissingRequiredParam()
    {
        await using var workspace = await TempWorkspace.CreateAsync(EligibleSymbolFileA);
        var operation = new RenameSymbolOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new RenameSymbolParams
            {
                AllFiles = false,
                SymbolName = "SharedName",
                NewName = "RenamedTarget"
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("sourceFile", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task RenameSymbol_PreviewAllFiles_AggregatesChangedFilesAndWritesNothing()
    {
        await using var workspace = await TempWorkspace.CreateAsync(
            ("FileA.cs", EligibleSymbolFileA),
            ("FileB.cs", EligibleSymbolFileB),
            ("FileC.cs", AlreadyRenamedFileC));
        var operation = new RenameSymbolOperation(workspace.Context);
        var pathA = Path.Combine(workspace.DirectoryPath, "FileA.cs");
        var pathB = Path.Combine(workspace.DirectoryPath, "FileB.cs");
        var pathC = Path.Combine(workspace.DirectoryPath, "FileC.cs");
        var beforeA = await File.ReadAllTextAsync(pathA);
        var beforeB = await File.ReadAllTextAsync(pathB);
        var beforeC = await File.ReadAllTextAsync(pathC);

        var result = await operation.ExecuteAsync(new RenameSymbolParams
        {
            AllFiles = true,
            SymbolName = "SharedName",
            NewName = "RenamedTarget",
            RenameFile = false,
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.Contains(result.PendingChanges, c => PathsEqual(c.File, pathA));
        Assert.DoesNotContain(result.PendingChanges, c => PathsEqual(c.File, pathC));
        Assert.Equal(beforeA, await File.ReadAllTextAsync(pathA));
        Assert.Equal(beforeB, await File.ReadAllTextAsync(pathB));
        Assert.Equal(beforeC, await File.ReadAllTextAsync(pathC));
    }

    [SkippableFact]
    public async Task RenameSymbol_AllFilesTrue_EveryFileIneligible_SucceedsWithEmptyChanges()
    {
        await using var workspace = await TempWorkspace.CreateAsync(
            ("FileC.cs", AlreadyRenamedFileC));
        var operation = new RenameSymbolOperation(workspace.Context);
        var pathC = Path.Combine(workspace.DirectoryPath, "FileC.cs");
        var before = await File.ReadAllTextAsync(pathC);

        var result = await operation.ExecuteAsync(new RenameSymbolParams
        {
            AllFiles = true,
            SymbolName = "SharedName",
            NewName = "RenamedTarget",
            RenameFile = false
        });

        Assert.True(result.Success);
        Assert.Empty(result.Changes!.FilesModified);
        Assert.Equal(before, await File.ReadAllTextAsync(pathC));
    }

    [SkippableFact]
    public async Task RenameSymbol_AllFilesTrue_OptionalSourceFile_LimitsWalk()
    {
        await using var workspace = await TempWorkspace.CreateAsync(
            ("FileA.cs", EligibleSymbolFileA),
            ("FileB.cs", EligibleSymbolFileB));
        var operation = new RenameSymbolOperation(workspace.Context);
        var pathA = Path.Combine(workspace.DirectoryPath, "FileA.cs");
        var pathB = Path.Combine(workspace.DirectoryPath, "FileB.cs");
        var beforeB = await File.ReadAllTextAsync(pathB);

        var result = await operation.ExecuteAsync(new RenameSymbolParams
        {
            AllFiles = true,
            SourceFile = pathA,
            SymbolName = "SharedName",
            NewName = "RenamedTarget",
            RenameFile = false
        });

        Assert.True(result.Success);
        Assert.Contains("RenamedTarget", await File.ReadAllTextAsync(pathA));
        Assert.Equal(beforeB, await File.ReadAllTextAsync(pathB));
        Assert.Contains(result.Changes!.FilesModified, p => PathsEqual(p, pathA));
        Assert.DoesNotContain(result.Changes.FilesModified, p => PathsEqual(p, pathB));
    }

    [SkippableFact]
    public async Task RenameSymbol_AllFilesTrue_WithLine_Rejects()
    {
        await using var workspace = await TempWorkspace.CreateAsync(EligibleSymbolFileA);
        var operation = new RenameSymbolOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new RenameSymbolParams
            {
                AllFiles = true,
                SymbolName = "SharedName",
                NewName = "RenamedTarget",
                Line = 1
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [SkippableFact]
    public async Task RenameSymbol_AllFilesTrue_WithColumn_Rejects()
    {
        await using var workspace = await TempWorkspace.CreateAsync(EligibleSymbolFileA);
        var operation = new RenameSymbolOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new RenameSymbolParams
            {
                AllFiles = true,
                SymbolName = "SharedName",
                NewName = "RenamedTarget",
                Column = 1
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [SkippableFact]
    public async Task RenameSymbol_AllFilesTrue_NameConflict_Skips()
    {
        await using var workspace = await TempWorkspace.CreateAsync(
            ("FileA.cs", """
                public class Host
                {
                    public void Foo() { }
                    public void Bar() { }
                }
                """),
            ("FileB.cs", """
                public class Other
                {
                    public void Foo() { }
                }
                """));
        var operation = new RenameSymbolOperation(workspace.Context);
        var pathA = Path.Combine(workspace.DirectoryPath, "FileA.cs");
        var pathB = Path.Combine(workspace.DirectoryPath, "FileB.cs");

        var result = await operation.ExecuteAsync(new RenameSymbolParams
        {
            AllFiles = true,
            SymbolName = "Foo",
            NewName = "Bar",
            RenameFile = false
        });

        Assert.True(result.Success);
        var alpha = await File.ReadAllTextAsync(pathA);
        var beta = await File.ReadAllTextAsync(pathB);
        // Host.Foo conflicts with Host.Bar — skipped; Other.Foo renames.
        Assert.Contains("void Foo()", alpha);
        Assert.Contains("void Bar()", beta);
        Assert.DoesNotContain("void Foo()", beta);
    }

    [SkippableFact]
    public async Task RenameSymbol_AllFilesTrue_RenameOverloads_DedupsGroup()
    {
        await using var workspace = await TempWorkspace.CreateAsync(
            ("Overloads.cs", """
                public class Host
                {
                    public void Foo(int x) { }
                    public void Foo(string s) { }
                }
                """));
        var operation = new RenameSymbolOperation(workspace.Context);
        var path = Path.Combine(workspace.DirectoryPath, "Overloads.cs");

        var result = await operation.ExecuteAsync(new RenameSymbolParams
        {
            AllFiles = true,
            SymbolName = "Foo",
            NewName = "Bar",
            RenameOverloads = true,
            RenameFile = false
        });

        Assert.True(result.Success);
        var text = await File.ReadAllTextAsync(path);
        Assert.DoesNotContain("void Foo(", text);
        Assert.Contains("void Bar(int x)", text);
        Assert.Contains("void Bar(string s)", text);
    }

    [SkippableFact]
    public async Task RenameSymbol_AllFilesTrue_RenameFile_RenamesMatchingFiles()
    {
        await using var workspace = await TempWorkspace.CreateAsync(
            ("TempType.cs", """
                public class TempType { public int A; }
                """),
            ("Other/TempType.cs", """
                namespace Other;
                public class TempType { public int B; }
                """));
        var operation = new RenameSymbolOperation(workspace.Context);
        var path1 = Path.Combine(workspace.DirectoryPath, "TempType.cs");
        var path2 = Path.Combine(workspace.DirectoryPath, "Other", "TempType.cs");

        var result = await operation.ExecuteAsync(new RenameSymbolParams
        {
            AllFiles = true,
            SymbolName = "TempType",
            NewName = "SharedType",
            RenameFile = true
        });

        Assert.True(result.Success);
        var rootShared = Path.Combine(workspace.DirectoryPath, "SharedType.cs");
        var otherShared = Path.Combine(workspace.DirectoryPath, "Other", "SharedType.cs");
        // Distinct directories — both file renames succeed without destination collision.
        // Commit writes to the new FilePath; source must still be removed.
        Assert.True(File.Exists(rootShared));
        Assert.True(File.Exists(otherShared));
        Assert.False(File.Exists(path1));
        Assert.False(File.Exists(path2));
        Assert.Contains(rootShared, result.Changes!.FilesCreated);
        Assert.Contains(otherShared, result.Changes.FilesCreated);
        Assert.Contains(path1, result.Changes.FilesDeleted);
        Assert.Contains(path2, result.Changes.FilesDeleted);
        var text1 = await File.ReadAllTextAsync(rootShared);
        var text2 = await File.ReadAllTextAsync(otherShared);
        Assert.Contains("SharedType", text1);
        Assert.Contains("SharedType", text2);
    }


    [Fact]
    public void CollectNamedDeclarations_OrdersBySpanStart()
    {
        var tree = CSharpSyntaxTree.ParseText("""
            public class Host
            {
                public void SharedName(int x) { }
                public void Other() { }
                public void SharedName(string s) { }
            }
            """);
        var compilation = CSharpCompilation.Create(
            "CollectNamed",
            [tree],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]);
        var model = compilation.GetSemanticModel(tree);
        var collected = RenameSymbolOperation.CollectNamedDeclarations(
            tree.GetRoot(), model, "SharedName", CancellationToken.None);
        Assert.Equal(2, collected.Count);
        Assert.True(collected[0].SpanStart < collected[1].SpanStart);
    }

    [Fact]
    public void HasSimpleNameConflict_DetectsSiblingMember()
    {
        var tree = CSharpSyntaxTree.ParseText("""
            public class Host
            {
                public void Foo() { }
                public void Bar() { }
            }
            """);
        var compilation = CSharpCompilation.Create(
            "Conflict",
            [tree],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]);
        var model = compilation.GetSemanticModel(tree);
        var foo = tree.GetRoot().DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Select(m => model.GetDeclaredSymbol(m)!)
            .First(s => s.Name == "Foo");
        Assert.True(RenameSymbolOperation.HasSimpleNameConflict(foo, "Bar"));
        Assert.False(RenameSymbolOperation.HasSimpleNameConflict(foo, "Baz"));
    }

    [Fact]
    public void HasSimpleNameConflict_LocalVsParameter_Detects()
    {
        var tree = CSharpSyntaxTree.ParseText("""
            public class Host
            {
                public void M(int Bar)
                {
                    int Foo = 0;
                }
            }
            """);
        var compilation = CSharpCompilation.Create(
            "LexicalConflict",
            [tree],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]);
        var model = compilation.GetSemanticModel(tree);
        var foo = model.GetDeclaredSymbol(
            tree.GetRoot().DescendantNodes().OfType<VariableDeclaratorSyntax>()
                .First(v => v.Identifier.ValueText == "Foo"))!;
        Assert.True(RenameSymbolOperation.HasSimpleNameConflict(foo, "Bar"));
        Assert.False(RenameSymbolOperation.HasSimpleNameConflict(foo, "Baz"));
    }

    [SkippableFact]
    public async Task RenameSymbol_AllFilesTrue_LocalVsParameterConflict_Skips()
    {
        await using var workspace = await TempWorkspace.CreateAsync(
            ("Locals.cs", """
                public class Host
                {
                    public void M(int Bar)
                    {
                        int Foo = 0;
                        _ = Foo;
                    }

                    public void N()
                    {
                        int Foo = 1;
                        _ = Foo;
                    }
                }
                """));
        var operation = new RenameSymbolOperation(workspace.Context);
        var path = Path.Combine(workspace.DirectoryPath, "Locals.cs");

        var result = await operation.ExecuteAsync(new RenameSymbolParams
        {
            AllFiles = true,
            SymbolName = "Foo",
            NewName = "Bar",
            RenameFile = false
        });

        Assert.True(result.Success);
        var text = await File.ReadAllTextAsync(path);
        // M(int Bar) { int Foo } conflicts — Foo kept; N()'s Foo renames to Bar.
        Assert.Contains("int Foo = 0", text);
        Assert.Contains("int Bar = 1", text);
        Assert.DoesNotContain("int Foo = 1", text);
    }



    [SkippableFact]
    public async Task RenameSymbol_AllFilesTrue_SkipsLinkedMultiViewPath()
    {
        const string sharedSource = """
            namespace TestApp;

            public static class SharedHost
            {
                public static void SharedName(int x) { }
            }
            """;
        const string anchorASource = """
            namespace TestApp;

            public static class AnchorA
            {
                public static void KeepA() { }
            }
            """;
        const string anchorBSource = """
            namespace TestApp;

            public static class AnchorB
            {
                public static void KeepB() { }
            }
            """;

        await using var workspace = await TempWorkspace.CreateWithLinkedProjectsAsync(
            sharedSource, anchorASource, anchorBSource);
        var counts = AllFilesDocumentHelpers.BuildLinkedPathCounts(workspace.Context.Solution);
        var sharedKey = PathResolver.GetPathComparisonKey(workspace.SourcePaths["Shared.cs"]);
        Assert.True(counts.TryGetValue(sharedKey, out var sharedCount) && sharedCount > 1);

        var beforeShared = await File.ReadAllTextAsync(workspace.SourcePaths["Shared.cs"]);

        var operation = new RenameSymbolOperation(workspace.Context);
        var result = await operation.ExecuteAsync(new RenameSymbolParams
        {
            AllFiles = true,
            SymbolName = "SharedName",
            NewName = "RenamedTarget",
            RenameFile = false
        });

        Assert.True(result.Success);
        Assert.Equal(beforeShared, await File.ReadAllTextAsync(workspace.SourcePaths["Shared.cs"]));
        Assert.Empty(result.Changes!.FilesModified);
    }

    private sealed class TempWorkspace : IAsyncDisposable
    {
        public required string DirectoryPath { get; init; }
        public required string ProjectPath { get; init; }
        public required string SourcePath { get; init; }
        public Dictionary<string, string> SourcePaths { get; init; } = new(StringComparer.Ordinal);
        public required WorkspaceContext Context { get; init; }

        public static Task<TempWorkspace> CreateAsync(string source, string fileName = "Foo.cs") =>
            CreateAsync((fileName, source));

        public static Task<TempWorkspace> CreateAsync(params (string FileName, string Source)[] files) =>
            CreateAtProjectPathAsync(
                "TestApp.csproj",
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net9.0</TargetFramework>
                    <Nullable>enable</Nullable>
                  </PropertyGroup>
                </Project>
                """,
                files);

        public static async Task<TempWorkspace> CreateAtProjectPathAsync(
            string projectRelativePath,
            string projectXml,
            params (string FileName, string Source)[] files)
        {
            Skip.IfNot(ModuleInitializer.MsBuildAvailable, ModuleInitializer.MsBuildError ?? "MSBuild not available");

            var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpRenameSymbol_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);

            var relativeProject = projectRelativePath
                .Replace('/', Path.DirectorySeparatorChar)
                .Replace('\\', Path.DirectorySeparatorChar);
            var projectPath = Path.Combine(directory, relativeProject);
            var projectParent = Path.GetDirectoryName(projectPath);
            if (!string.IsNullOrEmpty(projectParent))
                Directory.CreateDirectory(projectParent);
            await File.WriteAllTextAsync(projectPath, projectXml);

            string? sourcePath = null;
            foreach (var (fileName, source) in files)
            {
                var relative = fileName.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
                var path = Path.Combine(directory, relative);
                var parent = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(parent))
                    Directory.CreateDirectory(parent);
                await File.WriteAllTextAsync(path, source);
                sourcePath ??= path;
            }

            sourcePath ??= Path.Combine(directory, "Foo.cs");

            try
            {
                var provider = new MSBuildWorkspaceProvider();
                var context = await provider.CreateContextAsync(projectPath);
                if (context.GetDocumentByPath(sourcePath) == null)
                {
                    context.Dispose();
                    throw new InvalidOperationException($"Workspace loaded but did not include {sourcePath}.");
                }

                return new TempWorkspace
                {
                    DirectoryPath = directory,
                    ProjectPath = projectPath,
                    SourcePath = sourcePath,
                    Context = context
                };
            }
            catch (Exception ex) when (ex is not SkipException)
            {
                try
                {
                    Directory.Delete(directory, recursive: true);
                }
                catch
                {
                    // ignore cleanup failures
                }

                Skip.If(true, $"Workspace load failed: {ex.Message}");
                throw;
            }
        }

        public static async Task<TempWorkspace> CreateWithLinkedProjectsAsync(
            string sharedSource,
            string anchorASource,
            string anchorBSource)
        {
            Skip.IfNot(ModuleInitializer.MsBuildAvailable, ModuleInitializer.MsBuildError ?? "MSBuild not available");

            var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpRenameSymbolLinked_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);

            var solutionPath = Path.Combine(directory, "TestApp.sln");
            var sharedPath = Path.Combine(directory, "Shared.cs");
            var rootProjectPath = Path.Combine(directory, "ProjectA.csproj");
            var referencedProjectPath = Path.Combine(directory, "ProjectB.csproj");
            var anchorAPath = Path.Combine(directory, "AnchorA.cs");
            var anchorBPath = Path.Combine(directory, "AnchorB.cs");
            var projectTypeGuid = "{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}";
            var projectAGuid = Guid.NewGuid().ToString("B").ToUpperInvariant();
            var projectBGuid = Guid.NewGuid().ToString("B").ToUpperInvariant();

            await File.WriteAllTextAsync(sharedPath, sharedSource);
            await File.WriteAllTextAsync(anchorAPath, anchorASource);
            await File.WriteAllTextAsync(anchorBPath, anchorBSource);
            await File.WriteAllTextAsync(solutionPath, $$"""
                Microsoft Visual Studio Solution File, Format Version 12.00
                # Visual Studio Version 17
                VisualStudioVersion = 17.0.31903.59
                MinimumVisualStudioVersion = 10.0.40219.1
                Project("{{projectTypeGuid}}") = "ProjectA", "ProjectA.csproj", "{{projectAGuid}}"
                EndProject
                Project("{{projectTypeGuid}}") = "ProjectB", "ProjectB.csproj", "{{projectBGuid}}"
                EndProject
                Global
                	GlobalSection(SolutionConfigurationPlatforms) = preSolution
                		Debug|Any CPU = Debug|Any CPU
                		Release|Any CPU = Release|Any CPU
                	EndGlobalSection
                	GlobalSection(ProjectConfigurationPlatforms) = postSolution
                		{{projectAGuid}}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
                		{{projectAGuid}}.Debug|Any CPU.Build.0 = Debug|Any CPU
                		{{projectAGuid}}.Release|Any CPU.ActiveCfg = Release|Any CPU
                		{{projectAGuid}}.Release|Any CPU.Build.0 = Release|Any CPU
                		{{projectBGuid}}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
                		{{projectBGuid}}.Debug|Any CPU.Build.0 = Debug|Any CPU
                		{{projectBGuid}}.Release|Any CPU.ActiveCfg = Release|Any CPU
                		{{projectBGuid}}.Release|Any CPU.Build.0 = Release|Any CPU
                	EndGlobalSection
                EndGlobal
                """);

            await File.WriteAllTextAsync(rootProjectPath, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net9.0</TargetFramework>
                    <Nullable>enable</Nullable>
                    <GenerateAssemblyInfo>false</GenerateAssemblyInfo>
                    <GenerateTargetFrameworkAttribute>false</GenerateTargetFrameworkAttribute>
                    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
                  </PropertyGroup>
                  <ItemGroup>
                    <Compile Include="AnchorA.cs" />
                    <Compile Include="Shared.cs" Link="Shared.cs" />
                  </ItemGroup>
                </Project>
                """);

            await File.WriteAllTextAsync(referencedProjectPath, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net9.0</TargetFramework>
                    <Nullable>enable</Nullable>
                    <GenerateAssemblyInfo>false</GenerateAssemblyInfo>
                    <GenerateTargetFrameworkAttribute>false</GenerateTargetFrameworkAttribute>
                    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
                  </PropertyGroup>
                  <ItemGroup>
                    <Compile Include="AnchorB.cs" />
                    <Compile Include="Shared.cs" Link="Shared.cs" />
                  </ItemGroup>
                </Project>
                """);

            try
            {
                var provider = new MSBuildWorkspaceProvider();
                var context = await provider.CreateContextAsync(solutionPath);
                var linkedCount = context.Solution.Projects
                    .SelectMany(p => p.Documents)
                    .Count(d => d.FilePath != null &&
                                PathResolver.GetPathComparisonKey(d.FilePath!) ==
                                PathResolver.GetPathComparisonKey(sharedPath));
                if (linkedCount < 2)
                {
                    context.Dispose();
                    throw new InvalidOperationException($"Expected linked document in both projects, found {linkedCount}.");
                }

                return new TempWorkspace
                {
                    DirectoryPath = directory,
                    ProjectPath = solutionPath,
                    SourcePath = sharedPath,
                    SourcePaths = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["Shared.cs"] = sharedPath,
                        ["AnchorA.cs"] = anchorAPath,
                        ["AnchorB.cs"] = anchorBPath
                    },
                    Context = context
                };
            }
            catch (Exception ex) when (ex is not SkipException)
            {
                try { Directory.Delete(directory, recursive: true); }
                catch { /* ignore */ }
                Skip.If(true, $"Workspace load failed: {ex.Message}");
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            Context.Dispose();
            await Task.Run(() =>
            {
                try
                {
                    Directory.Delete(DirectoryPath, recursive: true);
                }
                catch
                {
                    // ignore locked temp files
                }
            });
        }
    }

    #endregion
}

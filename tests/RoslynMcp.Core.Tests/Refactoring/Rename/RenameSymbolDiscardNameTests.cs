using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Rename;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Rename;

/// <summary>
/// Regression tests for #3296: renaming a field / local / parameter to '_' made existing discards in its scope
/// bind to it, so the rename is refused.
/// </summary>
public sealed class RenameSymbolDiscardNameTests : IDisposable
{
    private readonly string _directory;
    private readonly string _sourceFile;

    public RenameSymbolDiscardNameTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "RoslynMcpDiscardName_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _sourceFile = Path.Combine(_directory, "Counter.cs");
        File.WriteAllText(_sourceFile, "class Counter { private int total; }");
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Theory]
    [InlineData("_")]
    [InlineData("@_")]
    public void Validate_DiscardNewName_IsRejected(string newName)
    {
        var ex = Assert.Throws<RefactoringException>(() => RenameSymbolOperation.Validate(new RenameSymbolParams
        {
            SourceFile = _sourceFile,
            SymbolName = "total",
            NewName = newName
        }));

        Assert.Equal(ErrorCodes.InvalidNewName, ex.ErrorCode);
        Assert.Contains("discards", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("__")]
    [InlineData("_total")]
    [InlineData("total_")]
    public void Validate_UnderscoreContainingNewName_IsAccepted(string newName)
    {
        RenameSymbolOperation.Validate(new RenameSymbolParams
        {
            SourceFile = _sourceFile,
            SymbolName = "total",
            NewName = newName
        });
    }
}

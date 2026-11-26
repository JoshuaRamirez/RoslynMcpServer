using Microsoft.CodeAnalysis;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring.Extract;
using RoslynMcp.Core.Workspace;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Extract;

/// <summary>
/// Regression tests for #3123: extract_constant / introduce_field inserted the new member at
/// column 0 after a blank line, with hard-coded CRLF line breaks even in an LF-only file.
/// </summary>
public class InsertedMemberLayoutTests
{
    private const string PricingSource = """
        namespace TestApp;

        public class Pricing
        {
            private const int Max = 5;

            public decimal A(decimal p) => p * 1.08m;

            public decimal B(decimal p) => p / 1.08m;
        }

        """;

    private const string PricingExpected = """
        namespace TestApp;

        public class Pricing
        {
            private const int Max = 5;
            private const decimal TaxRate = 1.08m;

            public decimal A(decimal p) => p * TaxRate;

            public decimal B(decimal p) => p / 1.08m;
        }

        """;

    [SkippableTheory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public async Task ExtractConstant_UsesSiblingIndentationAndFileLineEnding(string eol)
    {
        var source = WithEol(PricingSource, eol);
        await using var workspace = await TempWorkspace.CreateAsync(source, "latest");
        var (line, column) = Find(source, "1.08m");

        var result = await new ExtractConstantOperation(workspace.Context).ExecuteAsync(new ExtractConstantParams
        {
            SourceFile = workspace.SourcePath,
            StartLine = line,
            StartColumn = column,
            EndLine = line,
            EndColumn = column + 5,
            ConstantName = "TaxRate"
        });

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(WithEol(PricingExpected, eol), await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task ExtractConstant_ReplaceAll_TypeWithoutFields_IsIndentedWithLf()
    {
        var source = WithEol("""
            namespace TestApp;

            public class Pricing
            {
                public decimal A(decimal p) => p * 1.08m;

                public decimal B(decimal p) => p / 1.08m;
            }

            """, "\n");
        await using var workspace = await TempWorkspace.CreateAsync(source, "latest");
        var (line, column) = Find(source, "1.08m");

        var result = await new ExtractConstantOperation(workspace.Context).ExecuteAsync(new ExtractConstantParams
        {
            SourceFile = workspace.SourcePath,
            StartLine = line,
            StartColumn = column,
            EndLine = line,
            EndColumn = column + 5,
            ConstantName = "TaxRate",
            ReplaceAll = true
        });

        Assert.True(result.Success, result.Error?.Message);
        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.DoesNotContain("\r", text, StringComparison.Ordinal);
        Assert.Contains("{\n    private const decimal TaxRate = 1.08m;\n    public decimal A(decimal p) => p * TaxRate;\n", text, StringComparison.Ordinal);
        Assert.Contains("p / TaxRate", text, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task ExtractConstant_AllFiles_IsIndentedWithLf()
    {
        var source = WithEol(PricingSource, "\n");
        await using var workspace = await TempWorkspace.CreateAsync(source, "latest");

        var result = await new ExtractConstantOperation(workspace.Context).ExecuteAsync(new ExtractConstantParams
        {
            AllFiles = true
        });

        Assert.True(result.Success, result.Error?.Message);
        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.DoesNotContain("\r", text, StringComparison.Ordinal);
        var name = ExtractConstantOperation.DeriveConstantNameFromLiteral(
            (Microsoft.CodeAnalysis.CSharp.Syntax.LiteralExpressionSyntax)Microsoft.CodeAnalysis.CSharp.SyntaxFactory.ParseExpression("1.08m"));
        Assert.NotNull(name);
        Assert.Contains($"\n    private const int Max = 5;\n    private const decimal {name} = 1.08m;\n\n    public decimal A", text, StringComparison.Ordinal);
    }

    [SkippableTheory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public async Task IntroduceField_ExpressionPath_UsesSiblingIndentationAndFileLineEnding(string eol)
    {
        var source = WithEol("""
            namespace TestApp;

            public class Cart
            {
                private int _count;

                public decimal Total(decimal p)
                {
                    return p * (1m + 0.08m);
                }
            }

            """, eol);
        await using var workspace = await TempWorkspace.CreateAsync(source, "latest");
        var (line, column) = Find(source, "(1m + 0.08m)");

        var result = await new IntroduceFieldOperation(workspace.Context).ExecuteAsync(new IntroduceFieldParams
        {
            SourceFile = workspace.SourcePath,
            StartLine = line,
            StartColumn = column,
            EndLine = line,
            EndColumn = column + "(1m + 0.08m)".Length,
            FieldName = "_rate"
        });

        Assert.True(result.Success, result.Error?.Message);
        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains(WithEol("""
                private int _count;
                private decimal _rate = (1m + 0.08m);

                public decimal Total(decimal p)
            """, eol), text, StringComparison.Ordinal);
        AssertSingleLineEndingStyle(text, eol);
        Assert.Empty(await workspace.GetDiskErrorsAsync());
    }

    [SkippableFact]
    public async Task IntroduceField_InitializeInSynthesizedConstructor_IsIndentedWithLf()
    {
        var source = WithEol("""
            namespace TestApp;

            public class P
            {
                public int A;

                public int Get()
                {
                    return A + 42;
                }
            }

            """, "\n");
        await using var workspace = await TempWorkspace.CreateAsync(source, "latest");
        var (line, column) = Find(source, "42");

        var result = await new IntroduceFieldOperation(workspace.Context).ExecuteAsync(new IntroduceFieldParams
        {
            SourceFile = workspace.SourcePath,
            StartLine = line,
            StartColumn = column,
            EndLine = line,
            EndColumn = column + 2,
            FieldName = "_k",
            InitializeInConstructor = true
        });

        Assert.True(result.Success, result.Error?.Message);
        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.DoesNotContain("\r", text, StringComparison.Ordinal);
        Assert.Equal(WithEol("""
            namespace TestApp;

            public class P
            {
                public int A;
                private int _k;
                public P()
                {
                    this._k = 42;
                }

                public int Get()
                {
                    return A + this._k;
                }
            }

            """, "\n"), text);
        Assert.Empty(await workspace.GetDiskErrorsAsync());
    }

    [SkippableFact]
    public async Task IntroduceField_InitializeInExistingConstructor_IsIndentedWithLf()
    {
        var source = WithEol("""
            namespace TestApp;

            public class P
            {
                public int A;

                public P()
                {
                    A = 1;
                }

                public int Get()
                {
                    return A + 42;
                }
            }

            """, "\n");
        await using var workspace = await TempWorkspace.CreateAsync(source, "latest");
        var (line, column) = Find(source, "42");

        var result = await new IntroduceFieldOperation(workspace.Context).ExecuteAsync(new IntroduceFieldParams
        {
            SourceFile = workspace.SourcePath,
            StartLine = line,
            StartColumn = column,
            EndLine = line,
            EndColumn = column + 2,
            FieldName = "_k",
            InitializeInConstructor = true
        });

        Assert.True(result.Success, result.Error?.Message);
        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.DoesNotContain("\r", text, StringComparison.Ordinal);
        Assert.Equal(WithEol("""
            namespace TestApp;

            public class P
            {
                public int A;
                private int _k;

                public P()
                {
                    this._k = 42;
                    A = 1;
                }

                public int Get()
                {
                    return A + this._k;
                }
            }

            """, "\n"), text);
        Assert.Empty(await workspace.GetDiskErrorsAsync());
    }

    [SkippableTheory]
    [InlineData("@\"a\nb\"")]
    [InlineData("$@\"a{A}\nb\"")]
    [InlineData("\"\"\"\n        a\n          b\n        \"\"\"")]
    public async Task IntroduceField_InitializeInSynthesizedConstructor_KeepsMultiLineLiteralValue(string literal)
    {
        // Reindent split the whole constructor text on line endings and indented every line, including the
        // lines inside a multi-line literal, so @"a<LF>b" became @"a<LF>    b" (#3123 review).
        var source = WithEol($$"""
            namespace TestApp;

            public class P
            {
                public string A = "";

                public string Get()
                {
                    return {{literal}};
                }
            }

            """, "\n");
        var expected = WithEol($$"""
            namespace TestApp;

            public class P
            {
                public string A = "";
                private string _k;
                public P()
                {
                    this._k = {{literal}};
                }

                public string Get()
                {
                    return this._k;
                }
            }

            """, "\n");
        await using var workspace = await TempWorkspace.CreateAsync(source, "latest");
        var start = source.IndexOf("return ", StringComparison.Ordinal) + "return ".Length;
        var (line, column) = Position(source, start);
        var (endLine, endColumn) = Position(source, start + literal.Length);

        var result = await new IntroduceFieldOperation(workspace.Context).ExecuteAsync(new IntroduceFieldParams
        {
            SourceFile = workspace.SourcePath,
            StartLine = line,
            StartColumn = column,
            EndLine = endLine,
            EndColumn = endColumn,
            FieldName = "_k",
            InitializeInConstructor = true
        });

        Assert.True(result.Success, result.Error?.Message);
        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("this._k = " + literal + ";", text, StringComparison.Ordinal);
        Assert.Equal(expected, text);
        Assert.Empty(await workspace.GetDiskErrorsAsync());
    }

    [SkippableTheory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public async Task IntroduceField_InitializeInExpressionBodiedConstructor_UsesIndentationAndFileLineEnding(string eol)
    {
        // P() => A = 1; was expanded with NormalizeWhitespace: CRLF at column 0 and the blank line after the
        // constructor dropped (#3123 review).
        var source = WithEol("""
            namespace TestApp;

            public class P
            {
                public int A;

                public P() => A = 1;

                public int Get()
                {
                    return A + 42;
                }
            }

            """, eol);
        var expected = WithEol("""
            namespace TestApp;

            public class P
            {
                public int A;
                private int _k;

                public P()
                {
                    this._k = 42;
                    A = 1;
                }

                public int Get()
                {
                    return A + this._k;
                }
            }

            """, eol);
        await using var workspace = await TempWorkspace.CreateAsync(source, "latest");
        var (line, column) = Find(source, "42");

        var result = await new IntroduceFieldOperation(workspace.Context).ExecuteAsync(new IntroduceFieldParams
        {
            SourceFile = workspace.SourcePath,
            StartLine = line,
            StartColumn = column,
            EndLine = line,
            EndColumn = column + 2,
            FieldName = "_k",
            InitializeInConstructor = true
        });

        Assert.True(result.Success, result.Error?.Message);
        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Equal(expected, text);
        Assert.Empty(await workspace.GetDiskErrorsAsync());
    }

    private static (int Line, int Column) Position(string source, int offset)
    {
        var before = source[..offset];
        var line = before.Count(c => c == '\n') + 1;
        var column = offset - (before.LastIndexOf('\n') + 1) + 1;
        return (line, column);
    }

    private static string WithEol(string text, string eol) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", eol, StringComparison.Ordinal);

    private static (int Line, int Column) Find(string source, string text)
    {
        var lines = source.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var index = lines[i].IndexOf(text, StringComparison.Ordinal);
            if (index >= 0)
                return (i + 1, index + 1);
        }

        throw new InvalidOperationException($"'{text}' not found");
    }

    private static void AssertSingleLineEndingStyle(string text, string eol)
    {
        if (eol == "\n")
            Assert.DoesNotContain("\r", text, StringComparison.Ordinal);
        else
            Assert.DoesNotMatch("(?<!\r)\n", text);
    }

    private sealed class TempWorkspace : IAsyncDisposable
    {
        public required string DirectoryPath { get; init; }
        public required string SourcePath { get; init; }
        public required WorkspaceContext Context { get; init; }

        public static async Task<TempWorkspace> CreateAsync(string source, string langVersion)
        {
            Skip.IfNot(ModuleInitializer.MsBuildAvailable, ModuleInitializer.MsBuildError ?? "MSBuild not available");

            var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpLangVersion_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var projectPath = Path.Combine(directory, "Case.csproj");
            var sourcePath = Path.Combine(directory, "Program.cs");
            await File.WriteAllTextAsync(projectPath, $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <OutputType>Library</OutputType>
                    <TargetFramework>net9.0</TargetFramework>
                    <ImplicitUsings>disable</ImplicitUsings>
                    <LangVersion>{langVersion}</LangVersion>
                  </PropertyGroup>
                </Project>
                """);
            await File.WriteAllTextAsync(sourcePath, source);

            try
            {
                var context = await new MSBuildWorkspaceProvider().CreateContextAsync(projectPath);
                if (context.GetDocumentByPath(sourcePath) == null)
                {
                    context.Dispose();
                    throw new InvalidOperationException($"Workspace loaded but did not include {sourcePath}.");
                }

                return new TempWorkspace { DirectoryPath = directory, SourcePath = sourcePath, Context = context };
            }
            catch (Exception ex) when (ex is not SkipException)
            {
                try { Directory.Delete(directory, recursive: true); } catch { /* ignore cleanup failures */ }
                Skip.If(true, $"Workspace load failed: {ex.Message}");
                throw;
            }
        }

        public async Task<List<string>> GetErrorsAsync()
        {
            var document = Context.GetDocumentByPath(SourcePath);
            Assert.NotNull(document);
            var compilation = await document.Project.GetCompilationAsync();
            Assert.NotNull(compilation);
            return compilation.GetDiagnostics()
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(d => d.ToString())
                .ToList();
        }

        /// <summary>Compile errors of the project with every .cs file re-read from disk.</summary>
        public async Task<List<string>> GetDiskErrorsAsync()
        {
            var document = Context.GetDocumentByPath(SourcePath);
            Assert.NotNull(document);
            var project = document.Project;
            foreach (var path in Directory.GetFiles(DirectoryPath, "*.cs"))
            {
                var existing = project.Documents.FirstOrDefault(d => string.Equals(d.FilePath, path, StringComparison.Ordinal));
                var text = Microsoft.CodeAnalysis.Text.SourceText.From(await File.ReadAllTextAsync(path));
                project = existing != null
                    ? existing.WithText(text).Project
                    : project.AddDocument(Path.GetFileName(path), text, filePath: path).Project;
            }

            var compilation = await project.GetCompilationAsync();
            Assert.NotNull(compilation);
            return compilation.GetDiagnostics()
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(d => d.ToString())
                .ToList();
        }

        public async ValueTask DisposeAsync()
        {
            Context.Dispose();
            await Task.Run(() =>
            {
                try { Directory.Delete(DirectoryPath, recursive: true); } catch { /* ignore locked temp files */ }
            });
        }
    }
}

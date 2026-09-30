using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynMcp.Core.Refactoring.Utilities;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Utilities;

public class NonReturningMethodHelpersTests
{
    [Fact]
    public void IsVoidReturn_True_ForVoidPredefinedType()
    {
        var returnType = ParseReturnType("void");
        Assert.True(NonReturningMethodHelpers.IsVoidReturn(returnType));
    }

    [Fact]
    public void IsVoidReturn_False_ForInt()
    {
        var returnType = ParseReturnType("int");
        Assert.False(NonReturningMethodHelpers.IsVoidReturn(returnType));
    }

    [Theory]
    [InlineData("Task")]
    [InlineData("ValueTask")]
    [InlineData("System.Threading.Tasks.Task")]
    [InlineData("global::System.Threading.Tasks.ValueTask")]
    public void IsNonGenericTaskLike_True_ForTaskLikeNames(string typeText)
    {
        Assert.True(NonReturningMethodHelpers.IsNonGenericTaskLike(ParseReturnType(typeText)));
    }

    [Theory]
    [InlineData("Task<int>")]
    [InlineData("ValueTask<string>")]
    [InlineData("int")]
    [InlineData("MyTask")]
    public void IsNonGenericTaskLike_False_ForNonMatching(string typeText)
    {
        Assert.False(NonReturningMethodHelpers.IsNonGenericTaskLike(ParseReturnType(typeText)));
    }

    [Theory]
    [InlineData("Task")]
    [InlineData("ValueTask")]
    public void IsTaskLikeName_True_ForTaskAndValueTask(string name)
    {
        Assert.True(NonReturningMethodHelpers.IsTaskLikeName(name));
    }

    [Fact]
    public void IsTaskLikeName_False_ForOtherNames()
    {
        Assert.False(NonReturningMethodHelpers.IsTaskLikeName("MyTask"));
    }

    [Fact]
    public void IsNonReturning_Syntax_True_ForVoid()
    {
        var method = ParseMethod("void M() => Do();");
        Assert.True(NonReturningMethodHelpers.IsNonReturning(method.ReturnType, method.Modifiers));
    }

    [Fact]
    public void IsNonReturning_Syntax_True_ForAsyncTask()
    {
        var method = ParseMethod("async Task M() => Do();");
        Assert.True(NonReturningMethodHelpers.IsNonReturning(method.ReturnType, method.Modifiers));
    }

    [Fact]
    public void IsNonReturning_Syntax_True_ForAsyncValueTask()
    {
        var method = ParseMethod("async ValueTask M() => Do();");
        Assert.True(NonReturningMethodHelpers.IsNonReturning(method.ReturnType, method.Modifiers));
    }

    [Fact]
    public void IsNonReturning_Syntax_False_ForAsyncTaskOfT()
    {
        var method = ParseMethod("async Task<int> M() => 1;");
        Assert.False(NonReturningMethodHelpers.IsNonReturning(method.ReturnType, method.Modifiers));
    }

    [Fact]
    public void IsNonReturning_Syntax_False_ForInt()
    {
        var method = ParseMethod("int M() => 1;");
        Assert.False(NonReturningMethodHelpers.IsNonReturning(method.ReturnType, method.Modifiers));
    }

    [Fact]
    public void IsNonReturning_MethodOverload_UsesSemanticModel_Void()
    {
        var (method, model) = GetMethodAndModel("""
            class C
            {
                void M() => System.Console.WriteLine();
            }
            """);
        Assert.True(NonReturningMethodHelpers.IsNonReturning(method, model));
    }

    [Fact]
    public void IsNonReturning_MethodOverload_UsesSemanticModel_AsyncTask()
    {
        var (method, model) = GetMethodAndModel("""
            using System.Threading.Tasks;
            class C
            {
                async Task M() => await Task.CompletedTask;
            }
            """);
        Assert.True(NonReturningMethodHelpers.IsNonReturning(method, model));
    }

    [Fact]
    public void IsNonReturning_MethodOverload_UsesSemanticModel_AsyncValueTask()
    {
        var (method, model) = GetMethodAndModel("""
            using System.Threading.Tasks;
            class C
            {
                async ValueTask M() => await ValueTask.CompletedTask;
            }
            """);
        Assert.True(NonReturningMethodHelpers.IsNonReturning(method, model));
    }

    [Fact]
    public void IsNonReturning_MethodOverload_UsesSemanticModel_AsyncTaskOfT_False()
    {
        var (method, model) = GetMethodAndModel("""
            using System.Threading.Tasks;
            class C
            {
                async Task<int> M() => 1;
            }
            """);
        Assert.False(NonReturningMethodHelpers.IsNonReturning(method, model));
    }

    [Fact]
    public void IsNonGenericTaskLikeSymbol_True_ForTask()
    {
        var type = GetReturnType("""
            using System.Threading.Tasks;
            class C { Task M() => Task.CompletedTask; }
            """);
        Assert.True(NonReturningMethodHelpers.IsNonGenericTaskLikeSymbol(type));
    }

    [Fact]
    public void IsNonGenericTaskLikeSymbol_True_ForValueTask()
    {
        var type = GetReturnType("""
            using System.Threading.Tasks;
            class C { ValueTask M() => ValueTask.CompletedTask; }
            """);
        Assert.True(NonReturningMethodHelpers.IsNonGenericTaskLikeSymbol(type));
    }

    [Fact]
    public void IsNonGenericTaskLikeSymbol_False_ForTaskOfT()
    {
        var type = GetReturnType("""
            using System.Threading.Tasks;
            class C { Task<int> M() => Task.FromResult(1); }
            """);
        Assert.False(NonReturningMethodHelpers.IsNonGenericTaskLikeSymbol(type));
    }

    [Fact]
    public void IsBclTaskLikeSymbol_True_ForTaskAndTaskOfT()
    {
        var task = GetReturnType("""
            using System.Threading.Tasks;
            class C { Task M() => Task.CompletedTask; }
            """);
        var taskOfT = GetReturnType("""
            using System.Threading.Tasks;
            class C { Task<int> M() => Task.FromResult(1); }
            """);
        Assert.True(NonReturningMethodHelpers.IsBclTaskLikeSymbol(task));
        Assert.True(NonReturningMethodHelpers.IsBclTaskLikeSymbol(taskOfT));
    }

    [Fact]
    public void IsBclTaskLikeSymbol_True_ForValueTask()
    {
        var valueTask = GetReturnType("""
            using System.Threading.Tasks;
            class C { ValueTask M() => ValueTask.CompletedTask; }
            """);
        Assert.True(NonReturningMethodHelpers.IsBclTaskLikeSymbol(valueTask));
    }

    [Fact]
    public void IsBclTaskLikeSymbol_False_ForLookalikeOutsideBcl()
    {
        var type = GetReturnType("""
            namespace Other
            {
                class Task { }
                class C { Task M() => new Task(); }
            }
            """, typeName: "Other.C");
        Assert.False(NonReturningMethodHelpers.IsBclTaskLikeSymbol(type));
    }

    private static TypeSyntax ParseReturnType(string typeText)
    {
        var method = ParseMethod($"{typeText} M() => default!;");
        return method.ReturnType;
    }

    private static MethodDeclarationSyntax ParseMethod(string methodSource)
    {
        var source = "using System.Threading.Tasks;\nclass C\n{\n    " + methodSource + "\n}\n";
        var root = (CompilationUnitSyntax)CSharpSyntaxTree.ParseText(source).GetRoot();
        return root.DescendantNodes().OfType<MethodDeclarationSyntax>().Single();
    }

    private static (MethodDeclarationSyntax method, SemanticModel model) GetMethodAndModel(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var compilation = CreateCompilation(tree);
        var model = compilation.GetSemanticModel(tree);
        var method = tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().Single();
        return (method, model);
    }

    private static ITypeSymbol GetReturnType(string source, string typeName = "C")
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var compilation = CreateCompilation(tree);
        var type = compilation.GetTypeByMetadataName(typeName)!;
        var method = type.GetMembers("M").OfType<IMethodSymbol>().Single();
        return method.ReturnType;
    }

    private static CSharpCompilation CreateCompilation(SyntaxTree tree)
    {
        var refs = new[]
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(Task).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(ValueTask).Assembly.Location),
        };
        return CSharpCompilation.Create(
            "NonReturningMethodHelpersTests",
            [tree],
            refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }
}

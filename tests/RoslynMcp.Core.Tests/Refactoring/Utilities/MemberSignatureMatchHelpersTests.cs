using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using RoslynMcp.Core.Refactoring.Utilities;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Utilities;

public class MemberSignatureMatchHelpersTests
{
    private static readonly Func<ISymbol, string> PlainName = static symbol => symbol.Name;

    private static readonly Func<ISymbol, string> Unqualified = static symbol =>
    {
        var name = symbol.Name;
        var dot = name.LastIndexOf('.');
        return dot >= 0 ? name[(dot + 1)..] : name;
    };

    private const string Source = """
        public interface IFoo
        {
            void M(int a);
            int P { get; }
            int this[int i] { get; }
            event System.EventHandler E;
        }

        public class A
        {
            public void M(int a) { }
            public void M(ref int a) { }
            public void M(long a) { }
            public void M<T>(int a) { }
            public void M(int a, int b) { }
            public void G<T>(T a) { }
            public int P => 0;
            public int Q => 0;
            public int this[int i] => 0;
            public int this[string s] => 0;
            public event System.EventHandler E;
            public event System.EventHandler F;
        }

        public class B
        {
            public void M(int a) { }
            public void G<U>(U a) { }
            public int P => 0;
            public int this[int i] => 0;
            public event System.EventHandler E;
        }

        public class X : IFoo
        {
            void IFoo.M(int a) { }
            int IFoo.P => 0;
            int IFoo.this[int i] => 0;
            event System.EventHandler IFoo.E { add { } remove { } }
        }
        """;

    [Fact]
    public void Methods_same_name_arity_and_parameters_match()
    {
        var (a, b, _) = Types();

        Assert.True(MemberSignatureMatchHelpers.SignaturesMatch(Method(a, "M", 0, "int"), Method(b, "M", 0, "int"), PlainName));
    }

    [Fact]
    public void Methods_differing_by_ref_kind_do_not_match()
    {
        var (a, b, _) = Types();
        var byRef = a.GetMembers("M").OfType<IMethodSymbol>()
            .Single(m => m.Parameters.Length == 1 && m.Parameters[0].RefKind == RefKind.Ref);

        Assert.False(MemberSignatureMatchHelpers.SignaturesMatch(byRef, Method(b, "M", 0, "int"), PlainName));
    }

    [Fact]
    public void Methods_differing_by_parameter_type_or_count_do_not_match()
    {
        var (a, b, _) = Types();
        var other = Method(b, "M", 0, "int");
        var twoParams = a.GetMembers("M").OfType<IMethodSymbol>().Single(m => m.Parameters.Length == 2);

        Assert.False(MemberSignatureMatchHelpers.MethodSignaturesMatch(Method(a, "M", 0, "long"), other, PlainName));
        Assert.False(MemberSignatureMatchHelpers.MethodSignaturesMatch(twoParams, other, PlainName));
    }

    [Fact]
    public void Methods_differing_by_arity_do_not_match()
    {
        var (a, b, _) = Types();

        Assert.False(MemberSignatureMatchHelpers.SignaturesMatch(Method(a, "M", 1, "int"), Method(b, "M", 0, "int"), PlainName));
    }

    [Fact]
    public void Generic_methods_match_by_type_parameter_ordinal()
    {
        var (a, b, _) = Types();
        var left = a.GetMembers("G").OfType<IMethodSymbol>().Single();
        var right = b.GetMembers("G").OfType<IMethodSymbol>().Single();

        Assert.True(MemberSignatureMatchHelpers.SignaturesMatch(left, right, PlainName));
    }

    [Fact]
    public void Explicit_interface_method_matches_only_with_unqualified_selector()
    {
        var (_, b, x) = Types();
        var explicitM = x.GetMembers().OfType<IMethodSymbol>().Single(m => m.Name.EndsWith(".M", StringComparison.Ordinal));
        var plainM = Method(b, "M", 0, "int");

        Assert.False(MemberSignatureMatchHelpers.SignaturesMatch(explicitM, plainM, PlainName));
        Assert.True(MemberSignatureMatchHelpers.SignaturesMatch(explicitM, plainM, Unqualified));
    }

    [Fact]
    public void Properties_match_by_name_only()
    {
        var (a, b, _) = Types();
        var p = Property(a, "P");

        Assert.True(MemberSignatureMatchHelpers.SignaturesMatch(p, Property(b, "P"), PlainName));
        Assert.False(MemberSignatureMatchHelpers.SignaturesMatch(Property(a, "Q"), Property(b, "P"), PlainName));
    }

    [Fact]
    public void Explicit_interface_property_matches_only_with_unqualified_selector()
    {
        var (_, b, x) = Types();
        var explicitP = x.GetMembers().OfType<IPropertySymbol>().Single(p => !p.IsIndexer);

        Assert.False(MemberSignatureMatchHelpers.PropertySignaturesMatch(explicitP, Property(b, "P"), PlainName));
        Assert.True(MemberSignatureMatchHelpers.PropertySignaturesMatch(explicitP, Property(b, "P"), Unqualified));
    }

    [Fact]
    public void Indexers_match_by_parameter_list()
    {
        var (a, b, x) = Types();
        var intIndexer = Indexer(a, "int");
        var stringIndexer = Indexer(a, "string");
        var otherInt = Indexer(b, "int");
        var explicitIndexer = x.GetMembers().OfType<IPropertySymbol>().Single(p => p.IsIndexer);

        Assert.True(MemberSignatureMatchHelpers.SignaturesMatch(intIndexer, otherInt, PlainName));
        Assert.False(MemberSignatureMatchHelpers.SignaturesMatch(stringIndexer, otherInt, PlainName));
        Assert.True(MemberSignatureMatchHelpers.SignaturesMatch(explicitIndexer, otherInt, PlainName));
    }

    [Fact]
    public void Indexer_and_property_do_not_match()
    {
        var (a, b, _) = Types();

        Assert.False(MemberSignatureMatchHelpers.SignaturesMatch(Indexer(a, "int"), Property(b, "P"), PlainName));
        Assert.False(MemberSignatureMatchHelpers.SignaturesMatch(Property(b, "P"), Indexer(a, "int"), PlainName));
    }

    [Fact]
    public void Events_match_by_name()
    {
        var (a, b, x) = Types();
        var explicitE = x.GetMembers().OfType<IEventSymbol>().Single();

        Assert.True(MemberSignatureMatchHelpers.SignaturesMatch(Event(a, "E"), Event(b, "E"), PlainName));
        Assert.False(MemberSignatureMatchHelpers.SignaturesMatch(Event(a, "F"), Event(b, "E"), PlainName));
        Assert.False(MemberSignatureMatchHelpers.SignaturesMatch(explicitE, Event(b, "E"), PlainName));
        Assert.True(MemberSignatureMatchHelpers.SignaturesMatch(explicitE, Event(b, "E"), Unqualified));
    }

    [Fact]
    public void Different_member_kinds_do_not_match()
    {
        var (a, b, _) = Types();

        Assert.False(MemberSignatureMatchHelpers.SignaturesMatch(Method(a, "M", 0, "int"), Property(b, "P"), PlainName));
        Assert.False(MemberSignatureMatchHelpers.SignaturesMatch(Property(a, "P"), Event(b, "E"), PlainName));
        Assert.False(MemberSignatureMatchHelpers.SignaturesMatch(Event(a, "E"), Method(b, "M", 0, "int"), PlainName));
    }

    [Fact]
    public void AddUnique_skips_member_matching_an_existing_one_and_keeps_first()
    {
        var (a, b, _) = Types();
        var first = Method(a, "M", 0, "int");
        var members = new List<ISymbol> { first };

        MemberSignatureMatchHelpers.AddUnique(
            members,
            Method(b, "M", 0, "int"),
            static (existing, candidate) => MemberSignatureMatchHelpers.SignaturesMatch(existing, candidate, PlainName));

        Assert.Same(first, Assert.Single(members));
    }

    [Fact]
    public void AddUnique_appends_non_matching_member_at_end()
    {
        var (a, b, _) = Types();
        var first = Method(a, "M", 0, "int");
        var members = new List<ISymbol> { first };
        var property = Property(b, "P");

        MemberSignatureMatchHelpers.AddUnique(
            members,
            property,
            static (existing, candidate) => MemberSignatureMatchHelpers.SignaturesMatch(existing, candidate, PlainName));

        Assert.Equal(new ISymbol[] { first, property }, members);
    }

    [Fact]
    public void AddUnique_passes_existing_then_candidate_to_predicate()
    {
        var (a, b, _) = Types();
        var first = Method(a, "M", 0, "int");
        var candidate = Method(b, "M", 0, "int");
        var members = new List<ISymbol> { first };
        var calls = new List<(ISymbol Existing, ISymbol Candidate)>();

        MemberSignatureMatchHelpers.AddUnique(members, candidate, (existing, member) =>
        {
            calls.Add((existing, member));
            return false;
        });

        var call = Assert.Single(calls);
        Assert.Same(first, call.Existing);
        Assert.Same(candidate, call.Candidate);
        Assert.Equal(new ISymbol[] { first, candidate }, members);
    }

    [Fact]
    public void AddUnique_with_identity_predicate_keeps_same_signature_distinct_symbols()
    {
        var (a, b, _) = Types();
        var left = Method(a, "M", 0, "int");
        var right = Method(b, "M", 0, "int");
        var members = new List<ISymbol>();
        Func<ISymbol, ISymbol, bool> identity =
            static (existing, candidate) => SymbolEqualityComparer.Default.Equals(existing, candidate);

        MemberSignatureMatchHelpers.AddUnique(members, left, identity);
        MemberSignatureMatchHelpers.AddUnique(members, right, identity);
        MemberSignatureMatchHelpers.AddUnique(members, left, identity);

        Assert.Equal(new ISymbol[] { left, right }, members);
    }

    private static (INamedTypeSymbol A, INamedTypeSymbol B, INamedTypeSymbol X) Types()
    {
        var tree = CSharpSyntaxTree.ParseText(Source);
        var compilation = CSharpCompilation.Create(
            "MemberSignatureMatchHelpersTests",
            [tree],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        return (
            compilation.GetTypeByMetadataName("A")!,
            compilation.GetTypeByMetadataName("B")!,
            compilation.GetTypeByMetadataName("X")!);
    }

    private static IMethodSymbol Method(INamedTypeSymbol type, string name, int arity, string firstParameterKeyword) =>
        type.GetMembers(name).OfType<IMethodSymbol>().Single(m =>
            m.Arity == arity
            && m.Parameters.Length == 1
            && m.Parameters[0].RefKind == RefKind.None
            && m.Parameters[0].Type.ToDisplayString() == firstParameterKeyword);

    private static IPropertySymbol Property(INamedTypeSymbol type, string name) =>
        type.GetMembers(name).OfType<IPropertySymbol>().Single();

    private static IPropertySymbol Indexer(INamedTypeSymbol type, string parameterKeyword) =>
        type.GetMembers().OfType<IPropertySymbol>()
            .Single(p => p.IsIndexer && p.Parameters[0].Type.ToDisplayString() == parameterKeyword);

    private static IEventSymbol Event(INamedTypeSymbol type, string name) =>
        type.GetMembers(name).OfType<IEventSymbol>().Single();
}

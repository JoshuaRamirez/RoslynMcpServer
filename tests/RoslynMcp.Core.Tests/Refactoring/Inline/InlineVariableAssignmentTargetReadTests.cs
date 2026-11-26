using Microsoft.CodeAnalysis;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Inline;
using RoslynMcp.Core.Workspace;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Inline;

/// <summary>
/// Regression tests for #3090: inline_variable treated every identifier under an assignment's
/// left side as a write, so a local only read inside an index (<c>values[index] = 10</c>) was
/// refused with MultipleAssignments (single-site) or silently skipped (allFiles).
/// </summary>
public class InlineVariableAssignmentTargetReadTests
{
    private const string IndexSource = """
        namespace Demo;

        public class Slots
        {
            public int[] Fill(int[] values)
            {
                int index = 2;
                values[index] = 10;
                values[index + 1] += 3;
                return values;
            }
        }
        """;

    [SkippableFact]
    public async Task SingleSite_LocalReadInsideIndexOfTarget_IsInlined()
    {
        await using var workspace = await TempWorkspace.CreateAsync(IndexSource, "latest");

        var result = await new InlineVariableOperation(workspace.Context).ExecuteAsync(new InlineVariableParams
        {
            SourceFile = workspace.SourcePath,
            VariableName = "index"
        });

        Assert.True(result.Success, result.Error?.Message);
        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("values[2] = 10;", text, StringComparison.Ordinal);
        Assert.Contains("values[2 + 1] += 3;", text, StringComparison.Ordinal);
        Assert.DoesNotContain("int index", text, StringComparison.Ordinal);
        Assert.Empty(await workspace.GetDiskErrorsAsync());
    }

    [SkippableFact]
    public async Task AllFiles_LocalReadInsideIndexOfTarget_IsInlined()
    {
        await using var workspace = await TempWorkspace.CreateAsync(IndexSource, "latest");

        var result = await new InlineVariableOperation(workspace.Context).ExecuteAsync(new InlineVariableParams
        {
            AllFiles = true
        });

        Assert.True(result.Success, result.Error?.Message);
        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("values[2] = 10;", text, StringComparison.Ordinal);
        Assert.DoesNotContain("int index", text, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task SingleSite_ReferenceTypeReceiverOfMemberWrite_IsInlined()
    {
        const string source = """
            namespace Demo;

            public class Widget { public string Name = ""; }

            public class Holder
            {
                private Widget _current = new Widget();

                public Widget Rename()
                {
                    Widget target = _current;
                    target.Name = "renamed";
                    return target;
                }
            }
            """;
        await using var workspace = await TempWorkspace.CreateAsync(source, "latest");

        var result = await new InlineVariableOperation(workspace.Context).ExecuteAsync(new InlineVariableParams
        {
            SourceFile = workspace.SourcePath,
            VariableName = "target"
        });

        Assert.True(result.Success, result.Error?.Message);
        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("_current.Name = \"renamed\";", text, StringComparison.Ordinal);
        Assert.Contains("return _current;", text, StringComparison.Ordinal);
    }

    [SkippableTheory]
    [InlineData("copy.Value = 5;")]
    [InlineData("(copy).Value = 5;")]
    [InlineData("copy.Inner.Value = 5;")]
    [InlineData("copy!.Value = 5;")]
    [InlineData("(copy!).Value = 5;")]
    [InlineData("copy!.Inner.Value = 5;")]
    [InlineData("copy = default;")]
    [InlineData("(copy, other) = (other, copy);")]
    public async Task SingleSite_LocalItselfOrValueTypeReceiverWritten_StillRefused(string write)
    {
        var source = $$"""
            namespace Demo;

            public struct Cell { public int Value; public Inner Inner; }
            public struct Inner { public int Value; }

            public class Grid
            {
                private Cell _cell;

                public int Touch()
                {
                    Cell copy = _cell;
                    Cell other = _cell;
                    {{write}}
                    return copy.Value + other.Value;
                }
            }
            """;
        await using var workspace = await TempWorkspace.CreateAsync(source, "latest");

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            new InlineVariableOperation(workspace.Context).ExecuteAsync(new InlineVariableParams
            {
                SourceFile = workspace.SourcePath,
                VariableName = "copy"
            }));

        Assert.Equal(ErrorCodes.MultipleAssignments, ex.ErrorCode);
        Assert.Equal(source, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    /// <summary>
    /// Sources where writing through the local and through the inlined initializer would not hit the same
    /// object (#3090 review): a boxed struct copy, or a fresh instance re-created at every use site (which
    /// also fails to compile: CS0178, CS9176, CS1002). The receiver may sit under parentheses, casts,
    /// <c>as</c> or <c>!</c>, and a user-defined operator / conversion or a property getter counts as a
    /// call that may return a fresh object (third review). checked/unchecked, ?:, ??, switch arms, an
    /// assignment's value and a call argument inside the target do not hide a write either (fourth review),
    /// nor do ??=, an out argument or __makeref / __refvalue (fifth review: unknown parents refuse).
    /// </summary>
    public static TheoryData<string, string> NotSharedReceiverSources => new()
    {
        {
            "boxing",
            """
            namespace Demo;

            public interface IHas { int Value { get; set; } }
            public struct S : IHas { public int Value { get; set; } }

            public class Box
            {
                private S _s;

                public int Touch()
                {
                    IHas v = _s;
                    v.Value = 5;
                    return _s.Value;
                }
            }
            """
        },
        {
            "boxing-suppressed",
            """
            namespace Demo;

            public interface IHas { int Value { get; set; } }
            public struct S : IHas { public int Value { get; set; } }

            public class Box
            {
                private S _s;

                public int Touch()
                {
                    IHas v = _s;
                    v!.Value = 5;
                    return _s.Value;
                }
            }
            """
        },
        {
            "array-creation-suppressed",
            """
            namespace Demo;

            public class Arrays
            {
                public int[] Make()
                {
                    int[] arr = new int[3];
                    arr![0] = 1;
                    return arr;
                }
            }
            """
        },
        {
            "boxing-cast",
            """
            namespace Demo;

            public interface IHas { int Value { get; set; } }
            public struct S : IHas { public int Value { get; set; } }

            public class Box
            {
                private S _s;

                public int Touch()
                {
                    IHas v = _s;
                    ((IHas)v).Value = 5;
                    return v.Value;
                }
            }
            """
        },
        {
            "boxing-cast-suppressed",
            """
            namespace Demo;

            public interface IHas { int Value { get; set; } }
            public struct S : IHas { public int Value { get; set; } }

            public class Box
            {
                private S _s;

                public int Touch()
                {
                    IHas v = _s;
                    ((IHas)v!).Value = 5;
                    return v.Value;
                }
            }
            """
        },
        {
            "boxing-double-cast",
            """
            namespace Demo;

            public interface IHas { int Value { get; set; } }
            public struct S : IHas { public int Value { get; set; } }

            public class Box
            {
                private S _s;

                public int Touch()
                {
                    IHas v = _s;
                    ((IHas)(object)v!).Value = 5;
                    return v.Value;
                }
            }
            """
        },
        {
            "boxing-as",
            """
            namespace Demo;

            public interface IHas { int Value { get; set; } }
            public struct S : IHas { public int Value { get; set; } }

            public class Box
            {
                private S _s;

                public int Touch()
                {
                    IHas v = _s;
                    (v as IHas)!.Value = 5;
                    return v.Value;
                }
            }
            """
        },
        {
            "array-cast",
            """
            namespace Demo;

            public class Arrays
            {
                public int[] Make()
                {
                    int[] arr = new int[3];
                    ((int[])arr)[0] = 1;
                    return arr;
                }
            }
            """
        },
        {
            "array-as",
            """
            namespace Demo;

            public class Arrays
            {
                public int[] Make()
                {
                    int[] arr = new int[3];
                    (arr as int[])![0] = 1;
                    return arr;
                }
            }
            """
        },
        {
            "operator-unary",
            """
            namespace Demo;

            public class Gadget { }

            public class Widget
            {
                public string Name = "";
                public static Widget operator -(Widget w) => new Widget { Name = w.Name };
                public static Widget operator +(Widget a, Widget b) => new Widget();
                public static explicit operator Widget(Gadget g) => new Widget();
            }

            public class Factory
            {
                private Widget _w = new Widget();
                private Gadget _g = new Gadget();
                private Widget Fresh => new Widget();

                public Widget Make()
                {
                    Widget w = -_w;
                    w.Name = "x";
                    return w;
                }
            }
            """
        },
        {
            "operator-binary",
            """
            namespace Demo;

            public class Gadget { }

            public class Widget
            {
                public string Name = "";
                public static Widget operator -(Widget w) => new Widget { Name = w.Name };
                public static Widget operator +(Widget a, Widget b) => new Widget();
                public static explicit operator Widget(Gadget g) => new Widget();
            }

            public class Factory
            {
                private Widget _w = new Widget();
                private Gadget _g = new Gadget();
                private Widget Fresh => new Widget();

                public Widget Make()
                {
                    Widget w = _w + _w;
                    w.Name = "x";
                    return w;
                }
            }
            """
        },
        {
            "operator-conversion",
            """
            namespace Demo;

            public class Gadget { }

            public class Widget
            {
                public string Name = "";
                public static Widget operator -(Widget w) => new Widget { Name = w.Name };
                public static Widget operator +(Widget a, Widget b) => new Widget();
                public static explicit operator Widget(Gadget g) => new Widget();
            }

            public class Factory
            {
                private Widget _w = new Widget();
                private Gadget _g = new Gadget();
                private Widget Fresh => new Widget();

                public Widget Make()
                {
                    Widget w = (Widget)_g;
                    w.Name = "x";
                    return w;
                }
            }
            """
        },
        {
            "property-getter",
            """
            namespace Demo;

            public class Gadget { }

            public class Widget
            {
                public string Name = "";
                public static Widget operator -(Widget w) => new Widget { Name = w.Name };
                public static Widget operator +(Widget a, Widget b) => new Widget();
                public static explicit operator Widget(Gadget g) => new Widget();
            }

            public class Factory
            {
                private Widget _w = new Widget();
                private Gadget _g = new Gadget();
                private Widget Fresh => new Widget();

                public Widget Make()
                {
                    Widget w = Fresh;
                    w.Name = "x";
                    return w;
                }
            }
            """
        },
        {
            "checked-assign",
            """
            namespace Demo;

            public class Counter
            {
                private int _i = 1;

                public int Touch()
                {
                    int n = _i;
                    checked(n) = 4;
                    return n;
                }
            }
            """
        },
        {
            "unchecked-assign",
            """
            namespace Demo;

            public class Counter
            {
                private int _i = 1;

                public int Touch()
                {
                    int n = _i;
                    unchecked(n) = 4;
                    return n;
                }
            }
            """
        },
        {
            "checked-increment",
            """
            namespace Demo;

            public class Counter
            {
                private int _i = 1;

                public int Touch()
                {
                    int n = _i;
                    checked(n)++;
                    return n;
                }
            }
            """
        },
        {
            "array-checked",
            """
            namespace Demo;

            public static class Ext
            {
                public static int[] IdA(int[] a) => a;
            }

            public class Arrays
            {
                private bool _flag = true;
                private int[] _other = new int[1];

                public int[] Make()
                {
                    int[] arr = new int[3];
                    checked(arr[0]) = 5;
                    return arr;
                }
            }
            """
        },
        {
            "array-conditional",
            """
            namespace Demo;

            public static class Ext
            {
                public static int[] IdA(int[] a) => a;
            }

            public class Arrays
            {
                private bool _flag = true;
                private int[] _other = new int[1];

                public int[] Make()
                {
                    int[] arr = new int[3];
                    (_flag ? arr : _other)[0] = 1;
                    return arr;
                }
            }
            """
        },
        {
            "array-coalesce",
            """
            namespace Demo;

            public static class Ext
            {
                public static int[] IdA(int[] a) => a;
            }

            public class Arrays
            {
                private bool _flag = true;
                private int[] _other = new int[1];

                public int[] Make()
                {
                    int[] arr = new int[3];
                    (arr ?? _other)[0] = 1;
                    return arr;
                }
            }
            """
        },
        {
            "array-switch",
            """
            namespace Demo;

            public static class Ext
            {
                public static int[] IdA(int[] a) => a;
            }

            public class Arrays
            {
                private bool _flag = true;
                private int[] _other = new int[1];

                public int[] Make()
                {
                    int[] arr = new int[3];
                    (_flag switch { true => arr, _ => _other })[0] = 1;
                    return arr;
                }
            }
            """
        },
        {
            "array-assignment-value",
            """
            namespace Demo;

            public static class Ext
            {
                public static int[] IdA(int[] a) => a;
            }

            public class Arrays
            {
                private bool _flag = true;
                private int[] _other = new int[1];

                public int[] Make()
                {
                    int[] arr = new int[3];
                    (_other = arr)[0] = 1;
                    return arr;
                }
            }
            """
        },
        {
            "array-argument-escape",
            """
            namespace Demo;

            public static class Ext
            {
                public static int[] IdA(int[] a) => a;
            }

            public class Arrays
            {
                private bool _flag = true;
                private int[] _other = new int[1];

                public int[] Make()
                {
                    int[] arr = new int[3];
                    Ext.IdA(arr)[0] = 1;
                    return arr;
                }
            }
            """
        },
        {
            "array-coalesce-assignment-value",
            """
            namespace Demo;

            public static class Ext
            {
                public static int[] IdA(int[] a) => a;
            }

            public class Arrays
            {
                private bool _flag = true;
                private int[] _other = new int[1];

                public int[] Make()
                {
                    int[] arr = new int[3];
                    (_other ??= arr)[0] = 1;
                    return arr;
                }
            }
            """
        },
        {
            "array-out-argument-escape",
            """
            namespace Demo;

            public static class Ext
            {
                public static int[] IdA(int[] a) => a;
            }

            public class Arrays
            {
                private bool _flag = true;
                private int[] _other = new int[1];

                public int[] Make()
                {
                    int[] arr = new int[3];
                    int.TryParse("1", out Ext.IdA(arr)[0]);
                    return arr;
                }
            }
            """
        },
        {
            "makeref",
            """
            namespace Demo;

            public class Counter
            {
                private int _i = 1;

                public int Touch()
                {
                    int n = _i;
                    System.TypedReference tr = __makeref(n);
                    __refvalue(tr, int) = 4;
                    return n;
                }
            }
            """
        },
        {
            "array-creation",
            """
            namespace Demo;

            public class Arrays
            {
                public int[] Make()
                {
                    int[] arr = new int[3];
                    arr[0] = 1;
                    return arr;
                }
            }
            """
        },
        {
            "array-initializer",
            """
            namespace Demo;

            public class Arrays
            {
                public int[] Make()
                {
                    int[] arr = { 1, 2 };
                    arr[0] = 5;
                    return arr;
                }
            }
            """
        },
        {
            "collection-expression",
            """
            using System.Collections.Generic;

            namespace Demo;

            public class Lists
            {
                public List<int> Make()
                {
                    List<int> list = [1, 2];
                    list[0] = 5;
                    return list;
                }
            }
            """
        },
        {
            "with-expression",
            """
            namespace Demo;

            public record R(int[] A);

            public class Records
            {
                private R _r = new R(new int[1]);

                public R Make()
                {
                    R r = _r with { };
                    r.A[0] = 1;
                    return r;
                }
            }
            """
        },
        {
            "target-typed-new",
            """
            namespace Demo;

            public class Widget { public string Name = ""; }

            public class Factory
            {
                public Widget Make()
                {
                    Widget w = new();
                    w.Name = "made";
                    return w;
                }
            }
            """
        },
    };

    [SkippableTheory]
    [MemberData(nameof(NotSharedReceiverSources))]
    public async Task SingleSite_ReceiverNotSharedWithInitializer_IsRefused(string kind, string source)
    {
        await using var workspace = await TempWorkspace.CreateAsync(source, "latest");
        var variableName = kind switch
        {
            _ when kind.StartsWith("boxing", StringComparison.Ordinal) => "v",
            _ when kind.StartsWith("checked", StringComparison.Ordinal) || kind.StartsWith("unchecked", StringComparison.Ordinal) || kind == "makeref" => "n",
            _ when kind.StartsWith("operator", StringComparison.Ordinal) || kind == "property-getter" => "w",
            "collection-expression" => "list",
            "with-expression" => "r",
            "target-typed-new" => "w",
            _ => "arr"
        };

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            new InlineVariableOperation(workspace.Context).ExecuteAsync(new InlineVariableParams
            {
                SourceFile = workspace.SourcePath,
                VariableName = variableName
            }));

        Assert.Equal(ErrorCodes.MultipleAssignments, ex.ErrorCode);
        Assert.Equal(source, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableTheory]
    [MemberData(nameof(NotSharedReceiverSources))]
    public async Task AllFiles_ReceiverNotSharedWithInitializer_IsLeftAlone(string kind, string source)
    {
        await using var workspace = await TempWorkspace.CreateAsync(source, "latest");

        var result = await new InlineVariableOperation(workspace.Context).ExecuteAsync(new InlineVariableParams
        {
            AllFiles = true
        });

        Assert.True(result.Success, $"{kind}: {result.Error?.Message}");
        Assert.Equal(source, await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Empty(await workspace.GetDiskErrorsAsync());
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ImplicitReferenceConversionReceiver_IsInlined_FullOutput(bool allFiles)
    {
        // Class -> interface is an implicit reference conversion: the local and the field are the same object.
        // The doubled indentation is the existing declaration-removal trivia behavior, not part of #3090.
        const string source = """
            namespace Demo;

            public interface IHas { int Value { get; set; } }
            public class C : IHas { public int Value { get; set; } }

            public class Holder
            {
                private C _c = new C();

                public int Touch()
                {
                    IHas v = _c;
                    v.Value = 5;
                    return _c.Value;
                }
            }
            """;
        const string expected = """
            namespace Demo;

            public interface IHas { int Value { get; set; } }
            public class C : IHas { public int Value { get; set; } }

            public class Holder
            {
                private C _c = new C();

                public int Touch()
                {
                            _c.Value = 5;
                    return _c.Value;
                }
            }
            """;
        await using var workspace = await TempWorkspace.CreateAsync(source, "latest");

        var result = await new InlineVariableOperation(workspace.Context).ExecuteAsync(allFiles
            ? new InlineVariableParams { AllFiles = true }
            : new InlineVariableParams { SourceFile = workspace.SourcePath, VariableName = "v" });

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(expected, await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Empty(await workspace.GetDiskErrorsAsync());
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CastInitializer_IsParenthesizedBeforeMemberAndElementAccess(bool allFiles)
    {
        // (Derived)_base.X casts _base.X, and (IList<int>)_a[0] casts _a[0] (#3090 review).
        // The extra indentation is the existing declaration-removal trivia behavior (one level per removed line).
        const string source = """
            using System.Collections.Generic;

            namespace Demo;

            public class Base { }
            public class Derived : Base { public int X; }

            public class Holder
            {
                private Base _base = new Derived();
                private object _a = new List<int> { 7 };

                public int Read()
                {
                    var d = (Derived)_base;
                    var a = (IList<int>)_a;
                    return d.X + a[0];
                }
            }
            """;
        const string expected = """
            using System.Collections.Generic;

            namespace Demo;

            public class Base { }
            public class Derived : Base { public int X; }

            public class Holder
            {
                private Base _base = new Derived();
                private object _a = new List<int> { 7 };

                public int Read()
                {
                                    return ((Derived)_base).X + ((IList<int>)_a)[0];
                }
            }
            """;
        await using var workspace = await TempWorkspace.CreateAsync(source, "latest");
        var operation = new InlineVariableOperation(workspace.Context);

        if (allFiles)
        {
            var result = await operation.ExecuteAsync(new InlineVariableParams { AllFiles = true });
            Assert.True(result.Success, result.Error?.Message);
        }
        else
        {
            foreach (var name in new[] { "d", "a" })
            {
                var result = await new InlineVariableOperation(workspace.Context).ExecuteAsync(
                    new InlineVariableParams { SourceFile = workspace.SourcePath, VariableName = name });
                Assert.True(result.Success, result.Error?.Message);
            }
        }

        Assert.Equal(expected, await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Empty(await workspace.GetDiskErrorsAsync());
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PrefixUnaryInitializer_IsParenthesizedAsReceiver(bool allFiles)
    {
        // -_i.ToString() negates the string (CS0023) and -_w.Name negates the field (#3090 review).
        // The extra indentation is the existing declaration-removal trivia behavior (one level per removed line).
        const string source = """
            namespace Demo;

            public class Widget
            {
                public string Name = "";
                public static Widget operator -(Widget w) => new Widget { Name = w.Name };
            }

            public class Reader
            {
                private int _i = 3;
                private Widget _w = new Widget();

                public string Read()
                {
                    int n = -_i;
                    Widget w = -_w;
                    return n.ToString() + w.Name;
                }
            }
            """;
        const string expected = """
            namespace Demo;

            public class Widget
            {
                public string Name = "";
                public static Widget operator -(Widget w) => new Widget { Name = w.Name };
            }

            public class Reader
            {
                private int _i = 3;
                private Widget _w = new Widget();

                public string Read()
                {
                                    return (-_i).ToString() + (-_w).Name;
                }
            }
            """;
        await using var workspace = await TempWorkspace.CreateAsync(source, "latest");

        if (allFiles)
        {
            var result = await new InlineVariableOperation(workspace.Context).ExecuteAsync(new InlineVariableParams { AllFiles = true });
            Assert.True(result.Success, result.Error?.Message);
        }
        else
        {
            foreach (var name in new[] { "n", "w" })
            {
                var result = await new InlineVariableOperation(workspace.Context).ExecuteAsync(
                    new InlineVariableParams { SourceFile = workspace.SourcePath, VariableName = name });
                Assert.True(result.Success, result.Error?.Message);
            }
        }

        Assert.Equal(expected, await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Empty(await workspace.GetDiskErrorsAsync());
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SharedReceiverUnderCast_IsStillInlined_FullOutput(bool allFiles)
    {
        // Walking through casts must not over-refuse: the field is the same object at every site.
        const string source = """
            namespace Demo;

            public class Widget { public string Name = ""; }

            public class Holder
            {
                private Widget _current = new Widget();

                public object Rename()
                {
                    object target = _current;
                    ((Widget)target).Name = "renamed";
                    return target;
                }
            }
            """;
        const string expected = """
            namespace Demo;

            public class Widget { public string Name = ""; }

            public class Holder
            {
                private Widget _current = new Widget();

                public object Rename()
                {
                            ((Widget)_current).Name = "renamed";
                    return _current;
                }
            }
            """;
        await using var workspace = await TempWorkspace.CreateAsync(source, "latest");

        var result = await new InlineVariableOperation(workspace.Context).ExecuteAsync(allFiles
            ? new InlineVariableParams { AllFiles = true }
            : new InlineVariableParams { SourceFile = workspace.SourcePath, VariableName = "target" });

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(expected, await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Empty(await workspace.GetDiskErrorsAsync());
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ValueTypeLocalPassedToCallInsideIndex_IsInlined_FullOutput(bool allFiles)
    {
        // An int argument carries no reference, so the call-argument escape rule must not refuse it (#3090).
        // The extra indentation is the existing declaration-removal trivia behavior.
        const string source = """
            namespace Demo;

            public class Slots
            {
                private static int Compute(int i) => i + 1;

                public int[] Fill(int[] values)
                {
                    int index = 2;
                    values[Compute(index)] = 10;
                    return values;
                }
            }
            """;
        const string expected = """
            namespace Demo;

            public class Slots
            {
                private static int Compute(int i) => i + 1;

                public int[] Fill(int[] values)
                {
                            values[Compute(2)] = 10;
                    return values;
                }
            }
            """;
        await using var workspace = await TempWorkspace.CreateAsync(source, "latest");

        var result = await new InlineVariableOperation(workspace.Context).ExecuteAsync(allFiles
            ? new InlineVariableParams { AllFiles = true }
            : new InlineVariableParams { SourceFile = workspace.SourcePath, VariableName = "index" });

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(expected, await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Empty(await workspace.GetDiskErrorsAsync());
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

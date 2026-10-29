using System.Reflection;
using System.Runtime.Loader;
using System.Text.RegularExpressions;
using CSharpToJava.Core.RefOutInPreprocessor;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// CS1612 family: a held delegate lambda whose carried state T is a VALUE type
/// used to be rewritten with body accesses straight through the abstract
/// <c>ByRef&lt;T&gt;.Value</c> property return (<c>holder.Value.X = …</c>,
/// <c>holder.Value.M()</c>, <c>ref holder.Value.X</c>, <c>--holder.Value.X</c>),
/// which C# rejects because the property return is not a variable. The fix
/// lowers such a body to a shadow local copy-in/copy-out frame:
/// <code>
/// (ByRef&lt;T&gt; state, …) =>
/// {
///     T __shadow = state.Value;
///     … state occurrences bind to __shadow …
///     state.Value = __shadow;   // before each return / at void fall-through
/// }
/// </code>
/// The same sites had a second defect: an internal-source <c>ref</c> call in
/// the body (e.g. <c>state.Item1.ReplacementImpl(ref state.Item2, …)</c>) was
/// never statement-lifted, because the lambda's parameter list was rebuilt
/// BEFORE its body was visited; the rebuilt node is detached from the original
/// syntax tree and every body <c>GetSymbolInfo</c> throws ArgumentException
/// ("Syntax node is not within syntax tree"), silently swallowed by the
/// lifter. Visiting the body first fixes both; the lifted accessor proxy then
/// naturally closes over the shadow local (<c>() => __shadow.Item2</c>).
/// </summary>
public class RefOutInStructHolderLambdaTests
{
    // Mirrors src/Cs2jRefOutIn/ByRef.cs.
    private const string ByRefSupportStub = """
        namespace Cs2jByRef
        {
            public abstract class ByRef<T>
            {
                public abstract T Value { get; set; }
                public static ByRef<T> Create() => new SlotByRef<T>();
                public static ByRef<T> Create(T value) => new SlotByRef<T>(value);
                public static ByRef<T> Create(System.Func<T> getter, System.Action<T> setter)
                    => new AccessorByRef<T>(getter, setter);
                // out 值类型读回（与 src/Cs2jRefOutIn/ByRef.cs 的 ReadbackX 一致，Java 半区 null→default 桥）
                public static int ReadbackInt(ByRef<int> slot) => slot.Value;
                public static int ReadbackInt(ByRef<uint> slot) => (int)slot.Value;
                public static int ReadbackInt(ByRef<byte> slot) => slot.Value;
                public static short ReadbackShort(ByRef<short> slot) => slot.Value;
                public static short ReadbackShort(ByRef<ushort> slot) => (short)slot.Value;
                public static byte ReadbackByte(ByRef<sbyte> slot) => (byte)slot.Value;
                public static long ReadbackLong(ByRef<long> slot) => slot.Value;
                public static long ReadbackLong(ByRef<ulong> slot) => (long)slot.Value;
                public static char ReadbackChar(ByRef<char> slot) => slot.Value;
                public static bool ReadbackBool(ByRef<bool> slot) => slot.Value;
                public static float ReadbackFloat(ByRef<float> slot) => slot.Value;
                public static double ReadbackDouble(ByRef<double> slot) => slot.Value;
            }
            internal sealed class SlotByRef<T> : ByRef<T>
            {
                private T _slot = default!;
                public SlotByRef() { }
                public SlotByRef(T value) { _slot = value; }
                public override T Value { get => _slot; set => _slot = value; }
            }
            internal sealed class AccessorByRef<T> : ByRef<T>
            {
                private readonly System.Func<T> _getter;
                private readonly System.Action<T> _setter;
                public AccessorByRef(System.Func<T> getter, System.Action<T> setter)
                { _getter = getter; _setter = setter; }
                public override T Value { get => _getter(); set => _setter(value); }
            }
        }
        """;

    private static IEnumerable<MetadataReference> GetReferences()
    {
        var trusted = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES");
        if (trusted != null)
        {
            foreach (var assembly in trusted.Split(Path.PathSeparator))
            {
                if (File.Exists(assembly)) yield return MetadataReference.CreateFromFile(assembly);
            }

            yield break;
        }

        yield return MetadataReference.CreateFromFile(typeof(object).Assembly.Location);
    }

    private static RefOutInRewriterResult Run(string src)
    {
        var tree = CSharpSyntaxTree.ParseText(src);
        var compilation = CSharpCompilation.Create(
            "roi-structheld-" + Guid.NewGuid().ToString("N"),
            new[] { tree },
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var model = compilation.GetSemanticModel(tree);
        return new RefOutInRewriter().Rewrite(src, model);
    }

    private static void AssertOutputCompiles(string code)
    {
        var tree = CSharpSyntaxTree.ParseText(code);
        var support = CSharpSyntaxTree.ParseText(ByRefSupportStub);
        var compilation = CSharpCompilation.Create(
            "roi-structheld-verify-" + Guid.NewGuid().ToString("N"),
            new[] { tree, support },
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(errors.Count == 0,
            "ref/out/in struct-holder output failed to compile:\n" + string.Join("\n", errors));
    }

    private static string? Invoke(string source, string typeName, string method, bool withByRefStub)
    {
        var trees = new List<SyntaxTree> { CSharpSyntaxTree.ParseText(source) };
        if (withByRefStub) trees.Add(CSharpSyntaxTree.ParseText(ByRefSupportStub));
        var compilation = CSharpCompilation.Create(
            "roi-structheld-emit-" + Guid.NewGuid().ToString("N"),
            trees,
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        using var ms = new MemoryStream();
        var emit = compilation.Emit(ms);
        Assert.True(emit.Success,
            "emit failed:\n" + string.Join("\n",
                emit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));

        var asm = AssemblyLoadContext.Default.LoadFromStream(new MemoryStream(ms.ToArray()));
        var type = asm.GetType(typeName)!;
        return (string?)type.GetMethod(method, BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, null);
    }

    // No naked `ref/out <ident>.Value` may survive (CS1615/CS1612 shape).
    private static void AssertNoBareRefHolderValue(string code)
    {
        Assert.DoesNotMatch(@"\bref\s+[A-Za-z_][A-Za-z0-9_]*\.Value\b", code);
        Assert.DoesNotMatch(@"\bout\s+[A-Za-z_][A-Za-z0-9_]*\.Value\b", code);
    }

    private const string StructFixtures = """
        namespace DemoNs;
        public struct Sub
        {
            public int Z;
            public int[] Hist;
            public void Bump(int d) { Z += d; }
            // Mutating a heap-reached field stays observable even when the
            // receiver is a ByRef<T>.Value copy (mirrors SegmentStringBuilder,
            // which mutates its backing array).
            public void Hit(int d) { Hist[d]++; }
            public int Peek() => Z;
        }
        public struct State
        {
            public int A;
            public Sub B;
            public string C;
            public void Inc() { A++; }
        }
        internal delegate bool Cb<T>(ref T s, int m);
        internal delegate void Vcb<T>(ref T s, int m);
        internal class Runner
        {
            // Internal source method whose ref parameter is rewritten to
            // ByRef<Sub>; the callback must statement-lift the call.
            public void Helper(ref Sub b, int d) { b.Hit(d); }
            public void Run<T>(ref T s, Cb<T> cb, bool x)
            {
                for (int i = 0; i < 4; i++)
                {
                    if (!cb(ref s, i)) break;
                }
            }
            public void RunVoid<T>(ref T s, Vcb<T> cb, int rounds)
            {
                for (int i = 0; i < rounds; i++) cb(ref s, i);
            }
        }
        """;

    [Fact]
    public void Block_Body_Struct_Holder_Uses_Shadow_Local_And_Lifts_Internal_Ref_Call()
    {
        var src = StructFixtures + """
            public static class Demo
            {
                public static string Go()
                {
                    var r = new Runner();
                    var state = new State
                    {
                        A = 0,
                        B = new Sub { Z = 0, Hist = new int[4] },
                        C = ""
                    };
                    r.Run(ref state, (ref State s, int m) =>
                    {
                        s.B.Bump(m);                 // struct method on shadow field
                        s.A += m;                    // field assign
                        r.Helper(ref s.B, m);         // ref field arg to internal method
                        s.Inc();                      // struct method on shadow
                        s.C = s.C + ".";              // reference-type field
                        return ++s.A < 8;              // pre-increment field in return
                    }, true);
                    return state.A + "|" + state.B.Z + "|"
                        + string.Join(",", state.B.Hist) + "|" + state.C;
                }
            }
            """;

        var result = Run(src);
        Assert.True(result.Changed);
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == RefOutInSeverity.Error);

        // Shadow frame shape.
        Assert.Contains("DemoNs.State ", result.OutputCode);
        Assert.Contains("__shadow", result.OutputCode);
        Assert.Contains("= s.Value;", result.OutputCode);
        Assert.Contains("s.Value = __shadow", result.OutputCode);
        // The return expression is materialized BEFORE copy-out so its ++ lands.
        Assert.Contains("bool __ret", result.OutputCode);
        Assert.DoesNotContain("return ++__shadow", result.OutputCode);
        // The internal call reads the shadow into a copy-in slot and writes it
        // back after the call (no lambda over the mutable capture).
        Assert.Contains("ByRef<DemoNs.Sub>.Create(__shadow", result.OutputCode);
        Assert.Contains(".Helper(", result.OutputCode);
        Assert.DoesNotContain("ref s.Value", result.OutputCode);
        AssertNoBareRefHolderValue(result.OutputCode);
        AssertOutputCompiles(result.OutputCode);

        var expected = Invoke(src, "DemoNs.Demo", "Go", false);
        var actual = Invoke(result.OutputCode, "DemoNs.Demo", "Go", true);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Multiple_Return_Branches_Each_Copy_Back_The_Shadow()
    {
        var src = StructFixtures + """
            public static class Demo
            {
                public static string Go()
                {
                    var r = new Runner();
                    var state = new State
                    {
                        A = 0,
                        B = new Sub { Z = 0, Hist = new int[4] },
                        C = ""
                    };
                    r.Run(ref state, (ref State s, int m) =>
                    {
                        if (m == 0)
                        {
                            s.A = 100;
                            return true;   // continue: second round exercises the else branch
                        }
                        else
                        {
                            s.B.Z = 9;
                            return false;
                        }
                    }, true);
                    return state.A + "|" + state.B.Z;
                }
            }
            """;

        var result = Run(src);
        Assert.True(result.Changed);
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == RefOutInSeverity.Error);
        Assert.Contains("__shadow", result.OutputCode);
        // Copy-out appears on BOTH branches (two writeback sites before returns).
        var writebacks = Regex.Matches(result.OutputCode, @"s\.Value = __shadow").Count;
        Assert.True(writebacks >= 2, $"expected >=2 writebacks, got {writebacks}");
        AssertNoBareRefHolderValue(result.OutputCode);
        AssertOutputCompiles(result.OutputCode);

        var expected = Invoke(src, "DemoNs.Demo", "Go", false);
        var actual = Invoke(result.OutputCode, "DemoNs.Demo", "Go", true);
        Assert.Equal(expected, actual);
        Assert.Equal("100|9", actual);
    }

    [Fact]
    public void Expression_Bodied_Struct_Holder_Lambda_Forces_Block_With_Copy_In_Copy_Out()
    {
        var src = StructFixtures + """
            public static class Demo
            {
                public static string Go()
                {
                    var r = new Runner();
                    var state = new State { A = 0, B = new Sub(), C = "" };
                    r.Run(ref state, (ref State s, int m) => ++s.A < 3, true);
                    return state.A + "";
                }
            }
            """;

        var result = Run(src);
        Assert.True(result.Changed);
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == RefOutInSeverity.Error);
        Assert.Contains("__shadow", result.OutputCode);
        Assert.Contains("= s.Value;", result.OutputCode);
        Assert.Contains("s.Value = __shadow", result.OutputCode);
        // The single return expression is materialized into a temp first.
        Assert.Contains("bool __ret", result.OutputCode);
        Assert.Contains("return __ret", result.OutputCode);
        Assert.DoesNotContain("return ++__shadow", result.OutputCode);
        AssertNoBareRefHolderValue(result.OutputCode);
        AssertOutputCompiles(result.OutputCode);

        var expected = Invoke(src, "DemoNs.Demo", "Go", false);
        var actual = Invoke(result.OutputCode, "DemoNs.Demo", "Go", true);
        Assert.Equal(expected, actual);
        Assert.Equal("3", actual);
    }

    [Fact]
    public void Void_Block_Body_Writes_Back_At_Return_And_Fall_Through()
    {
        var src = StructFixtures + """
            public static class Demo
            {
                public static string Go()
                {
                    var r = new Runner();
                    var state = new State { A = 0, B = new Sub(), C = "" };
                    r.RunVoid(ref state, (ref State s, int m) =>
                    {
                        if (m == 1)
                        {
                            s.B.Z = 7;
                            return;                 // early return needs copy-out
                        }
                        s.A += m;                   // fall-through copy-out at end
                    }, 3);
                    return state.A + "|" + state.B.Z;
                }
            }
            """;

        var result = Run(src);
        Assert.True(result.Changed);
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == RefOutInSeverity.Error);
        Assert.Contains("__shadow", result.OutputCode);
        AssertNoBareRefHolderValue(result.OutputCode);
        AssertOutputCompiles(result.OutputCode);

        var expected = Invoke(src, "DemoNs.Demo", "Go", false);
        var actual = Invoke(result.OutputCode, "DemoNs.Demo", "Go", true);
        Assert.Equal(expected, actual);
        Assert.Equal("2|7", actual);
    }

    [Fact]
    public void Void_Expression_Body_Writes_Back_At_Fall_Through()
    {
        var src = StructFixtures + """
            public static class Demo
            {
                public static string Go()
                {
                    var r = new Runner();
                    var state = new State { A = 1, B = new Sub(), C = "" };
                    r.RunVoid(ref state, (ref State s, int m) => s.A += m, 3);
                    return state.A + "";
                }
            }
            """;

        var result = Run(src);
        Assert.True(result.Changed);
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == RefOutInSeverity.Error);
        Assert.Contains("__shadow", result.OutputCode);
        AssertNoBareRefHolderValue(result.OutputCode);
        AssertOutputCompiles(result.OutputCode);

        var expected = Invoke(src, "DemoNs.Demo", "Go", false);
        var actual = Invoke(result.OutputCode, "DemoNs.Demo", "Go", true);
        Assert.Equal(expected, actual);
        Assert.Equal("4", actual);
    }

    [Fact]
    public void Multiple_Value_Holders_Each_Get_Own_Shadow()
    {
        var src = """
            namespace DemoNs;
            public struct State { public int A; }
            internal delegate bool Cb2<TA, TB>(ref TA a, ref TB b, int m);
            internal class Runner
            {
                public void RunTwo<TA, TB>(ref TA a, ref TB b, Cb2<TA, TB> cb)
                {
                    cb(ref a, ref b, 1);
                    cb(ref a, ref b, 2);
                }
            }
            public static class Demo
            {
                public static string Go()
                {
                    var r = new Runner();
                    var x = new State { A = 0 };
                    var y = new State { A = 0 };
                    r.RunTwo(ref x, ref y, (ref State a, ref State b, int m) =>
                    {
                        a.A += m;
                        b.A += 2 * m;
                        return false;
                    });
                    return x.A + "|" + y.A;
                }
            }
            """;

        var result = Run(src);
        Assert.True(result.Changed);
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == RefOutInSeverity.Error);
        Assert.Contains("= a.Value;", result.OutputCode);
        Assert.Contains("= b.Value;", result.OutputCode);
        Assert.Contains("a.Value = __shadow", result.OutputCode);
        Assert.Contains("b.Value = __shadow", result.OutputCode);
        AssertNoBareRefHolderValue(result.OutputCode);
        AssertOutputCompiles(result.OutputCode);

        var expected = Invoke(src, "DemoNs.Demo", "Go", false);
        var actual = Invoke(result.OutputCode, "DemoNs.Demo", "Go", true);
        Assert.Equal(expected, actual);
        Assert.Equal("3|6", actual);
    }

    [Fact]
    public void Anonymous_Method_Struct_Holder_Uses_Shadow()
    {
        var src = StructFixtures + """
            public static class Demo
            {
                public static string Go()
                {
                    var r = new Runner();
                    var state = new State
                    {
                        A = 0,
                        B = new Sub { Z = 0, Hist = new int[4] },
                        C = ""
                    };
                    Cb<State> cb = delegate (ref State s, int m)
                    {
                        s.A += m;
                        r.Helper(ref s.B, m);
                        return false;
                    };
                    r.Run(ref state, cb, true);
                    return state.A + "|" + state.B.Z;
                }
            }
            """;

        var result = Run(src);
        Assert.True(result.Changed);
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == RefOutInSeverity.Error);
        Assert.Contains("__shadow", result.OutputCode);
        Assert.Contains("ByRef<DemoNs.Sub>.Create(__shadow", result.OutputCode);
        AssertNoBareRefHolderValue(result.OutputCode);
        AssertOutputCompiles(result.OutputCode);

        var expected = Invoke(src, "DemoNs.Demo", "Go", false);
        var actual = Invoke(result.OutputCode, "DemoNs.Demo", "Go", true);
        Assert.Equal(expected, actual);
        Assert.Equal("0|0", actual);
    }

    [Fact]
    public void Nested_Lambda_And_Local_Function_Are_Copyout_Barriers()
    {
        // NOTE: a nested lambda/local function that captures the ref parameter
        // itself is CS1628 in the ORIGINAL source, so legal nesting uses
        // by-value params / ordinary captured locals. In the lowered output the
        // shadow local is captured by the accessor-proxy lambdas the lifter
        // generates for internal ref calls, and the nested constructs must act
        // as barriers: their own returns must never receive shadow copy-out.
        var src = StructFixtures + """
            public static class Demo
            {
                public static string Go()
                {
                    var r = new Runner();
                    var state = new State
                    {
                        A = 0,
                        B = new Sub { Z = 0, Hist = new int[4] },
                        C = ""
                    };
                    r.Run(ref state, (ref State s, int m) =>
                    {
                        int AddTo(int v, int d) { return v + d; }
                        s.A = AddTo(s.A, m);
                        int cur = s.A;
                        System.Func<int, int> twice = d => cur + 2 * d;
                        s.B.Z = twice(m);
                        r.Helper(ref s.B, m);
                        return s.A < 8;
                    }, true);
                    return state.A + "|" + state.B.Z + "|"
                        + string.Join(",", state.B.Hist);
                }
            }
            """;

        var result = Run(src);
        Assert.True(result.Changed);
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == RefOutInSeverity.Error);
        Assert.Contains("__shadow", result.OutputCode);
        // Exactly ONE copy-out: before the outer lambda's own return. The
        // return inside the local function and the nested lambda are barriers.
        var writebacks = Regex.Matches(result.OutputCode, @"s\.Value = __shadow").Count;
        Assert.Equal(1, writebacks);
        // The lifted internal call copies the shadow into a slot and writes back.
        Assert.Contains("ByRef<DemoNs.Sub>.Create(__shadow", result.OutputCode);
        AssertNoBareRefHolderValue(result.OutputCode);
        AssertOutputCompiles(result.OutputCode);

        var expected = Invoke(src, "DemoNs.Demo", "Go", false);
        var actual = Invoke(result.OutputCode, "DemoNs.Demo", "Go", true);
        Assert.Equal(expected, actual);
        Assert.Equal("6|12|1,1,1,1", actual);
    }

    [Fact]
    public void Reference_Type_Holder_Keeps_Direct_Value_Access()
    {
        var src = """
            namespace DemoNs;
            public class RState { public int A; }
            internal delegate bool Cb(ref RState s, int m);
            internal class Runner
            {
                public void Run(ref RState s, Cb cb)
                {
                    int i = 0;
                    while (cb(ref s, i)) i++;
                }
            }
            public static class Demo
            {
                public static string Go()
                {
                    var r = new Runner();
                    var state = new RState();
                    r.Run(ref state, (ref RState s, int m) =>
                    {
                        s.A += m;
                        return ++s.A < 3;
                    });
                    return state.A + "";
                }
            }
            """;

        var result = Run(src);
        Assert.True(result.Changed);
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == RefOutInSeverity.Error);
        Assert.Contains("(ByRef<RState> s, int m)", result.OutputCode);
        Assert.Contains("s.Value.A += m", result.OutputCode);
        Assert.DoesNotContain("__shadow", result.OutputCode);
        AssertOutputCompiles(result.OutputCode);

        var expected = Invoke(src, "DemoNs.Demo", "Go", false);
        var actual = Invoke(result.OutputCode, "DemoNs.Demo", "Go", true);
        Assert.Equal(expected, actual);
        Assert.Equal("3", actual);
    }

    [Fact]
    public void Throwing_Body_Does_Not_Copy_Out_The_Shadow()
    {
        var src = StructFixtures + """
            public static class Demo
            {
                public static string Go()
                {
                    var r = new Runner();
                    var state = new State { A = 5, B = new Sub(), C = "" };
                    try
                    {
                        r.RunVoid(ref state, (ref State s, int m) =>
                        {
                            s.A = 999;
                            throw new System.InvalidOperationException();
                        }, 1);
                    }
                    catch (System.InvalidOperationException) { }
                    return state.A + "";
                }
            }
            """;

        var result = Run(src);
        Assert.True(result.Changed);
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == RefOutInSeverity.Error);
        AssertOutputCompiles(result.OutputCode);

        var actual = Invoke(result.OutputCode, "DemoNs.Demo", "Go", true);
        // Exception path: no copy-out, outer state keeps its pre-call value.
        Assert.Equal("5", actual);
    }

    // The same CS1612 family on ordinary method declarations: an internal
    // method whose ref parameter was rewritten to ByRef<T> must lower its body
    // to the same shadow frame (SegmentStringBuilder.Add pattern: a mutating
    // method on a ByRef<T>.Value copy would otherwise lose _count updates).
    [Fact]
    public void Method_Body_Struct_Holder_Uses_Shadow_Frame()
    {
        var src = StructFixtures + """
            public class Acc
            {
                public void Accum(ref State s, int m)
                {
                    s.A += m;
                    s.B.Bump(m);
                }
                public int More(ref State s, int m)
                {
                    if (m == 0)
                    {
                        s.A = 5;
                        return s.A;
                    }
                    s.A += m;
                    s.B.Bump(m);
                    return s.A;
                }
            }
            public static class Demo
            {
                public static string Go()
                {
                    var a = new Acc();
                    var state = new State
                    {
                        A = 0,
                        B = new Sub { Z = 0, Hist = new int[4] },
                        C = ""
                    };
                    a.Accum(ref state, 3);
                    int r = a.More(ref state, 1);
                    return state.A + "|" + state.B.Z + "|" + r;
                }
            }
            """;

        var result = Run(src);
        Assert.True(result.Changed);
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == RefOutInSeverity.Error);
        Assert.Contains("DemoNs.State ", result.OutputCode);
        Assert.Contains("__shadow", result.OutputCode);
        Assert.Contains("= s.Value;", result.OutputCode);
        Assert.Contains("s.Value = __shadow", result.OutputCode);
        // Value return materialized before copy-out on both return sites.
        Assert.Contains("int __ret", result.OutputCode);
        AssertNoBareRefHolderValue(result.OutputCode);
        AssertOutputCompiles(result.OutputCode);

        var expected = Invoke(src, "DemoNs.Demo", "Go", false);
        var actual = Invoke(result.OutputCode, "DemoNs.Demo", "Go", true);
        Assert.Equal(expected, actual);
        Assert.Equal("4|4|4", actual);
    }

    [Fact]
    public void Expression_Bodied_Method_Struct_Holder_Forces_Block_Shadow_Frame()
    {
        var src = StructFixtures + """
            public class Acc2
            {
                public int Tick(ref State s) => ++s.A;
            }
            public static class Demo
            {
                public static string Go()
                {
                    var a = new Acc2();
                    var state = new State { A = 10, B = new Sub(), C = "" };
                    int r = a.Tick(ref state);
                    return state.A + "|" + r;
                }
            }
            """;

        var result = Run(src);
        Assert.True(result.Changed);
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == RefOutInSeverity.Error);
        Assert.Contains("__shadow", result.OutputCode);
        Assert.Contains("int __ret", result.OutputCode);
        Assert.Contains("s.Value = __shadow", result.OutputCode);
        AssertNoBareRefHolderValue(result.OutputCode);
        AssertOutputCompiles(result.OutputCode);

        var expected = Invoke(src, "DemoNs.Demo", "Go", false);
        var actual = Invoke(result.OutputCode, "DemoNs.Demo", "Go", true);
        Assert.Equal(expected, actual);
        Assert.Equal("11|11", actual);
    }

    [Fact]
    public void Local_Function_With_Ref_Struct_Param_Uses_Shadow_Frame()
    {
        var src = StructFixtures + """
            public static class Demo
            {
                public static string Go()
                {
                    var state = new State
                    {
                        A = 0,
                        B = new Sub { Z = 0, Hist = new int[4] },
                        C = ""
                    };
                    int Work(ref State s, int m)
                    {
                        s.A += m;
                        s.B.Bump(m);
                        return s.A;
                    }
                    int r = Work(ref state, 4);
                    return state.A + "|" + state.B.Z + "|" + r;
                }
            }
            """;

        var result = Run(src);
        Assert.True(result.Changed);
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == RefOutInSeverity.Error);
        Assert.Contains("__shadow", result.OutputCode);
        Assert.Contains("s.Value = __shadow", result.OutputCode);
        Assert.Contains("int __ret", result.OutputCode);
        AssertNoBareRefHolderValue(result.OutputCode);
        AssertOutputCompiles(result.OutputCode);

        var expected = Invoke(src, "DemoNs.Demo", "Go", false);
        var actual = Invoke(result.OutputCode, "DemoNs.Demo", "Go", true);
        Assert.Equal(expected, actual);
        Assert.Equal("4|4|4", actual);
    }
}

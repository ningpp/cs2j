using System.Reflection;
using System.Runtime.Loader;
using CSharpToJava.Core.EqualityConcat;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace CSharpToJava.Tests;

public class EqualityConcatPreprocessorUnitTests
{
    private static readonly MetadataReference[] References =
    {
        MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(Console).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(System.Linq.Expressions.Expression).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(System.Delegate).Assembly.Location),
        MetadataReference.CreateFromFile(
            typeof(Microsoft.CSharp.RuntimeBinder.RuntimeBinderException).Assembly.Location),
    };

    private static EqualityConcatResult Preprocess(
        string source,
        EqualityConcatOptions? options = null)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var compilation = CSharpCompilation.Create("ec-test",
            new[] { tree },
            References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                allowUnsafe: true,
                nullableContextOptions: NullableContextOptions.Enable));
        var model = compilation.GetSemanticModel(tree);
        return new EqualityConcatPreprocessor().Preprocess(source, model, options);
    }

    // ---------- EC-1 string equality ----------

    [Fact]
    public void EC1_String_Equals_Rewrites_To_Static_StringEquals()
    {
        var src = """
            class C { bool M(string a, string b) => a == b; }
            """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.Contains("string.Equals(a, b)", result.OutputCode);
        Assert.DoesNotContain("==", result.OutputCode);
        Assert.Equal(1, result.Statistics.StringEqualsRewritten);
    }

    [Fact]
    public void EC1_String_NotEquals_Rewrites_To_Negated_StringEquals()
    {
        var src = """
            class C { bool M(string a, string b) => a != b; }
            """;
        var result = Preprocess(src);
        Assert.Contains("!string.Equals(a, b)", result.OutputCode);
        Assert.DoesNotContain("!=", result.OutputCode);
    }

    [Fact]
    public void EC1_Literal_And_RuntimeStrings_Are_Not_Exempt()
    {
        // Even compile-time constants and interned literals are rewritten:
        // no interning侥幸 — semantics must be explicit.
        var src = """
            class C {
                bool M(string s) {
                    var runtime = new string("a".ToCharArray());
                    return "a" == "b" || s == "x" || runtime == s;
                }
            }
            """;
        var result = Preprocess(src);
        Assert.Equal(3, result.Statistics.StringEqualsRewritten);
        Assert.DoesNotContain("==", result.OutputCode);
    }

    // ---------- EC-2 object identity ----------

    [Fact]
    public void EC2_ObjectCast_Escapes_To_ReferenceEquals()
    {
        var src = """
            class C { bool M(string a, string b) => (object)a == b; }
            """;
        var result = Preprocess(src);
        Assert.Contains("object.ReferenceEquals(a, b)", result.OutputCode);
        Assert.Equal(1, result.Statistics.ObjectReferenceRewritten);
    }

    [Fact]
    public void EC2_ObjectVariable_Compared_With_String_Is_Identity()
    {
        var src = """
            class C { bool M(object o) => o == "x"; }
            """;
        var result = Preprocess(src);
        Assert.Contains("object.ReferenceEquals(o, \"x\")", result.OutputCode);
    }

    [Fact]
    public void EC2_Object_NotEquals_Wraps_Negation()
    {
        var src = """
            class C { bool M(object a, object b) => a != b; }
            """;
        var result = Preprocess(src);
        Assert.Contains("!object.ReferenceEquals(a, b)", result.OutputCode);
    }

    [Fact]
    public void EC2_MulticastDelegate_StaticType_Binds_Object_Identity()
    {
        // M0 probe: md == md binds object.op_Equality, not delegate equality.
        var src = """
            class C { bool M(System.MulticastDelegate md) => md == md; }
            """;
        var result = Preprocess(src);
        Assert.Equal(1, result.Statistics.ObjectReferenceRewritten);
        Assert.Equal(0, result.Statistics.DelegateEqualsRewritten);
        Assert.Contains("object.ReferenceEquals", result.OutputCode);
    }

    // ---------- EC-3 string concat chains ----------

    [Fact]
    public void EC3_LeftChain_Folds_Into_Single_Concat()
    {
        var src = """
            class C { string M(string a, string b, string c) => a + b + c; }
            """;
        var result = Preprocess(src);
        Assert.Contains("string.Concat(a, b, c)", result.OutputCode);
        Assert.Equal(1, result.Statistics.ConcatChainsFolded);
        Assert.Equal(3, result.Statistics.ConcatOperandsTotal);
    }

    [Fact]
    public void EC3_Operands_With_Calls_Appear_Exactly_Once()
    {
        var src = """
            class C {
                string Get() => "g";
                string M(string a) => Get() + a + Get();
            }
            """;
        var result = Preprocess(src);
        // The method declaration "string Get()" itself contains the token;
        // exclude it so we assert on the folded call sites only.
        var bodyOnly = result.OutputCode.Replace("string Get()", "", StringComparison.Ordinal);
        Assert.Equal(2, CountOccurrences(bodyOnly, "Get()"));
        Assert.Contains("string.Concat(Get(), a, Get())", result.OutputCode);
    }

    [Fact]
    public void EC3_RightAssociative_Chain_Stays_Nested()
    {
        var src = """
            class C { string M(string a, string b, string c) => a + (b + c); }
            """;
        var result = Preprocess(src);
        // The right-associative inner (b + c) is an independent StringConcat
        // chain (its parent is a parenthesized expression, not a + binary),
        // so it is folded to its own string.Concat call. The outer a + (…)
        // chain then folds into string.Concat(a, (string.Concat(b, c))).
        Assert.Contains("string.Concat(a, (string.Concat(b, c)))", result.OutputCode);
    }

    [Fact]
    public void EC3_Mixed_Object_Operand_Passes_Through()
    {
        var src = """
            class C { string M(string a, object o) => a + o + "!"; }
            """;
        var result = Preprocess(src);
        Assert.Contains("string.Concat(a, o, \"!\")", result.OutputCode);
    }

    // ---------- EC-4 compound concat ----------

    [Fact]
    public void EC4_SimpleTarget_Becomes_Concat_Assignment()
    {
        var src = """
            class C { void M(string s, string x) { s += x; } }
            """;
        var result = Preprocess(src);
        Assert.Contains("s = string.Concat(s, x)", result.OutputCode);
        Assert.Equal(1, result.Statistics.ConcatCompoundRewritten);
        Assert.Equal(0, result.Statistics.TemporariesLifted);
    }

    [Fact]
    public void EC4_Stable_Property_Target_Needs_No_Temp()
    {
        var src = """
            class C {
                string P { get; set; }
                void M(string x) { P += x; }
            }
            """;
        var result = Preprocess(src);
        Assert.Contains("P = string.Concat(P, x)", result.OutputCode);
        Assert.Equal(0, result.Statistics.TemporariesLifted);
    }

    [Fact]
    public void EC4_Unstable_Property_Receiver_Lifts_One_Temp()
    {
        var src = """
            class C {
                C Next() => new C();
                string P { get; set; }
                void M(string x) { Next().P += x; }
            }
            """;
        var result = Preprocess(src);
        Assert.Equal(1, result.Statistics.TemporariesLifted);
        Assert.DoesNotContain("Next().P", result.OutputCode);
        Assert.Contains("__cs2j_ec1 = Next()", result.OutputCode);
    }

    [Fact]
    public void EC4_Unstable_Indexer_Receiver_And_Index_Lift_Two_Temps()
    {
        var src = """
            class C {
                string this[int i] { get => ""; set { } }
                C Rcv() => new C();
                int Idx() => 0;
                void M(string x) { Rcv()[Idx()] += x; }
            }
            """;
        var result = Preprocess(src);
        Assert.Equal(2, result.Statistics.TemporariesLifted);
        Assert.Contains("__cs2j_ec1 = Rcv()", result.OutputCode);
        Assert.Contains("__cs2j_ec2 = Idx()", result.OutputCode);
        Assert.Contains("__cs2j_ec1[__cs2j_ec2] = string.Concat(", result.OutputCode);
    }

    [Fact]
    public void EC4_Property_Evaluation_Order_Is_Preserved()
        => AssertOrder(
            """
            using System.Collections.Generic;
            public static class Log { public static List<string> Events = new List<string>(); }
            public class Target {
                public string P {
                    get { Log.Events.Add("get"); return "p"; }
                    set { Log.Events.Add("set"); }
                }
                public static Target Rcv() { Log.Events.Add("rcv"); return new Target(); }
                public static string Val() { Log.Events.Add("val"); return "v"; }
                public static List<string> Run() {
                    Log.Events = new List<string>();
                    Rcv().P += Val();
                    return Log.Events;
                }
            }
            """,
            new[] { "rcv", "get", "val", "set" });

    [Fact]
    public void EC4_Indexer_Evaluation_Order_Is_Preserved()
        => AssertOrder(
            """
            using System.Collections.Generic;
            public static class Log { public static List<string> Events = new List<string>(); }
            public class Bucket {
                public string this[int i] {
                    get { Log.Events.Add("get"); return "x"; }
                    set { Log.Events.Add("set"); }
                }
                public static Bucket Rcv() { Log.Events.Add("rcv"); return new Bucket(); }
                public static int Idx() { Log.Events.Add("idx"); return 0; }
                public static string Val() { Log.Events.Add("val"); return "v"; }
                public static List<string> Run() {
                    Log.Events = new List<string>();
                    Rcv()[Idx()] += Val();
                    return Log.Events;
                }
            }
            """,
            new[] { "rcv", "idx", "get", "val", "set" });

    private static void AssertOrder(string source, string[] expected)
    {
        var result = Preprocess(source);
        var rewrittenTree = CSharpSyntaxTree.ParseText(result.OutputCode);
        // Unique assembly name: AssemblyLoadContext may dedupe same-name
        // streams within one test run.
        var compilation = CSharpCompilation.Create(
            "ec-run-" + Guid.NewGuid().ToString("N"),
            new[] { rewrittenTree },
            References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        using var ms = new MemoryStream();
        var emit = compilation.Emit(ms);
        Assert.True(emit.Success,
            string.Join("\n", emit.Diagnostics.Select(d => d.ToString())));

        ms.Seek(0, SeekOrigin.Begin);
        var assembly = AssemblyLoadContext.Default.LoadFromStream(ms);
        var type = assembly.GetType("Target") ?? assembly.GetType("Bucket");
        var actual = (IEnumerable<string>)type!.GetMethod("Run")!
            .Invoke(null, null)!;

        Assert.Equal(expected, actual);
    }

    // ---------- EC-6 delegate equality ----------

    [Fact]
    public void EC6_Action_Equals_Rewrites_To_DelegateOps()
    {
        var src = """
            class C { bool M(System.Action a, System.Action b) => a == b; }
            """;
        var result = Preprocess(src);
        Assert.Contains("Cs2jPrelude.DelegateOps.Equals(a, b)", result.OutputCode);
        Assert.Equal(1, result.Statistics.DelegateEqualsRewritten);
    }

    [Fact]
    public void EC6_Delegate_NotEquals_And_Null()
    {
        var src = """
            class C {
                bool M(System.Action dg) => dg != null && dg == dg;
            }
            """;
        var result = Preprocess(src);
        Assert.Contains("!Cs2jPrelude.DelegateOps.Equals(dg, null)", result.OutputCode);
        Assert.Contains("Cs2jPrelude.DelegateOps.Equals(dg, dg)", result.OutputCode);
    }

    [Fact]
    public void EC6_Event_Null_Check_Is_Not_Delegate_Rewrite()
    {
        var src = """
            class C {
                event System.Action E;
                bool M() => E == null;
            }
            """;
        var result = Preprocess(src);
        Assert.Equal(0, result.Statistics.DelegateEqualsRewritten);
        Assert.DoesNotContain("DelegateOps", result.OutputCode);
    }

    [Fact]
    public void EC6_Disabled_Emits_0473_And_Does_Not_Rewrite()
    {
        var src = """
            class C { bool M(System.Action a, System.Action b) => a == b; }
            """;
        var result = Preprocess(src,
            new EqualityConcatOptions { EnableDelegateRule = false });
        Assert.False(result.Changed);
        Assert.Contains(result.Diagnostics,
            d => d.Code == EqualityConcatDiagnosticCode.CS2J0473
                 && d.Severity == EqualityConcatSeverity.Warning);
    }

    // ---------- EC-7 record value equality ----------

    [Fact]
    public void EC7_RecordClass_Equals_Rewrites_To_ObjectEquals()
    {
        var src = """
            class C {
                record Rc(string Name);
                bool M(Rc a, Rc b) => a == b;
            }
            """;
        var result = Preprocess(src);
        Assert.Contains("object.Equals(a, b)", result.OutputCode);
        Assert.Equal(1, result.Statistics.RecordEqualsRewritten);
    }

    [Fact]
    public void EC7_RecordStruct_NotEquals_Rewrites_To_Negated_ObjectEquals()
    {
        var src = """
            class C {
                record struct Rs(int X);
                bool M(Rs a, Rs b) => a != b;
            }
            """;
        var result = Preprocess(src);
        Assert.Contains("!object.Equals(a, b)", result.OutputCode);
    }

    [Fact]
    public void EC7_HandWritten_Operator_Is_Untouched()
    {
        var src = """
            struct SPoint {
                public int X;
                public static bool operator ==(SPoint a, SPoint b) => true;
                public static bool operator !=(SPoint a, SPoint b) => false;
                public override bool Equals(object? o) => true;
                public override int GetHashCode() => 0;
            }
            class C { bool M(SPoint a, SPoint b) => a == b; }
            """;
        var result = Preprocess(src);
        Assert.False(result.Changed);
        Assert.Equal(0, result.Statistics.RecordEqualsRewritten);
        Assert.Contains("a == b", result.OutputCode);
    }

    // ---------- EC-8 lifted equality ----------

    [Fact]
    public void EC8_Lifted_Bool_Equality_Is_Warning_Even_In_Strict_Mode()
    {
        // CS2J0474 is always a Warning — lifted nullable ==/!= is valid C#
        // owned by the C22 nullable pass; escalating to Error would abort
        // real-world SDK conversions (e.g. System.Uri).
        var src = """
            class C { bool M(bool? x) => x == true; }
            """;
        var result = Preprocess(src);
        Assert.False(result.Changed);
        Assert.Equal(1, result.Statistics.LiftedEqualityPendingC22);
        Assert.Contains(result.Diagnostics,
            d => d.Code == EqualityConcatDiagnosticCode.CS2J0474
                 && d.Severity == EqualityConcatSeverity.Warning);
    }

    [Fact]
    public void EC8_Lifted_Int_NotEquals_Warns_When_NonStrict()
    {
        var src = """
            class C { bool M(int? i) => i != i; }
            """;
        var result = Preprocess(src, new EqualityConcatOptions { Strict = false });
        Assert.Contains(result.Diagnostics,
            d => d.Code == EqualityConcatDiagnosticCode.CS2J0474
                 && d.Severity == EqualityConcatSeverity.Warning);
    }

    // ---------- EC-9 tuple residual ----------

    [Fact]
    public void EC9_Tuple_Equality_Warns_0476()
    {
        var src = """
            class C { bool M((int A, string B) t) => t == t; }
            """;
        var result = Preprocess(src);
        Assert.Contains(result.Diagnostics,
            d => d.Code == EqualityConcatDiagnosticCode.CS2J0476
                 && d.Severity == EqualityConcatSeverity.Warning);
        Assert.Equal(1, result.Statistics.TupleNullEdgeSites);
    }

    [Fact]
    public void EC9_NullableTuple_Compared_With_Null_Warns_Not_ReferenceEquals()
    {
        var src = """
            class C { bool M((int, string)? nt) => nt == null; }
            """;
        var result = Preprocess(src);
        Assert.DoesNotContain("ReferenceEquals", result.OutputCode);
        Assert.Contains(result.Diagnostics,
            d => d.Code == EqualityConcatDiagnosticCode.CS2J0476);
    }

    // ---------- expression trees, dynamic, primitives ----------

    [Fact]
    public void ExpressionTree_Site_Is_Skipped_With_0472_Info()
    {
        var src = """
            using System;
            using System.Linq.Expressions;
            class C {
                Expression<Func<string, string, bool>> M() => (a, b) => a == b;
            }
            """;
        var result = Preprocess(src);
        Assert.Contains(result.Diagnostics,
            d => d.Code == EqualityConcatDiagnosticCode.CS2J0472
                 && d.Severity == EqualityConcatSeverity.Info);
        Assert.Equal(1, result.Statistics.ExpressionTreeSitesSkipped);
        Assert.Contains("a == b", result.OutputCode);
    }

    [Fact]
    public void Dynamic_Equality_Is_0471_Error()
    {
        var src = """
            class C { bool M(dynamic z) => z == "a"; }
            """;
        var result = Preprocess(src);
        Assert.Contains(result.Diagnostics,
            d => d.Code == EqualityConcatDiagnosticCode.CS2J0471
                 && d.Severity == EqualityConcatSeverity.Error);
    }

    [Fact]
    public void Primitive_Equality_Is_Untouched()
    {
        var src = """
            class C { bool M(int a, int b) => a == b; }
            """;
        var result = Preprocess(src);
        Assert.False(result.Changed);
    }

    [Fact]
    public void Explicit_ReferenceEquals_Call_Stays_Put()
    {
        var src = """
            class C {
                bool M(object a, object b) => object.ReferenceEquals(a, b);
            }
            """;
        var result = Preprocess(src);
        Assert.False(result.Changed);
    }

    // ---------- idempotency / clean preservation ----------

    [Fact]
    public void Rewritten_Output_Is_Idempotent()
    {
        var src = """
            class C {
                bool Eq(string a, string b, object o, System.Action d1, System.Action d2)
                    => a == b || (object)a == o || d1 == d2;
                string Cat(string a, string b, string c) => a + b + c;
                void Add(string s, string x) { s += x; }
            }
            """;
        var first = Preprocess(src);
        Assert.True(first.Changed);
        var second = Preprocess(first.OutputCode);
        Assert.False(second.Changed);
        Assert.Equal(first.OutputCode, second.OutputCode);
    }

    [Fact]
    public void Clean_File_Is_Byte_Level_Preserved_With_Bom()
    {
        var src = "\ufeffclass C { void M() { System.Console.WriteLine(\"hi\"); } }";
        var result = Preprocess(src);
        Assert.False(result.Changed);
        Assert.Equal(src, result.OutputCode);
    }

    [Fact]
    public void Statistics_Snapshot_For_Mixed_File()
    {
        var src = """
            class C {
                record R(string S);
                bool M(string a, string b, object o, R r1, R r2, System.Action d) {
                    var x = a + b + "!";
                    a += x;
                    return a == b || (object)a == o || r1 == r2 || d == null;
                }
            }
            """;
        var result = Preprocess(src);
        var s = result.Statistics;
        Assert.Equal(1, s.StringEqualsRewritten);
        Assert.Equal(1, s.ObjectReferenceRewritten);
        Assert.Equal(1, s.RecordEqualsRewritten);
        Assert.Equal(1, s.DelegateEqualsRewritten);
        Assert.Equal(1, s.ConcatChainsFolded);
        Assert.Equal(1, s.ConcatCompoundRewritten);
        Assert.Equal(0, s.ResidualCount);
    }

    [Fact]
    public void EC3_Add_With_Failed_Operand_Binding_Uses_Type_Fallback_Not_Unknown()
    {
        // TPA 手工引用集可能让重载解析失败（IBinaryOperation.OperatorMethod 与
        // GetSymbolInfo 双双为 null）。Add 站点必须按操作数类型兜底：
        // string 侧 -> 拼接改写；纯算术 -> 预定义透传；均不得发 CS2J0470。
        var src = """
            class C {
                string ConcatSite(Unknown log) => "order:" + log;
                int ArithmeticSite(Unknown x) => 1 + x;
            }
            """;
        var result = Preprocess(src);
        Assert.DoesNotContain(result.Diagnostics,
            d => d.Code == EqualityConcatDiagnosticCode.CS2J0470);
        Assert.Contains("string.Concat(\"order:\", log)", result.OutputCode);
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }
        return count;
    }
}

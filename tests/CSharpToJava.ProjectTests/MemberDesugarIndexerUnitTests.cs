using CSharpToJava.Core.MemberDesugar;
using Microsoft.CodeAnalysis;

namespace CSharpToJava.ProjectTests;

/// <summary>
/// C12 indexer 去糖 Core 单测矩阵（设计 §7.1）：内联源码 → RewriteFile →
/// 忽略空白断言 + AssertOutputCompiles。复用 MemberDesugarPropertyUnitTests 的
/// internal Rewrite/AssertOutputCompiles 帮助方法（同程序集可见）。
/// </summary>
public class MemberDesugarIndexerUnitTests
{
    internal const string SimpleIndexer = """
        class C
        {
            private readonly int[] _a = new int[8];
            public int this[int i] { get => _a[i]; set => _a[i] = value; }
            public int Read(int i) => this[i];
        }
        """;

    private static string Normalize(string code)
        => new string(code.Where(ch => !char.IsWhiteSpace(ch)).ToArray());

    internal static void AssertContains(string output, string expected)
        => Assert.True(Normalize(output).Contains(Normalize(expected).Trim(), StringComparison.Ordinal),
            $"Expected output to contain:\n{expected}\n--- actual ---\n{output}");

    internal static void AssertNotContains(string output, string unexpected)
        => Assert.False(Normalize(output).Contains(Normalize(unexpected).Trim(), StringComparison.Ordinal),
            $"Expected output NOT to contain:\n{unexpected}\n--- actual ---\n{output}");

    private static IEnumerable<string> Codes(List<MemberDesugarDiagnostic> diags)
        => diags.Select(d => $"CS2J{(int)d.Code}");

    private static string CS2J(int code) => $"CS2J{code}";

    // ---- 声明级闸门（分析面，plan 原子跳过 + 必留诊断）----

    [Fact]
    public void PartialIndexer_GatedWith5138()
    {
        var (outCode, _, diags) = MemberDesugarPropertyUnitTests.Rewrite("""
            partial class C
            {
                public partial int this[int i] { get; set; }
            }
            """);
        Assert.Contains(CS2J(5138), Codes(diags));
        Assert.Contains("this[int i]", outCode);
    }

    [Fact]
    public void ExternIndexer_GatedWith5139()
    {
        var (outCode, _, diags) = MemberDesugarPropertyUnitTests.Rewrite("""
            class C
            {
                public extern int this[int i] { get; }
            }
            """);
        Assert.Contains(CS2J(5139), Codes(diags));
        Assert.Contains("this[int i]", outCode);
    }

    [Fact]
    public void ExpressionTreeSite_GatedWith5135_AndForceLifts()
    {
        var src = """
            class C
            {
                public int this[int i] => i;
            }
            class App
            {
                System.Linq.Expressions.Expression<System.Func<int, int>> F(C c)
                    => x => c[x];
            }
            """;
        var (outCode, _, diags) = MemberDesugarPropertyUnitTests.Rewrite(src);
        Assert.Contains(CS2J(5135), Codes(diags));
        Assert.Contains("this[int i]", outCode);

        var forced = new MemberDesugarOptions
        {
            ForceQualifiedNames = new HashSet<string> { "C::Item" },
        };
        var (_, _, diags2) = MemberDesugarPropertyUnitTests.Rewrite(src, forced);
        Assert.DoesNotContain(CS2J(5135), Codes(diags2));
    }

    [Fact]
    public void SkipOverrides_ExcludesPlan()
    {
        var options = new MemberDesugarOptions
        {
            SkipQualifiedNames = new HashSet<string> { "C" },
        };
        var (outCode, _, diags) = MemberDesugarPropertyUnitTests.Rewrite(SimpleIndexer, options);
        Assert.Contains("this[int i]", outCode);
        Assert.Contains(CS2J(5110), Codes(diags)); // 族级 PlanCreated Info（UserSkipped 复用）
    }

    // ---- 声明归约（IX-1..IX-5）----

    [Fact]
    public void GetSetIndexer_ReducedToBothMethods()
    {
        var (outCode, stats, _) = MemberDesugarPropertyUnitTests.Rewrite("""
            class C
            {
                private readonly int[] _a = new int[8];
                public int this[int i] { get => _a[i]; set => _a[i] = value; }
                public int Read(int i) => this[i];
            }
            """);
        AssertContains(outCode, "public int get_Item(int i) => _a[i];");
        AssertContains(outCode, "public void set_Item(int i, int value) => _a[i] = value;");
        AssertContains(outCode, "this.get_Item(i)");
        AssertNotContains(outCode, "this[int i]");
        Assert.Equal(1, stats.IndexersPlanned);
        Assert.True(stats.IndexerReadSitesRewritten >= 1);
        MemberDesugarPropertyUnitTests.AssertOutputCompiles(outCode);
    }

    [Fact]
    public void MultiParam_BlockAccessorBodies()
    {
        var (outCode, _, _) = MemberDesugarPropertyUnitTests.Rewrite("""
            class M
            {
                private readonly int[,] _g = new int[4, 4];
                public int this[int r, int c]
                {
                    get { return _g[r, c]; }
                    set { _g[r, c] = value; }
                }
                public int Corner => this[0, 0];
            }
            """);
        AssertContains(outCode, "public int get_Item(int r, int c)");
        AssertContains(outCode, "public void set_Item(int r, int c, int value)");
        AssertContains(outCode, "return _g[r, c];");
        AssertContains(outCode, "_g[r, c] = value;");
        AssertContains(outCode, "this.get_Item(0, 0)");
        MemberDesugarPropertyUnitTests.AssertOutputCompiles(outCode);
    }

    [Fact]
    public void AbstractVirtualOverrideChain_ModifiersCarry()
    {
        var (outCode, _, _) = MemberDesugarPropertyUnitTests.Rewrite("""
            abstract class A
            {
                public abstract int this[int i] { get; set; }
            }
            class B : A
            {
                public override int this[int i] { get => i; set { } }
            }
            sealed class C2 : B
            {
                public override sealed int this[int i] { get => base[i]; set { } }
            }
            """);
        AssertContains(outCode, "public abstract int get_Item(int i);");
        AssertContains(outCode, "public override int get_Item(int i)");
        AssertContains(outCode, "public override sealed int get_Item(int i)");
        AssertContains(outCode, "base.get_Item(i)");
        MemberDesugarPropertyUnitTests.AssertOutputCompiles(outCode);
    }

    [Fact]
    public void InterfaceGetOnly_NoSetterGenerated_DimKeptBody()
    {
        var (outCode, _, _) = MemberDesugarPropertyUnitTests.Rewrite("""
            interface IFoo
            {
                int this[int i] { get; }
            }
            interface IDim
            {
                int this[string k] => k.Length;
            }
            """);
        AssertContains(outCode, "int get_Item(int i);");
        AssertNotContains(outCode, "set_Item");
        AssertContains(outCode, "int get_Item(string k) => k.Length;");
        MemberDesugarPropertyUnitTests.AssertOutputCompiles(outCode);
    }

    [Fact]
    public void AccessorAccessibility_PrivateSet()
    {
        var (outCode, _, _) = MemberDesugarPropertyUnitTests.Rewrite("""
            class C
            {
                public int this[int i] { get => i; private set { } }
                public void M(int i) { this[i] = i; }
            }
            """);
        AssertContains(outCode, "public int get_Item(int i) => i;");
        AssertContains(outCode, "private void set_Item(int i, int value)");
        MemberDesugarPropertyUnitTests.AssertOutputCompiles(outCode);
    }

    [Fact]
    public void InitAccessor_WidenedWith5132()
    {
        var (outCode, _, diags) = MemberDesugarPropertyUnitTests.Rewrite("""
            class C
            {
                public int this[int i] { get => i; init { } }
            }
            """);
        AssertContains(outCode, "public void set_Item(int i, int value)");
        Assert.Contains(CS2J(5132), Codes(diags));
        MemberDesugarPropertyUnitTests.AssertOutputCompiles(outCode);
    }

    [Fact]
    public void ReadonlyStructIndexer_ModifierDropped()
    {
        var (outCode, _, _) = MemberDesugarPropertyUnitTests.Rewrite("""
            struct S
            {
                private int _x;
                public readonly int this[int i] => _x;
            }
            """);
        AssertContains(outCode, "public int get_Item(int i) => _x;");
        AssertNotContains(outCode, "readonly");
        MemberDesugarPropertyUnitTests.AssertOutputCompiles(outCode);
    }

    [Fact]
    public void OverloadPair_And_DefaultParameterValue()
    {
        var (outCode, _, _) = MemberDesugarPropertyUnitTests.Rewrite("""
            class C
            {
                public int this[int i] => i;
                public string this[string s] => s;
                public int this[int i, int j = 5] => i + j;
                public int Sum() => this[1] + this[2, 3];
            }
            """);
        AssertContains(outCode, "public int get_Item(int i) => i;");
        AssertContains(outCode, "public string get_Item(string s) => s;");
        AssertContains(outCode, "public int get_Item(int i, int j = 5) => i + j;");
        MemberDesugarPropertyUnitTests.AssertOutputCompiles(outCode);
    }

    // ---- 读站点（IX-6/IX-9/IX-11）----

    [Fact]
    public void ReadSites_Rewritten_IncludingThisAndBase()
    {
        var (outCode, stats, _) = MemberDesugarPropertyUnitTests.Rewrite("""
            class B
            {
                public virtual int this[int i] => i;
                public int Wrap(int i) => this[i] * 2;
            }
            class D : B
            {
                public override int this[int i] => base[i] + 1;
                public int CallBase(int i) => base[i];
            }
            """);
        AssertContains(outCode, "this.get_Item(i)");
        AssertContains(outCode, "base.get_Item(i)");
        Assert.True(stats.IndexerReadSitesRewritten >= 2);
        MemberDesugarPropertyUnitTests.AssertOutputCompiles(outCode);
    }

    [Fact]
    public void NullConditional_ReadRewritten()
    {
        var (outCode, _, _) = MemberDesugarPropertyUnitTests.Rewrite("""
            class C
            {
                public int this[int i] => i;
                public int? M(C c) => c?[0];
            }
            """);
        AssertContains(outCode, "c?.get_Item(0)");
        MemberDesugarPropertyUnitTests.AssertOutputCompiles(outCode);
    }

    [Fact]
    public void IndexAndRangeArguments_PreservedVerbatim()
    {
        var (outCode, _, _) = MemberDesugarPropertyUnitTests.Rewrite("""
            class C
            {
                public string this[object k] => k.ToString();
                public string Last(int a) => this[^1] + this[a..^1];
            }
            """);
        AssertContains(outCode, "this.get_Item(^1)");
        AssertContains(outCode, "this.get_Item(a..^1)");
        MemberDesugarPropertyUnitTests.AssertOutputCompiles(outCode);
    }

    [Fact]
    public void ExternalIndexerAccesses_UntouchedAndCounted()
    {
        var (outCode, stats, _) = MemberDesugarPropertyUnitTests.Rewrite("""
            using System.Collections.Generic;
            class C
            {
                public int this[int i] => i;
                public int M(List<int> list, Dictionary<int, string> map, string s, int[] arr)
                    => list[0] + int.Parse(map[1]) + s[2] + arr[3] + this[4];
            }
            """);
        AssertContains(outCode, "list[0]");
        AssertContains(outCode, "map[1]");
        AssertContains(outCode, "s[2]");
        AssertContains(outCode, "arr[3]");
        AssertContains(outCode, "this.get_Item(4)");
        Assert.True(stats.ExternalIndexerSites >= 3);
        MemberDesugarPropertyUnitTests.AssertOutputCompiles(outCode);
    }

    [Fact]
    public void MultiDimAndJaggedArrays_Untouched_BesideMultiParamIndexer()
    {
        var (outCode, _, _) = MemberDesugarPropertyUnitTests.Rewrite("""
            class C
            {
                public int this[int r, int c] => r + c;
                public int Sum(int[,] m, int[][] j)
                    => m[0, 1] + j[1][0] + this[2, 3];
            }
            """);
        AssertContains(outCode, "m[0, 1]");
        AssertContains(outCode, "j[1][0]");
        AssertContains(outCode, "get_Item(2, 3)");
        MemberDesugarPropertyUnitTests.AssertOutputCompiles(outCode);
    }

    // ---- 写站点（IX-7）----

    [Fact]
    public void SimpleWriteStatement_RewrittenToSetter()
    {
        var (outCode, stats, _) = MemberDesugarPropertyUnitTests.Rewrite("""
            class C
            {
                private readonly int[] _a = new int[8];
                public int this[int i] { get => _a[i]; set => _a[i] = value; }
                public void M(C c, int v) { c[3] = v; this[1] = 2; }
            }
            """);
        AssertContains(outCode, "c.set_Item(3, v)");
        AssertContains(outCode, "this.set_Item(1, 2)");
        Assert.True(stats.IndexerWriteSitesRewritten >= 2);
        MemberDesugarPropertyUnitTests.AssertOutputCompiles(outCode);
    }

    [Fact]
    public void RefReturnIndexer_VariablePositionsStayLegal()
    {
        var (outCode, _, _) = MemberDesugarPropertyUnitTests.Rewrite("""
            class Cells
            {
                private readonly int[] _d = new int[4];
                public ref int this[int i] => ref _d[i];
                public int M(int v)
                {
                    this[0] = v;
                    this[0] += 5;
                    this[1]++;
                    int r = this[2];
                    Take(ref _d[3]);
                    return r;
                }
                static void Take(ref int x) => x++;
            }
            """);
        AssertContains(outCode, "public ref int get_Item(int i) => ref _d[i]");
        AssertContains(outCode, "this.get_Item(0) = v");
        AssertContains(outCode, "this.get_Item(0) += 5");
        AssertContains(outCode, "this.get_Item(1)++");
        AssertContains(outCode, "int r = this.get_Item(2)");
        AssertContains(outCode, "Take(ref _d[3])");
        AssertNotContains(outCode, "set_Item");
        MemberDesugarPropertyUnitTests.AssertOutputCompiles(outCode);
    }

    [Fact]
    public void RefReturnIndexer_RefLocalAndRefOutArgs()
    {
        var (outCode, _, _) = MemberDesugarPropertyUnitTests.Rewrite("""
            class Cells
            {
                private readonly int[] _d = new int[4];
                public ref int this[int i] => ref _d[i];
                public int M()
                {
                    ref var x = ref this[1];
                    x = 7;
                    TakeOut(out this[2]);
                    return _d[1] + _d[2];
                }
                static void TakeOut(out int y) => y = 9;
            }
            """);
        AssertContains(outCode, "ref var x = ref this.get_Item(1)");
        AssertContains(outCode, "TakeOut(out this.get_Item(2))");
        MemberDesugarPropertyUnitTests.AssertOutputCompiles(outCode);
    }

    // ---- 复合/自增（IX-8 / D11 单次求值）----

    [Fact]
    public void Compound_SimpleArgs_ExpandedWithoutLift()
    {
        var (outCode, stats, _) = MemberDesugarPropertyUnitTests.Rewrite("""
            class C
            {
                private readonly int[] _a = new int[8];
                public int this[int i] { get => _a[i]; set => _a[i] = value; }
                public void M(int i, int v) { this[i] += v; this[i] -= 1; this[i] *= 2; }
            }
            """);
        AssertContains(outCode, "this.set_Item(i, this.get_Item(i) + v)");
        AssertContains(outCode, "this.set_Item(i, this.get_Item(i) - 1)");
        AssertContains(outCode, "this.set_Item(i, this.get_Item(i) * 2)");
        Assert.True(stats.IndexerCompoundSitesExpanded >= 3);
        MemberDesugarPropertyUnitTests.AssertOutputCompiles(outCode);
    }

    [Fact]
    public void Compound_SideEffectArgument_EvaluatedExactlyOnce()
    {
        var (outCode, _, _) = MemberDesugarPropertyUnitTests.Rewrite("""
            class C
            {
                private readonly int[] _a = new int[8];
                private int _seed;
                public int Next() => _seed++;
                public int this[int i] { get => _a[i]; set => _a[i] = value; }
                public int Bump(int v)
                {
                    this[Next()] += v;
                    return _seed;
                }
            }
            """);
        AssertContains(outCode, "var __cs2j_idx0 = Next()");
        AssertContains(outCode, "this.set_Item(__cs2j_idx0, this.get_Item(__cs2j_idx0) + v)");
        // Next() 在产物中恰好出现两次：方法声明本身 + 一次求值（单次求值硬断言）。
        Assert.Equal(2, CountOccurrences(outCode, "Next()"));
        MemberDesugarPropertyUnitTests.AssertOutputCompiles(outCode);
    }

    private static int CountOccurrences(string code, string token)
    {
        var count = 0;
        for (var idx = code.IndexOf(token, StringComparison.Ordinal);
             idx >= 0;
             idx = code.IndexOf(token, idx + token.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    [Fact]
    public void IncrementStatements_Expanded()
    {
        var (outCode, _, _) = MemberDesugarPropertyUnitTests.Rewrite("""
            class C
            {
                private readonly int[] _a = new int[8];
                public int this[int i] { get => _a[i]; set => _a[i] = value; }
                public void M(int i) { this[i]++; --this[i]; }
            }
            """);
        AssertContains(outCode, "this.set_Item(i, this.get_Item(i) + 1)");
        AssertContains(outCode, "this.set_Item(i, this.get_Item(i) - 1)");
        MemberDesugarPropertyUnitTests.AssertOutputCompiles(outCode);
    }

    [Fact]
    public void ConsumedCompoundInExpression_GatedWith5137()
    {
        var (outCode, _, diags) = MemberDesugarPropertyUnitTests.Rewrite("""
            class C
            {
                private readonly int[] _a = new int[8];
                public int this[int i] { get => _a[i]; set => _a[i] = value; }
                public int M(int i) => F(this[i] += 1);
                static int F(int x) => x;
            }
            """);
        Assert.Contains(CS2J(5137), Codes(diags));
        Assert.Contains("this[int i]", outCode); // plan 原子跳过
    }

    // ---- 集合初始化器（IX-10）----

    [Fact]
    public void IndexInitializer_StatementPosition_Lifted()
    {
        var (outCode, stats, _) = MemberDesugarPropertyUnitTests.Rewrite("""
            class C
            {
                public int this[int k] { get => k; set { } }
                public void M()
                {
                    var c = new C { [1] = 10, [2] = 20 };
                    c[3] = 30;
                }
            }
            """);
        AssertContains(outCode, "var __cs2j_init0 = new C()");
        AssertContains(outCode, "__cs2j_init0.set_Item(1, 10)");
        AssertContains(outCode, "__cs2j_init0.set_Item(2, 20)");
        AssertContains(outCode, "c.set_Item(3, 30)");
        Assert.True(stats.IndexerInitializerSitesRewritten >= 1);
        MemberDesugarPropertyUnitTests.AssertOutputCompiles(outCode);
    }

    [Fact]
    public void IndexInitializer_NonLiftableField_GatedWith5137()
    {
        var (outCode, _, diags) = MemberDesugarPropertyUnitTests.Rewrite("""
            class C
            {
                public int this[int k] { get => k; set { } }
            }
            class Host
            {
                private readonly C _c = new C { [1] = 10 };
            }
            """);
        Assert.Contains(CS2J(5137), Codes(diags));
        Assert.Contains("this[int k]", outCode);
    }

    // ---- 显式接口 / 特性 / 混合 / 幂等 ----

    [Fact]
    public void ExcludedPlan_IndexerDeclarationBecomesResidual()
    {
        // 同文件另一索引器仍在计划内 → 改写器运行；被排除的重载声明落入残差闸门。
        var src = """
            class C
            {
                public int this[int i] => i;
                public string this[string k] => k;
                public int M() => this[0];
            }
            """;
        var model = MemberDesugarPropertyUnitTests.ParseWithSemanticsPublic(src);
        var pp = new MemberDesugarPreprocessor();
        pp.BindCompilation(model.Compilation);
        var excluded = pp.Plans.Single(p =>
            p.Symbol is IPropertySymbol { IsIndexer: true } ip
            && ip.Parameters[0].Type.SpecialType == SpecialType.System_String);
        pp.ExcludePlans(new[] { excluded.Id });
        var stats = new MemberDesugarStatistics();
        var diags = new List<MemberDesugarDiagnostic>();
        var result = pp.RewriteFile(src, model, stats, diags);
        Assert.Contains(CS2J(5141), Codes(diags));
        Assert.Contains("this[string k]", result.OutputCode); // 排除 plan → 声明保持
        AssertContains(result.OutputCode, "this.get_Item(0)"); // 计划内站点照常改写
    }

    [Fact]
    public void ExplicitInterface_QualifiedMethodsGenerated()
    {
        var (outCode, stats, _) = MemberDesugarPropertyUnitTests.Rewrite("""
            interface IFoo
            {
                int this[int i] { get; set; }
            }
            class C : IFoo
            {
                int IFoo.this[int i] { get => i; set { } }
                public int ViaIface(IFoo f) => f[0];
            }
            """);
        AssertContains(outCode, "int IFoo.get_Item(int i) => i;");
        AssertContains(outCode, "void IFoo.set_Item(int i, int value)");
        AssertContains(outCode, "f.get_Item(0)");
        Assert.True(stats.ExplicitInterfaceIndexers >= 1);
        MemberDesugarPropertyUnitTests.AssertOutputCompiles(outCode);
    }

    [Fact]
    public void TwoExplicitInterfaces_NoCollision()
    {
        var (outCode, _, _) = MemberDesugarPropertyUnitTests.Rewrite("""
            interface IA { int this[int i] { get; } }
            interface IB { int this[int i] { get; } }
            class C : IA, IB
            {
                int IA.this[int i] => i;
                int IB.this[int i] => i + 1;
            }
            """);
        AssertContains(outCode, "int IA.get_Item(int i) => i;");
        AssertContains(outCode, "int IB.get_Item(int i) => i + 1;");
        MemberDesugarPropertyUnitTests.AssertOutputCompiles(outCode);
    }

    [Fact]
    public void IndexerNameAttribute_CustomNameUsed_AttributeRemoved()
    {
        var (outCode, _, diags) = MemberDesugarPropertyUnitTests.Rewrite("""
            using System.Runtime.CompilerServices;
            class C
            {
                [IndexerName("Slot")]
                public string this[int i] => i.ToString();
                public string Get(int i) => this[i];
            }
            """);
        AssertContains(outCode, "public string get_Slot(int i) => i.ToString();");
        AssertContains(outCode, "this.get_Slot(i)");
        AssertNotContains(outCode, "IndexerName");
        Assert.Contains(CS2J(5131), Codes(diags));
        MemberDesugarPropertyUnitTests.AssertOutputCompiles(outCode);
    }

    [Fact]
    public void DefaultMemberAttribute_RetainedWith5133()
    {
        var (outCode, _, diags) = MemberDesugarPropertyUnitTests.Rewrite("""
            using System.Reflection;
            [DefaultMember("Item")]
            class C
            {
                public int this[int i] => i;
            }
            """);
        AssertContains(outCode, "[DefaultMember(\"Item\")]");
        Assert.Contains(CS2J(5133), Codes(diags));
        MemberDesugarPropertyUnitTests.AssertOutputCompiles(outCode);
    }

    [Fact]
    public void MixedPropertyAndIndexerInOneFile_BothDesugared()
    {
        var (outCode, stats, _) = MemberDesugarPropertyUnitTests.Rewrite("""
            class C
            {
                public int Count { get; set; }
                public int this[int i] { get => i; set { } }
                public int M(int i) { Count = this[i]; return Count + this[i]; }
            }
            """);
        AssertContains(outCode, "set_Count(this.get_Item(i))");
        AssertContains(outCode, "get_Count() + this.get_Item(i)");
        Assert.True(stats.PropertiesPlanned >= 1 && stats.IndexersPlanned >= 1);
        MemberDesugarPropertyUnitTests.AssertOutputCompiles(outCode);
    }

    [Fact]
    public void Idempotent_SecondRun_NoChange()
    {
        var (first, _, _) = MemberDesugarPropertyUnitTests.Rewrite(SimpleIndexer);
        var (second, _, _) = MemberDesugarPropertyUnitTests.Rewrite(first);
        Assert.Equal(Normalize(first), Normalize(second));
    }
}

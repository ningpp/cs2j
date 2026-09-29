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
}

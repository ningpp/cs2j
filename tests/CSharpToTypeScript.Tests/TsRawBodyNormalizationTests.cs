namespace CSharpToTypeScript.Tests;

/// <summary>
/// M5 / MSAGL: raw method bodies and field initializers (Java-flavor text produced
/// by the shared front-end) are normalized at emission time: Java declarations
/// become let, classic for heads let-ify, float suffixes drop, and Java array
/// creation literals become TS array literals. All cases compile under real tsc.
/// </summary>
public class TsRawBodyNormalizationTests : TsConversionTestBase
{
    [Fact]
    public void Method_Body_Declaration_And_Float_Suffix_Are_Normalized()
    {
        var result = Convert("""
class Sample
{
    double Scale()
    {
        double factor = 1.5f;
        return factor;
    }
}
""");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("let factor: number = 1.5;", result.GeneratedCode);
        AssertCompiles(result.GeneratedCode);
    }

    [Fact]
    public void Method_Body_Multi_Variable_Declaration_Is_Normalized()
    {
        var result = Convert("""
class Sample
{
    double MinMax()
    {
        double aSol, bSol;
        aSol = 1;
        bSol = 2;
        return aSol + bSol;
    }
}
""");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("let aSol: number, bSol: number;", result.GeneratedCode);
        AssertCompiles(result.GeneratedCode);
    }

    [Fact]
    public void Field_Array_Initializer_Becomes_Array_Literal()
    {
        var result = Convert("""
class Sample
{
    static readonly string[] Colors = new string[] { "red", "green" };
}
""");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("private static readonly Colors: string[] = [\"red\", \"green\"];", result.GeneratedCode);
        AssertCompiles(result.GeneratedCode);
    }

    [Fact]
    public void Field_Multi_Dimensional_Array_Initializer_Becomes_Nested_Literals()
    {
        var result = Convert("""
class Sample
{
    readonly double[][] Points = new double[][] { new double[] { 1, 2 }, new double[] { 3, 4 } };
}
""");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("private Points: number[][] = [[1, 2], [3, 4]];", result.GeneratedCode);
        AssertCompiles(result.GeneratedCode);
    }

    [Fact]
    public void Intersect_Raw_Method_Reference_Is_Normalized_And_Compiles()
    {
        // MSAGL LayoutAlgorithmHelpers.cs:106 `double l = u.Union(v).Count() - u.Intersect(v).Count();`
        // 的 Intersect 分支：LINQ 重写产 `{receiver}.filter(new HashSet<>(v)::contains)`；raw 文本
        // 归一化须剥空菱形、改写表达式接收者 :: 为箭头函数、裸 HashSet 映射为 CSharpHashSet
        // （Round 15 E 族）。global.d.ts 桩同时声明类型别名与构造值（TS 允许同名 type/const 合并），
        // Stream/StreamSupport 探针声明使整行可被 strict tsc 上下文定型。
        var result = Convert("""
using System.Collections.Generic;
using System.Linq;
class Sample
{
    double CountShared(HashSet<int> u, HashSet<int> v)
    {
        double l = u.Union(v).Count() - u.Intersect(v).Count();
        return l;
    }
}
""");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("new CSharpHashSet(v).contains", result.GeneratedCode);
        Assert.DoesNotContain("::", result.GeneratedCode);
        Assert.DoesNotContain("<>", result.GeneratedCode);
        AssertCompilesWithCompat(result.GeneratedCode,
            "global.d.ts", IntersectGlobalStub);
    }

    private const string IntersectGlobalStub =
        """
        // Compile stub: declares the JVM-shaped API the generated code expects
        // (Java-flavor names stream/contains; the real compat CsSet is PascalCase,
        // which belongs to the semantic-error rounds). type+const same-name merging
        // covers type positions (alias) and value positions (new CSharpHashSet(v)).
        interface StreamLike {
            distinct(): StreamLike;
            size(): number;
            filter(pred: (v: number) => boolean): StreamLike;
        }
        type CSharpHashSet<T> = {
            stream(): StreamLike;
            contains(value: T): boolean;
        } & import("@csharptotypescript/compat").CsSet<T>;
        declare const CSharpHashSet: new <T>(value?: Iterable<T>) => CSharpHashSet<T>;
        declare const Stream: { concat(...streams: StreamLike[]): StreamLike };
        declare const StreamSupport: { stream(spliterator: unknown, parallel: boolean): StreamLike };
        """;
}

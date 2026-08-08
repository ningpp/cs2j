using CSharpToJava.Core.TypeScript;

namespace CSharpToTypeScript.Tests;

/// <summary>
/// Round 19（根因 B）：compat Java 集合 API 面缺口。
/// C# List/Stack 索引器与方法映射为 Java 风味调用点（.get(i)/.set(i,v)/.removeAt(i)/…），
/// 但 global.d.ts 桩 `type CSharpList&lt;T&gt; = Array&lt;T&gt;` 无方法面 → TS2339；
/// `new CSharpList&lt;T&gt;(coll)` 值位 → TS2693（类型别名当值用）。修复：
/// ① 桩增 Array&lt;T&gt; 接口合并方法面（与 compat 运行时 polyfill 一致）；
/// ② raw 文本/结构化 new 表达式改写：`new CSharpList&lt;X&gt;()` → `[]`、
/// `new CSharpList&lt;X&gt;(e)` → `Array.from(e)`（数字字面量 → `new Array(n)`）、
/// `new CSharpStack&lt;X&gt;()` → `[]`。
/// </summary>
public class TsCollectionCompatRound19Tests : TsConversionTestBase
{
    private const string CollectionGlobalStub =
        """
        type CSharpList<T> = Array<T>;
        type CSharpStack<T> = Array<T>;
        interface Array<T> {
            get(index: number): T;
            set(index: number, value: T): void;
            add(value: T): void;
            addAll(values: Iterable<T>): void;
            remove(value: T): boolean;
            removeAt(index: number): void;
            insert(index: number, value: T): void;
            removeRange(start: number, end: number): void;
            size(): number;
            toArray(): T[];
            iterator(): IterableIterator<T>;
            exists(predicate: (value: T) => boolean): boolean;
            ensureCapacity(minimumCapacity: number): void;
            peek(): T;
        }
        """;

    [Fact]
    public void Raw_New_CSharpList_With_Arg_Becomes_Array_From()
    {
        var normalized = TsRawStatementNormalizer.Normalize(
            "CSharpList<Polyline> holes = new CSharpList<Polyline>(orientHolesClockwise(obstacles));",
            s => s, null);
        Assert.Contains("Array.from(typeof (orientHolesClockwise(obstacles)) === \"number\" ? new Array(orientHolesClockwise(obstacles)) : (orientHolesClockwise(obstacles)))", normalized);
        Assert.DoesNotContain("new CSharpList", normalized);
    }

    [Fact]
    public void Raw_New_CSharpList_Empty_Becomes_Array_Literal()
    {
        var normalized = TsRawStatementNormalizer.Normalize(
            "CSharpList<Point> pts = new CSharpList<Point>();",
            s => s, null);
        Assert.Contains("= []", normalized);
    }

    [Fact]
    public void Raw_New_CSharpStack_Empty_Becomes_Array_Literal()
    {
        var normalized = TsRawStatementNormalizer.Normalize(
            "CSharpStack<CdtSite> stack = new CSharpStack<CdtSite>();",
            s => s, null);
        Assert.Contains("= []", normalized);
    }

    [Fact]
    public void Raw_New_CSharpList_Numeric_Arg_Becomes_New_Array()
    {
        // 数字字面量 → new Array(n)（ArrayList(int) 容量语义）
        var normalized = TsRawStatementNormalizer.Normalize(
            "CSharpList<Point> pts = new CSharpList<Point>(5);",
            s => s, null);
        Assert.Contains("= new Array(5)", normalized);
        // 数字表达式非字面量 → typeof 分发三元（编译期类型不可判定）
        var normalizedExpr = TsRawStatementNormalizer.Normalize(
            "CSharpList<Point> pts = new CSharpList<Point>(count - 1);",
            s => s, null);
        Assert.Contains("Array.from(typeof (count - 1) === \"number\" ? new Array(count - 1) : (count - 1))", normalizedExpr);
    }

    [Fact]
    public void Raw_NonCompat_Constructors_Untouched()
    {
        var input = "new CSharpHashSet<>(v).contains(x); new Point(1, 2); new CSharpList<Point>(a, b);";
        var normalized = TsRawStatementNormalizer.Normalize(input, s => s, null);
        // 空菱形被 NormalizeDiamondConstructors 剥（既有 pass），集合名本身不被本 pass 改写
        Assert.Contains("new CSharpHashSet(v)", normalized);
        Assert.Contains("new Point(1, 2)", normalized);
        // 多实参形态（非 List 构造）原样直通
        Assert.Contains("new CSharpList<Point>(a, b)", normalized);
    }

    [Fact]
    public void EndToEnd_List_Indexer_And_Methods_Compile_With_Stub()
    {
        // probe19d 镜像：索引器读 → .get(i)、索引器写 → .set(i,v)、RemoveAt → .removeAt、
        // new List<T>(coll) → Array.from、new List<T>() → []；global.d.ts 桩合并 Array
        // 方法面后 strict tsc 编译通过。
        var result = Convert("""
using System.Collections.Generic;
public class Probe
{
    private List<int> A;
    private List<int> B;
    public void M(List<int> other)
    {
        A = new List<int>();
        B = new List<int>(other);
        int x = A[0];
        A[0] = x;
        A.Add(5);
        A.RemoveAt(0);
        int n = A.Count;
    }
}
""");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("this.A = [];", result.GeneratedCode);
        Assert.Contains("this.B = Array.from(typeof (other) === \"number\" ? new Array(other) : (other));", result.GeneratedCode);
        Assert.Contains("let x: number = this.A.get(0);", result.GeneratedCode);
        Assert.Contains("this.A.set(0, x);", result.GeneratedCode);
        Assert.Contains("this.A.removeAt(0);", result.GeneratedCode);
        Assert.Contains("this.A.push(5);", result.GeneratedCode);
        AssertCompilesWithCompat(result.GeneratedCode, "global.d.ts", CollectionGlobalStub);
    }
}

namespace CSharpToTypeScript.Tests;

/// <summary>
/// Task 36 MemberMappings 生效与 LINQ 目标分叉发射测试（spec §2.4 / §3.1.2 / §4.3.8）。
/// 覆盖：BCL 静态调用映射（CsString.*/Math.*，imports 自动收集）、List/Dictionary/HashSet
/// 成员映射、简单 LINQ 链 → filter/map（return 与 foreach 上下文）、复杂链 → Enumerable.*
/// 惰性链、GroupBy g.key、链式 Split(...).Length。全部用例经 L3 tsc --noEmit（compat 桩 +
/// global.d.ts Java 风味名桩）编译。
/// </summary>
public class TsMemberMappingEmissionTests : TsConversionTestBase
{
    // CSharpList/Integer/CSharpDictionary/CSharpHashSet 等 Java 风味名泄漏为既有已知
    //（Task 36 不修），L3 用 global.d.ts 桩声明（CSharpDictionary/CSharpHashSet 别名到
    // compat 类，保证静态调用形态类型兼容）。
    private const string GlobalStub = """
type Integer = number;
type CSharpList<T> = Array<T>;
type CSharpDictionary<K, V> = import("@csharptotypescript/compat").CsDictionary<K, V>;
type CSharpHashSet<T> = import("@csharptotypescript/compat").CsSet<T>;
""";

    [Fact]
    public void Bcl_Static_Mappings_Emits_CsString_And_Math_With_Compat_Import()
    {
        var result = Convert("""
class X
{
    string Bcl(string s)
    {
        string[] parts = s.Split(',');
        string joined = string.Join(",", parts);
        string padded = s.PadLeft(5);
        bool isNull = string.IsNullOrEmpty(s);
        double root = Math.Sqrt(16.0);
        int conv = Convert.ToInt32(42.7);
        return joined + padded;
    }
}
""");
        Assert.True(result.Success);
        Assert.Contains("""let parts: string[] = CsString.split(s, ',');""", result.GeneratedCode);
        Assert.Contains("""CsString.join(",", parts)""", result.GeneratedCode);
        Assert.Contains("""CsString.padLeft(s, 5)""", result.GeneratedCode);
        Assert.Contains("""CsString.isNullOrEmpty(s)""", result.GeneratedCode);
        Assert.Contains("""Math.sqrt(16.0)""", result.GeneratedCode);
        Assert.Contains("""Math.trunc(42.7)""", result.GeneratedCode);
        Assert.Contains("""import { CsString } from "@csharptotypescript/compat";""", result.GeneratedCode);
        AssertCompilesWithCompat(result.GeneratedCode, "global.d.ts", GlobalStub);
    }

    [Fact]
    public void List_Members_Map_To_Native_Array_Shape()
    {
        var result = Convert("""
using System.Collections.Generic;
class X
{
    void Ops(List<int> xs)
    {
        xs.Add(1);
        int n = xs.Count;
        bool has = xs.Contains(3);
    }
}
""");
        Assert.True(result.Success);
        Assert.Contains("""xs.push(1);""", result.GeneratedCode);
        Assert.Contains("""let n: number = xs.length;""", result.GeneratedCode);
        Assert.Contains("""let has: boolean = xs.includes(3);""", result.GeneratedCode);
        Assert.DoesNotContain(".add(", result.GeneratedCode);
        AssertCompilesWithCompat(result.GeneratedCode, "global.d.ts", GlobalStub);
    }

    [Fact]
    public void Dictionary_And_HashSet_Map_To_CsDictionary_And_CsSet()
    {
        var result = Convert("""
using System.Collections.Generic;
class X
{
    void Ops(Dictionary<string, int> d, HashSet<int> h)
    {
        d.Add("k", 1);
        int dc = d.Count;
        bool dk = d.ContainsKey("k");
        h.Add(1);
        int hc = h.Count;
        bool hr = h.Remove(1);
    }
}
""");
        Assert.True(result.Success);
        Assert.Contains("""CsDictionary.add(d, "k", 1);""", result.GeneratedCode);
        Assert.Contains("""let dc: number = CsDictionary.count(d);""", result.GeneratedCode);
        Assert.Contains("""let dk: boolean = CsDictionary.containsKey(d, "k");""", result.GeneratedCode);
        Assert.Contains("""CsSet.add(h, 1);""", result.GeneratedCode);
        Assert.Contains("""let hc: number = CsSet.count(h);""", result.GeneratedCode);
        Assert.Contains("""let hr: boolean = CsSet.remove(h, 1);""", result.GeneratedCode);
        Assert.Contains("""import { CsDictionary, CsSet } from "@csharptotypescript/compat";""", result.GeneratedCode);
        AssertCompilesWithCompat(result.GeneratedCode, "global.d.ts", GlobalStub);
    }

    [Fact]
    public void Simple_Linq_Chain_Becomes_Filter_Map_In_Return()
    {
        var result = Convert("""
using System;
using System.Collections.Generic;
using System.Linq;
class X
{
    public List<int> Direct(List<int> values)
    {
        return values.Where(x => x > 0).Select(x => x * 2);
    }
}
""");
        Assert.True(result.Success);
        Assert.Contains("""return values.filter(x => x > 0).map(x => x * 2);""", result.GeneratedCode);
        Assert.DoesNotContain(".stream()", result.GeneratedCode);
        Assert.DoesNotContain("Enumerable", result.GeneratedCode);
        AssertCompilesWithCompat(result.GeneratedCode, "global.d.ts", GlobalStub);
    }

    [Fact]
    public void Simple_Linq_Chain_Becomes_Filter_Map_In_Foreach()
    {
        var result = Convert("""
using System;
using System.Collections.Generic;
using System.Linq;
class X
{
    public int Sum(List<int> values)
    {
        int sum = 0;
        foreach (int v in values.Where(x => x > 0).Select(x => x * 2)) { sum = sum + v; }
        return sum;
    }
}
""");
        Assert.True(result.Success);
        Assert.Contains("""for (const v of values.filter(x => x > 0).map(x => x * 2)) {""", result.GeneratedCode);
        Assert.DoesNotContain("collect(", result.GeneratedCode);
        AssertCompilesWithCompat(result.GeneratedCode, "global.d.ts", GlobalStub);
    }

    [Fact]
    public void Complex_Linq_Chain_Becomes_Enumerable_Lazy_Chain()
    {
        var result = Convert("""
using System;
using System.Collections.Generic;
using System.Linq;
class X
{
    public int[] Sorted(List<int> values)
    {
        return values.Where(x => x > 0).OrderBy(x => x).ToArray();
    }
    public int Count(List<int> values)
    {
        return values.Where(x => x > 0).Count();
    }
}
""");
        Assert.True(result.Success);
        Assert.Contains("""Enumerable.toArray(Enumerable.orderBy(Enumerable.where(values, x => x > 0), x => x))""", result.GeneratedCode);
        Assert.Contains("""Enumerable.count(Enumerable.where(values, x => x > 0))""", result.GeneratedCode);
        Assert.Contains("""import { Enumerable } from "@csharptotypescript/compat";""", result.GeneratedCode);
        AssertCompilesWithCompat(result.GeneratedCode, "global.d.ts", GlobalStub);
    }

    [Fact]
    public void GroupBy_Emits_Enumerable_GroupBy_And_Key_Property()
    {
        var result = Convert("""
using System;
using System.Collections.Generic;
using System.Linq;
class X
{
    public int GroupBySum(List<string> words)
    {
        int n = 0;
        foreach (var g in words.GroupBy(w => w.Length)) { n = n + g.Key; }
        return n;
    }
}
""");
        Assert.True(result.Success);
        Assert.Contains("""for (const g of Enumerable.groupBy(words, w => w.length)) {""", result.GeneratedCode);
        Assert.Contains("""n = n + g.key;""", result.GeneratedCode);
        AssertCompilesWithCompat(result.GeneratedCode, "global.d.ts", GlobalStub);
    }

    [Fact]
    public void Chained_Split_Length_Maps_Static_Then_Property()
    {
        var result = Convert("""
class X
{
    public int ArrayLen(string s) { return s.Split(' ').Length; }
}
""");
        Assert.True(result.Success);
        Assert.Contains("""return CsString.split(s, " ").length;""", result.GeneratedCode);
        AssertCompilesWithCompat(result.GeneratedCode, "global.d.ts", GlobalStub);
    }

    [Fact]
    public void MemberMapping_Applies_Without_LinqRewrite_Flag()
    {
        // --no-linq-rewrite（EnableLinqRewrite=false）时成员映射仍生效。
        var result = ConvertWithLinqRewriteDisabled("""
using System;
class X
{
    public string Bcl(string s)
    {
        string[] parts = s.Split(',');
        string joined = string.Join(",", parts);
        return joined;
    }
}
""");
        Assert.True(result.Success);
        Assert.Contains("""let parts: string[] = CsString.split(s, ',');""", result.GeneratedCode);
        Assert.Contains("""CsString.join(",", parts)""", result.GeneratedCode);
        AssertCompilesWithCompat(result.GeneratedCode, "global.d.ts", GlobalStub);
    }
}

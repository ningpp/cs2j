using CSharpToJava.Core.Context;

namespace CSharpToTypeScript.Tests;

/// <summary>
/// Task 27：扩展方法发射（spec §1.4.3/§5.3）。C# 扩展方法统一降级为静态宿主调用
/// （两目标一致）：`s.WordCount()` → `StringExt.wordCount(s)`；静态类 → 私有构造器
/// 的 TS class。全部用例经 L3 tsc strict 编译。
/// </summary>
public class TsExtensionMethodTests : TsConversionTestBase
{
    [Fact]
    public void Extension_Method_Emits_Static_Host_Call()
    {
        var result = Convert("""
public static class StringExt
{
    public static int WordCount(this string s) { return s.Split(' ').Length; }
}
public class Use
{
    public int Count(string s) { return s.WordCount(); }
}
""");
        Assert.True(result.Success);
        Assert.Contains("public static wordCount(s: string): number", result.GeneratedCode);
        Assert.Contains("return StringExt.wordCount(s);", result.GeneratedCode);
        Assert.DoesNotContain("s.wordCount", result.GeneratedCode);
        // Task 36: s.Split(' ').Length 命中 MemberMappings（CsString.split + length），
        // 需要 compat 桩参与 L3 编译。
        AssertCompilesWithCompat(result.GeneratedCode);
    }

    [Fact]
    public void Extension_With_Extra_Args_Passes_Receiver_First()
    {
        var result = Convert("""
public static class StringExt
{
    public static int CountChar(this string s, char c)
    {
        int n = 0;
        foreach (char ch in s) { if (ch == c) n = n + 1; }
        return n;
    }
}
public class Use
{
    public int Count(string s) { return s.CountChar('a'); }
}
""");
        Assert.True(result.Success);
        Assert.Contains("return StringExt.countChar(s, \"a\");", result.GeneratedCode);
        AssertCompiles(result.GeneratedCode);
    }

    [Fact]
    public void Extension_On_Array_Receiver()
    {
        var result = Convert("""
public static class ArrayExt
{
    public static int Total(this int[] xs)
    {
        int sum = 0;
        for (int i = 0; i < xs.Length; i = i + 1) { sum = sum + xs[i]; }
        return sum;
    }
}
public class Use
{
    public int Sum(int[] xs) { return xs.Total(); }
}
""");
        Assert.True(result.Success);
        Assert.Contains("public static total(xs: number[]): number", result.GeneratedCode);
        Assert.Contains("return ArrayExt.total(xs);", result.GeneratedCode);
        AssertCompiles(result.GeneratedCode);
    }

    [Fact]
    public void Static_Extension_Class_Emits_Private_Ctor()
    {
        var result = Convert("""
public static class MathExt
{
    public static int Twice(this int x) { return x * 2; }
}
public class Use
{
    public int T(int x) { return x.Twice(); }
}
""");
        Assert.True(result.Success);
        Assert.Contains("export class MathExt {", result.GeneratedCode);
        Assert.Contains("private constructor()", result.GeneratedCode);
        Assert.Contains("return MathExt.twice(x);", result.GeneratedCode);
        AssertCompiles(result.GeneratedCode);
    }
}
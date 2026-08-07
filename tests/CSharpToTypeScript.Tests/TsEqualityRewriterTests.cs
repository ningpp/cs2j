using CSharpToJava.Core.Context;

namespace CSharpToTypeScript.Tests;

/// <summary>
/// Task 26：相等语义（spec §2.6.4）。record/struct/覆写 Equals 的类的 ==/!=/.Equals()
/// 调用点 → CsObject.equals；原始类型/字符串/普通引用/可空 null 的 ==/!= → ===/!==；
/// 静态 Equals(a,b) → CsObject.equals；ReferenceEquals → ===；GetHashCode → hashCode()。
/// 全部用例经 L3 tsc --noEmit 编译（compat 桩含 CsObject）；struct 用例因 struct 发射
/// 本身属 Task 27（Cloneable/clone/raw 声明），仅做调用点文本断言。
/// </summary>
public class TsEqualityRewriterTests : TsConversionTestBase
{
    [Fact]
    public void Primitive_Equality_Emits_Strict_Equality()
    {
        var result = Convert("""
class F
{
    bool A(int a, int b) { return a == b; }
    bool B(int a, int b) { return a != b; }
    bool C(bool x, bool y) { return x == y; }
    bool D(long a, long b) { return a != b; }
}
""");
        Assert.True(result.Success);
        Assert.Contains("return a === b;", result.GeneratedCode);
        Assert.Contains("return a !== b;", result.GeneratedCode);
        Assert.Contains("return x === y;", result.GeneratedCode);
        Assert.DoesNotContain("Objects.", result.GeneratedCode);
        AssertCompiles(result.GeneratedCode);
    }

    [Fact]
    public void String_Equality_Emits_Strict_Equality()
    {
        var result = Convert("""
class F
{
    bool A(string s1, string s2) { return s1 == s2; }
    bool B(string s1, string s2) { return s1 != s2; }
    bool C(string s) { return s == null; }
}
""");
        Assert.True(result.Success);
        Assert.Contains("return s1 === s2;", result.GeneratedCode);
        Assert.Contains("return s1 !== s2;", result.GeneratedCode);
        Assert.Contains("return s === null;", result.GeneratedCode);
        Assert.DoesNotContain("Objects.equals", result.GeneratedCode);
        AssertCompiles(result.GeneratedCode);
    }

    [Fact]
    public void Plain_Class_Equality_Emits_Strict_Equality()
    {
        var result = Convert("""
class C { }
class F
{
    bool A(C a, C b) { return a == b; }
    bool B(object a, object b) { return a != b; }
    bool C(C c) { return c == null; }
}
""");
        Assert.True(result.Success);
        Assert.Contains("return a === b;", result.GeneratedCode);
        Assert.Contains("return a !== b;", result.GeneratedCode);
        Assert.Contains("return c === null;", result.GeneratedCode);
        Assert.DoesNotContain("CsObject", result.GeneratedCode);
        AssertCompiles(result.GeneratedCode);
    }

    [Fact]
    public void Equals_Overriding_Class_Equality_Emits_CsObject_Equals()
    {
        var result = Convert("""
class Point
{
    public int X;
    public override bool Equals(object obj)
    {
        if (!(obj is Point)) return false;
        Point other = (Point)obj;
        return this.X == other.X;
    }
    public override int GetHashCode() { return this.X; }
}
class F
{
    bool A(Point p1, Point p2) { return p1 == p2; }
    bool B(Point p1, Point p2) { return p1 != p2; }
    bool C(Point p) { return p == null; }
}
""");
        Assert.True(result.Success);
        Assert.Contains("return CsObject.equals(p1, p2);", result.GeneratedCode);
        Assert.Contains("return !CsObject.equals(p1, p2);", result.GeneratedCode);
        // p == null：引用比较保持 ===（null 字面量不触发深比较）
        Assert.Contains("return p === null;", result.GeneratedCode);
        // Equals 覆写体内字段比较 → ===
        Assert.Contains("this.X === other.X", result.GeneratedCode);
        // GetHashCode → 实例 hashCode() 方法
        Assert.Contains("public hashCode(): number", result.GeneratedCode);
        Assert.Contains("import { CsObject } from \"@csharptotypescript/compat\";", result.GeneratedCode);
        AssertCompilesWithCompat(result.GeneratedCode);
    }

    [Fact]
    public void Struct_Equality_Emits_CsObject_Equals()
    {
        // struct 发射（Cloneable/clone/raw 声明）属 Task 27，此处仅验证调用点改写
        var result = Convert("""
struct Point
{
    public int X;
    public static bool operator ==(Point a, Point b) { return a.X == b.X; }
    public static bool operator !=(Point a, Point b) { return !(a == b); }
    public override bool Equals(object obj)
    {
        if (!(obj is Point)) return false;
        Point other = (Point)obj;
        return this.X == other.X;
    }
    public override int GetHashCode() { return this.X; }
}
class F
{
    bool A(Point p1, Point p2) { return p1 == p2; }
    bool B(Point p1, Point p2) { return p1 != p2; }
}
""");
        Assert.True(result.Success);
        Assert.Contains("return CsObject.equals(p1, p2);", result.GeneratedCode);
        Assert.Contains("return !CsObject.equals(p1, p2);", result.GeneratedCode);
        Assert.DoesNotContain("op_Equality", result.GeneratedCode);
    }

    [Fact]
    public void Record_Equality_Emits_CsObject_Equals()
    {
        var result = Convert("""
record Point(int X, int Y);
class F
{
    bool A(Point p1, Point p2) { return p1 == p2; }
    bool B(Point p1, Point p2) { return p1.Equals(p2); }
}
""");
        Assert.True(result.Success);
        Assert.Contains("return CsObject.equals(p1, p2);", result.GeneratedCode);
        Assert.DoesNotContain("p1 == p2", result.GeneratedCode);
        AssertCompilesWithCompat(result.GeneratedCode);
    }

    [Fact]
    public void Instance_Equals_On_Value_Class_Emits_CsObject_Equals()
    {
        var result = Convert("""
class Point
{
    public int X;
    public override bool Equals(object obj)
    {
        if (!(obj is Point)) return false;
        Point other = (Point)obj;
        return this.X == other.X;
    }
    public override int GetHashCode() { return this.X; }
}
class F
{
    bool A(Point p1, Point p2) { return p1.Equals(p2); }
    bool B(Point p1) { return p1.Equals(null); }
}
""");
        Assert.True(result.Success);
        Assert.Contains("return CsObject.equals(p1, p2);", result.GeneratedCode);
        Assert.Contains("return CsObject.equals(p1, null);", result.GeneratedCode);
        AssertCompilesWithCompat(result.GeneratedCode);
    }

    [Fact]
    public void Instance_Equals_On_String_And_Primitive_Emits_Strict_Equality()
    {
        var result = Convert("""
class F
{
    bool A(string s1, string s2) { return s1.Equals(s2); }
    bool B(int a, int b) { return a.Equals(b); }
    bool C(object o1, object o2) { return o1.Equals(o2); }
}
""");
        Assert.True(result.Success);
        Assert.Contains("return s1 === s2;", result.GeneratedCode);
        Assert.Contains("return a === b;", result.GeneratedCode);
        Assert.Contains("return o1 === o2;", result.GeneratedCode);
        Assert.DoesNotContain("Objects.equals", result.GeneratedCode);
        Assert.DoesNotContain("CsObject", result.GeneratedCode);
        AssertCompiles(result.GeneratedCode);
    }

    [Fact]
    public void Static_Equals_Emits_CsObject_Equals()
    {
        var result = Convert("""
class F
{
    bool A(object o1, object o2) { return object.Equals(o1, o2); }
    bool B(string s1, string s2) { return string.Equals(s1, s2); }
    bool C(object o1, object o2) { return !object.Equals(o1, o2); }
}
""");
        Assert.True(result.Success);
        Assert.Contains("return CsObject.equals(o1, o2);", result.GeneratedCode);
        Assert.Contains("return CsObject.equals(s1, s2);", result.GeneratedCode);
        Assert.Contains("return !CsObject.equals(o1, o2);", result.GeneratedCode);
        Assert.DoesNotContain("Objects.equals", result.GeneratedCode);
        AssertCompilesWithCompat(result.GeneratedCode);
    }

    [Fact]
    public void ReferenceEquals_Emits_Strict_Equality()
    {
        var result = Convert("""
class F
{
    bool A(object o1, object o2) { return object.ReferenceEquals(o1, o2); }
    bool B(string s1, string s2) { return ReferenceEquals(s1, s2); }
}
""");
        Assert.True(result.Success);
        Assert.Contains("return o1 === o2;", result.GeneratedCode);
        Assert.Contains("return s1 === s2;", result.GeneratedCode);
        Assert.DoesNotContain("referenceEquals", result.GeneratedCode);
        AssertCompiles(result.GeneratedCode);
    }

    [Fact]
    public void Nullable_Null_Comparison_Emits_Strict_Equality()
    {
        var result = Convert("""
class F
{
    bool A(int? x) { return x == null; }
    bool B(int? x) { return x != null; }
    bool C(int? x, int? y) { return x == y; }
}
""");
        Assert.True(result.Success);
        Assert.Contains("return x === null;", result.GeneratedCode);
        Assert.Contains("return x !== null;", result.GeneratedCode);
        Assert.Contains("return x === y;", result.GeneratedCode);
        Assert.DoesNotContain("CsObject", result.GeneratedCode);
        AssertCompiles(result.GeneratedCode);
    }

    [Fact]
    public void Mixed_Operand_Equality_Keeps_Reference_Semantics()
    {
        // 覆写 Equals 的类与 object 混比：C# 引用相等 → ===（不触发 CsObject.equals）
        var result = Convert("""
class Point
{
    public override bool Equals(object obj) { return true; }
    public override int GetHashCode() { return 0; }
}
class F
{
    bool A(Point p, object o) { return p == o; }
}
""");
        Assert.True(result.Success);
        Assert.Contains("return p === o;", result.GeneratedCode);
        Assert.DoesNotContain("CsObject", result.GeneratedCode);
        AssertCompiles(result.GeneratedCode);
    }

    [Fact]
    public void Explicit_Operator_On_Reference_Class_Is_Left_To_Operator_Task()
    {
        // 引用类型显式 operator==：Task 27 运算符重载处理调用点，Task 26 不改写
        var result = Convert("""
class Money
{
    public int Amount;
    public static bool operator ==(Money a, Money b) { return a.Amount == b.Amount; }
    public static bool operator !=(Money a, Money b) { return !(a == b); }
}
class F
{
    bool A(Money a, Money b) { return a == b; }
}
""");
        Assert.True(result.Success);
        Assert.DoesNotContain("CsObject", result.GeneratedCode);
    }

    [Fact]
    public void GetHashCode_Emits_Instance_HashCode_Method()
    {
        var result = Convert("""
class C
{
    public override int GetHashCode() { return 42; }
}
class F
{
    int A(C c) { return c.GetHashCode(); }
    int B(object o) { return o.GetHashCode(); }
}
""");
        Assert.True(result.Success);
        Assert.Contains("public hashCode(): number", result.GeneratedCode);
        Assert.Contains("return c.hashCode();", result.GeneratedCode);
        Assert.Contains("return o.hashCode();", result.GeneratedCode);
        Assert.DoesNotContain("Integer.hashCode", result.GeneratedCode);
        AssertCompiles(result.GeneratedCode);
    }

    [Fact]
    public void Equality_In_Expressions_And_Statements_Is_Rewritten()
    {
        var result = Convert("""
class Point
{
    public int X;
    public override bool Equals(object obj)
    {
        if (!(obj is Point)) return false;
        Point other = (Point)obj;
        return this.X == other.X;
    }
    public override int GetHashCode() { return this.X; }
}
class F
{
    bool A(Point p1, Point p2)
    {
        bool eq = p1 == p2;
        if (p1 != p2) { return false; }
        return eq && (p1 == p2) == (p1 == p2);
    }
}
""");
        Assert.True(result.Success);
        Assert.Contains("let eq: boolean = CsObject.equals(p1, p2);", result.GeneratedCode);
        Assert.Contains("if (!CsObject.equals(p1, p2))", result.GeneratedCode);
        // 内层 p1 == p2（Point）→ CsObject.equals；外层两个 bool 结果比较 → ===
        Assert.Contains("(CsObject.equals(p1, p2)) === (CsObject.equals(p1, p2))", result.GeneratedCode);
        AssertCompilesWithCompat(result.GeneratedCode);
    }
}

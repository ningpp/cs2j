using CSharpToJava.Core.Context;

namespace CSharpToTypeScript.Tests;

/// <summary>
/// Task 27：运算符重载发射（spec §3.2/§5.3）。`operator +` 声明 → 静态方法
/// `op_Add` 等 PascalCase（OperatorTransformer.OpSymbolToTsName）；调用点
/// `a + b` → `Type.op_Add(a, b)`（BinaryExpressionTransformer TS 分叉），
/// 类内调用点省略类限定符。结构体的 ==/!= 例外：声明端保持 valueEquals/notEquals
/// （StructTransformer 合成 equals/hashCode 依赖），调用点已由 TsEqualityRewriter
/// 改写为 CsObject.equals（Task 26），断言不出现 op_Equality。struct 发射
/// （implements Cloneable 移除、clone()/合成 equals/hashCode 方法体）由
/// TsStructRewriter 收尾，用例经 L3 tsc strict 编译（compat 桩含 CsObject.getHashCode）。
/// </summary>
public class TsOperatorOverloadTests : TsConversionTestBase
{
    [Fact]
    public void Struct_Operator_Plus_Emits_OpAdd_And_Rewrites_CallSite()
    {
        var result = Convert("""
public struct Vector
{
    public int X;
    public static Vector operator +(Vector a, Vector b) { return new Vector { X = a.X + b.X }; }
}
public class Use
{
    public Vector Add(Vector a, Vector b) { return a + b; }
}
""");
        Assert.True(result.Success);
        Assert.Contains("public static op_Add(a: Vector, b: Vector): Vector", result.GeneratedCode);
        Assert.Contains("return Vector.op_Add(a, b);", result.GeneratedCode);
        Assert.DoesNotContain("implements Cloneable", result.GeneratedCode);
        AssertCompilesWithCompat(result.GeneratedCode);
    }

    [Fact]
    public void Class_Operators_Emits_PascalCase_OpNames()
    {
        var result = Convert("""
public class Money
{
    public int Amount;
    public static Money operator +(Money a, Money b) { return new Money { Amount = a.Amount + b.Amount }; }
    public static Money operator -(Money a, Money b) { return new Money { Amount = a.Amount - b.Amount }; }
    public static Money operator *(Money a, Money b) { return new Money { Amount = a.Amount * b.Amount }; }
    public static Money operator /(Money a, Money b) { return new Money { Amount = a.Amount / b.Amount }; }
    public static Money operator %(Money a, Money b) { return new Money { Amount = a.Amount % b.Amount }; }
}
public class Use
{
    public Money M(Money a, Money b)
    {
        Money x = a + b;
        Money y = a - b;
        Money z = a * b;
        Money w = a / b;
        Money v = a % b;
        return v;
    }
}
""");
        Assert.True(result.Success);
        Assert.Contains("static op_Add(", result.GeneratedCode);
        Assert.Contains("static op_Subtract(", result.GeneratedCode);
        Assert.Contains("static op_Multiply(", result.GeneratedCode);
        Assert.Contains("static op_Divide(", result.GeneratedCode);
        Assert.Contains("static op_Modulus(", result.GeneratedCode);
        Assert.Contains("Money.op_Add(a, b)", result.GeneratedCode);
        Assert.Contains("Money.op_Subtract(a, b)", result.GeneratedCode);
        Assert.Contains("Money.op_Multiply(a, b)", result.GeneratedCode);
        Assert.Contains("Money.op_Divide(a, b)", result.GeneratedCode);
        Assert.Contains("Money.op_Modulus(a, b)", result.GeneratedCode);
        AssertCompiles(result.GeneratedCode);
    }

    [Fact]
    public void InClass_CallSite_Qualifies_Static_Operator()
    {
        // TS 无 Java 的隐式静态访问：类内调用点同样
        // 需要 Vec.op_Add(a, a) 限定（否则 tsc TS2662）。
        var result = Convert("""
public class Vec
{
    public int X;
    public static Vec operator +(Vec a, Vec b) { return new Vec { X = a.X + b.X }; }
    public Vec Twice(Vec a) { return a + a; }
}
""");
        Assert.True(result.Success);
        Assert.Contains("return Vec.op_Add(a, a);", result.GeneratedCode);
        AssertCompiles(result.GeneratedCode);
    }

    [Fact]
    public void Unary_Operator_Emits_OpUnaryNegation()
    {
        var result = Convert("""
public class Counter
{
    public int N;
    public static Counter operator -(Counter c) { return new Counter { N = -c.N }; }
}
public class Use
{
    public Counter Neg(Counter c) { return -c; }
}
""");
        Assert.True(result.Success);
        Assert.Contains("static op_UnaryNegation(c: Counter): Counter", result.GeneratedCode);
        Assert.Contains("return Counter.op_UnaryNegation(c);", result.GeneratedCode);
        AssertCompiles(result.GeneratedCode);
    }

    [Fact]
    public void Struct_Equality_Keeps_ValueEquals_And_CsObject_CallSites()
    {
        var result = Convert("""
public struct Vector
{
    public int X;
    public static bool operator ==(Vector a, Vector b) { return a.X == b.X; }
    public static bool operator !=(Vector a, Vector b) { return a.X != b.X; }
}
public class Use
{
    public bool Eq(Vector a, Vector b) { return a == b; }
    public bool Ne(Vector a, Vector b) { return a != b; }
}
""");
        Assert.True(result.Success);
        Assert.Contains("static valueEquals(a: Vector, b: Vector): boolean", result.GeneratedCode);
        Assert.Contains("static notEquals(a: Vector, b: Vector): boolean", result.GeneratedCode);
        Assert.Contains("return CsObject.equals(a, b);", result.GeneratedCode);
        Assert.Contains("return !CsObject.equals(a, b);", result.GeneratedCode);
        Assert.DoesNotContain("op_Equality", result.GeneratedCode);
        AssertCompilesWithCompat(result.GeneratedCode);
    }

    [Fact]
    public void Struct_Equality_Helpers_Are_Valid_TypeScript()
    {
        // 合成 equals(Object)/hashCode()/clone() 经 TsStructRewriter 收尾后必须可编译
        var result = Convert("""
public struct Point
{
    public int X;
    public int Y;
    public static bool operator ==(Point a, Point b) { return a.X == b.X && a.Y == b.Y; }
    public static bool operator !=(Point a, Point b) { return !(a == b); }
}
public class Use
{
    public bool Eq(Point a, Point b) { return a == b; }
    public Point Copy(Point a) { return a; }
}
""");
        Assert.True(result.Success);
        Assert.Contains("const copy = new Point();", result.GeneratedCode);
        Assert.Contains("if (this === o) return true;", result.GeneratedCode);
        Assert.Contains("const other = o as Point;", result.GeneratedCode);
        Assert.Contains("Point.valueEquals(this, other);", result.GeneratedCode);
        Assert.Contains("CsObject.getHashCode(this.X)", result.GeneratedCode);
        AssertCompilesWithCompat(result.GeneratedCode);
    }
}
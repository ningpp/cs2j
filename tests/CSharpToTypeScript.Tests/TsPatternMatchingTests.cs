using CSharpToJava.Core.Context;

namespace CSharpToTypeScript.Tests;

/// <summary>
/// Task 27：模式匹配发射（spec §1.4.4/§5.3）。共享前端按 Java 16 形态降低
/// 模式匹配（`expr instanceof T p` 模式变量绑定、switch 表达式三元链），TS 无模式变量
/// 语法，由 TsPatternMatchingRewriter 在语法层改写为前端可直译形态：
/// ① if/while 声明模式 → instanceof 条件 + 段首声明（`let c: Circle = (s as Circle);`）；
/// ② `is not T` → `!(s instanceof T)`；③ switch 语句模式标签 → if-else 链 + 段首声明（含
/// 接口探针 `is$IFoo(o)`），`case null:` → `o === null`；④ switch 表达式 → 嵌套三元（
/// when 防卫与模式变量使用处强转）；⑤ 递归/属性模式 → 类型检查 +
/// 成员条件。原始类型模式（`int i`）与 `and`+recursive 组合登记限制不改写。
/// 全部可编译用例经 L3。
/// </summary>
public class TsPatternMatchingTests : TsConversionTestBase
{
    [Fact]
    public void If_DeclarationPattern_Emits_InstanceOf_Guard_With_Declaration()
    {
        var result = Convert("""
public class Shape { }
public class Circle : Shape { public double R; }
public class Use
{
    public double Area(Shape s)
    {
        if (s is Circle c) return c.R * c.R;
        return 0;
    }
}
""");
        Assert.True(result.Success);
        Assert.Contains("if (s instanceof Circle) {", result.GeneratedCode);
        Assert.Contains("let c: Circle = (s as Circle);", result.GeneratedCode);
        Assert.Contains("return c.R * c.R;", result.GeneratedCode);
        AssertCompilesWithCompat(result.GeneratedCode);
    }

    [Fact]
    public void While_DeclarationPattern_Emits_Guard_With_Declaration()
    {
        var result = Convert("""
public class Shape { }
public class Circle : Shape { public double R; }
public class Use
{
    public double AreaWhile(Shape s)
    {
        double sum = 0;
        while (s is Circle c) { sum += c.R; break; }
        return sum;
    }
}
""");
        Assert.True(result.Success);
        Assert.Contains("while (s instanceof Circle) {", result.GeneratedCode);
        Assert.Contains("let c: Circle = (s as Circle);", result.GeneratedCode);
        Assert.Contains("sum += c.R;", result.GeneratedCode);
        AssertCompilesWithCompat(result.GeneratedCode);
    }

    [Fact]
    public void IsNot_Emits_Negated_InstanceOf_In_Expression_And_If()
    {
        var result = Convert("""
public class Shape { }
public class Circle : Shape { }
public class Use
{
    public bool IsNot(Shape s) { return s is not Circle; }
    public bool IfNot(Shape s)
    {
        if (s is not Circle) return true;
        return false;
    }
}
""");
        Assert.True(result.Success);
        Assert.Contains("return !(s instanceof Circle);", result.GeneratedCode);
        Assert.Contains("if (!(s instanceof Circle)) {", result.GeneratedCode);
        AssertCompilesWithCompat(result.GeneratedCode);
    }

    [Fact]
    public void SwitchStatement_PatternLabels_Emits_IfElse_With_Declarations()
    {
        var result = Convert("""
public class Shape { }
public class Circle : Shape { }
public interface IFoo { void M(); }
public class Foo : IFoo { public void M() { } }
public class Use
{
    public string SwitchStmt(object o)
    {
        switch (o)
        {
            case Circle c: return "circle";
            case string s: return "str";
            case IFoo f: return "foo";
            case null: return "null";
            default: return "other";
        }
    }
}
""");
        Assert.True(result.Success);
        Assert.Contains("if ((o instanceof Circle)) {", result.GeneratedCode);
        Assert.Contains("let c: Circle = (o as Circle);", result.GeneratedCode);
        Assert.Contains("let s: string = (o as string);", result.GeneratedCode);
        Assert.Contains("else if ((is$IFoo(o))) {", result.GeneratedCode);
        Assert.Contains("let f: IFoo = (o as IFoo);", result.GeneratedCode);
        Assert.Contains("else if (o === null) {", result.GeneratedCode);
        Assert.Contains("return \"circle\";", result.GeneratedCode);
        AssertCompilesWithCompat(result.GeneratedCode);
    }

    [Fact]
    public void SwitchExpression_Emits_NestedTernary_With_When_Guard()
    {
        var result = Convert("""
public class Shape { }
public class Circle : Shape { public double R; }
public class Use
{
    public string Kind(object o)
    {
        return o switch
        {
            Circle c when c.R > 0 => "pos",
            string s => "str",
            _ => "other"
        };
    }
}
""");
        Assert.True(result.Success);
        Assert.Contains("((o instanceof Circle) && ((o as Circle).R > 0)) ? \"pos\" :", result.GeneratedCode);
        Assert.Contains("((o instanceof String) ? \"str\" : \"other\")", result.GeneratedCode);
        AssertCompilesWithCompat(result.GeneratedCode);
    }

    [Fact]
    public void PropertyPattern_Emits_TypeCheck_And_Member_Condition()
    {
        var result = Convert("""
public class Shape { }
public class Circle : Shape { public double R; }
public class Use
{
    public bool Prop(Shape s) { return s is Circle { R: > 0 }; }
}
""");
        Assert.True(result.Success);
        Assert.Contains("((s as Circle)).R > 0", result.GeneratedCode);
        AssertCompilesWithCompat(result.GeneratedCode);
    }

    [Fact]
    public void Primitive_Pattern_Remains_Unrewritten_Registered_Limitation()
    {
        var result = Convert("""
public class Use
{
    public string Prim(object o)
    {
        return o switch { int i => "int", _ => "other" };
    }
}
""");
        Assert.True(result.Success);
        // 原始类型模式前端降低产出 instanceof int（C# 装箱模式，Java 16/TS 均无对应），
        // 登记限制不改写；不执行 L3（输出不是合法 TS）。
        Assert.Contains("instanceof int", result.GeneratedCode);
    }
}

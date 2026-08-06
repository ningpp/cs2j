using CSharpToJava.Core.Java;
using CSharpToJava.Core.TypeScript;
using CSharpToJava.Core.TypeScript.Rewriters;

namespace CSharpToTypeScript.Tests;

/// <summary>
/// Task 17 枚举发射测试（M2）：普通 enum 直译（显式值保留，_UNMAPPED 哨兵剔除），
/// [Flags] → const 对象 + 联合类型，枚举转换调用点（getValue/fromValue/fromValueUnchecked/
/// ordinal）改写，Enum.Parse/ToString 等复杂 API → 诊断（兼容库桩 M4）。
/// 先写失败测试（红），实现后全绿；完整转换用例含 L3 tsc。
/// </summary>
public class TsEnumEmissionTests : TsConversionTestBase
{
    // ─── 普通枚举发射 ─────────────────────────────────────────────────

    [Fact]
    public void Enum_With_Explicit_Values_Emits_Assignments()
    {
        var result = Convert("""
enum E { A = 1, B = 5, C = 100 }
""");
        Assert.True(result.Success);
        Assert.Contains("export enum E {", result.GeneratedCode);
        Assert.Contains("A = 1,", result.GeneratedCode);
        Assert.Contains("B = 5,", result.GeneratedCode);
        Assert.Contains("C = 100", result.GeneratedCode);
        // 哨兵/构造参数形态不得外泄
        Assert.DoesNotContain("_UNMAPPED", result.GeneratedCode);
        Assert.DoesNotContain("(1)", result.GeneratedCode);
        Assert.DoesNotContain("getValue", result.GeneratedCode);
        AssertCompiles(result.GeneratedCode);
    }

    [Fact]
    public void Enum_With_Implicit_Values_Emits_Zero_Based_Assignments()
    {
        var result = Convert("""
enum E { A, B, C }
""");
        Assert.True(result.Success);
        Assert.Contains("A = 0,", result.GeneratedCode);
        Assert.Contains("B = 1,", result.GeneratedCode);
        Assert.Contains("C = 2", result.GeneratedCode);
        Assert.DoesNotContain("_UNMAPPED", result.GeneratedCode);
        AssertCompiles(result.GeneratedCode);
    }

    [Fact]
    public void Enum_Negative_Value_Emits_Assignment()
    {
        var result = Convert("""
enum E { A = -1, B = 0, C = 2 }
""");
        Assert.True(result.Success);
        Assert.Contains("A = -1,", result.GeneratedCode);
        Assert.Contains("B = 0,", result.GeneratedCode);
        Assert.Contains("C = 2", result.GeneratedCode);
        AssertCompiles(result.GeneratedCode);
    }

    [Fact]
    public void Enum_Member_Usage_Compiles()
    {
        var result = Convert("""
enum E { A = 1 }
class X
{
    E e = E.A;
    E F(E x) { return E.A; }
}
""");
        Assert.True(result.Success);
        Assert.Contains("private e: E = E.A;", result.GeneratedCode);
        Assert.Contains("f(x: E): E {", result.GeneratedCode);
        Assert.Contains("return E.A;", result.GeneratedCode);
        AssertCompiles(result.GeneratedCode);
    }

    // ─── 枚举转换调用点（getValue/fromValue/ordinal） ───────────────────

    [Fact]
    public void Enum_Cast_Call_Sites_Are_Rewritten()
    {
        var result = Convert("""
enum Color { Red = 10, Green = 20 }
class X
{
    Color c = Color.Red;
    int ToInt(Color c2) { return (int)c2; }
    Color FromInt(int i) { return (Color)i; }
}
""");
        Assert.True(result.Success);
        // (int)c2 → c2.getValue() → 数字枚举即 number → c2；
        // (Color)i → Color.fromValue((int)(i)) → (i) as Color
        Assert.Contains("return c2;", result.GeneratedCode);
        Assert.Contains("return (i) as Color;", result.GeneratedCode);
        Assert.DoesNotContain("getValue()", result.GeneratedCode);
        Assert.DoesNotContain("fromValue", result.GeneratedCode);
        Assert.DoesNotContain("(int)", result.GeneratedCode);
        AssertCompiles(result.GeneratedCode);
    }

    [Fact]
    public void Enum_Implicit_Bitwise_Call_Sites_Are_Rewritten()
    {
        var result = Convert("""
enum E { A, B }
class X
{
    bool IsSet(E e, E f) { return (e & f) == f; }
}
""");
        Assert.True(result.Success);
        // (e & f) == f → (e.ordinal() & f.ordinal()) == f.getValue() → (e & f) == f
        Assert.Contains("(e & f) == f", result.GeneratedCode);
        Assert.DoesNotContain("ordinal()", result.GeneratedCode);
        Assert.DoesNotContain("getValue()", result.GeneratedCode);
        AssertCompiles(result.GeneratedCode);
    }

    [Fact]
    public void Enum_This_Field_Cast_Call_Site_Is_Rewritten()
    {
        var result = Convert("""
enum E { A = 1 }
class X
{
    E e = E.A;
    int ToInt() { return (int)this.e; }
}
""");
        Assert.True(result.Success);
        Assert.Contains("return this.e;", result.GeneratedCode);
        Assert.DoesNotContain("getValue()", result.GeneratedCode);
        AssertCompiles(result.GeneratedCode);
    }

    // ─── [Flags] 枚举 → const 对象 + 联合类型 ──────────────────────────

    [Fact]
    public void Flags_Enum_Emits_Const_Object_And_Union_Type()
    {
        var result = Convert("""
[System.Flags]
enum Style { None = 0, Bold = 1, Italic = 2 }
""");
        Assert.True(result.Success);
        Assert.Contains("export const Style = {", result.GeneratedCode);
        Assert.Contains("None: 0", result.GeneratedCode);
        Assert.Contains("Bold: 1", result.GeneratedCode);
        Assert.Contains("Italic: 2", result.GeneratedCode);
        Assert.Contains("as const;", result.GeneratedCode);
        Assert.Contains("export type Style = (typeof Style)[keyof typeof Style];", result.GeneratedCode);
        // 不发射 class 形态与死代码辅助方法
        Assert.DoesNotContain("export class Style", result.GeneratedCode);
        Assert.DoesNotContain("public static", result.GeneratedCode);
        AssertCompiles(result.GeneratedCode);
    }

    [Fact]
    public void Flags_Enum_Usage_And_HasFlag_Compile()
    {
        var result = Convert("""
[System.Flags]
enum Style { None = 0, Bold = 1, Italic = 2 }
class X
{
    Style s = Style.Bold | Style.Italic;
    bool Has(Style f) { return this.s.HasFlag(f); }
}
""");
        Assert.True(result.Success);
        // 位运算直接使用；HasFlag 已被共享管线内联为位运算
        Assert.Contains("Style.Bold | Style.Italic", result.GeneratedCode);
        Assert.Contains("(this.s & f) != 0", result.GeneratedCode);
        Assert.DoesNotContain("public static has", result.GeneratedCode);
        AssertCompiles(result.GeneratedCode);
    }

    // ─── 复杂枚举 API → 诊断 + 兼容库桩（M4） ──────────────────────────

    [Fact]
    public void Enum_Parse_Emits_Diagnostic()
    {
        var result = Convert("""
enum Color { Red = 10, Green = 20 }
class X
{
    Color P(string s) { return (Color)System.Enum.Parse(typeof(Color), s); }
}
""");
        Assert.True(result.Success);
        // EnumHelper.parse 无 TS 对应：诊断 + 兼容库桩（M4 Task 29-35）
        AssertDiagnostic(result, "Enum.Parse");
    }

    [Fact]
    public void Enum_ToString_Emits_Diagnostic()
    {
        var result = Convert("""
enum Color { Red = 10 }
class X
{
    string S(Color c) { return c.ToString(); }
}
""");
        Assert.True(result.Success);
        // 数字枚举 .toString() 返回 "10" 而非 "Red"（C# 语义）：诊断（M4 CsEnum.getName 桩）
        AssertDiagnostic(result, "ToString");
    }

    // ─── IR 级：结构化调用点分支（真实管线走 raw，结构化分支兜底） ──────

    [Fact]
    public void Structured_Getter_Value_Call_Is_Rewritten()
    {
        var en = new JavaEnumDeclaration { Name = "E", Modifiers = JavaModifiers.Public };
        en.Values.Add("A(1)");
        var cls = new JavaClassDeclaration { Name = "X", Modifiers = JavaModifiers.Public };
        cls.Methods.Add(new JavaMethodDeclaration
        {
            Name = "read",
            ReturnType = "int",
            Modifiers = JavaModifiers.Public,
            Parameters = { new JavaParameter("E", "e") },
            StructuredBody = new JavaMethodBody(
            [
                new JavaExpressionStatement(new JavaMethodCallExpression
                {
                    Target = new JavaIdentifierExpression("e"),
                    MethodName = "getValue",
                }),
            ]),
        });
        var unit = new JavaCompilationUnit();
        unit.TypeDeclarations.Add(en);
        unit.TypeDeclarations.Add(cls);

        var rewriter = new TsEnumCallRewriter();
        rewriter.VisitCompilationUnit(unit);

        var expr = Assert.IsType<JavaExpressionStatement>(cls.Methods.Single().StructuredBody!.Statements[0]).Expression;
        Assert.IsType<JavaIdentifierExpression>(expr);
        Assert.Equal("e", ((JavaIdentifierExpression)expr).Name);
    }

    [Fact]
    public void Structured_From_Value_Call_Is_Rewritten()
    {
        var en = new JavaEnumDeclaration { Name = "E", Modifiers = JavaModifiers.Public };
        en.Values.Add("A(1)");
        var cls = new JavaClassDeclaration { Name = "X", Modifiers = JavaModifiers.Public };
        cls.Methods.Add(new JavaMethodDeclaration
        {
            Name = "fromInt",
            ReturnType = "E",
            Modifiers = JavaModifiers.Public,
            Parameters = { new JavaParameter("int", "i") },
            StructuredBody = new JavaMethodBody(
            [
                new JavaExpressionStatement(new JavaMethodCallExpression
                {
                    Target = new JavaIdentifierExpression("E"),
                    MethodName = "fromValue",
                    Arguments = { new JavaCastExpression { Type = "int", Expression = new JavaIdentifierExpression("i") } },
                }),
            ]),
        });
        var unit = new JavaCompilationUnit();
        unit.TypeDeclarations.Add(en);
        unit.TypeDeclarations.Add(cls);

        var rewriter = new TsEnumCallRewriter();
        rewriter.VisitCompilationUnit(unit);

        var expr = Assert.IsType<JavaExpressionStatement>(cls.Methods.Single().StructuredBody!.Statements[0]).Expression;
        var raw = Assert.IsType<JavaRawExpression>(expr);
        Assert.Equal("(i) as E", raw.Code);
    }
}
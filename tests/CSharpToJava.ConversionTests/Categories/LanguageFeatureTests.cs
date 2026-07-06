using Xunit;

namespace CSharpToJava.ConversionTests.Categories;

/// <summary>
/// Verifies correct conversion of miscellaneous C# language features: yield return, lock, nameof, typeof,
/// sizeof, attributes, static classes, goto-based switch, nested classes, object methods, const, object initializers.
/// </summary>
public class LanguageFeatureTests : ConversionTestBase
{
    [Fact]
    public void YieldReturn_ConvertsToYieldList()
    {
        var result = Convert("class C { public System.Collections.Generic.IEnumerable<int> M() { yield return 1; yield return 2; } }");
        AssertConversion(result,
            "import io.github.ningpp.compat.CSharpGenericIterable;",
            "public CSharpGenericIterable<Integer> m() {",
            "CSharpList<Integer> _yieldResult = new CSharpList<Integer>();",
            "_yieldResult.add(1);",
            "_yieldResult.add(2);",
            "return _yieldResult;");
    }

    [Fact]
    public void LockStatement_ConvertsToSynchronized()
    {
        var result = Convert("class C { private readonly object l = new object(); public void M() { lock (l) { var x = 1; } } }");
        AssertConversion(result, "private Object l = new Object();", "synchronized (l) {", "var x = 1;");
    }

    [Fact]
    public void NameOf_ConvertsToLiteral()
    {
        var result = Convert("class C { public int X; public string N() => nameof(X); }");
        AssertConversion(result, "public String n() { return \"X\"; }");
        AssertJavaDoesNotContain(result, "nameof(", "C# 'nameof' must resolve to a literal");
    }

    [Fact]
    public void TypeOf_ConvertsToClassLiteral()
    {
        var result = Convert("class C { public System.Type T() => typeof(int); }");
        AssertConversion(result, "public Class t() { return int.class; }");
    }

    [Fact]
    public void SizeOf_ConvertsToConstant()
    {
        var result = Convert("class C { public int S() => sizeof(int); }");
        AssertConversion(result, "public int s() { return 4; }");
    }

    [Fact]
    public void ObsoleteAttribute_Dropped()
    {
        var result = Convert("public class C { [System.Obsolete] public void M() { } }");
        AssertConversion(result, "public void m() {");
        AssertJavaDoesNotContain(result, "Obsolete", "C# [Obsolete] attribute must be dropped");
    }

    [Fact]
    public void AttributeOnField_Dropped()
    {
        var result = Convert("public class C { [System.Serializable] public int V; }");
        AssertConversion(result, "public int V;");
        AssertJavaDoesNotContain(result, "Serializable");
    }

    [Fact]
    public void StaticClass_ConvertsToFinalClass()
    {
        var result = Convert("public static class S { public static int V; public static void M() { } }");
        AssertConversion(result, "public final class S {", "private S() {", "public static int V;", "public static void m() {");
    }

    [Fact]
    public void GotoSwitch_ConvertsToStateMachine()
    {
        var result = Convert("class C { public void M(int x) { switch(x) { case 1: goto case 2; case 2: break; } } }");
        AssertConversion(result, "_switch1State", "_switch1Loop", "continue _switch1Loop");
    }

    [Fact]
    public void NestedClass_ConvertsToStaticNested()
    {
        var result = Convert("class C { public class Inner { public int V; } }");
        AssertConversion(result, "public static class Inner {", "public int V;");
    }

    [Fact]
    public void ObjectMethods_ConvertsToJavaEquivalents()
    {
        var result = Convert("class C { public string S() => ToString(); public bool E(object o) => Equals(o); public int H() => GetHashCode(); public System.Type G() => GetType(); }");
        AssertConversion(result,
            "public String s() { return toString(); }",
            "public boolean e(Object o) { return equals(o); }",
            "public int h() { return hashCode(); }",
            "public Class g() { return getClass(); }");
    }

    [Fact]
    public void ConstField_ConvertsToStaticFinal()
    {
        var result = Convert("class C { public const int Max = 10; }");
        AssertConversion(result, "public static final int Max = 10;");
    }

    [Fact]
    public void ObjectInitializer_ConvertsToTempVariable()
    {
        var result = Convert("class C { public int X; public int Y; public void M() { var c = new C { X = 1, Y = 2 }; } }");
        AssertConversion(result, "var _obj1 = new C();", "_obj1.X = 1;", "_obj1.Y = 2;", "var c = _obj1;");
    }
}

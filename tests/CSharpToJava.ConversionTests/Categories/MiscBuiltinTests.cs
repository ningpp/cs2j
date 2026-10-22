using Xunit;

namespace CSharpToJava.ConversionTests.Categories;

/// <summary>
/// Verifies correct conversion of miscellaneous C# built-in members: object.Equals, string interpolation
/// (with/without format), string.Empty, and string.Format.
/// </summary>
public class MiscBuiltinTests : ConversionTestBase
{
    [Fact]
    public void CharWidenedToStringHexFormat_ReboxesIntForStringFormat()
    {
        // `((int)c).ToString("X4")`: the redundant char→int cast is elided in
        // rendering, but String.format's varargs would box the char back to
        // Character ("x != java.lang.Character"). The (int) box must be restored.
        var result = Convert("class C { public string M(char c) { return ((int)c).ToString(\"X4\"); } }");
        AssertConversion(result, "String.format(\"%04X\", (int)((c)))");
    }

    [Fact]
    public void GenericHelperReturningTypeParameter_IsCastToInferredType()
    {
        // regex-sdk DebuggerAttributes: `pr.GetCustomAttribute<T>().State` — the
        // compat helper returns its own type parameter, which Java infers as
        // Object unless the converter restores the inferred type argument.
        var result = Convert(@"
using System.Reflection;
class MyAttr : System.Attribute { public int State; }
class C {
    public int F(PropertyInfo p) => p.GetCustomAttribute<MyAttr>().State;
}");
        AssertSuccess(result);
        AssertJavaContains(result, "CustomAttributeExtensions.getCustomAttribute(MyAttr.class, p)");
    }

    [Fact]
    public void MappedStaticProperty_InArrayInitializer_UsesCompatConstant()
    {
        var result = Convert(
            "using System; class C { public object[] F() => new object[] { TimeSpan.Zero }; }");
        AssertSuccess(result);
        AssertJavaContains(result, "CSharpTimeSpan.ZERO");
    }

    [Fact]
    public void ThreadCurrentThread_MapsToJavaCurrentThread()
    {
        var result = Convert(
            "using System.Threading; using System.Globalization; class C { public CultureInfo F() => Thread.CurrentThread.CurrentCulture; }");
        AssertSuccess(result);
        AssertJavaContains(result, "ThreadHelper.currentThread().getCurrentCulture()");
    }

    [Fact]
    public void TypeGenericTypeArguments_UsesCompatHelper()
    {
        var result = Convert(
            "class C { public int F(System.Type t) => t.GenericTypeArguments.Length; }");
        AssertSuccess(result);
        AssertJavaContains(result, "TypeHelper.getGenericTypeArguments(t)");
    }

    [Fact]
    public void StringInterpolationMulti_ConvertsToConcatenation()
    {
        var result = Convert("class C { public string M(int a, int b) { return $\"{a}-{b}\"; } }");
        AssertConversion(result, "return a + \"-\" + b;");
    }

    [Fact]
    public void StringInterpolationFormat_ConvertsToFormatWithTodo()
    {
        var result = Convert("class C { public string M(int x) { return $\"{x:000}\"; } }");
        AssertConversion(result, "return String.format(\"%s /* TODO: format specifier 000 */\", x);");
    }

    [Fact]
    public void StringInterpolationNoCSharpResidue()
    {
        var result = Convert("class C { public string M(int a) { return $\"v={a}\"; } }");
        AssertNoCSharpResidue(result);
        AssertJavaContains(result, "v=\" + a");
    }

    [Fact]
    public void ObjectEqualsStaticNoCSharpResidue()
    {
        var result = Convert("class C { public bool M(object a, object b) { return object.Equals(a, b); } }");
        AssertJavaDoesNotContain(result, "object.Equals", "C# 'object.Equals' must be rewritten");
    }

}

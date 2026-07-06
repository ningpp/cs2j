using Xunit;

namespace CSharpToJava.ConversionTests.Categories;

/// <summary>
/// Verifies correct conversion of C# interfaces (methods, properties, events, inheritance, default methods,
/// explicit implementations) to Java interfaces/classes.
/// </summary>
public class InterfaceTests : ConversionTestBase
{
    [Fact]
    public void SimpleInterface_ConvertsToJavaInterface()
    {
        var result = Convert("public interface I { void M(); int P { get; set; } }");
        AssertConversion(result,
            "public interface I {",
            "void m();",
            "public int getP();",
            "public void setP(int value);");
    }

    [Fact]
    public void InterfaceInheritance_ConvertsToExtends()
    {
        var result = Convert("public interface I : IB { void M(); } public interface IB { void N(); }");
        AssertConversion(result, "public interface I extends IB {", "void m();", "public interface IB {", "void n();");
    }

    [Fact]
    public void InterfaceDefaultMethod_ConvertsToDefault()
    {
        var result = Convert("public interface I { void M() { var x = 1; } }");
        AssertConversion(result, "default void m() {", "var x = 1;");
    }

    [Fact]
    public void InterfaceEvent_ConvertsToListenerMethods()
    {
        var result = Convert("public interface I { event System.Action E; }");
        AssertConversion(result,
            "import java.util.concurrent.CopyOnWriteArrayList;",
            "public void addEListener(Runnable handler);",
            "public void removeEListener(Runnable handler);");
    }

    [Fact]
    public void InterfaceImplementation_ConvertsToImplements()
    {
        var result = Convert("public class C : I { public void M() { } public int P { get; set; } } public interface I { void M(); int P { get; set; } }");
        AssertConversion(result,
            "public class C implements I {",
            "private int p;",
            "public void m() {",
            "public int getP() {",
            "public void setP(int value) {");
    }

    [Fact]
    public void GenericInterface_ConvertsToGenericJavaInterface()
    {
        var result = Convert("public interface I<T> { void M(T x); }");
        AssertConversion(result, "public interface I<T> {", "void m(T x);");
    }

    [Fact]
    public void InterfaceMethodOnly_ConvertsToMethod()
    {
        var result = Convert("public interface I { int Compute(int x); }");
        AssertConversion(result, "int compute(int x);");
    }

    [Fact]
    public void InterfaceWithProperty_ConvertsToGetterSetter()
    {
        var result = Convert("public interface I { string Name { get; set; } }");
        AssertConversion(result, "public String getName();", "public void setName(String value);");
    }

    [Fact]
    public void MultipleInterfaceInheritance_ConvertsToExtends()
    {
        var result = Convert("public interface I : IA, IB { void M(); } public interface IA { void A(); } public interface IB { void B(); }");
        AssertConversion(result, "public interface I extends IA, IB {", "void m();", "void a();", "void b();");
    }
}

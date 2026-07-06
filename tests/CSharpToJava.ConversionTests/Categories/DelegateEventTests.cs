using Xunit;

namespace CSharpToJava.ConversionTests.Categories;

/// <summary>
/// Verifies correct conversion of C# delegates, lambdas, Func/Action, and events to Java functional types.
/// </summary>
public class DelegateEventTests : ConversionTestBase
{
    [Fact]
    public void ActionLambda_ConvertsToRunnable()
    {
        var result = Convert("class C { public void M() { System.Action a = () => { }; } }");
        AssertConversion(result, "Runnable a = () -> {");
    }

    [Fact]
    public void FuncLambda_ConvertsToSupplier()
    {
        var result = Convert("class C { public void M() { System.Func<int> f = () => 1; } }");
        AssertConversion(result, "import java.util.function.Supplier;", "Supplier<Integer> f = () -> 1;");
    }

    [Fact]
    public void ActionWithParamLambda_ConvertsToConsumer()
    {
        var result = Convert("class C { public void M() { System.Action<int> a = x => { }; } }");
        AssertConversion(result, "import java.util.function.Consumer;", "Consumer<Integer> a = x -> {");
    }

    [Fact]
    public void FuncWithParamLambda_ConvertsToFunction()
    {
        var result = Convert("class C { public void M() { System.Func<int,int> f = x => { return x; }; } }");
        AssertConversion(result, "import java.util.function.Function;", "Function<Integer, Integer> f = x -> {");
    }

    [Fact]
    public void LambdaInMethod_ConvertsToArrow()
    {
        var result = Convert("class C { public void M() { var f = (int x) => x + 1; } }");
        AssertConversion(result, "var f = (Integer x) -> x + 1;");
    }

    [Fact]
    public void DelegateType_ConvertsToFunctionalInterface()
    {
        var result = Convert("public delegate int D(int x); class C { public D Handler; }");
        AssertConversion(result,
            "@FunctionalInterface public interface D {",
            "int apply(int x);",
            "default Object getTarget() {",
            "public D Handler;");
    }

    [Fact]
    public void EventField_ConvertsToListenerList()
    {
        var result = Convert("class C { public event System.Action E; public void Raise() { E?.Invoke(); } }");
        AssertConversion(result,
            "import java.util.concurrent.CopyOnWriteArrayList;",
            "private java.util.concurrent.CopyOnWriteArrayList<Runnable> _eListeners",
            "public void addEListener(Runnable handler) {",
            "public void removeEListener(Runnable handler) {",
            "protected void fireE() {",
            "if (fireE != null) { fireE.run(); }");
    }

    [Fact]
    public void EventInvocation_ConvertsToFireCall()
    {
        var result = Convert("class C { public event System.Action E; public void Raise() { E?.Invoke(); } }");
        AssertJavaContains(result, "fireE");
        AssertJavaDoesNotContain(result, "?.Invoke()", "C# event '?.Invoke()' must be rewritten");
    }

    [Fact]
    public void LambdaNoCSharpResidue()
    {
        var result = Convert("class C { public void M() { System.Action a = () => { System.Console.WriteLine(1); }; } }");
        AssertNoCSharpResidue(result);
        AssertConversion(result, "Runnable a = () -> {");
    }
}

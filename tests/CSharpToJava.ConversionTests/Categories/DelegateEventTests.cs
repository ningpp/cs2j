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

    [Fact]
    public void EventHandlerGenericEvent_UsesBiConsumerListenerType()
    {
        var result = Convert(
            "using System;" +
            "class C {" +
            "  public event EventHandler<EventArgs> E;" +
            "  public void Raise() { E?.Invoke(this, EventArgs.Empty); }" +
            "}");
        // EventHandler<T> is a 2-parameter void delegate (sender, e) → must map to BiConsumer,
        // not to a single-arg Consumer (which would make the fire call `accept(sender, args)` fail).
        AssertConversion(result,
            "private java.util.concurrent.CopyOnWriteArrayList<BiConsumer<Object, Object>> _eListeners",
            "public void addEListener(BiConsumer<Object, Object> handler) {",
            "protected void fireE(Object sender, Object args) {");
        AssertJavaDoesNotContain(result, "addEListener(Consumer<",
            "EventHandler<T> (2-parameter void delegate) must map to BiConsumer, not Consumer");
    }

    [Fact]
    public void EventHandlerWithCustomArgs_UsesBiConsumerListenerType()
    {
        var result = Convert(
            "using System;" +
            "class MyArgs : EventArgs { public int V; }" +
            "class C {" +
            "  public event EventHandler<MyArgs> E;" +
            "  public void Raise() { E?.Invoke(this, new MyArgs()); }" +
            "}");
        // A custom event-args type keeps the 2-parameter (sender, args) shape → BiConsumer,
        // and the add/remove parameter must match the fire body's 2-argument accept(...) call.
        AssertConversion(result,
            "private java.util.concurrent.CopyOnWriteArrayList<BiConsumer<Object, MyArgs>> _eListeners",
            "public void addEListener(BiConsumer<Object, MyArgs> handler) {");
        AssertJavaDoesNotContain(result, "addEListener(Consumer<",
            "EventHandler<T> must map to BiConsumer even with a custom event-args type");
    }

    [Fact]
    public void EventSubscribe_MethodGroup_BecomesLambda()
    {
        var result = Convert(
            "using System;" +
            "class MyArgs : EventArgs { public int V; }" +
            "class C {" +
            "  public event EventHandler<MyArgs> E;" +
            "}" +
            "class D {" +
            "  private void Handler(object sender, MyArgs args) {}" +
            "  public void M(C c) { c.E += Handler; }" +
            "}");
        // Subscribing a method group to an event must produce a valid Java functional-interface
        // expression (lambda / method reference), never a bare method name which is invalid Java.
        AssertConversion(result, "c.addEListener((sender, args) -> handler(sender, args));");
    }

    [Fact]
    public void EventSubscribe_SameClassMethodGroup_BecomesLambda()
    {
        var result = Convert(
            "using System;" +
            "class MyArgs : EventArgs { public int V; }" +
            "class C {" +
            "  public event EventHandler<MyArgs> E;" +
            "  private void Handler(object sender, MyArgs args) {}" +
            "  public void M() { E += Handler; }" +
            "}");
        AssertConversion(result, "_eListeners.add((sender, args) -> handler(sender, args));");
    }
}

using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class DelegateTransformerTests
{

    // -----------------------------------------------------------------------
    // Full delegate declaration conversion
    // -----------------------------------------------------------------------

    [Fact]
    public void VoidNoParam_Delegate_EmitsRunMethod()
    {
        // delegate void MyCallback(); → interface with void run()
        var result = Convert(@"
public delegate void MyCallback();

public class Sample
{
    public MyCallback Field;
}");

        Assert.True(result.Success);
        Assert.Contains("@FunctionalInterface", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("interface MyCallback", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("void run()", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void VoidWithParams_Delegate_EmitsAcceptMethod()
    {
        // delegate void MyHandler(int x); → interface with void accept(int x)
        var result = Convert(@"
public delegate void MyHandler(int x);

public class Sample
{
    public MyHandler Field;
}");

        Assert.True(result.Success);
        Assert.Contains("@FunctionalInterface", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("interface MyHandler", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("void accept(int x)", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void NonVoidNoParam_Delegate_EmitsGetMethod()
    {
        // delegate int MySupplier(); → interface with int get()
        var result = Convert(@"
public delegate int MySupplier();

public class Sample
{
    public MySupplier Field;
}");

        Assert.True(result.Success);
        Assert.Contains("@FunctionalInterface", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("interface MySupplier", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("int get()", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void NonVoidWithParams_Delegate_EmitsApplyMethod()
    {
        // delegate int MyFunc(string s); → interface with int apply(String s)
        var result = Convert(@"
public delegate int MyFunc(string s);

public class Sample
{
    public MyFunc Field;
}");

        Assert.True(result.Success);
        Assert.Contains("@FunctionalInterface", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("interface MyFunc", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("int apply(String s)", result.GeneratedCode, StringComparison.Ordinal);
    }

    // -----------------------------------------------------------------------
    // Nested delegate — enclosing type parameter filtering
    // -----------------------------------------------------------------------

    [Fact]
    public void NestedDelegate_OnlyIncludesUsedEnclosingTypeParams()
    {
        // delegate int MyLambdaFunc(string x) inside Outer<T> should NOT have <T>,
        // because T is not referenced in the delegate signature.
        var result = Convert(@"
public class Outer<T>
{
    public delegate int MyLambdaFunc(string x);

    public MyLambdaFunc Field;
}");

        Assert.True(result.Success);
        // The interface declaration must NOT have <T> since T is not used in the signature.
        // The field type reference may still include <T> from Roslyn, but the interface itself should not.
        Assert.Contains("interface MyLambdaFunc {", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void NestedDelegate_IncludesUsedEnclosingTypeParam()
    {
        // delegate T MyProducer() inside Container<T> SHOULD have <T>
        var result = Convert(@"
public class Container<T>
{
    public delegate T MyProducer();

    public MyProducer Field;
}");

        Assert.True(result.Success);
        Assert.Contains("MyProducer<T>", result.GeneratedCode, StringComparison.Ordinal);
    }

    // -----------------------------------------------------------------------
    // Generic delegates
    // -----------------------------------------------------------------------

    [Fact]
    public void GenericDelegate_EmitsTypeParameters()
    {
        var result = Convert(@"
public delegate TResult MyPipeline<T, TResult>(T input);

public class Sample
{
    public MyPipeline<int, string> Field;
}");

        Assert.True(result.Success);
        Assert.Contains("interface MyPipeline<T, TResult>", result.GeneratedCode, StringComparison.Ordinal);
    }

    private static ConversionResult Convert(string sourceCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = sourceCode,
            FileName = "Sample.cs",
            Options = new ConversionOptions(),
        });
    }
}

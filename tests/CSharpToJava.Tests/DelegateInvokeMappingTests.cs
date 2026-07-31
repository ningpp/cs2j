using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that C# delegate .Invoke() is mapped to the correct Java SAM method name
/// instead of being lowercased to "invoke" (which doesn't exist on Java functional interfaces).
/// </summary>
public class DelegateInvokeMappingTests
{
    [Fact]
    public void DelegateInvoke_VoidNoArgs_MappedToRun()
    {
        var result = Convert(@"
using System;

class Test
{
    Action handler;
    void M()
    {
        handler.Invoke();
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.DoesNotContain(".invoke()", result.GeneratedCode);
        Assert.DoesNotContain(".Invoke()", result.GeneratedCode);
        Assert.Contains(".run()", result.GeneratedCode);
    }

    [Fact]
    public void DelegateInvoke_VoidWithArgs_MappedToAccept()
    {
        var result = Convert(@"
using System;

class Test
{
    Action<int> handler;
    void M()
    {
        handler.Invoke(42);
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.DoesNotContain(".invoke(", result.GeneratedCode);
        Assert.DoesNotContain(".Invoke(", result.GeneratedCode);
        Assert.Contains(".accept(42)", result.GeneratedCode);
    }

    [Fact]
    public void DelegateInvoke_ActionWithoutArgs_MappedToRun()
    {
        var result = Convert(@"
using System;

class Test
{
    Func<int, string> converter;
    void M()
    {
        var result = converter.Invoke(42);
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.DoesNotContain(".invoke(", result.GeneratedCode);
        Assert.DoesNotContain(".Invoke(", result.GeneratedCode);
        Assert.Contains(".apply(42)", result.GeneratedCode);
    }

    [Fact]
    public void DelegateInvoke_BooleanFunc_MappedToPredicateTest()
    {
        var result = Convert(@"
using System;

class Test
{
    Func<int, bool> predicate;
    void M()
    {
        var result = predicate(42);
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("Predicate<Integer> predicate", result.GeneratedCode);
        Assert.DoesNotContain("predicate.apply(42)", result.GeneratedCode);
        Assert.Contains("predicate.test(42)", result.GeneratedCode);
    }

    [Fact]
    public void DelegateInvoke_BooleanFuncWithTwoInputs_MappedToBiPredicateTest()
    {
        var result = Convert(@"
using System;

class Test
{
    Func<int, int, bool> predicate;
    void M()
    {
        var result = predicate(1, 2);
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("BiPredicate<Integer, Integer> predicate", result.GeneratedCode);
        Assert.DoesNotContain("predicate.apply(1, 2)", result.GeneratedCode);
        Assert.Contains("predicate.test(1, 2)", result.GeneratedCode);
    }

    [Fact]
    public void CustomDelegate_Invoke_MappedToCorrectSAM()
    {
        var result = Convert(@"
delegate void MyCallback();

class Test
{
    MyCallback callback;
    void M()
    {
        callback.Invoke();
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.DoesNotContain(".invoke()", result.GeneratedCode);
        Assert.DoesNotContain(".Invoke()", result.GeneratedCode);
        Assert.Contains(".run()", result.GeneratedCode);
    }

    /// <summary>
    /// Conditional access ?.Invoke() should also map to the correct SAM method.
    /// The conditional access handler in StatementTransformer.ExpressionAndReturn.cs must detect
    /// "Invoke" and map it, not emit it verbatim.
    /// </summary>
    [Fact]
    public void ConditionalAccess_DelegateInvoke_MappedToAccept()
    {
        var result = Convert(@"
using System;

class Test
{
    Action<int, int, int> handler;
    void M()
    {
        handler?.Invoke(1, 2, 3);
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.DoesNotContain(".Invoke(", result.GeneratedCode);
        Assert.DoesNotContain(".invoke(", result.GeneratedCode);
        Assert.Contains(".accept(", result.GeneratedCode);
    }

    [Fact]
    public void InterfaceInvoke_MethodNamedInvoke_PreservedAsInvoke()
    {
        var result = Convert(@"
interface IFunction {
    object Invoke(object context, object[] args, object doc);
}

class Test {
    IFunction function;
    object M(object context, object[] args, object doc) {
        return function.Invoke(context, args, doc);
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        // Interface methods named Invoke must keep Java camelCase name invoke, not be rewritten to apply.
        Assert.Contains(".invoke(", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain(".apply(", result.GeneratedCode, StringComparison.Ordinal);
    }

    private static ConversionResult Convert(string sourceCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = sourceCode,
            FileName = "Test.cs",
            Options = new ConversionOptions(),
        });
    }
}

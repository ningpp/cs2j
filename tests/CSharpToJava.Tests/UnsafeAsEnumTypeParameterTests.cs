using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// System.Runtime.CompilerServices.Unsafe.As&lt;int, T&gt;(ref int value) where "T : Enum"
/// must not be emitted as "(T)(Object)(Integer)(...)": javac compiles the (T) cast to a
/// checkcast against T's erasure (java.lang.Enum), which throws ClassCastException for
/// the boxed primitive. Regression: EnumConverter&lt;T&gt;.Read returned (T) from JSON
/// numbers — every enum property deserialization failed with Integer → Enum CCE.
/// The generic Unsafe.as(Object) overload erases to Object, so no checkcast is emitted.
/// </summary>
public class UnsafeAsEnumTypeParameterTests
{
    [Fact]
    public void UnsafeAsWithEnumTypeParameter_UsesGenericUncheckedCast()
    {
        var result = Convert(@"
using System;
using System.Runtime.CompilerServices;

class Sample<T> where T : Enum
{
    T Read(int raw)
    {
        return Unsafe.As<int, T>(ref raw);
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        var code = result.GeneratedCode ?? "";

        // The value must be CONVERTED to an enum constant (EnumHelper.toObject through
        // the generated fromValue/_UNMAPPED machinery), not cast — the (T) cast would
        // compile to checkcast Enum and throw CCE for the boxed primitive.
        Assert.Contains("EnumHelper.toObject(", code, StringComparison.Ordinal);
        Assert.DoesNotContain("(T)(Object)(Integer)", code, StringComparison.Ordinal);
        Assert.DoesNotContain("(T)(Object)", code, StringComparison.Ordinal);
    }

    [Fact]
    public void UnsafeAsWithUnconstrainedTypeParameter_KeepsBoxingCastChain()
    {
        var result = Convert(@"
using System;
using System.Runtime.CompilerServices;

class Sample<T>
{
    T Read(int raw)
    {
        return Unsafe.As<int, T>(ref raw);
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        var code = result.GeneratedCode ?? "";

        // Unconstrained T erases to Object: the (T)(Object) chain is a no-op cast
        // and the boxing wrapper must be kept so the runtime payload survives.
        Assert.Contains("(T)(Object)(Integer)", code, StringComparison.Ordinal);
    }

    [Fact]
    public void UnsafeAsWithConcreteTarget_KeepsBoxingCastChain()
    {
        var result = Convert(@"
using System;
using System.Runtime.CompilerServices;

class Sample
{
    uint Read(int raw)
    {
        return Unsafe.As<int, uint>(ref raw);
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        var code = result.GeneratedCode ?? "";

        // Concrete numeric targets keep the boxing chain so the unsigned value survives.
        Assert.Contains("(Integer)(Unsafe.as(", code, StringComparison.Ordinal);
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

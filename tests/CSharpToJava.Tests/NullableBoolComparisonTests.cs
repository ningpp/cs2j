using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that nullable bool comparisons (bool? == true / bool? != false) are
/// emitted null-safely. Reproduces the NPE in generated
/// IEnumerableConverterFactoryHelpers.isNonGenericStackOrQueue:
/// `(stackType != null ? stackType.isAssignableFrom(type) : null) == true`
/// unboxes the null Boolean and throws NPE for every type when the
/// System.Collections.NonGeneric assembly is not resolvable.
/// </summary>
public class NullableBoolComparisonTests
{
    [Fact]
    public void NullConditionalBool_ComparedToTrue_IsNullSafe()
    {
        var result = Convert("""
class Holder
{
    public bool? Flag;

    public bool M()
    {
        return this.Flag == true;
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        var code = result.GeneratedCode ?? "";

        Assert.Contains("Objects.equals", code);
        Assert.Contains("Boolean.TRUE", code);
        // The direct `expr == true` on a nullable value would NPE when null.
        Assert.DoesNotContain(": null) == true", code);
    }

    [Fact]
    public void NullConditionalBool_ComparedToFalse_IsNullSafe()
    {
        var result = Convert("""
class Holder
{
    public bool? Flag;

    public bool M()
    {
        return this.Flag != false;
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        var code = result.GeneratedCode ?? "";

        Assert.Contains("Objects.equals", code);
        Assert.Contains("Boolean.FALSE", code);
        Assert.DoesNotContain(": null) != false", code);
    }

    [Fact]
    public void NullConditionalInvocationBool_ComparedToTrue_IsNullSafe()
    {
        var result = Convert("""
using System;

class TypeCheck
{
    public static bool IsMatch(Type type)
    {
        Type? stackType = Type.GetType("System.Collections.Stack, System.Collections.NonGeneric");
        return stackType?.IsAssignableFrom(type) == true;
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        var code = result.GeneratedCode ?? "";

        // Must not emit `(stackType != null ? stackType.isAssignableFrom(type) : null) == true`
        // which unboxes a null Boolean and throws NPE.
        Assert.Contains("Objects.equals", code);
        Assert.DoesNotContain(": null) == true", code);
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

using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Regression: readonly structs converted to Java value classes require every
/// instance field to be final (implicitly), so a field that is conditionally
/// assigned in a constructor (e.g. <c>if (capacity &gt; 0) _array = ...;</c>)
/// cannot be emitted as a value class. The converter must fall back to a
/// regular class and drop final from such fields instead of producing code
/// javac rejects with "variable might already have been assigned" /
/// "variable might not have been initialized".
/// </summary>
public class ReadonlyStructConditionalFieldAssignmentTests
{
    [Fact]
    public void ReadonlyStruct_ConditionalFieldAssignment_FallsBackToRegularClass()
    {
        var result = Convert("""
readonly struct S
{
    private readonly int[] _a;
    private readonly int _b;

    public S(int x)
    {
        _b = x;
        if (x > 0)
        {
            _a = new int[x];
        }
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)) + "\n---Generated---\n" + result.GeneratedCode);
        var code = result.GeneratedCode ?? "";

        // value class fields are implicitly final; a conditionally-assigned field
        // must instead fall back to a plain class with a non-final field.
        Assert.DoesNotContain("value class", code, StringComparison.Ordinal);
        Assert.Contains("private int[] _a;", code, StringComparison.Ordinal);
        Assert.DoesNotContain("private final int[] _a;", code, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadonlyStruct_ThisChainWithRuntimeClass_FallsBackToRegularClass()
    {
        var result = Convert("""
readonly struct ArrayBuilder<T>
{
    private readonly T[] _array;
    private readonly int _count;

    public ArrayBuilder(int capacity) : this()
    {
        if (capacity > 0)
        {
            _array = new T[capacity];
        }
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)) + "\n---Generated---\n" + result.GeneratedCode);
        var code = result.GeneratedCode ?? "";

        // The this() chain inlines the default constructor's `_array = null`
        // before the conditional assignment; a value class cannot carry a
        // non-final field, so fall back to a regular class.
        Assert.DoesNotContain("value class", code, StringComparison.Ordinal);
        Assert.Contains("private T[] _array;", code, StringComparison.Ordinal);
        Assert.DoesNotContain("private final T[] _array;", code, StringComparison.Ordinal);
    }

    private static ConversionResult Convert(string sourceCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = sourceCode,
            FileName = "Sample.cs",
            Options = new ConversionOptions
            {
                UseValueClass = true,
            },
        });
    }
}

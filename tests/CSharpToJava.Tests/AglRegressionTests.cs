using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Regression tests for bugs found when converting the agl-master (GraphLayout) project.
/// Each test reproduces a specific conversion defect and verifies the fix.
/// </summary>
public class AglRegressionTests
{

    // ------------------------------------------------------------------
    // Bug 2: default(T) for class-level type parameter returns null
    //        when a runtime Class token is available
    //
    // GenericBinaryHeapPriorityQueue<T> has a Class<T> field (tClass)
    // added by the converter. The _cs2jDefault_T() factory method
    // should use DefaultValue.ofClass(tClass) to return the correct
    // zero value for primitive wrappers (e.g., 0 for Integer).
    // ------------------------------------------------------------------
    [Fact]
    public void DefaultFactoryMethod_UsesRuntimeClassToken_WhenAvailable()
    {
        var result = Convert("""
using System;
using System.Collections.Generic;

public class GenericBinaryHeapPriorityQueue<T>
{
    const int InitialHeapCapacity = 16;
    T[] A;
    int heapSize;

    public GenericBinaryHeapPriorityQueue() { }

    public T Peek(out double priority)
    {
        if (heapSize == 0)
        {
            priority = 0.0;
            return default(T);
        }
        priority = 0.0;
        return default(T);
    }
}
""");

        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        var code = result.GeneratedCode!;

        // The _cs2jDefault_T() method should use DefaultValue.ofClass(tClass)
        // instead of DefaultValue.of() when a runtime class token is available.
        Assert.Contains("_cs2jDefault_T", code);
        Assert.Contains("DefaultValue.ofClass(tClass)", code);
        Assert.DoesNotContain("DefaultValue.of()", code);
    }

    [Fact]
    public void LinqFirst_WithPredicate_EmptyCollection_ThrowsIllegalStateException()
    {
        var result = Convert("""
using System;
using System.Collections.Generic;
using System.Linq;

public class Demo
{
    public string FirstMatch(List<string> items)
    {
        return items.First(x => x.Length > 0);
    }
}
""");

        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        var code = result.GeneratedCode!;

        // First(predicate) should throw IllegalStateException, not NoSuchElementException
        Assert.Contains("IllegalStateException", code);
        Assert.DoesNotContain("orElseThrow()", code);
    }

    // ------------------------------------------------------------------
    // Bug 5: default(T) rewritten to 'default' literal by GenericPreprocessor
    //        causes RuntimeClassParameterHelper to miss the type parameter,
    //        leading to DefaultValue.of() (null) instead of DefaultValue.ofClass().
    //
    // GenericPreprocessor rewrites `return default(T);` to `return default;`
    // (C# 7.1+ bare literal) in safe contexts. AddTypeOfDefaultRequirements
    // only scanned DefaultExpressionSyntax (default(T)), not the bare literal,
    // so the class-level type parameter T was not registered for a runtime
    // Class field. AddDefaultFactoryMethods then generated
    //   protected T _cs2jDefault_T() { return DefaultValue.of(); }
    // which returns null, causing NPE when T=Integer is unboxed.
    //
    // Fix: AddTypeOfDefaultRequirements now also scans DefaultLiteralExpression
    // nodes, inferring the target type from the semantic model's ConvertedType.
    // ------------------------------------------------------------------
    [Fact]
    public void DefaultLiteral_AfterGenericPreprocess_StillRegistersRuntimeClassField()
    {
        // This source uses bare 'default' literal — exactly what GenericPreprocessor
        // produces when rewriting default(T) in safe (return) contexts.
        var result = Convert("""
using System;
using System.Collections.Generic;

public class GenericBinaryHeapPriorityQueue<T>
{
    const int InitialHeapCapacity = 16;
    T[] A;
    int heapSize;

    public GenericBinaryHeapPriorityQueue() { }

    public T Peek(out double priority)
    {
        if (heapSize == 0)
        {
            priority = 0.0;
            return default;
        }
        priority = 0.0;
        return default;
    }
}
""");

        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        var code = result.GeneratedCode!;

        // The _cs2jDefault_T() method should use DefaultValue.ofClass(tClass)
        // instead of DefaultValue.of() when a runtime class token is available.
        Assert.Contains("_cs2jDefault_T", code);
        Assert.Contains("DefaultValue.ofClass(tClass)", code);
        Assert.DoesNotContain("DefaultValue.of()", code);
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

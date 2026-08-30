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
    // Bug 1: AddToMap with same key/value types swaps key and value arguments
    //
    // When Dictionary<string, HashSet<string>> is used with
    // AddToMap<string, string, HashSet<string>>(dict, "fruits", "apple"),
    // both key and value are string, so the key-identification logic
    // in TryGenerateAddToMapInline matches BOTH parameters as keys,
    // causing the value parameter to be treated as the key and vice versa.
    // ------------------------------------------------------------------
    [Fact]
    public void AddToMap_SameKeyTypeAndValueType_ParametersNotSwapped()
    {
        var result = Convert("""
using System.Collections.Generic;

public static class CollectionUtilities
{
    public static void AddToMap<TS, T, TC>(Dictionary<T, TC> dictionary, T key, TS value)
        where TC : ICollection<TS>, new()
    {
        TC tc;
        if (!dictionary.TryGetValue(key, out tc))
            dictionary[key] = tc = new TC();
        tc.Add(value);
    }
}

public class Demo
{
    public void Build()
    {
        var dict = new Dictionary<string, HashSet<string>>();
        CollectionUtilities.AddToMap<string, string, HashSet<string>>(dict, "fruits", "apple");
    }
}
""");

        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        var code = result.GeneratedCode!;

        // The key "fruits" should appear in dict.get("fruits"), not dict.get("apple")
        Assert.Contains(".get(\"fruits\")", code);
        Assert.Contains(".add(\"apple\")", code);
        // Make sure key/value are NOT swapped
        Assert.DoesNotContain(".get(\"apple\")", code);
        Assert.DoesNotContain(".add(\"fruits\")", code);
    }

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

    // ------------------------------------------------------------------
    // Bug 3: CSharpList.get(index) throws IndexOutOfBoundsException
    //        instead of ArgumentOutOfRangeException on out-of-bounds access
    //
    // C# List<T>[index] throws ArgumentOutOfRangeException, which cs2j
    // maps to the compat ArgumentOutOfRangeException class.
    // Java's ArrayList.get(index) throws IndexOutOfBoundsException.
    // CSharpList should override get() to throw ArgumentOutOfRangeException.
    // ------------------------------------------------------------------
    // This is a compat library fix, tested via the Java compat test suite.

    // ------------------------------------------------------------------
    // Bug 4: LINQ First() on empty collection throws NoSuchElementException
    //        instead of IllegalStateException (cs2j's mapping of InvalidOperationException)
    //
    // C# Enumerable.First() throws InvalidOperationException on empty source,
    // which cs2j maps to IllegalStateException.
    // The current conversion uses findFirst().orElseThrow() which throws
    // NoSuchElementException. It should use
    // findFirst().orElseThrow(() -> new IllegalStateException())
    // ------------------------------------------------------------------
    [Fact]
    public void LinqFirst_EmptyCollection_ThrowsIllegalStateException()
    {
        var result = Convert("""
using System;
using System.Collections.Generic;
using System.Linq;

public class Demo
{
    public string FirstItem(List<string> items)
    {
        return items.First();
    }
}
""");

        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        var code = result.GeneratedCode!;

        // First() should throw IllegalStateException, not NoSuchElementException
        Assert.Contains("IllegalStateException", code);
        Assert.DoesNotContain("orElseThrow()", code);
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

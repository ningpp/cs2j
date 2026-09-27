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

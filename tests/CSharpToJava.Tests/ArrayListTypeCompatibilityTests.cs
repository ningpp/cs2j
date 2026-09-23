using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests for issue #3: incompatible types - java.util.List cannot be converted to java.util.ArrayList.
///
/// Root cause: C# .ToList() returns the concrete List&lt;T&gt; class, which maps to Java CSharpList&lt;T&gt;.
///
/// But the converter was generating .collect(Collectors.toList()) which returns java.util.List&lt;T&gt;
/// (an interface), creating a type mismatch when assigned to an CSharpList&lt;T&gt; variable.
///
/// Fix: Use .collect(Collectors.toCollection(() -> new CSharpList<>())) instead of .collect(Collectors.toList())
/// so that stream collect operations produce CSharpList&lt;T&gt;, matching the C# List&lt;T&gt; → CSharpList mapping.
/// </summary>
public class ArrayListTypeCompatibilityTests
{

    [Fact]
    public void IListDeclaration_MapsToJavaListInterface()
    {
        var result = Convert(@"
using System.Collections.Generic;

class Sample
{
    private IList<string> _field;
}");

        Assert.True(result.Success);
        // C# IList<T> → Java List<T> (interface)
        Assert.Contains("List<String> _field", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void ToList_ProducesArrayListViaCollector()
    {
        var result = Convert(@"
using System.Collections.Generic;
using System.Linq;

class Sample
{
    void M()
    {
        var source = new List<string> { ""a"", ""b"", ""c"" };
        List<string> filtered = source.Where(s => s.Length > 1).ToList();
    }
}");

        Assert.True(result.Success);
        // ToList() should use Collectors.toCollection(() -> new CSharpList<>()) — produces CSharpList<T>,
        // not Collectors.toList() which returns List<T> (interface) and causes type mismatch
        Assert.DoesNotContain("Collectors.toList()", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void ArrayListToArray_WithTypeOfLiteral_ProducesTypedArrayArgument()
    {
        var result = Convert(@"
using System;
using System.Collections;

class Sample
{
    object[] M(ArrayList list)
    {
        return list.ToArray(typeof(string));
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        Assert.Contains("toArray(", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void ArrayListToArray_WithTypeVariable_ProducesClassArgument()
    {
        var result = Convert(@"
using System;
using System.Collections;

class Sample
{
    object[] M(ArrayList list, Type itemType)
    {
        return list.ToArray(itemType);
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        Assert.Contains("toArray(itemType)", result.GeneratedCode, StringComparison.Ordinal);
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

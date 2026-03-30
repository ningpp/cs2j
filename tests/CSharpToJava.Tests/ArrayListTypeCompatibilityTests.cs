using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests for issue #3: incompatible types - java.util.List cannot be converted to java.util.ArrayList.
/// C# allows assigning IList&lt;T&gt; (or IReadOnlyList&lt;T&gt;) to a List&lt;T&gt; variable, but in Java
/// List&lt;T&gt; (interface) is not assignable to ArrayList&lt;T&gt; (concrete class).
/// The converter must wrap such assignments with new ArrayList&lt;&gt;(...).
/// </summary>
public class ArrayListTypeCompatibilityTests
{
    [Fact]
    public void LocalDeclaration_WrapsWithNewArrayList_WhenAssignedFromIListMethod()
    {
        var result = Convert(@"
using System.Collections.Generic;

class Sample
{
    IList<string> GetItems() { return new List<string>(); }

    void M()
    {
        List<string> items = GetItems();
    }
}");

        Assert.True(result.Success);
        Assert.Contains("new ArrayList<>(getItems())", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void LocalDeclaration_WrapsWithNewArrayList_WhenAssignedFromIReadOnlyListMethod()
    {
        var result = Convert(@"
using System.Collections.Generic;

class Sample
{
    IReadOnlyList<string> GetItems() { return new List<string>(); }

    void M()
    {
        List<string> items = GetItems();
    }
}");

        Assert.True(result.Success);
        Assert.Contains("new ArrayList<>(getItems())", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void Assignment_WrapsWithNewArrayList_WhenAssignedFromIListMethod()
    {
        var result = Convert(@"
using System.Collections.Generic;

class Sample
{
    IList<string> GetItems() { return new List<string>(); }

    void M()
    {
        List<string> items = new List<string>();
        items = GetItems();
    }
}");

        Assert.True(result.Success);
        Assert.Contains("items = new ArrayList<>(getItems())", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void FieldAssignment_WrapsWithNewArrayList_WhenAssignedFromIListMethod()
    {
        var result = Convert(@"
using System.Collections.Generic;

class Sample
{
    private List<string> _field;
    IList<string> GetItems() { return new List<string>(); }

    void M()
    {
        _field = GetItems();
    }
}");

        Assert.True(result.Success);
        Assert.Contains("_field = new ArrayList<>(getItems())", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void LocalDeclaration_DoesNotDoubleWrap_WhenAlreadyNewArrayList()
    {
        var result = Convert(@"
using System.Collections.Generic;

class Sample
{
    void M()
    {
        List<string> items = new List<string>();
    }
}");

        Assert.True(result.Success);
        // Should not contain double-wrapping like new ArrayList<>(new ArrayList<>())
        Assert.DoesNotContain("new ArrayList<>(new ArrayList<>", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void LocalDeclaration_DoesNotDoubleWrap_WhenManuallyWrappedInConstructor()
    {
        var result = Convert(@"
using System.Collections.Generic;

class Sample
{
    IList<string> GetItems() { return new List<string>(); }

    void M()
    {
        // Developer explicitly constructs List<string> from IList<string> - should not double-wrap
        List<string> items = new List<string>(GetItems());
    }
}");

        Assert.True(result.Success);
        // Should produce new ArrayList<>(getItems()) or new ArrayList<>(new ArrayList<>(getItems()))
        // but NOT new ArrayList<>(new ArrayList<>(new ArrayList<>(getItems())))
        Assert.DoesNotContain("new ArrayList<>(new ArrayList<>(new ArrayList<>", result.GeneratedCode, StringComparison.Ordinal);
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

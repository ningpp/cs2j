using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests for issue #3: incompatible types - java.util.List cannot be converted to java.util.ArrayList.
/// Root cause: C# List&lt;T&gt; was mapped to Java ArrayList (concrete class) for type declarations,
/// while IList&lt;T&gt; / IReadOnlyList&lt;T&gt; mapped to List (interface). This created type mismatches.
/// Fix: Map C# List&lt;T&gt; to Java List (interface) for type references, and use ArrayList only
/// for object instantiation (new expressions).
/// </summary>
public class ArrayListTypeCompatibilityTests
{
    [Fact]
    public void ListDeclaration_MapsToJavaListInterface_NotArrayList()
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
        // Variable type should be List<String> (Java interface), not ArrayList<String>
        Assert.Contains("List<String> items = new ArrayList<String>()", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void NewListExpression_MapsToNewArrayList()
    {
        var result = Convert(@"
using System.Collections.Generic;

class Sample
{
    List<string> GetList()
    {
        return new List<string>();
    }
}");

        Assert.True(result.Success);
        // new List<T>() must map to new ArrayList<T>() (List is an interface in Java)
        Assert.Contains("new ArrayList<String>()", result.GeneratedCode, StringComparison.Ordinal);
        // Return type should be List<String> (interface)
        Assert.Contains("List<String> getList()", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void LocalDeclaration_AssignableFromIListMethod_NoWrapperNeeded()
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
        // With root cause fix, both sides are List<String> — no wrapping needed
        Assert.DoesNotContain("new ArrayList<>(getItems())", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("getItems()", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void LocalDeclaration_AssignableFromIReadOnlyListMethod_NoWrapperNeeded()
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
        // With root cause fix, both sides are List<String> — no wrapping needed
        Assert.DoesNotContain("new ArrayList<>(getItems())", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("getItems()", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void FieldAssignment_AssignableFromIListMethod_NoWrapperNeeded()
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
        // Field type is List<String>, method returns List<String> — compatible
        Assert.Contains("_field = getItems()", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("new ArrayList<>(getItems())", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void FieldDeclaration_MapsToJavaListInterface()
    {
        var result = Convert(@"
using System.Collections.Generic;

class Sample
{
    private List<string> _field;
}");

        Assert.True(result.Success);
        // Field type should be List<String>, not ArrayList<String>
        Assert.Contains("List<String> _field", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void CollectionInitializer_UsesArrayListForInstantiation()
    {
        var result = Convert(@"
using System.Collections.Generic;

class Sample
{
    void M()
    {
        List<string> items = new List<string> { ""a"", ""b"" };
    }
}");

        Assert.True(result.Success);
        // Instantiation should use ArrayList, even though the type reference is List
        Assert.Contains("new ArrayList<String>", result.GeneratedCode, StringComparison.Ordinal);
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

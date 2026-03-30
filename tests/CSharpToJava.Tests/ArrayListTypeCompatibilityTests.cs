using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests for issue #3: incompatible types - java.util.List cannot be converted to java.util.ArrayList.
///
/// Root cause: C# .ToList() returns the concrete List&lt;T&gt; class, which maps to Java ArrayList&lt;T&gt;.
/// But the converter was generating .collect(Collectors.toList()) which returns java.util.List&lt;T&gt;
/// (an interface), creating a type mismatch when assigned to an ArrayList&lt;T&gt; variable.
///
/// Fix: Use .collect(Collectors.toCollection(ArrayList::new)) instead of .collect(Collectors.toList())
/// so that stream collect operations produce ArrayList&lt;T&gt;, matching the C# List&lt;T&gt; → ArrayList mapping.
/// </summary>
public class ArrayListTypeCompatibilityTests
{
    [Fact]
    public void ListDeclaration_MapsToArrayList()
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
        // C# List<T> maps to Java ArrayList<T> (concrete class)
        Assert.Contains("new ArrayList<String>()", result.GeneratedCode, StringComparison.Ordinal);
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
        // new List<T>() → new ArrayList<T>()
        Assert.Contains("new ArrayList<String>()", result.GeneratedCode, StringComparison.Ordinal);
        // Return type: C# List<T> → Java ArrayList<T>
        Assert.Contains("ArrayList<String> getList()", result.GeneratedCode, StringComparison.Ordinal);
    }

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
    public void FieldDeclaration_ListMapsToArrayList()
    {
        var result = Convert(@"
using System.Collections.Generic;

class Sample
{
    private List<string> _field;
}");

        Assert.True(result.Success);
        // C# List<T> → Java ArrayList<T> (concrete class)
        Assert.Contains("ArrayList<String> _field", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void CollectionInitializer_UsesArrayList()
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
        // Instantiation uses ArrayList
        Assert.Contains("new ArrayList<String>", result.GeneratedCode, StringComparison.Ordinal);
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
        // ToList() should use Collectors.toCollection(ArrayList::new) — produces ArrayList<T>,
        // not Collectors.toList() which returns List<T> (interface) and causes type mismatch
        Assert.Contains("Collectors.toCollection(ArrayList::new)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("Collectors.toList()", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void MethodReturningList_AssignedToListVariable_NoTypeMismatch()
    {
        var result = Convert(@"
using System.Collections.Generic;

class Sample
{
    List<string> GetItems() { return new List<string>(); }

    void M()
    {
        List<string> items = GetItems();
    }
}");

        Assert.True(result.Success);
        // Both sides are ArrayList<String> — no wrapping needed
        Assert.Contains("getItems()", result.GeneratedCode, StringComparison.Ordinal);
        // Method return type is ArrayList<String>
        Assert.Contains("ArrayList<String> getItems()", result.GeneratedCode, StringComparison.Ordinal);
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

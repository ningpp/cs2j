using Xunit;

namespace CSharpToJava.ConversionTests.Categories;

/// <summary>
/// Verifies correct conversion of C# List&lt;T&gt;.Sort() to Java list.sort().
/// </summary>
public class ListSortTests : ConversionTestBase
{
    [Fact]
    public void ListSortNoArgs_ConvertsToInstanceSort()
    {
        var result = Convert(@"
using System.Collections.Generic;
class C {
    public void M(List<object> items) {
        items.Sort();
    }
}");
        AssertConversion(result, "items.sort();");
    }
}

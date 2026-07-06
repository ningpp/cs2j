using System.Collections.Generic;
using CSharpToJava.Core.Pipeline;
using Xunit;

namespace CSharpToJava.ConversionTests.Categories;

/// <summary>
/// Additional verification that C# LINQ operations are rewritten into valid Java (CSharpList-producing
/// procedural helpers or Stream pipelines). Uses the same rewrite-correctness checks as LinqTests.
/// </summary>
public class LinqBulkTests : ConversionTestBase
{
    private static void AssertLinqConverted(ConversionResult result)
    {
        AssertSuccess(result);
        AssertNoCSharpResidue(result);
        AssertJavaDoesNotContain(result, ".Where(", "C# Where call must be rewritten");
        AssertJavaDoesNotContain(result, ".Select(", "C# Select call must be rewritten");
        AssertJavaDoesNotContain(result, ".ToList()", "C# ToList call must be rewritten");
        AssertJavaContainsAny(result, "CSharpList", "stream()", "_ProceduralLinq");
    }

    public static IEnumerable<object[]> LinqSnippets()
    {
        yield return new object[] { "Where lt", "class C { public System.Collections.Generic.List<int> M(System.Collections.Generic.List<int> l) { return l.Where(x => x < 0).ToList(); } }" };
        yield return new object[] { "Where ge", "class C { public System.Collections.Generic.List<int> M(System.Collections.Generic.List<int> l) { return l.Where(x => x >= 10).ToList(); } }" };
        yield return new object[] { "Select add", "class C { public System.Collections.Generic.List<int> M(System.Collections.Generic.List<int> l) { return l.Select(x => x + 10).ToList(); } }" };
        yield return new object[] { "Select neg", "class C { public System.Collections.Generic.List<int> M(System.Collections.Generic.List<int> l) { return l.Select(x => -x).ToList(); } }" };
        yield return new object[] { "Select square", "class C { public System.Collections.Generic.List<int> M(System.Collections.Generic.List<int> l) { return l.Select(x => x * x).ToList(); } }" };
        yield return new object[] { "Where then Select", "class C { public System.Collections.Generic.List<int> M(System.Collections.Generic.List<int> l) { return l.Where(x => x > 0).Select(x => x * 2).ToList(); } }" };
        yield return new object[] { "Select then Where", "class C { public System.Collections.Generic.List<int> M(System.Collections.Generic.List<int> l) { return l.Select(x => x + 1).Where(x => x > 0).ToList(); } }" };
        yield return new object[] { "OrderBy asc", "class C { public System.Collections.Generic.List<int> M(System.Collections.Generic.List<int> l) { return l.OrderBy(x => x).ToList(); } }" };
        yield return new object[] { "OrderBy desc", "class C { public System.Collections.Generic.List<int> M(System.Collections.Generic.List<int> l) { return l.OrderByDescending(x => x).ToList(); } }" };
        yield return new object[] { "OrderBy thenby", "class C { public System.Collections.Generic.List<int> M(System.Collections.Generic.List<int> l) { return l.OrderBy(x => x % 2).ThenBy(x => x).ToList(); } }" };
        yield return new object[] { "Count pred", "class C { public int M(System.Collections.Generic.List<int> l) { return l.Count(x => x < 0); } }" };
        yield return new object[] { "Any pred", "class C { public bool M(System.Collections.Generic.List<int> l) { return l.Any(x => x < 0); } }" };
        yield return new object[] { "All pred", "class C { public bool M(System.Collections.Generic.List<int> l) { return l.All(x => x > 0); } }" };
        yield return new object[] { "Sum sel", "class C { public int M(System.Collections.Generic.List<int> l) { return l.Sum(x => x + 1); } }" };
        yield return new object[] { "Min sel", "class C { public int M(System.Collections.Generic.List<int> l) { return l.Min(x => x + 1); } }" };
        yield return new object[] { "Max sel", "class C { public int M(System.Collections.Generic.List<int> l) { return l.Max(x => x + 1); } }" };
        yield return new object[] { "First pred", "class C { public int M(System.Collections.Generic.List<int> l) { return l.First(x => x > 5); } }" };
        yield return new object[] { "Last pred", "class C { public int M(System.Collections.Generic.List<int> l) { return l.Last(x => x > 5); } }" };
        yield return new object[] { "Take n", "class C { public System.Collections.Generic.List<int> M(System.Collections.Generic.List<int> l) { return l.Take(5).ToList(); } }" };
        yield return new object[] { "Skip n", "class C { public System.Collections.Generic.List<int> M(System.Collections.Generic.List<int> l) { return l.Skip(3).ToList(); } }" };
        yield return new object[] { "Distinct", "class C { public System.Collections.Generic.List<int> M(System.Collections.Generic.List<int> l) { return l.Distinct().ToList(); } }" };
        yield return new object[] { "Reverse", "class C { public System.Collections.Generic.List<int> M(System.Collections.Generic.List<int> l) { return l.Reverse().ToList(); } }" };
        yield return new object[] { "Contains", "class C { public bool M(System.Collections.Generic.List<int> l) { return l.Contains(7); } }" };
        yield return new object[] { "ElementAt", "class C { public int M(System.Collections.Generic.List<int> l) { return l.ElementAt(2); } }" };
        yield return new object[] { "Aggregate seed", "class C { public int M(System.Collections.Generic.List<int> l) { return l.Aggregate(1, (a, b) => a * b); } }" };
        yield return new object[] { "GroupBy key", "class C { public void M(System.Collections.Generic.List<int> l) { var g = l.GroupBy(x => x % 3); } }" };
        yield return new object[] { "GroupBy select key", "class C { public System.Collections.Generic.List<int> M(System.Collections.Generic.List<int> l) { return l.GroupBy(x => x % 3).Select(g => g.Key).ToList(); } }" };
    }

    [Theory]
    [MemberData(nameof(LinqSnippets))]
    public void LinqOperation_ConvertsToJava(string name, string snippet)
    {
        var result = Convert(snippet);
        AssertLinqConverted(result);
    }
}

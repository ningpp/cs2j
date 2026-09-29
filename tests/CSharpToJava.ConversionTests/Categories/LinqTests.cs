using CSharpToJava.Core.Pipeline;
using Xunit;

namespace CSharpToJava.ConversionTests.Categories;

/// <summary>
/// Verifies correct conversion of C# LINQ query methods to Java (either procedural helper
/// methods or Java Stream API). Each test confirms the C# LINQ call was rewritten into valid Java.
/// </summary>
public class LinqTests : ConversionTestBase
{
    private static void AssertLinqConverted(ConversionResult result)
    {
        // The C# lambda/query arrow must be gone and the LINQ call must be rewritten into
        // either a CSharpList-producing procedural helper or a Java Stream pipeline.
        AssertSuccess(result);
        AssertNoCSharpResidue(result);
        AssertJavaDoesNotContain(result, ".Where(", "C# Where call must be rewritten");
        AssertJavaDoesNotContain(result, ".Select(", "C# Select call must be rewritten");
        AssertJavaDoesNotContain(result, ".ToList()", "C# ToList call must be rewritten");
        AssertJavaContainsAny(result, "CSharpList", "stream()", "_ProceduralLinq");
    }

    public static IEnumerable<object[]> LinqSnippets()
    {
        yield return new object[] { "Where", "class C { public System.Collections.Generic.List<int> M(System.Collections.Generic.List<int> l) { return l.Where(x => x > 0).ToList(); } }" };
        yield return new object[] { "Select", "class C { public System.Collections.Generic.List<int> M(System.Collections.Generic.List<int> l) { return l.Select(x => x * 2).ToList(); } }" };
        yield return new object[] { "SelectMany", "class C { public System.Collections.Generic.List<int> M(System.Collections.Generic.List<System.Collections.Generic.List<int>> l) { return l.SelectMany(x => x).ToList(); } }" };
        yield return new object[] { "Count predicate", "class C { public int M(System.Collections.Generic.List<int> l) { return l.Count(x => x > 0); } }" };
        yield return new object[] { "Any predicate", "class C { public bool M(System.Collections.Generic.List<int> l) { return l.Any(x => x > 0); } }" };
        yield return new object[] { "All", "class C { public bool M(System.Collections.Generic.List<int> l) { return l.All(x => x > 0); } }" };
        yield return new object[] { "First", "class C { public int M(System.Collections.Generic.List<int> l) { return l.First(x => x > 0); } }" };
        yield return new object[] { "FirstOrDefault", "class C { public int M(System.Collections.Generic.List<int> l) { return l.FirstOrDefault(x => x > 0); } }" };
        yield return new object[] { "Last", "class C { public int M(System.Collections.Generic.List<int> l) { return l.Last(x => x > 0); } }" };
        yield return new object[] { "Single", "class C { public int M(System.Collections.Generic.List<int> l) { return l.Single(x => x > 0); } }" };
        yield return new object[] { "Where chained Select", "class C { public System.Collections.Generic.List<int> M(System.Collections.Generic.List<int> l) { return l.Where(x => x > 0).Select(x => x + 1).ToList(); } }" };
        yield return new object[] { "Distinct", "class C { public System.Collections.Generic.List<int> M(System.Collections.Generic.List<int> l) { return l.Distinct().ToList(); } }" };
        yield return new object[] { "Take", "class C { public System.Collections.Generic.List<int> M(System.Collections.Generic.List<int> l) { return l.Take(3).ToList(); } }" };
        yield return new object[] { "Skip", "class C { public System.Collections.Generic.List<int> M(System.Collections.Generic.List<int> l) { return l.Skip(2).ToList(); } }" };
        yield return new object[] { "TakeWhile", "class C { public System.Collections.Generic.List<int> M(System.Collections.Generic.List<int> l) { return l.TakeWhile(x => x > 0).ToList(); } }" };
        yield return new object[] { "SkipWhile", "class C { public System.Collections.Generic.List<int> M(System.Collections.Generic.List<int> l) { return l.SkipWhile(x => x > 0).ToList(); } }" };
        yield return new object[] { "GroupBy", "class C { public void M(System.Collections.Generic.List<int> l) { var g = l.GroupBy(x => x % 2); } }" };
        yield return new object[] { "GroupBy select", "class C { public System.Collections.Generic.List<int> M(System.Collections.Generic.List<int> l) { return l.GroupBy(x => x % 2).Select(g => g.Key).ToList(); } }" };
        yield return new object[] { "Aggregate seed", "class C { public int M(System.Collections.Generic.List<int> l) { return l.Aggregate(0, (a, b) => a + b); } }" };
        yield return new object[] { "Aggregate no seed", "class C { public int M(System.Collections.Generic.List<int> l) { return l.Aggregate((a, b) => a + b); } }" };
        yield return new object[] { "Sum selector", "class C { public int M(System.Collections.Generic.List<int> l) { return l.Sum(x => x * 2); } }" };
        yield return new object[] { "Where on strings", "class C { public System.Collections.Generic.List<string> M(System.Collections.Generic.List<string> l) { return l.Where(s => s.Length > 0).ToList(); } }" };
        yield return new object[] { "Select to string", "class C { public System.Collections.Generic.List<string> M(System.Collections.Generic.List<int> l) { return l.Select(x => x.ToString()).ToList(); } }" };
        yield return new object[] { "DefaultIfEmpty", "class C { public System.Collections.Generic.List<int> M(System.Collections.Generic.List<int> l) { return l.DefaultIfEmpty().ToList(); } }" };
        yield return new object[] { "Zip", "class C { public System.Collections.Generic.List<int> M(System.Collections.Generic.List<int> a, System.Collections.Generic.List<int> b) { return a.Zip(b, (x, y) => x + y).ToList(); } }" };
        yield return new object[] { "Where nested", "class C { public System.Collections.Generic.List<int> M(System.Collections.Generic.List<int> l) { return l.Where(x => x > 0).Where(x => x < 10).ToList(); } }" };
        yield return new object[] { "Select many nested", "class C { public System.Collections.Generic.List<int> M(System.Collections.Generic.List<System.Collections.Generic.List<int>> l) { return l.SelectMany(x => x.Where(y => y > 0)).ToList(); } }" };
        yield return new object[] { "Distinct count", "class C { public int M(System.Collections.Generic.List<int> l) { return l.Distinct().Count(); } }" };
        yield return new object[] { "Sum of where", "class C { public int M(System.Collections.Generic.List<int> l) { return l.Where(x => x > 0).Sum(); } }" };
        yield return new object[] { "Select with index", "class C { public System.Collections.Generic.List<int> M(System.Collections.Generic.List<int> l) { return l.Select((x, i) => x + i).ToList(); } }" };
        yield return new object[] { "GroupBy count", "class C { public int M(System.Collections.Generic.List<int> l) { return l.GroupBy(x => x).Count(); } }" };
        yield return new object[] { "SequenceEqual", "class C { public bool M(System.Collections.Generic.List<int> a, System.Collections.Generic.List<int> b) { return a.SequenceEqual(b); } }" };
        yield return new object[] { "Take count", "class C { public int M(System.Collections.Generic.List<int> l) { return l.Take(5).Count(); } }" };
    }

    [Fact]
    public void Where_ProducesProceduralHelper()
    {
        var result = Convert("class C { public System.Collections.Generic.List<int> M(System.Collections.Generic.List<int> l) { return l.Where(x => x > 0).ToList(); } }");
        AssertConversion(result, "m_ProceduralLinq", "throw new ArgumentNullException()");
    }

    [Fact]
    public void LinqWithStringInterpolationInSelector()
    {
        var result = Convert("class C { public System.Collections.Generic.List<string> M(System.Collections.Generic.List<int> l) { return l.Select(x => $\"v={x}\").ToList(); } }");
        AssertLinqConverted(result);
        AssertJavaDoesNotContain(result, "$\"");
    }

    [Fact]
    public void LinqQuerySyntax_FromWhereSelect()
    {
        var result = Convert("using System.Linq; class C { public System.Collections.Generic.List<int> M(System.Collections.Generic.List<int> l) { return (from x in l where x > 0 select x * 2).ToList(); } }");
        AssertLinqConverted(result);
    }

    [Fact]
    public void LinqQuerySyntax_GroupBy()
    {
        var result = Convert("using System.Linq; class C { public void M(System.Collections.Generic.List<int> l) { var g = from x in l group x by x % 2; } }");
        AssertLinqConverted(result);
    }

    [Fact]
    public void LinqOnArray_Where()
    {
        var result = Convert("class C { public int[] M(int[] a) { return System.Linq.Enumerable.Where(a, x => x > 0).ToArray(); } }");
        AssertSuccess(result);
        AssertNoCSharpResidue(result);
    }

}

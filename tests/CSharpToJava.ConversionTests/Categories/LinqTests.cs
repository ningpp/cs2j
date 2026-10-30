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

    /// <summary>
    /// Enumerable.Range materializes its compat source with
    /// <c>IntStream.range(...).collect(...)</c>, which is nested inside the
    /// StreamSupport.stream argument of the Select pipeline. The ToArray terminal
    /// must still recognize the Select receiver as a Stream and call
    /// <c>Stream.toArray(...)</c> on it, not wrap it in
    /// <c>StreamSupport.stream((Iterable) …)</c>: the raw cast compiles but fails
    /// at runtime with
    /// <c>ClassCastException: ReferencePipeline cannot be cast to Iterable</c>.
    /// </summary>
    [Fact]
    public void StreamPath_RangeSelectToArray_DoesNotCastStreamToIterable()
    {
        var result = ConvertStream(
            "using System.Linq; class C { char[] M() { return Enumerable.Range(0, 100).Select(i => (char)i).ToArray(); } }");

        AssertSuccess(result);
        AssertNoCSharpResidue(result);
        AssertJavaDoesNotContain(
            result, "(Iterable) (StreamSupport.stream",
            "The Select pipeline must stay a Stream for its ToArray terminal.");
        AssertJavaDoesNotContain(
            result, "spliterator(), false).toArray(Character[]::new)",
            "ToArray must be called directly on the Select Stream, not on a re-wrapped copy.");
        // The Range seed itself must still materialize its compat iterable.
        AssertJavaContains(result, "CSharpList.toCSharpList()");
        AssertJavaContains(result, ".map(");
        AssertJavaContains(result, ".toArray(Character[]::new)");
        AssertBalancedDelimiters(result.GeneratedCode);
    }

    public static IEnumerable<object[]> RangeStreamSnippets()
    {
        yield return new object[]
        {
            "ToArray",
            "using System.Linq; class C { char[] M() { return Enumerable.Range(0, 100).Select(i => (char)i).ToArray(); } }",
        };
        yield return new object[]
        {
            "ToList",
            "using System.Linq; class C { System.Collections.Generic.List<int> M() { return Enumerable.Range(0, 100).Select(i => i + 1).ToList(); } }",
        };
        yield return new object[]
        {
            "Count predicate",
            "using System.Linq; class C { int M() { return Enumerable.Range(0, 100).Select(i => i + 1).Count(i => i > 10); } }",
        };
        yield return new object[]
        {
            "Where Select ToArray",
            "using System.Linq; class C { int[] M() { return Enumerable.Range(0, 100).Where(i => i % 2 == 0).Select(i => i * 2).ToArray(); } }",
        };
        yield return new object[]
        {
            "String.Concat char array",
            "using System; using System.Linq; class C { string M() { return string.Concat(Enumerable.Range(0, 100).Select(i => (char)i).ToArray()); } }",
        };
    }

    /// <summary>
    /// Regression matrix for stream terminals over an Enumerable.Range chain: every
    /// terminal consumes the previous Stream directly. None of them may re-wrap the
    /// pipeline in an Iterable cast.
    /// </summary>
    [Theory]
    [MemberData(nameof(RangeStreamSnippets))]
    public void StreamPath_RangeChains_KeepStreamsUnwrapped(string shape, string source)
    {
        var result = ConvertStream(source);

        AssertSuccess(result);
        AssertJavaDoesNotContain(
            result, "(Iterable) (StreamSupport.stream",
            $"{shape}: a Stream pipeline must not be cast to Iterable for a terminal operator.");
        AssertJavaContains(result, "CSharpList.toCSharpList()");
        AssertBalancedDelimiters(result.GeneratedCode);
    }

    /// <summary>
    /// A residual foreach over a LINQ chain iterates a Stream, which is not an
    /// Iterable: the enhanced-for source must be materialized before the re-type
    /// cast rather than cast directly (runtime
    /// <c>ClassCastException: ReferencePipeline cannot be cast to Iterable</c>).
    /// </summary>
    [Fact]
    public void StreamPath_ForeachOverRangeSelect_MaterializesStreamBeforeCast()
    {
        var result = ConvertStream(
            "using System.Linq; class C { int M() { int n = 0; foreach (var c in Enumerable.Range(0, 100).Select(i => (char)i)) { n += c; } return n; } }");

        AssertSuccess(result);
        AssertNoCSharpResidue(result);
        AssertJavaDoesNotContain(result, "(Iterable) (StreamSupport.stream",
            "foreach over a Stream must materialize it before the Iterable cast.");
        AssertJavaContains(result, "CSharpGenericIterable.from(StreamSupport.stream");
        AssertJavaContains(result, "CSharpList.toCSharpList()");
        AssertBalancedDelimiters(result.GeneratedCode);
    }

    /// <summary>
    /// Cheap "syntactically plausible" gate: emitted Java must have balanced
    /// (), [], {} outside string/char literals. Catches dropped/mismatched
    /// delimiters from expression rewrites without requiring a JDK.
    /// </summary>
    private static void AssertBalancedDelimiters(string java)
    {
        var expected = new Stack<char>();
        var quote = '\0';
        var escaped = false;

        foreach (var ch in java)
        {
            if (quote != '\0')
            {
                if (escaped)
                    escaped = false;
                else if (ch == '\\')
                    escaped = true;
                else if (ch == quote)
                    quote = '\0';
                continue;
            }

            switch (ch)
            {
                case '"':
                case '\'':
                    quote = ch;
                    break;
                case '(':
                    expected.Push(')');
                    break;
                case '[':
                    expected.Push(']');
                    break;
                case '{':
                    expected.Push('}');
                    break;
                case ')' or ']' or '}':
                    Assert.True(
                        expected.Count > 0 && expected.Pop() == ch,
                        $"Unbalanced '{ch}' in generated Java:\n{java}");
                    break;
            }
        }

        Assert.True(expected.Count == 0,
            $"Unclosed {expected.Count} delimiter(s) in generated Java:\n{java}");
    }

}

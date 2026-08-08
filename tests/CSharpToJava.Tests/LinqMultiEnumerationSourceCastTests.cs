using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit.Abstractions;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests for issue: a C# collection type that implements BOTH a generic list
/// enumeration (IList&lt;T&gt; / IEnumerable&lt;T&gt;) AND IReadOnlyDictionary&lt;K, V&gt;
/// (e.g. the BCL GroupCollection) loses the IEnumerable&lt;T&gt; enumeration in the
/// Java mapping (CSharpReadOnlyDict iterates KeyValuePair&lt;K,V&gt;).
///
/// When such a collection is the source of a LINQ chain rewritten by the LINQ
/// rewriter (e.g. `.Cast&lt;T&gt;()`), the generated call site wraps the collection
/// in CSharpGenericIterable.from(...) with the WRONG element type
/// (KeyValuePair instead of the dictionary's Value type), so javac fails
/// generic type inference:
///   "incompatible types: inferred type variable T has incompatible bounds"
///   (equality constraints: Group; lower bounds: CSharpKeyValuePair&lt;String,Group&gt;)
///
/// Expected Java output: the collection must be adapted via .getValues()
/// (C# GroupCollection.Values enumerates index 0..Count-1, exactly like its
/// IEnumerable&lt;Group&gt; enumeration), which yields a CSharpReadOnlyCollection
/// assignable to CSharpGenericIterable&lt;Group&gt;.
/// </summary>
public class LinqMultiEnumerationSourceCastTests
{
    private readonly ITestOutputHelper _out;

    public LinqMultiEnumerationSourceCastTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void CastOnMultiEnumerationCollection_AdaptsViaGetValues()
    {
        // Mirrors d:\csharp-RegularExpressions\tests\AttRegexTests.cs:
        //   match.Groups.Cast<Group>().Select(g => (start: g.Index, end: g.Index + g.Length))...
        // where GroupCollection implements IList<Group> + IReadOnlyDictionary<string, Group>.
        var result = Convert("""
using System.Collections;
using System.Collections.Generic;
using System.Linq;

class Group
{
    public int Index { get; set; }
    public int Length { get; set; }
}

class GroupCollection : IList<Group>, IReadOnlyDictionary<string, Group>
{
    public int Count => 0;
    public bool IsReadOnly => true;
    public Group this[int index] { get => null; set { } }
    public IEnumerator GetEnumerator() => throw new System.NotImplementedException();
    IEnumerator<Group> IEnumerable<Group>.GetEnumerator() => throw new System.NotImplementedException();
    public void Add(Group item) { }
    public void Clear() { }
    public bool Contains(Group item) => false;
    public void CopyTo(Group[] array, int arrayIndex) { }
    public bool Remove(Group item) { return false; }
    public int IndexOf(Group item) => -1;
    public void Insert(int index, Group item) { }
    public void RemoveAt(int index) { }
    public Group this[string key] => null;
    public IEnumerable<string> Keys => null;
    public IEnumerable<Group> Values => null;
    public bool ContainsKey(string key) => false;
    public bool TryGetValue(string key, out Group value) { value = null; return false; }
}

class Test
{
    static GroupCollection Groups { get; } = null;

    static void M()
    {
        var actual = new HashSet<(int start, int end)>(
            Groups
            .Cast<Group>()
            .Select(g => (start: g.Index, end: g.Index + g.Length))
            .Distinct()
            .OrderBy(g => g.start)
            .ThenBy(g => g.end));
        System.Console.WriteLine(actual);
    }
}
""");

        _out.WriteLine(result.GeneratedCode ?? "FAILED");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";

        // The dictionary-typed source must be adapted via .getValues() so the
        // procedural LINQ parameter CSharpGenericIterable<Group> type-checks.
        Assert.Contains(".getValues()", code);
        Assert.DoesNotContain("CSharpGenericIterable.from(getGroups())", code, StringComparison.Ordinal);
        Assert.Contains("m_ProceduralLinq1(getGroups().getValues())", code, StringComparison.Ordinal);
    }

    [Fact]
    public void DictionarySourceWithKeyCast_AdaptsViaGetKeys()
    {
        var result = Convert("""
using System.Collections;
using System.Collections.Generic;
using System.Linq;

class Group { public int Index { get; set; } }

class GroupCollection : IEnumerable<string>, IReadOnlyDictionary<string, Group>
{
    public Group this[string key] => null;
    public IEnumerable<string> Keys => null;
    public IEnumerable<Group> Values => null;
    public int Count => 0;
    public bool ContainsKey(string key) => false;
    public bool TryGetValue(string key, out Group value) { value = null; return false; }
    public IEnumerator<string> GetEnumerator() => throw new System.NotImplementedException();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => throw new System.NotImplementedException();
}

class Test
{
    static GroupCollection Groups { get; } = null;

    static void M()
    {
        var names = Groups.Cast<string>().Select(s => s.Length).OrderBy(l => l).ToList();
        System.Console.WriteLine(names);
    }
}
""");

        _out.WriteLine(result.GeneratedCode ?? "FAILED");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";
        Assert.Contains(".getKeys()", code);
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

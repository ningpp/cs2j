using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Reproduces the GroupCollection javac failure:
/// CSharpReadOnlyDict&lt;K, V&gt; now declares containsKey(K)/get(K), so the
/// converter must NOT also emit containsKey(Object)/get(Object) bridges for a
/// concrete key type such as String. Those bridges have the same erasure as the
/// typed members and javac reports a name clash.
/// </summary>
public class ReadOnlyDictErasureClashTests
{
    [Fact]
    public void StringKeyedReadOnlyDict_DoesNotEmitObjectBridges()
    {
        var result = Convert("""
using System.Collections;
using System.Collections.Generic;

public class Group { }

public class GroupCollection : IList<Group>, IReadOnlyList<Group>, IReadOnlyDictionary<string, Group>
{
    public int Count => 0;
    public bool IsReadOnly => true;
    public Group this[int index] => null;
    public Group this[string key] => null;
    public bool ContainsKey(string key) => false;
    public IEnumerable<string> Keys => null;
    public IEnumerable<Group> Values => null;
    public bool TryGetValue(string key, out Group value) { value = null; return false; }

    public IEnumerator<Group> GetEnumerator() => null;
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    int IList<Group>.IndexOf(Group item) => -1;
    void IList<Group>.Insert(int index, Group item) { }
    void IList<Group>.RemoveAt(int index) { }
    Group IList<Group>.this[int index] { get => null; set { } }

    void ICollection<Group>.Add(Group item) { }
    void ICollection<Group>.Clear() { }
    bool ICollection<Group>.Contains(Group item) => false;
    void ICollection<Group>.CopyTo(Group[] array, int arrayIndex) { }
    bool ICollection<Group>.Remove(Group item) => false;
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        var code = result.GeneratedCode ?? "";

        // The typed members satisfy CSharpReadOnlyDict<String, Group>; explicit
        // Object overloads would erase-clash with them and break javac.
        Assert.Contains("implements CSharpReadOnlyDict<String, Group>", code, StringComparison.Ordinal);
        Assert.Contains("public boolean containsKey(String key)", code, StringComparison.Ordinal);
        Assert.Contains("public Group get(String key)", code, StringComparison.Ordinal);
        Assert.DoesNotContain("containsKey(Object key)", code, StringComparison.Ordinal);
        Assert.DoesNotContain("Group get(Object key)", code, StringComparison.Ordinal);
    }

    private static ConversionResult Convert(string csharpCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = csharpCode,
            FileName = "Test.cs",
            Options = new ConversionOptions(),
        });
    }
}

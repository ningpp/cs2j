using Xunit;

namespace CSharpToJava.ConversionTests.Categories;

/// <summary>
/// Verifies that the dictionary ENTRY view of GetEnumerator — the overload bound when
/// the receiver's static type is IReadOnlyDictionary&lt;K,V&gt;/IDictionary&lt;K,V&gt; or the
/// receiver is cast to IEnumerable&lt;KeyValuePair&lt;K,V&gt;&gt; — renders as
/// <c>pairIterator()</c> instead of the value-view <c>iterator()</c>. Dual-view generated
/// collections (Regex GroupCollection) expose their VALUE enumerator through the single
/// Java iterator(), so renaming the entry view to iterator() hands KeyValuePair-typed
/// callers Group elements and dies with a ClassCastException at the element cast.
/// </summary>
public class DictEntryGetEnumeratorTests : ConversionTestBase
{
    private const string NameBagSource = """
        using System.Collections;
        using System.Collections.Generic;

        public class NameBag : IReadOnlyDictionary<string, string>
        {
            private readonly List<string> _keys = new List<string>();
            private readonly List<string> _values = new List<string>();

            public int Count => 2;
            public bool IsReadOnly => true;
            public string this[string key] => "x";
            public bool ContainsKey(string key) => true;
            public bool TryGetValue(string key, out string value) { value = "x"; return true; }

            public IEnumerable<string> Keys => _keys;
            public IEnumerable<string> Values => _values;

            public IEnumerator<string> GetEnumerator() => null;
            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
            IEnumerator<KeyValuePair<string, string>> IEnumerable<KeyValuePair<string, string>>.GetEnumerator() => null;

            public void Add(string key, string value) { }
        }

        public static class BagUser
        {
            public static IEnumerator<KeyValuePair<string, string>> ViaCast(NameBag bag)
            {
                return ((IEnumerable<KeyValuePair<string, string>>)bag).GetEnumerator();
            }

            public static IEnumerator<KeyValuePair<string, string>> ViaReadOnlyDictionary(IReadOnlyDictionary<string, string> bag)
            {
                return bag.GetEnumerator();
            }

            public static IEnumerator ViaValueView(NameBag bag)
            {
                return bag.GetEnumerator();
            }
        }
        """;

    [Fact]
    public void EntryViewCastCall_RendersPairIterator()
    {
        var result = Convert(NameBagSource);
        AssertConversion(result);

        // ((IEnumerable<KeyValuePair<K,V>>)bag).GetEnumerator() must call the entry
        // view — and must not wrap the receiver in the fromAnyTyped adapter, which
        // has no pairIterator member.
        AssertJavaContains(result, "return bag.pairIterator();",
            "IEnumerable<KeyValuePair<K,V>>-cast GetEnumerator must render as pairIterator()");
        AssertJavaDoesNotContain(result, "fromAnyTyped",
            "the IEnumerable cast must be unwrapped, not adapterized");
    }

    [Fact]
    public void EntryViewInterfaceCall_RendersPairIterator()
    {
        var result = Convert(NameBagSource);
        AssertConversion(result);

        // IReadOnlyDictionary<K,V>-typed receiver GetEnumerator binds to the same
        // IEnumerator<KeyValuePair<K,V>> overload — entry view as well.
        AssertJavaContains(result, "return bag.pairIterator();",
            "IReadOnlyDictionary receiver GetEnumerator must render as pairIterator()");
    }

    [Fact]
    public void ValueViewCall_KeepsPlainIterator()
    {
        var result = Convert(NameBagSource);
        AssertConversion(result);

        // The public GetEnumerator on the concrete type stays the value view: the
        // well-known GetEnumerator → iterator rename must remain untouched.
        AssertJavaContains(result, "return bag.iterator();",
            "public GetEnumerator must keep the plain iterator() rendering");
        AssertJavaContains(result, "iterator()", "value view must still exist");
    }
}

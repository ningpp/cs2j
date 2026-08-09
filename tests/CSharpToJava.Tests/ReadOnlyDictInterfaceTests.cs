using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class ReadOnlyDictInterfaceTests
{
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

    [Fact]
    public void ClassWithListAndReadOnlyDictionary_DropsListInterfacesAndConformsToDict()
    {
        var result = Convert(@"
using System.Collections;
using System.Collections.Generic;
namespace N {
public class GC : IList<Group>, IReadOnlyList<Group>, IReadOnlyDictionary<string, Group> {
    public IEnumerator GetEnumerator() => new Enumerator(this);
    IEnumerator<Group> IEnumerable<Group>.GetEnumerator() => new Enumerator(this);
    IEnumerator<KeyValuePair<string, Group>> IEnumerable<KeyValuePair<string, Group>>.GetEnumerator() => new Enumerator(this);
    public bool TryGetValue(string key, out Group value) { Group group = this[key]; if (group == null) { value = null; return false; } value = group; return true; }
    public bool ContainsKey(string key) => key.Length > 0;
    public IEnumerable<string> Keys { get { for (int i = 0; i < Count; ++i) yield return GetGroup(i).Name; } }
    public IEnumerable<Group> Values { get { for (int i = 0; i < Count; ++i) yield return GetGroup(i); } }
    public int Count => 3;
    public Group this[int i] => GetGroup(i);
    public Group this[string s] => GetGroup(0);
    Group GetGroup(int i) => null;
    public bool IsReadOnly => true;
    void ICollection<Group>.Add(Group g) {}
    void ICollection<Group>.Clear() {}
    bool ICollection<Group>.Contains(Group g) => false;
    int IList<Group>.IndexOf(Group g) => 0;
    void IList<Group>.Insert(int i, Group g) {}
    bool ICollection<Group>.Remove(Group g) => false;
    void IList<Group>.RemoveAt(int i) {}
    public void CopyTo(Group[] a, int i) {}
    public void CopyTo(System.Array a, int i) {}
    public bool IsFixedSize => true;
    public bool IsSynchronized => true;
    public object SyncRoot => null;
    int IList.Add(object value) => 0;
    bool IList.Contains(object value) => false;
    int IList.IndexOf(object value) => 0;
    void IList.Insert(int index, object value) {}
    void IList.Remove(object value) {}
    void IList.RemoveAt(int index) {}
    object IList.this[int index] { get => GetGroup(index); set {} }
    private sealed class Enumerator {
        private GC _c; private int _i;
        public Enumerator(GC c) { _c = c; _i = -1; }
        public bool MoveNext() { int s = _c.Count; if (_i >= s) return false; _i++; return _i < s; }
        public Group Current => _c[_i];
        Group IEnumerator<KeyValuePair<string, Group>>.Current => new KeyValuePair<string, Group>(_c[_i].Name, _c[_i]);
        object IEnumerator.Current => Current;
        public void Reset() { _i = -1; }
        public void Dispose() {}
    }
}
public class Group { public string Name { get; set; } }
}
");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
        var code = result.GeneratedCode ?? "";

        // The dict interface is kept; the list interfaces that clash with the
        // dictionary's KVP-iterable chain are dropped.
        Assert.Contains("implements CSharpReadOnlyDict<String, Group>", code, StringComparison.Ordinal);
        Assert.DoesNotContain("CSharpGenericIList<Group>", code, StringComparison.Ordinal);
        Assert.DoesNotContain("CSharpReadOnlyList<Group>", code, StringComparison.Ordinal);

        // CSharpReadOnlyDict member contract.
        Assert.Contains("public boolean containsKey(Object key)", code, StringComparison.Ordinal);
        Assert.Contains("public Group get(Object key)", code, StringComparison.Ordinal);
        Assert.Contains("public Group tryGetValue(String key)", code, StringComparison.Ordinal);
        Assert.Contains("public CSharpReadOnlyCollection<String> getKeys()", code, StringComparison.Ordinal);
        Assert.Contains("public CSharpReadOnlyCollection<Group> getValues()", code, StringComparison.Ordinal);

        // The single iterator() satisfies the dictionary's KVP-iterable chain.
        Assert.Contains("CSharpGenericEnumerator<CSharpKeyValuePair<String, Group>> iterator()", code, StringComparison.Ordinal);

        // No bogus `this::compare` erasure-conflict helpers.
        Assert.DoesNotContain("this::compare", code, StringComparison.Ordinal);
    }

    [Fact]
    public void TryGetValueOutVarInt_OnHashtable_CastsGetResult()
    {
        var result = Convert(@"
using System.Collections;
class C {
    void Test(Hashtable table, int key) {
        if (table.TryGetValue(key, out int value)) {
            System.Console.WriteLine(value);
        }
    }
}");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
        var code = result.GeneratedCode ?? "";
        // The out-variable declaration is hoisted before the if (C# out-variable
        // scope covers the whole block); the get() result is cast inside the then-branch.
        Assert.Contains("Integer value = null;", code, StringComparison.Ordinal);
        Assert.Contains("value = (Integer) ", code, StringComparison.Ordinal);
        Assert.Contains(".get(", code, StringComparison.Ordinal);
    }

    [Fact]
    public void DynamicMethodCreateDelegate_UsesInstanceCall()
    {
        var result = Convert(@"
using System;
using System.Reflection.Emit;
class C {
    private DynamicMethod _method;
    private Action<string> _go;
    void Test() {
        _go = _method.CreateDelegate<Action<string>>();
    }
}");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
        var code = result.GeneratedCode ?? "";
        Assert.Contains("_method.createDelegate(", code, StringComparison.Ordinal);
        Assert.DoesNotContain("ReflectionHelper.createDelegate", code, StringComparison.Ordinal);
    }

    [Fact]
    public void VolatileReadWrite_MapToPlainAccess()
    {
        var result = Convert(@"
using System.Threading;
class C {
    long _field;
    long Test(long value) {
        Volatile.Write(ref _field, value);
        return Volatile.Read(ref _field);
    }
}");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
        var code = result.GeneratedCode ?? "";
        Assert.Contains("_field = value;", code, StringComparison.Ordinal);
        Assert.Contains("return _field;", code, StringComparison.Ordinal);
        Assert.DoesNotContain("Volatile", code, StringComparison.Ordinal);
        Assert.DoesNotContain("LongHolder", code, StringComparison.Ordinal);
    }

    [Fact]
    public void ConcurrentDictionaryTryRemoveDiscard_UsesRemove()
    {
        var result = Convert(@"
using System.Collections.Concurrent;
class C {
    ConcurrentDictionary<string, int> _dict = new ConcurrentDictionary<string, int>();
    void Test() {
        _dict.TryRemove(""key"", out _);
    }
}");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
        var code = result.GeneratedCode ?? "";
        Assert.Contains("_dict.remove(\"key\")", code, StringComparison.Ordinal);
        Assert.DoesNotContain("tryRemove", code, StringComparison.Ordinal);
    }

    [Fact]
    public void TryGetValueOutVar_UsedAfterIf_DeclaredBeforeIf()
    {
        var result = Convert(@"
using System.Collections.Generic;
class C {
    RegexHolder _cache = new RegexHolder();
    object Get(string key) {
        if (!_cache.TryGet(key, out object regex)) {
            regex = new object();
            _cache.Add(key, regex);
        }
        return regex;
    }
}
class RegexHolder {
    public bool TryGet(string key, out object value) { value = null; return false; }
    public void Add(string key, object value) {}
}");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
        var code = result.GeneratedCode ?? "";
        var ifIndex = code.IndexOf("if (");
        var declIndex = code.IndexOf("Object regex = ", StringComparison.Ordinal);
        Assert.True(declIndex >= 0 && declIndex < ifIndex,
            $"out-var declaration must precede the if statement. Generated code:\n{code}");
    }
}

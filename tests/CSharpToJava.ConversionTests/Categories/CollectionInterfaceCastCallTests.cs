using Xunit;

namespace CSharpToJava.ConversionTests.Categories;

/// <summary>
/// Verifies that <c>((ICollection&lt;T&gt;)x).Contains(y)</c>-style interface-cast calls
/// bind to the typed overload on the receiver's class instead of the Object-parameter
/// flavor. The compat collection interfaces (CSharpIList&lt;T&gt; and friends) erase every
/// member parameter to Object, so a call through the mapped interface dispatches to the
/// Object flavor even when the C# source selected the generic flavor via the cast.
/// Generated collections whose Object flavor delegates back through that same cast
/// (the standard C# explicit-implementation idiom, e.g. CaptureCollection.Contains)
/// then recurse into themselves and die with a StackOverflowError.
/// </summary>
public class CollectionInterfaceCastCallTests : ConversionTestBase
{
    private const string StringBagSource = """
        using System.Collections;
        using System.Collections.Generic;

        public class StringBag : IList<string>, IList
        {
            public int Count => 1;
            public bool IsReadOnly => true;
            public bool IsSynchronized => false;
            public object SyncRoot => this;
            public bool IsFixedSize => true;

            string IList<string>.this[int index] { get => null; set { } }
            object IList.this[int index] { get => null; set { } }

            public IEnumerator<string> GetEnumerator() => null;
            IEnumerator IEnumerable.GetEnumerator() => null;

            public void Add(string item) { }
            int IList.Add(object value) => 0;
            public void Clear() { }
            bool ICollection<string>.Contains(string item) => ((IList<string>)this).IndexOf(item) >= 0;
            bool IList.Contains(object value) => value is string other && ((ICollection<string>)this).Contains(other);
            int IList<string>.IndexOf(string item) => 0;
            int IList.IndexOf(object value) => value is string other ? ((IList<string>)this).IndexOf(other) : -1;
            public void Insert(int index, string item) { }
            void IList.Insert(int index, object value) { }
            public void RemoveAt(int index) { }
            public bool Remove(string item) => false;
            void IList.Remove(object value) { }
            public void CopyTo(string[] array, int arrayIndex) { }
            void ICollection.CopyTo(Array array, int index) { }
        }
        """;

    [Fact]
    public void InterfaceCastCall_ToTypedMember_DoesNotBindToErasedObjectFlavor()
    {
        var result = Convert(StringBagSource);
        AssertConversion(result);

        // The Object-flavor bridge must reach the typed contains directly — not
        // bounce through the erased CSharpIList interface, which dispatches back
        // into the same contains(Object) and overflows the stack.
        AssertJavaDoesNotContain(result, "((CSharpIList<String>)(this)).contains");
        AssertJavaDoesNotContain(result, "((CSharpIList<String>)(this)).indexOf");
        AssertJavaContains(result,
            "public boolean contains(Object value) { return value instanceof String other && this.contains(other); }");
    }

    [Fact]
    public void InterfaceCastCall_ToTypedIndexOf_ResolvesToTypedOverload()
    {
        var result = Convert(StringBagSource);
        AssertConversion(result);

        // ((IList<string>)this).IndexOf(item) inside the generic Contains and the
        // object-flavor IndexOf bridge must compile to direct typed calls; an
        // interface-bound indexOf would re-enter indexOf(Object) recursively.
        AssertJavaContains(result,
            "public boolean contains(String item) { return this.indexOf(item) >= 0; }");
        AssertJavaContains(result,
            "public int indexOf(Object value) { return (value instanceof String other ? this.indexOf(other) : -1); }");
    }
}

using Xunit;

namespace CSharpToJava.ConversionTests.Categories;

/// <summary>
/// Verifies that a CopyTo call bound to the NON-generic
/// <c>System.Collections.ICollection.CopyTo(System.Array, int)</c> renders the
/// destination through <c>CSharpArray.asArrayView</c>, so the Java call binds to
/// <c>copyTo(CSharpArray, int)</c> and virtually dispatches to the collection's own
/// CopyTo(Array,int) body. C# collections differ there: CaptureCollection/
/// GroupCollection write through Array.SetValue (IndexOutOfRangeException for bad
/// indexes) while List&lt;T&gt;-backed collections validate up front
/// (ArgumentOutOfRangeException) — a single compat default cannot express both.
/// Generic <c>ICollection&lt;T&gt;.CopyTo(T[], int)</c> bindings must keep the plain
/// T[] rendering: their C# implementations validate up front.
/// </summary>
public class NonGenericCollectionCopyToTests : ConversionTestBase
{
    [Fact]
    public void NonGenericCopyTo_RendersArrayViewDispatch()
    {
        const string source = """
            using System;
            using System.Collections;

            public class BagUser
            {
                public static void ViaNonGeneric(ICollection bag, int index)
                {
                    object[] copy = new object[bag.Count + index];
                    bag.CopyTo(copy, index);
                }

                public static void ViaArrayToken(ICollection bag, Array token)
                {
                    bag.CopyTo(token, 0);
                }
            }
            """;
        var result = Convert(source);
        AssertConversion(result);

        // The Java-array destination must be wrapped so the call binds to
        // copyTo(CSharpArray,int) — the virtual dispatch point.
        AssertJavaContains(result, "bag.copyTo(CSharpArray.asArrayView(copy), index);",
            "non-generic ICollection.CopyTo must route the destination through asArrayView");
        AssertJavaContains(result, "bag.copyTo(CSharpArray.asArrayView(token), 0);",
            "a System.Array-typed destination must keep the asArrayView binding");
    }

    [Fact]
    public void GenericCopyTo_KeepsPlainTRendering()
    {
        const string source = """
            using System.Collections.Generic;

            public class BagUser
            {
                public static void ViaGeneric(ICollection<string> bag)
                {
                    string[] copy = new string[bag.Count];
                    bag.CopyTo(copy, 0);
                }
            }
            """;
        var result = Convert(source);
        AssertConversion(result);

        // ICollection<T>.CopyTo(T[], int) validates up front in C# — it must NOT be
        // rerouted to the non-generic dispatch.
        AssertJavaContains(result, "bag.copyTo(copy, 0);",
            "generic ICollection<T>.CopyTo must keep the plain T[] rendering");
        AssertJavaDoesNotContain(result, "asArrayView",
            "the generic binding must not be routed through asArrayView");
    }
}

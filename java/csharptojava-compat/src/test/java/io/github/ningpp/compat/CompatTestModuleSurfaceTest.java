package io.github.ningpp.compat;

import org.junit.jupiter.api.Test;

import java.util.Comparator;
import java.util.List;
import java.util.stream.Collectors;
import java.util.stream.Stream;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertThrows;
import static org.junit.jupiter.api.Assertions.assertTrue;

/** Surface used by converted test modules (MSAGL aglningpptests). */
class CompatTestModuleSurfaceTest {

    @Test
    void assertFormattingOverloads() {
        Assert.isNotNull("value", "expected {0}", "value");
        Assert.isNull(null, "expected null {0}", "here");
        Assert.areEqual(1.0, 1.0001, 0.001, "within {0}", "1e-3");
        Assert.areNotEqual(1.0, 2.0, 0.1);
        Assert.areNotEqual(1.0, 1.5, 0.1, "must differ");
        Assert.areNotEqual(1.0, 1.5, 0.1, "must differ {0}", "always");

        assertThrows(AssertionError.class, () -> Assert.areNotEqual(1.0, 1.0, 0.0));
        assertThrows(AssertionError.class, () -> Assert.isNotNull(null, "v {0}", "x"));
        assertThrows(AssertionError.class, () -> Assert.isNull("v", "v {0}", "x"));
        assertThrows(AssertionError.class, () -> Assert.areEqual(1.0, 2.0, 0.001, "diff {0}", "x"));
        assertThrows(AssertionError.class, () -> Assert.fail("boom {0}", "now"));
    }

    @Test
    void assertInstanceAndThrows() {
        Assert.isInstanceOfType("text", String.class);
        Assert.isInstanceOfType(new Object());
        assertThrows(AssertionError.class, () -> Assert.isInstanceOfType(1, String.class));

        String copy = new String("a");
        Assert.areNotSame("a", copy);
        assertThrows(AssertionError.class, () -> Assert.areNotSame(copy, copy));

        Exception thrown = Assert.throwsException(() -> {
            throw new IllegalStateException("boom");
        });
        assertEquals(IllegalStateException.class, thrown.getClass());
        assertThrows(AssertionError.class, () -> Assert.throwsException(() -> { }));

        assertThrows(AssertionError.class, () -> Assert.inconclusive("skip"));
    }

    @Test
    void stringAssertChecksPrefixSuffixAndContent() {
        StringAssert.startsWith("abc", "ab");
        StringAssert.endsWith("abc", "bc");
        StringAssert.contains("abc", "b");

        assertThrows(AssertionError.class, () -> StringAssert.startsWith("abc", "bc"));
        assertThrows(AssertionError.class, () -> StringAssert.endsWith("abc", "ab"));
        assertThrows(AssertionError.class, () -> StringAssert.contains("abc", "z", "message"));
    }

    @Test
    void collectionAssertComparesMultisetsAndMembership() {
        CollectionAssert.areEquivalent(new int[] {1, 2, 2}, List.of(2, 1, 2));
        CollectionAssert.areEquivalent(List.of("a", "b"), new String[] {"b", "a"});
        assertThrows(AssertionError.class,
            () -> CollectionAssert.areEquivalent(new int[] {1, 1, 2}, new int[] {1, 2, 2}));

        CollectionAssert.contains(List.of(1, 2, 3), 2);
        assertThrows(AssertionError.class, () -> CollectionAssert.contains(List.of(1, 2), 5));

        CollectionAssert.areNotEqual(new int[] {1, 2}, new int[] {2, 1});
        assertThrows(AssertionError.class,
            () -> CollectionAssert.areNotEqual(new int[] {1, 2}, new int[] {1, 2}));
    }

    @Test
    void collectionAssertAreEquivalentAcceptsMessageFormatArguments() {
        // C# AreEquivalent(expected, actual, message, params object[]): arrays are
        // not Iterable, so this binds the (Object, Object, String, Object...) row.
        AssertionError error = assertThrows(AssertionError.class,
            () -> CollectionAssert.areEquivalent(
                new int[] {1, 2}, new int[] {1, 3}, "case {0} mismatch", 7));
        assertTrue(error.getMessage().startsWith("case 7 "), error.getMessage());

        CollectionAssert.areEquivalent(
            new int[] {1, 2}, new int[] {2, 1}, "case {0} ok", 7);
    }

    @Test
    void comparerDefaultAndCreate() {
        Comparator<Integer> natural = Comparer.getDefault();
        assertEquals(-1, natural.compare(1, 2));
        assertEquals(1, natural.compare(3, 2));

        Comparator<String> created = Comparer.create(String::compareTo);
        assertEquals(-1, created.compare("a", "b"));
    }

    @Test
    void csharpListAcceptsNonGenericIList() {
        CSharpIList<Integer> view = CSharpIList.fromAny(new int[] {1, 2, 3});
        CSharpList<Integer> list = new CSharpList<>(view);
        assertEquals(List.of(1, 2, 3), list);
    }

    @Test
    void arrayHelperContainsHandlesPrimitiveArrays() {
        assertTrue(ArrayHelper.contains(new int[] {1, 2, 3}, 2));
        assertFalse(ArrayHelper.contains(new int[] {1, 2, 3}, 9));
        assertFalse(ArrayHelper.contains(null, 1));
        assertTrue(ArrayHelper.contains(new String[] {"a", "b"}, "b"));
    }

    @Test
    void collectorsBuildCompatCollections() {
        CSharpHashSet<Integer> set = Stream.of(1, 2, 2).collect(CSharpHashSet.toCSharpHashSet());
        assertEquals(2, set.getCount());

        CSharpDictionary<String, Integer> map = Stream.of("a", "bb")
            .collect(CSharpDictionary.toCSharpMap(s -> s, String::length));
        assertEquals(2, map.get("bb"));
    }

    @Test
    void indexedFlatMapFeedsIndexAndAcceptsArrays() {
        List<Integer> fromIterable = Stream.of(10, 20)
            .flatMap(new IndexedFlatMapFunction<Integer, Integer>((value, index) -> List.of(value, index)))
            .collect(Collectors.toList());
        assertEquals(List.of(10, 0, 20, 1), fromIterable);

        List<Integer> fromArray = Stream.of(10, 20)
            .flatMap(new IndexedFlatMapFunction<Integer, Integer>((value, index) -> new Integer[] {value, index}))
            .collect(Collectors.toList());
        assertEquals(List.of(10, 0, 20, 1), fromArray);
    }
}

package io.github.ningpp.compat;

import org.junit.jupiter.api.Test;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertTrue;

/**
 * The generated STJ converters port the C# constraint
 * {@code where TCollection : IReadOnlyDictionary<TKey, TValue>} as the Java bound
 * {@code TCollection extends CSharpReadOnlyDict<TKey, TValue>}. .NET's
 * ImmutableDictionary/ImmutableSortedDictionary realize IReadOnlyDictionary, so the
 * erased bridge methods cast the untyped converter entry to CSharpReadOnlyDict — the
 * compat classes must realize the same interface or every immutable-dictionary
 * serialization dies in the bridge (ClassCastException at the converter class-decl line).
 */
class ImmutableDictionaryHierarchyTest {

    @Test
    void immutableDictionary_realizesTheReadOnlyDictionaryContract() {
        ImmutableDictionary<String, Integer> dict = new ImmutableDictionary<String, Integer>()
            .add("a", 1)
            .add("b", 2);

        // The exact cast the generated converter's bridge method performs.
        Object erased = dict;
        CSharpReadOnlyDict<String, Integer> readDict = (CSharpReadOnlyDict<String, Integer>) erased;

        assertEquals(2, readDict.getCount());
        assertTrue(readDict.containsKey("a"));
        assertEquals(Integer.valueOf(1), readDict.get("a"));
        assertEquals(Integer.valueOf(2), readDict.tryGetValue("b"));
        assertFalse(readDict.containsKey("c"));
    }

    @Test
    void immutableDictionary_iteratesAsKeyValuePairThroughTheContract() {
        ImmutableDictionary<String, Integer> dict = new ImmutableDictionary<String, Integer>()
            .add("a", 1);

        int seen = 0;
        CSharpReadOnlyDict<String, Integer> view = readAsContract(dict);
        // CSharpReadOnlyDict erases to a CSharpGenericEnumerator (not Iterable),
        // so walk it with the C#-style moveNext/getCurrent contract.
        CSharpGenericEnumerator<?> enumerator = view.iterator();
        while (enumerator.moveNext()) {
            CSharpKeyValuePair<String, Integer> pair =
                (CSharpKeyValuePair<String, Integer>) enumerator.getCurrent();
            assertEquals("a", pair.getKey());
            assertEquals(1, pair.getValue());
            seen++;
        }
        assertEquals(1, seen);
    }

    @Test
    void immutableSortedDictionary_realizesTheReadOnlyDictionaryContract() {
        ImmutableSortedDictionary<String, Integer> dict = new ImmutableSortedDictionary<String, Integer>()
            .add("a", 1)
            .add("b", 2);

        Object erased = dict;
        CSharpReadOnlyDict<String, Integer> readDict = (CSharpReadOnlyDict<String, Integer>) erased;

        assertEquals(2, readDict.getCount());
        assertTrue(readDict.containsKey("a"));
        assertEquals(Integer.valueOf(1), readDict.get("a"));
        assertEquals(Integer.valueOf(2), readDict.tryGetValue("b"));
    }

    @Test
    void emptySingleton_realizesTheContractWithoutState() {
        Object erased = ImmutableDictionary.Empty;
        assertTrue(erased instanceof CSharpReadOnlyDict<?, ?>);
        assertEquals(0, ((CSharpReadOnlyDict<?, ?>) erased).getCount());
    }

    @Test
    void immutabilityIsPreservedThroughTheContract() {
        ImmutableDictionary<String, Integer> dict = new ImmutableDictionary<String, Integer>().add("a", 1);
        CSharpReadOnlyDict<String, Integer> view = readAsContract(dict);

        ImmutableDictionary<String, Integer> derived = dict.add("b", 2).setItem("c", 3);

        assertEquals(1, view.getCount(), "the contract view must not see later operations");
        assertEquals(3, derived.getCount());
        assertFalse(derived == dict);
    }

    @Test
    void staticClearHelper_returnsAnEmptyIndependentDictionary() {
        ImmutableDictionary<String, Integer> dict = new ImmutableDictionary<String, Integer>()
            .add("a", 1)
            .add("b", 2);

        // The exact form the generated call site emits for C# ((IImmutableDictionary<K,V>)d).Clear().
        IImmutableDictionary<String, Integer> cleared = IImmutableDictionary.clear(dict);

        assertEquals(0, cleared.size());
        assertTrue(cleared.isEmpty());
        assertEquals(2, dict.getCount(), "the source dictionary must stay untouched");
        assertTrue(cleared instanceof ImmutableDictionary<?, ?>);
    }

    @SuppressWarnings("unchecked")
    private static <K, V> CSharpReadOnlyDict<K, V> readAsContract(Object value) {
        return (CSharpReadOnlyDict<K, V>) value;
    }
}

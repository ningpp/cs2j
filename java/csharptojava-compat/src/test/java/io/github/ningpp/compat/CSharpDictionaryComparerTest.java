package io.github.ningpp.compat;

import org.junit.jupiter.api.Test;

import java.util.Iterator;
import java.util.Map;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertDoesNotThrow;
import static org.junit.jupiter.api.Assertions.assertSame;
import static org.junit.jupiter.api.Assertions.assertTrue;

/**
 * C# Dictionary<TKey, TValue> hashes keys through its comparer
 * (GetHashCode(comparer) / Equals(comparer)), never through the key's own
 * hashCode(). The Preserve reference resolver keys a comparer-backed
 * CSharpDictionary by object identity, so the backing map must not call
 * key.hashCode():
 *
 * - arrayLoop: a circular CSharpList used as a key overflowed the stack in
 *   ArrayList.hashCode() while the LinkedHashMap hashed the key.
 * - customHashCode: a POCO whose GetHashCode() is unstable defeated map
 *   lookups ($ref was written as null).
 */
public class CSharpDictionaryComparerTest {

    /** A list that contains itself — hashCode() would recurse forever. */
    private static CSharpList<Object> circularList() {
        CSharpList<Object> list = new CSharpList<>();
        list.add(list);
        return list;
    }

    @Test
    public void refComparerSurvivesCircularListKey() {
        CSharpDictionary<Object, String> dict =
                new CSharpDictionary<>(CSharpRefEqualityComparer.getInstance());
        CSharpList<Object> circular = circularList();
        assertDoesNotThrow(() -> dict.add(circular, "1"));
        assertTrue(dict.containsKey(circular));
        assertEquals("1", dict.get(circular));
        assertEquals("1", dict.getChecked(circular));
        assertFalse(dict.tryAdd(circular, "2"));
    }

    /** Mirrors ClassIncorrectHashCode: hashCode() changes on every call. */
    public static class ShiftingHashCodeKey {
        private int calls;

        @Override
        public int hashCode() {
            return ++calls;
        }
    }

    @Test
    public void refComparerIgnoresKeyHashCode() {
        CSharpDictionary<Object, String> dict =
                new CSharpDictionary<>(CSharpRefEqualityComparer.getInstance());
        ShiftingHashCodeKey elem = new ShiftingHashCodeKey();
        dict.add(elem, "1");
        assertEquals("1", dict.get(elem));
        assertTrue(dict.containsKey(elem));
        // Same instance under a reference comparer: not an add.
        assertFalse(dict.tryAdd(elem, "1"));
        // A second, distinct instance gets its own slot even though the two
        // hashCodes may coincide.
        ShiftingHashCodeKey other = new ShiftingHashCodeKey();
        assertTrue(dict.tryAdd(other, "2"));
        assertEquals("1", dict.get(elem));
        assertEquals("2", dict.get(other));
    }

    @Test
    public void refComparerIterationsSurfaceRawKeys() {
        CSharpDictionary<Object, String> dict =
                new CSharpDictionary<>(CSharpRefEqualityComparer.getInstance());
        String key = "plain";
        dict.add(key, "v");
        // Enumerator: key comes back as the raw key, not an internal wrapper.
        Iterator<CSharpKeyValuePair<Object, String>> it = dict.iterator();
        assertTrue(it.hasNext());
        assertSame(key, it.next().getKey());
        // keySet() view.
        Object firstKey = dict.keySet().iterator().next();
        assertSame(key, firstKey);
        // entrySet() view.
        Map.Entry<Object, String> entry = dict.entrySet().iterator().next();
        assertSame(key, entry.getKey());
        // Keys collection copyTo.
        Object[] keys = new Object[1];
        dict.getKeys().copyTo(keys, 0);
        assertSame(key, keys[0]);
    }

    @Test
    public void customComparerMatchesAndKeepsStoredKeySpelling() {
        CSharpDictionary<String, String> dict =
                new CSharpDictionary<>(StringComparer.getOrdinalIgnoreCase());
        dict.put("MyKey", "v");
        assertEquals("v", dict.get("mykey"));
        assertTrue(dict.containsKey("MYKEY"));
        // C# keeps the originally stored key spelling; only the value changes.
        dict.put("MYKEY", "w");
        assertEquals("w", dict.get("mykey"));
        assertSame("MyKey", dict.keySet().iterator().next());
        assertEquals(1, dict.size());
    }

    /** Dictionaries built without a comparer keep equals/hashCode semantics. */
    @Test
    public void defaultComparerBehaviourUnchanged() {
        CSharpDictionary<String, Integer> dict = new CSharpDictionary<>();
        dict.add("a", 1);
        dict.put("b", 2);
        assertEquals(1, dict.get("a"));
        assertEquals(2, dict.getChecked("b"));
        assertTrue(dict.containsKey("a"));
        assertFalse(dict.containsKey("c"));
        dict.remove("a");
        assertFalse(dict.containsKey("a"));
        // Insertion order preserved.
        Iterator<CSharpKeyValuePair<String, Integer>> it = dict.iterator();
        assertTrue(it.hasNext());
        assertEquals("b", it.next().getKey());
        assertFalse(it.hasNext());
    }

    @Test
    public void clonePreservesComparer() {
        CSharpDictionary<String, String> dict =
                new CSharpDictionary<>(StringComparer.getOrdinalIgnoreCase());
        dict.add("MyKey", "v");
        CSharpDictionary<String, String> copy = dict.clone();
        assertSame(StringComparer.getOrdinalIgnoreCase(), copy.getComparer());
        assertEquals("v", copy.get("mykey"));
    }
}

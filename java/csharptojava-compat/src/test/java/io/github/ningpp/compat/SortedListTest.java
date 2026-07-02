package io.github.ningpp.compat;

import org.junit.jupiter.api.Test;
import java.util.Comparator;
import java.util.List;
import java.util.Map;
import static org.junit.jupiter.api.Assertions.*;

class SortedListTest {

    // ---- Construction ----

    @Test
    void defaultConstructor() {
        CSharpSortedList<String, Integer> list = new CSharpSortedList<>();
        assertEquals(0, list.getCount());
    }

    @Test
    void comparatorConstructor() {
        CSharpSortedList<String, Integer> list = new CSharpSortedList<>(Comparator.<String>reverseOrder());
        list.put("b", 2);
        list.put("a", 1);
        // Keys should be in reverse order
        assertEquals("b", list.getKeyAtIndex(0));
        assertEquals("a", list.getKeyAtIndex(1));
    }

    @Test
    void mapConstructor() {
        CSharpSortedList<String, Integer> list = new CSharpSortedList<>(Map.of("c", 3, "a", 1, "b", 2));
        assertEquals(3, list.getCount());
        assertEquals("a", list.getKeyAtIndex(0));
        assertEquals("b", list.getKeyAtIndex(1));
        assertEquals("c", list.getKeyAtIndex(2));
    }

    // ---- Core operations ----

    @Test
    void putAndGet() {
        CSharpSortedList<String, Integer> list = new CSharpSortedList<>();
        list.put("b", 2);
        list.put("a", 1);
        list.put("c", 3);
        assertEquals(1, list.get("a"));
        assertEquals(2, list.get("b"));
        assertEquals(3, list.get("c"));
    }

    @Test
    void put_overwritesExisting() {
        CSharpSortedList<String, Integer> list = new CSharpSortedList<>();
        list.put("a", 1);
        list.put("a", 10);
        assertEquals(10, list.get("a"));
        assertEquals(1, list.getCount());
    }

    @Test
    void add_throwsOnDuplicateKey() {
        CSharpSortedList<String, Integer> list = new CSharpSortedList<>();
        list.add("a", 1);
        assertThrows(IllegalArgumentException.class, () -> list.add("a", 2));
    }

    @Test
    void containsKey() {
        CSharpSortedList<String, Integer> list = new CSharpSortedList<>();
        list.put("a", 1);
        assertTrue(list.containsKey("a"));
        assertFalse(list.containsKey("z"));
    }

    @Test
    void containsValue() {
        CSharpSortedList<String, Integer> list = new CSharpSortedList<>();
        list.put("a", 1);
        assertTrue(list.containsValue(1));
        assertFalse(list.containsValue(99));
    }

    @Test
    void remove_byKey() {
        CSharpSortedList<String, Integer> list = new CSharpSortedList<>();
        list.put("a", 1);
        list.put("b", 2);
        assertTrue(list.remove("a"));
        assertEquals(1, list.getCount());
        assertFalse(list.containsKey("a"));
        assertNull(list.get("a"));
    }

    @Test
    void remove_nonExistingKey() {
        CSharpSortedList<String, Integer> list = new CSharpSortedList<>();
        list.put("a", 1);
        assertFalse(list.remove("z"));
    }

    @Test
    void clear_removesAll() {
        CSharpSortedList<String, Integer> list = new CSharpSortedList<>();
        list.put("a", 1);
        list.put("b", 2);
        list.clear();
        assertEquals(0, list.getCount());
    }

    // ---- Index-based access ----

    @Test
    void getKeyAtIndex() {
        CSharpSortedList<String, Integer> list = new CSharpSortedList<>();
        list.put("c", 3);
        list.put("a", 1);
        list.put("b", 2);
        assertEquals("a", list.getKeyAtIndex(0));
        assertEquals("b", list.getKeyAtIndex(1));
        assertEquals("c", list.getKeyAtIndex(2));
    }

    @Test
    void getValueAtIndex() {
        CSharpSortedList<String, Integer> list = new CSharpSortedList<>();
        list.put("c", 30);
        list.put("a", 10);
        list.put("b", 20);
        assertEquals(10, list.getValueAtIndex(0));
        assertEquals(20, list.getValueAtIndex(1));
        assertEquals(30, list.getValueAtIndex(2));
    }

    @Test
    void indexOfKey() {
        CSharpSortedList<String, Integer> list = new CSharpSortedList<>();
        list.put("c", 3);
        list.put("a", 1);
        list.put("b", 2);
        assertEquals(0, list.indexOfKey("a"));
        assertEquals(1, list.indexOfKey("b"));
        assertEquals(2, list.indexOfKey("c"));
        assertEquals(-1, list.indexOfKey("z"));
    }

    @Test
    void indexOfValue() {
        CSharpSortedList<String, Integer> list = new CSharpSortedList<>();
        list.put("c", 30);
        list.put("a", 10);
        list.put("b", 20);
        assertEquals(0, list.indexOfValue(10));
        assertEquals(1, list.indexOfValue(20));
        assertEquals(2, list.indexOfValue(30));
        assertEquals(-1, list.indexOfValue(99));
    }

    // ---- IDictionary-specific operations ----

    @Test
    void tryGetValue_existing() {
        CSharpSortedList<String, Integer> list = new CSharpSortedList<>();
        list.put("a", 1);
        assertEquals(1, list.tryGetValue("a"));
    }

    @Test
    void tryGetValue_missing() {
        CSharpSortedList<String, Integer> list = new CSharpSortedList<>();
        assertNull(list.tryGetValue("z"));
    }

    @Test
    void getKeys() {
        CSharpSortedList<String, Integer> list = new CSharpSortedList<>();
        list.put("c", 3);
        list.put("a", 1);
        list.put("b", 2);
        CSharpICollection<String> keys = list.getKeys();
        assertEquals(3, keys.getCount());
        assertTrue(keys.contains("a"));
        assertTrue(keys.contains("b"));
        assertTrue(keys.contains("c"));
    }

    @Test
    void getValues() {
        CSharpSortedList<String, Integer> list = new CSharpSortedList<>();
        list.put("c", 30);
        list.put("a", 10);
        list.put("b", 20);
        CSharpICollection<Integer> values = list.getValues();
        assertEquals(3, values.getCount());
        assertTrue(values.contains(10));
        assertTrue(values.contains(20));
        assertTrue(values.contains(30));
    }

    @Test
    void getIsReadOnly_returnsFalse() {
        CSharpSortedList<String, Integer> list = new CSharpSortedList<>();
        assertFalse(list.getIsReadOnly());
    }

    @Test
    void getComparer() {
        CSharpSortedList<String, Integer> list = new CSharpSortedList<>();
        assertNotNull(list.getComparer());
    }

    @Test
    void ensureCapacity_noOp() {
        CSharpSortedList<String, Integer> list = new CSharpSortedList<>();
        list.ensureCapacity(100); // should not throw
        assertEquals(0, list.getCount());
    }

    @Test
    void trimExcess_noOp() {
        CSharpSortedList<String, Integer> list = new CSharpSortedList<>();
        list.put("a", 1);
        list.trimExcess(); // should not throw
        assertEquals(1, list.getCount());
    }

    // ---- Edge cases ----

    @Test
    void emptyList_keysAndValuesEmpty() {
        CSharpSortedList<String, Integer> list = new CSharpSortedList<>();
        assertEquals(0, list.getKeys().getCount());
        assertEquals(0, list.getValues().getCount());
    }

    @Test
    void singleElement() {
        CSharpSortedList<String, Integer> list = new CSharpSortedList<>();
        list.put("only", 42);
        assertEquals("only", list.getKeyAtIndex(0));
        assertEquals(42, list.getValueAtIndex(0));
        assertEquals(0, list.indexOfKey("only"));
    }

    @Test
    void getKeyAtIndex_outOfBounds() {
        CSharpSortedList<String, Integer> list = new CSharpSortedList<>();
        list.put("a", 1);
        assertThrows(IndexOutOfBoundsException.class, () -> list.getKeyAtIndex(5));
    }

    // ---- clone ----

    @Test
    void clone_createsCopy() {
        CSharpSortedList<String, Integer> list = new CSharpSortedList<>();
        list.put("a", 1);
        list.put("b", 2);
        CSharpSortedList<String, Integer> cloned = list.clone();
        assertEquals(2, cloned.getCount());
        assertEquals(1, cloned.get("a"));
        // Modifying clone should not affect original
        cloned.put("c", 3);
        assertEquals(2, list.getCount());
    }

    // ---- comparator custom order ----

    @Test
    void comparator_customOrder() {
        CSharpSortedList<String, Integer> list = new CSharpSortedList<>(Comparator.<String>reverseOrder());
        list.put("a", 1);
        list.put("b", 2);
        list.put("c", 3);
        assertEquals("c", list.getKeyAtIndex(0));
        assertEquals("b", list.getKeyAtIndex(1));
        assertEquals("a", list.getKeyAtIndex(2));
    }
}

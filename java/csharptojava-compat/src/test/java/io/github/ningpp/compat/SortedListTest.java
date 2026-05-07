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
        SortedList<String, Integer> list = new SortedList<>();
        assertEquals(0, list.size());
        assertTrue(list.isEmpty());
    }

    @Test
    void comparatorConstructor() {
        SortedList<String, Integer> list = new SortedList<>(Comparator.<String>reverseOrder());
        list.put("b", 2);
        list.put("a", 1);
        assertEquals(List.of("b", "a"), list.keys());
    }

    @Test
    void mapConstructor() {
        SortedList<String, Integer> list = new SortedList<>(Map.of("c", 3, "a", 1, "b", 2));
        assertEquals(3, list.size());
        assertEquals(List.of("a", "b", "c"), list.keys());
    }

    // ---- Core operations ----

    @Test
    void putAndGet() {
        SortedList<String, Integer> list = new SortedList<>();
        list.put("b", 2);
        list.put("a", 1);
        list.put("c", 3);
        assertEquals(1, list.get("a"));
        assertEquals(2, list.get("b"));
        assertEquals(3, list.get("c"));
    }

    @Test
    void put_overwritesExisting() {
        SortedList<String, Integer> list = new SortedList<>();
        list.put("a", 1);
        list.put("a", 10);
        assertEquals(10, list.get("a"));
        assertEquals(1, list.size());
    }

    @Test
    void containsKey() {
        SortedList<String, Integer> list = new SortedList<>();
        list.put("a", 1);
        assertTrue(list.containsKey("a"));
        assertFalse(list.containsKey("z"));
    }

    @Test
    void containsValue() {
        SortedList<String, Integer> list = new SortedList<>();
        list.put("a", 1);
        assertTrue(list.containsValue(1));
        assertFalse(list.containsValue(99));
    }

    @Test
    void remove_byKey() {
        SortedList<String, Integer> list = new SortedList<>();
        list.put("a", 1);
        list.put("b", 2);
        list.remove("a");
        assertEquals(1, list.size());
        assertFalse(list.containsKey("a"));
        assertNull(list.get("a"));
    }

    @Test
    void clear_removesAll() {
        SortedList<String, Integer> list = new SortedList<>();
        list.put("a", 1);
        list.put("b", 2);
        list.clear();
        assertTrue(list.isEmpty());
        assertEquals(0, list.size());
    }

    @Test
    void putAll_bulkInsert() {
        SortedList<String, Integer> list = new SortedList<>();
        list.putAll(Map.of("c", 3, "a", 1, "b", 2));
        assertEquals(3, list.size());
        assertEquals(1, list.get("a"));
    }

    // ---- Index-based access ----

    @Test
    void keys_returnsSortedList() {
        SortedList<String, Integer> list = new SortedList<>();
        list.put("c", 3);
        list.put("a", 1);
        list.put("b", 2);
        List<String> keys = list.keys();
        assertEquals(List.of("a", "b", "c"), keys);
    }

    @Test
    void values_returnsSortedValues() {
        SortedList<String, Integer> list = new SortedList<>();
        list.put("c", 30);
        list.put("a", 10);
        list.put("b", 20);
        List<Integer> values = list.values();
        assertEquals(List.of(10, 20, 30), values);
    }

    @Test
    void getKeyAtIndex() {
        SortedList<String, Integer> list = new SortedList<>();
        list.put("c", 3);
        list.put("a", 1);
        list.put("b", 2);
        assertEquals("a", list.getKeyAtIndex(0));
        assertEquals("b", list.getKeyAtIndex(1));
        assertEquals("c", list.getKeyAtIndex(2));
    }

    @Test
    void getValueAtIndex() {
        SortedList<String, Integer> list = new SortedList<>();
        list.put("c", 30);
        list.put("a", 10);
        list.put("b", 20);
        assertEquals(10, list.getValueAtIndex(0));
        assertEquals(20, list.getValueAtIndex(1));
        assertEquals(30, list.getValueAtIndex(2));
    }

    @Test
    void indexOfKey() {
        SortedList<String, Integer> list = new SortedList<>();
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
        SortedList<String, Integer> list = new SortedList<>();
        list.put("c", 30);
        list.put("a", 10);
        list.put("b", 20);
        assertEquals(0, list.indexOfValue(10));
        assertEquals(1, list.indexOfValue(20));
        assertEquals(2, list.indexOfValue(30));
        assertEquals(-1, list.indexOfValue(99));
    }

    // ---- Extended operations ----

    @Test
    void tryGetValue_existing() {
        SortedList<String, Integer> list = new SortedList<>();
        list.put("a", 1);
        ObjectHolder<Integer> holder = new ObjectHolder<>();
        assertTrue(list.tryGetValue("a", holder));
        assertEquals(1, holder.value);
    }

    @Test
    void tryGetValue_missing() {
        SortedList<String, Integer> list = new SortedList<>();
        ObjectHolder<Integer> holder = new ObjectHolder<>();
        assertFalse(list.tryGetValue("z", holder));
        assertNull(holder.value);
    }

    @Test
    void removeAt() {
        SortedList<String, Integer> list = new SortedList<>();
        list.put("c", 3);
        list.put("a", 1);
        list.put("b", 2);
        list.removeAt(1); // remove "b"
        assertEquals(2, list.size());
        assertEquals(List.of("a", "c"), list.keys());
    }

    @Test
    void capacity_returnsSize() {
        SortedList<String, Integer> list = new SortedList<>();
        list.put("a", 1);
        assertEquals(1, list.capacity());
    }

    @Test
    void ensureCapacity_noOp() {
        SortedList<String, Integer> list = new SortedList<>();
        list.ensureCapacity(100); // should not throw
        assertEquals(0, list.size());
    }

    // ---- Edge cases ----

    @Test
    void emptyList_keysReturnsEmpty() {
        SortedList<String, Integer> list = new SortedList<>();
        assertTrue(list.keys().isEmpty());
        assertTrue(list.values().isEmpty());
    }

    @Test
    void singleElement() {
        SortedList<String, Integer> list = new SortedList<>();
        list.put("only", 42);
        assertEquals("only", list.getKeyAtIndex(0));
        assertEquals(42, list.getValueAtIndex(0));
        assertEquals(0, list.indexOfKey("only"));
    }

    @Test
    void comparator_customOrder() {
        // Reverse natural order
        SortedList<String, Integer> list = new SortedList<>(Comparator.<String>reverseOrder());
        list.put("a", 1);
        list.put("b", 2);
        list.put("c", 3);
        assertEquals(List.of("c", "b", "a"), list.keys());
        assertEquals(List.of(3, 2, 1), list.values());
    }

    // ---- Java interop ----

    @Test
    void entrySet_returnsEntries() {
        SortedList<String, Integer> list = new SortedList<>();
        list.put("a", 1);
        list.put("b", 2);
        assertEquals(2, list.entrySet().size());
    }

    @Test
    void keySet_returnsKeys() {
        SortedList<String, Integer> list = new SortedList<>();
        list.put("a", 1);
        list.put("b", 2);
        assertEquals(2, list.keySet().size());
        assertTrue(list.keySet().contains("a"));
    }

    @Test
    void comparator_returnsNullForNaturalOrder() {
        SortedList<String, Integer> list = new SortedList<>();
        assertNull(list.comparator()); // TreeMap returns null for natural ordering
    }

    @Test
    void comparator_returnsCustomComparator() {
        Comparator<String> cmp = Comparator.<String>reverseOrder();
        SortedList<String, Integer> list = new SortedList<>(cmp);
        assertSame(cmp, list.comparator());
    }
}

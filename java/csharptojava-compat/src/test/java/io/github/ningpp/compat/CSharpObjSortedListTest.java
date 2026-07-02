package io.github.ningpp.compat;

import org.junit.jupiter.api.Test;
import static org.junit.jupiter.api.Assertions.*;

import java.util.NoSuchElementException;

class CSharpObjSortedListTest {

    // ---- Construction ----

    @Test
    void defaultConstructor_createsEmpty() {
        CSharpObjSortedList sl = new CSharpObjSortedList();
        assertEquals(0, sl.getCount());
    }

    // ---- Add / Get ----

    @Test
    void add_andGet() {
        CSharpObjSortedList sl = new CSharpObjSortedList();
        sl.add("b", 2);
        sl.add("a", 1);
        sl.add("c", 3);
        assertEquals(3, sl.getCount());
        assertEquals(1, sl.get("a"));
        assertEquals(2, sl.get("b"));
        assertEquals(3, sl.get("c"));
    }

    // ---- ContainsKey / ContainsValue ----

    @Test
    void containsKey_foundAndMissing() {
        CSharpObjSortedList sl = new CSharpObjSortedList();
        sl.add("b", 2);
        assertTrue(sl.containsKey("b"));
        assertFalse(sl.containsKey("z"));
    }

    @Test
    void containsValue_foundAndMissing() {
        CSharpObjSortedList sl = new CSharpObjSortedList();
        sl.add("b", 2);
        assertTrue(sl.containsValue(2));
        assertFalse(sl.containsValue(99));
    }

    // ---- GetByIndex / GetKey ----

    @Test
    void getByIndex_returnsValueAtPosition() {
        CSharpObjSortedList sl = new CSharpObjSortedList();
        sl.add("b", 2);
        sl.add("a", 1);
        sl.add("c", 3);
        assertEquals(1, sl.getByIndex(0));
        assertEquals(2, sl.getByIndex(1));
        assertEquals(3, sl.getByIndex(2));
    }

    @Test
    void getKey_returnsKeyAtPosition() {
        CSharpObjSortedList sl = new CSharpObjSortedList();
        sl.add("b", 2);
        sl.add("a", 1);
        sl.add("c", 3);
        assertEquals("a", sl.getKey(0));
        assertEquals("b", sl.getKey(1));
        assertEquals("c", sl.getKey(2));
    }

    // ---- IndexOfKey / IndexOfValue ----

    @Test
    void indexOfKey_findsPosition() {
        CSharpObjSortedList sl = new CSharpObjSortedList();
        sl.add("b", 2);
        sl.add("a", 1);
        sl.add("c", 3);
        assertEquals(0, sl.indexOfKey("a"));
        assertEquals(1, sl.indexOfKey("b"));
        assertEquals(-1, sl.indexOfKey("z"));
    }

    @Test
    void indexOfValue_findsPosition() {
        CSharpObjSortedList sl = new CSharpObjSortedList();
        sl.add("b", 20);
        sl.add("a", 10);
        assertEquals(0, sl.indexOfValue(10));
        assertEquals(1, sl.indexOfValue(20));
        assertEquals(-1, sl.indexOfValue(99));
    }

    // ---- Keys / Values ----

    @Test
    void getKeys_returnsSortedKeys() {
        CSharpObjSortedList sl = new CSharpObjSortedList();
        sl.add("b", 2);
        sl.add("a", 1);
        sl.add("c", 3);
        CSharpCollection keys = sl.getKeys();
        assertEquals(3, keys.getCount());
        // Verify keys by iterating
        java.util.List<Object> keyList = new java.util.ArrayList<>();
        var en = keys.iterator();
        while (en.moveNext()) keyList.add(en.getCurrent());
        assertTrue(keyList.contains("a"));
        assertTrue(keyList.contains("b"));
        assertTrue(keyList.contains("c"));
    }

    @Test
    void getValues_returnsSortedValues() {
        CSharpObjSortedList sl = new CSharpObjSortedList();
        sl.add("b", 20);
        sl.add("a", 10);
        sl.add("c", 30);
        CSharpCollection values = sl.getValues();
        assertEquals(3, values.getCount());
        java.util.List<Object> valueList = new java.util.ArrayList<>();
        var en = values.iterator();
        while (en.moveNext()) valueList.add(en.getCurrent());
        assertTrue(valueList.contains(10));
        assertTrue(valueList.contains(20));
        assertTrue(valueList.contains(30));
    }

    // ---- Remove ----

    @Test
    void remove_existingKey() {
        CSharpObjSortedList sl = new CSharpObjSortedList();
        sl.add("a", 1);
        sl.add("b", 2);
        sl.remove("a");
        assertEquals(1, sl.getCount());
        assertFalse(sl.containsKey("a"));
    }

    // ---- Clone ----

    @Test
    void clone_createsCopy() {
        CSharpObjSortedList sl = new CSharpObjSortedList();
        sl.add("a", 1);
        sl.add("b", 2);
        CSharpObjSortedList cloned = sl.clone();
        assertEquals(2, cloned.getCount());
        assertEquals(1, cloned.get("a"));
    }

    // ---- Oracle data validation (SortedList - non-generic) ----

    @Test
    void oracleData_sortedListOperations() {
        CSharpObjSortedList sl = new CSharpObjSortedList();
        sl.add("b", 2);
        sl.add("a", 1);
        sl.add("c", 3);
        assertEquals(3, sl.getCount());
        assertEquals(1, sl.get("a"));
        assertEquals(1, sl.getByIndex(0));
        assertEquals("a", sl.getKey(0));
        assertTrue(sl.containsKey("b"));
        sl.remove("c");
        assertEquals(2, sl.getCount());
    }
}

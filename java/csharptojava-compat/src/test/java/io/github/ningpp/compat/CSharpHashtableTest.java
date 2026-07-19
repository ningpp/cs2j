package io.github.ningpp.compat;

import org.junit.jupiter.api.Test;
import static org.junit.jupiter.api.Assertions.*;

import java.util.Arrays;
import java.util.NoSuchElementException;

class CSharpHashtableTest {

    // ---- Construction ----

    @Test
    void defaultConstructor_createsEmpty() {
        CSharpHashtable ht = new CSharpHashtable();
        assertEquals(0, ht.getCount());
    }

    @Test
    void capacityConstructor_createsEmpty() {
        CSharpHashtable ht = new CSharpHashtable(16);
        assertEquals(0, ht.getCount());
    }

    @Test
    void capacityAndStringComparerConstructor_createsEmpty() {
        CSharpHashtable ht = new CSharpHashtable(16, StringComparer.getOrdinal());
        assertEquals(0, ht.getCount());
        ht.add("Key", "value");
        assertTrue(ht.containsKey("Key"));
    }

    // ---- Add / get / put ----

    @Test
    void add_andGet() {
        CSharpHashtable ht = new CSharpHashtable();
        ht.add("name", "Alice");
        ht.add("age", 30);
        assertEquals("Alice", ht.get("name"));
        assertEquals(30, ht.get("age"));
    }

    @Test
    void put_overwritesExisting() {
        CSharpHashtable ht = new CSharpHashtable();
        ht.add("age", 30);
        ht.put("age", 31);
        assertEquals(31, ht.get("age"));
    }

    @Test
    void put_addsNewKey() {
        CSharpHashtable ht = new CSharpHashtable();
        ht.put("key", "value");
        assertEquals("value", ht.get("key"));
    }

    // ---- Count ----

    @Test
    void getCount_reflectsState() {
        CSharpHashtable ht = new CSharpHashtable();
        assertEquals(0, ht.getCount());
        ht.add("a", 1);
        assertEquals(1, ht.getCount());
    }

    // ---- ContainsKey / ContainsValue ----

    @Test
    void containsKey_foundAndMissing() {
        CSharpHashtable ht = new CSharpHashtable();
        ht.add("name", "Alice");
        assertTrue(ht.containsKey("name"));
        assertFalse(ht.containsKey("missing"));
    }

    @Test
    void containsValue_foundAndMissing() {
        CSharpHashtable ht = new CSharpHashtable();
        ht.add("name", "Alice");
        assertTrue(ht.containsValue("Alice"));
        assertFalse(ht.containsValue("Bob"));
    }

    // ---- Remove ----

    @Test
    void remove_existingKey() {
        CSharpHashtable ht = new CSharpHashtable();
        ht.add("name", "Alice");
        ht.add("age", 30);
        ht.remove("age");
        assertEquals(1, ht.getCount());
        assertFalse(ht.containsKey("age"));
    }

    // ---- Keys / Values ----

    @Test
    void getKeys_returnsAllKeys() {
        CSharpHashtable ht = new CSharpHashtable();
        ht.add("name", "Alice");
        ht.add("age", 30);
        CSharpCollection keys = ht.getKeys();
        assertEquals(2, keys.getCount());
        // Verify keys by iterating
        java.util.List<Object> keyList = new java.util.ArrayList<>();
        var en = keys.iterator();
        while (en.moveNext()) keyList.add(en.getCurrent());
        assertTrue(keyList.contains("name"));
        assertTrue(keyList.contains("age"));
    }

    @Test
    void getValues_returnsAllValues() {
        CSharpHashtable ht = new CSharpHashtable();
        ht.add("name", "Alice");
        ht.add("age", 30);
        CSharpCollection values = ht.getValues();
        assertEquals(2, values.getCount());
        java.util.List<Object> valueList = new java.util.ArrayList<>();
        var en = values.iterator();
        while (en.moveNext()) valueList.add(en.getCurrent());
        assertTrue(valueList.contains("Alice"));
        assertTrue(valueList.contains(30));
    }

    // ---- CopyTo ----

    @Test
    void copyTo_copiesDictEntries() {
        CSharpHashtable ht = new CSharpHashtable();
        ht.add("name", "Alice");
        ht.add("age", 30);
        Object[] arr = new Object[4];
        ht.copyTo(arr, 1);
        assertNull(arr[0]);
        assertNotNull(arr[1]);
        assertNotNull(arr[2]);
    }

    // ---- Clone ----

    @Test
    void clone_createsCopy() {
        CSharpHashtable ht = new CSharpHashtable();
        ht.add("a", 1);
        ht.add("b", 2);
        CSharpHashtable cloned = ht.clone();
        assertEquals(2, cloned.getCount());
        assertEquals(1, cloned.get("a"));
        // Modifying clone should not affect original
        cloned.add("c", 3);
        assertEquals(2, ht.getCount());
    }

    // ---- Clear ----

    @Test
    void clear_emptiesTable() {
        CSharpHashtable ht = new CSharpHashtable();
        ht.add("a", 1);
        ht.add("b", 2);
        ht.clear();
        assertEquals(0, ht.getCount());
    }

    // ---- IsFixedSize / IsReadOnly ----

    @Test
    void isFixedSize_returnsFalse() {
        CSharpHashtable ht = new CSharpHashtable();
        assertFalse(ht.getIsFixedSize());
    }

    @Test
    void isReadOnly_returnsFalse() {
        CSharpHashtable ht = new CSharpHashtable();
        assertFalse(ht.getIsReadOnly());
    }

    // ---- Oracle data validation (Hashtable) ----

    @Test
    void oracleData_hashtableOperations() {
        CSharpHashtable ht = new CSharpHashtable();
        ht.add("name", "Alice");
        ht.add("age", 30);
        assertEquals(2, ht.getCount());
        assertEquals("Alice", ht.get("name"));
        ht.put("age", 31);
        assertTrue(ht.containsKey("name"));
        assertFalse(ht.containsKey("missing"));
        assertTrue(ht.containsValue("Alice"));
        ht.remove("age");
        assertEquals(1, ht.getCount());
    }
}

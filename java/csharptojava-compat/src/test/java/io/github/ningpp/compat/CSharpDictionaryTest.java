package io.github.ningpp.compat;

import org.junit.jupiter.api.Test;
import static org.junit.jupiter.api.Assertions.*;

import java.util.Map;

class CSharpDictionaryTest {

    // ---- Construction ----

    @Test
    void defaultConstructor_createsEmpty() {
        CSharpDictionary<String, Integer> dict = new CSharpDictionary<>();
        assertEquals(0, dict.getCount());
    }

    @Test
    void capacityConstructor_createsEmpty() {
        CSharpDictionary<String, Integer> dict = new CSharpDictionary<>(16);
        assertEquals(0, dict.getCount());
    }

    // ---- Add / Get / Put ----

    @Test
    void add_andGet() {
        CSharpDictionary<String, Integer> dict = new CSharpDictionary<>();
        dict.add("a", 1);
        dict.add("b", 2);
        assertEquals(1, dict.get("a"));
        assertEquals(2, dict.get("b"));
    }

    @Test
    void add_duplicateKey_throws() {
        CSharpDictionary<String, Integer> dict = new CSharpDictionary<>();
        dict.add("a", 1);
        assertThrows(IllegalArgumentException.class, () -> dict.add("a", 2));
    }

    @Test
    void put_overwritesAndReturns() {
        CSharpDictionary<String, Integer> dict = new CSharpDictionary<>();
        dict.put("a", 1);
        Integer old = dict.put("a", 10);
        assertEquals(1, old);
        assertEquals(10, dict.get("a"));
    }

    @Test
    void put_addsNewKey() {
        CSharpDictionary<String, Integer> dict = new CSharpDictionary<>();
        dict.put("c", 3);
        assertEquals(3, dict.get("c"));
    }

    // ---- Count ----

    @Test
    void getCount_reflectsState() {
        CSharpDictionary<String, Integer> dict = new CSharpDictionary<>();
        assertEquals(0, dict.getCount());
        dict.add("a", 1);
        assertEquals(1, dict.getCount());
    }

    // ---- ContainsKey / ContainsValue ----

    @Test
    void containsKey_foundAndMissing() {
        CSharpDictionary<String, Integer> dict = new CSharpDictionary<>();
        dict.add("b", 2);
        assertTrue(dict.containsKey("b"));
        assertFalse(dict.containsKey("z"));
    }

    @Test
    void containsValue_foundAndMissing() {
        CSharpDictionary<String, Integer> dict = new CSharpDictionary<>();
        dict.add("b", 2);
        assertTrue(dict.containsValue(2));
        assertFalse(dict.containsValue(99));
    }

    // ---- TryGetValue ----

    @Test
    void tryGetValue_existing() {
        CSharpDictionary<String, Integer> dict = new CSharpDictionary<>();
        dict.add("a", 1);
        assertEquals(1, dict.tryGetValue("a"));
    }

    @Test
    void tryGetValue_missing() {
        CSharpDictionary<String, Integer> dict = new CSharpDictionary<>();
        assertNull(dict.tryGetValue("z"));
    }

    // ---- TryAdd ----

    @Test
    void tryAdd_newKey_succeeds() {
        CSharpDictionary<String, Integer> dict = new CSharpDictionary<>();
        assertTrue(dict.tryAdd("a", 1));
        assertEquals(1, dict.get("a"));
    }

    @Test
    void tryAdd_existingKey_fails() {
        CSharpDictionary<String, Integer> dict = new CSharpDictionary<>();
        dict.add("a", 1);
        assertFalse(dict.tryAdd("a", 2));
        assertEquals(1, dict.get("a"));
    }

    // ---- Remove ----

    @Test
    void remove_existingKey() {
        CSharpDictionary<String, Integer> dict = new CSharpDictionary<>();
        dict.add("a", 1);
        dict.add("b", 2);
        assertTrue(dict.remove("b"));
        assertEquals(1, dict.getCount());
    }

    @Test
    void remove_nonExistingKey() {
        CSharpDictionary<String, Integer> dict = new CSharpDictionary<>();
        assertFalse(dict.remove("z"));
    }

    // ---- Keys / Values ----

    @Test
    void getKeys_returnsAllKeys() {
        CSharpDictionary<String, Integer> dict = new CSharpDictionary<>();
        dict.add("a", 1);
        dict.add("b", 2);
        dict.add("c", 3);
        CSharpICollection<String> keys = dict.getKeys();
        assertEquals(3, keys.getCount());
        assertTrue(keys.contains("a"));
        assertTrue(keys.contains("b"));
        assertTrue(keys.contains("c"));
    }

    @Test
    void getValues_returnsAllValues() {
        CSharpDictionary<String, Integer> dict = new CSharpDictionary<>();
        dict.add("a", 1);
        dict.add("b", 2);
        dict.add("c", 3);
        CSharpICollection<Integer> values = dict.getValues();
        assertEquals(3, values.getCount());
        assertTrue(values.contains(1));
        assertTrue(values.contains(2));
        assertTrue(values.contains(3));
    }

    // ---- EnsureCapacity ----

    @Test
    void ensureCapacity_doesNotThrow() {
        CSharpDictionary<String, Integer> dict = new CSharpDictionary<>();
        dict.ensureCapacity(100);
        assertEquals(0, dict.getCount());
    }

    // ---- Clone ----

    @Test
    void clone_createsCopy() {
        CSharpDictionary<String, Integer> dict = new CSharpDictionary<>();
        dict.add("a", 1);
        dict.add("b", 2);
        CSharpDictionary<String, Integer> cloned = dict.clone();
        assertEquals(2, cloned.getCount());
        assertEquals(1, cloned.get("a"));
        cloned.put("c", 3);
        assertEquals(2, dict.getCount());
    }

    // ---- Clear ----

    @Test
    void clear_emptiesDictionary() {
        CSharpDictionary<String, Integer> dict = new CSharpDictionary<>();
        dict.add("a", 1);
        dict.clear();
        assertEquals(0, dict.getCount());
    }

    // ---- IsReadOnly ----

    @Test
    void isReadOnly_returnsFalse() {
        CSharpDictionary<String, Integer> dict = new CSharpDictionary<>();
        assertFalse(dict.getIsReadOnly());
    }

    // ---- Oracle data validation (Dictionary<string,int>) ----

    @Test
    void oracleData_dictionaryOperations() {
        CSharpDictionary<String, Integer> dict = new CSharpDictionary<>();
        dict.add("a", 1);
        dict.add("b", 2);
        assertEquals(2, dict.getCount());
        assertEquals(1, dict.get("a"));
        dict.put("c", 3);
        assertTrue(dict.containsKey("b"));
        assertFalse(dict.containsKey("z"));
        assertTrue(dict.containsValue(2));
        assertEquals(1, dict.tryGetValue("a"));
        assertTrue(dict.remove("b"));
        assertEquals(2, dict.getCount());
    }
}

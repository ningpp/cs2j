package io.github.ningpp.compat;

import org.junit.jupiter.api.Test;
import static org.junit.jupiter.api.Assertions.*;

class CSharpSortedDictTest {

    // ---- Construction ----

    @Test
    void defaultConstructor_createsEmpty() {
        CSharpSortedDict<String, Integer> dict = new CSharpSortedDict<>();
        assertEquals(0, dict.getCount());
    }

    // ---- Add / Get / Put ----

    @Test
    void add_andGet_sortedByKey() {
        CSharpSortedDict<String, Integer> dict = new CSharpSortedDict<>();
        dict.add("cherry", 3);
        dict.add("apple", 1);
        dict.add("banana", 2);
        assertEquals(3, dict.getCount());
        assertEquals(1, dict.get("apple"));
        assertEquals(2, dict.get("banana"));
        assertEquals(3, dict.get("cherry"));
    }

    @Test
    void add_duplicateKey_throws() {
        CSharpSortedDict<String, Integer> dict = new CSharpSortedDict<>();
        dict.add("a", 1);
        assertThrows(IllegalArgumentException.class, () -> dict.add("a", 2));
    }

    @Test
    void put_overwritesExisting() {
        CSharpSortedDict<String, Integer> dict = new CSharpSortedDict<>();
        dict.put("a", 1);
        dict.put("a", 10);
        assertEquals(10, dict.get("a"));
        assertEquals(1, dict.getCount());
    }

    // ---- ContainsKey / ContainsValue ----

    @Test
    void containsKey_foundAndMissing() {
        CSharpSortedDict<String, Integer> dict = new CSharpSortedDict<>();
        dict.add("banana", 2);
        assertTrue(dict.containsKey("banana"));
        assertFalse(dict.containsKey("missing"));
    }

    @Test
    void containsValue_foundAndMissing() {
        CSharpSortedDict<String, Integer> dict = new CSharpSortedDict<>();
        dict.add("banana", 2);
        assertTrue(dict.containsValue(2));
        assertFalse(dict.containsValue(99));
    }

    // ---- Keys sorted ----

    @Test
    void getKeys_returnsSortedKeys() {
        CSharpSortedDict<String, Integer> dict = new CSharpSortedDict<>();
        dict.add("cherry", 3);
        dict.add("apple", 1);
        dict.add("banana", 2);
        CSharpICollection<String> keys = dict.getKeys();
        assertEquals(3, keys.getCount());
        // Keys are in sorted order: apple, banana, cherry
        // Verify via iteration
        var it = keys.iterator();
        assertEquals("apple", it.next());
        assertEquals("banana", it.next());
        assertEquals("cherry", it.next());
    }

    @Test
    void getValues_returnsSortedValues() {
        CSharpSortedDict<String, Integer> dict = new CSharpSortedDict<>();
        dict.add("cherry", 3);
        dict.add("apple", 1);
        dict.add("banana", 2);
        CSharpICollection<Integer> values = dict.getValues();
        assertEquals(3, values.getCount());
        // Values in key-sorted order: 1, 2, 3
        assertTrue(values.contains(1));
        assertTrue(values.contains(2));
        assertTrue(values.contains(3));
    }

    // ---- Remove ----

    @Test
    void remove_existingKey() {
        CSharpSortedDict<String, Integer> dict = new CSharpSortedDict<>();
        dict.add("cherry", 3);
        dict.add("apple", 1);
        assertTrue(dict.remove("cherry"));
        assertEquals(1, dict.getCount());
    }

    // ---- TryGetValue ----

    @Test
    void tryGetValue_existing() {
        CSharpSortedDict<String, Integer> dict = new CSharpSortedDict<>();
        dict.add("apple", 1);
        assertEquals(1, dict.tryGetValue("apple"));
    }

    @Test
    void tryGetValue_missing() {
        CSharpSortedDict<String, Integer> dict = new CSharpSortedDict<>();
        assertNull(dict.tryGetValue("missing"));
    }

    // ---- GetComparer ----

    @Test
    void getComparer_returnsNonNull() {
        CSharpSortedDict<String, Integer> dict = new CSharpSortedDict<>();
        assertNotNull(dict.getComparer());
    }

    // ---- Clone ----

    @Test
    void clone_createsCopy() {
        CSharpSortedDict<String, Integer> dict = new CSharpSortedDict<>();
        dict.add("a", 1);
        dict.add("b", 2);
        CSharpSortedDict<String, Integer> cloned = dict.clone();
        assertEquals(2, cloned.getCount());
        assertEquals(1, cloned.get("a"));
        cloned.put("c", 3);
        assertEquals(2, dict.getCount());
    }

    // ---- Clear ----

    @Test
    void clear_emptiesDict() {
        CSharpSortedDict<String, Integer> dict = new CSharpSortedDict<>();
        dict.add("a", 1);
        dict.clear();
        assertEquals(0, dict.getCount());
    }

    // ---- IsReadOnly ----

    @Test
    void isReadOnly_returnsFalse() {
        CSharpSortedDict<String, Integer> dict = new CSharpSortedDict<>();
        assertFalse(dict.getIsReadOnly());
    }

    // ---- Oracle data validation (SortedDictionary<string,int>) ----

    @Test
    void oracleData_sortedDictOperations() {
        CSharpSortedDict<String, Integer> dict = new CSharpSortedDict<>();
        dict.add("cherry", 3);
        dict.add("apple", 1);
        dict.add("banana", 2);
        assertEquals(3, dict.getCount());
        assertEquals(1, dict.get("apple"));
        assertTrue(dict.containsKey("banana"));
        // Keys are sorted: apple, banana, cherry
        CSharpICollection<String> keys = dict.getKeys();
        var it = keys.iterator();
        assertEquals("apple", it.next());
        assertEquals("banana", it.next());
        assertEquals("cherry", it.next());
        assertTrue(dict.remove("cherry"));
        assertEquals(2, dict.getCount());
    }
}

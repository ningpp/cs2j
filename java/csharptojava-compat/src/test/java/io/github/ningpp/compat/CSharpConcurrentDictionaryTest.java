package io.github.ningpp.compat;

import org.junit.jupiter.api.Test;
import static org.junit.jupiter.api.Assertions.*;

class CSharpConcurrentDictionaryTest {

    // ---- ContainsKey ----
    // containsKey reports presence based on the key alone; values are always
    // non-null here because ConcurrentHashMap rejects null values.

    @Test
    void containsKey_presentKey_returnsTrue() {
        CSharpConcurrentDictionary<String, Integer> dict = new CSharpConcurrentDictionary<>();
        dict.put("a", 1);
        assertTrue(dict.containsKey("a"));
    }

    @Test
    void containsKey_absentKey_returnsFalse() {
        CSharpConcurrentDictionary<String, Integer> dict = new CSharpConcurrentDictionary<>();
        dict.put("a", 1);
        assertFalse(dict.containsKey("b"));
    }

    @Test
    void containsKey_emptyDictionary_returnsFalse() {
        CSharpConcurrentDictionary<String, Integer> dict = new CSharpConcurrentDictionary<>();
        assertFalse(dict.containsKey("a"));
        assertEquals(0, dict.getCount());
    }

    @Test
    void containsKey_tracksTryAddAndRemove() {
        CSharpConcurrentDictionary<String, Integer> dict = new CSharpConcurrentDictionary<>();
        assertTrue(dict.tryAdd("a", 1));
        assertFalse(dict.containsKey("b"));
        assertTrue(dict.containsKey("a"));

        ObjectHolder<Integer> holder = new ObjectHolder<>();
        assertTrue(dict.tryRemove("a", holder));
        assertFalse(dict.containsKey("a"));
        assertEquals(1, holder.value);
    }

    @Test
    void containsKey_afterClear_returnsFalse() {
        CSharpConcurrentDictionary<String, Integer> dict = new CSharpConcurrentDictionary<>();
        dict.put("a", 1);
        dict.put("b", 2);
        dict.clear();
        assertFalse(dict.containsKey("a"));
        assertFalse(dict.containsKey("b"));
    }
}

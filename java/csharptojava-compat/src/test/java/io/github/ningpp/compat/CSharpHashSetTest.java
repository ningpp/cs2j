package io.github.ningpp.compat;

import org.junit.jupiter.api.Test;
import static org.junit.jupiter.api.Assertions.*;

import java.util.Arrays;
import java.util.List;

class CSharpHashSetTest {

    // ---- Construction ----

    @Test
    void defaultConstructor_createsEmpty() {
        CSharpHashSet<String> set = new CSharpHashSet<>();
        assertEquals(0, set.getCount());
    }

    @Test
    void collectionConstructor_copiesElements() {
        CSharpHashSet<String> set = new CSharpHashSet<>(Arrays.asList("a", "b"));
        assertEquals(2, set.getCount());
    }

    // ---- Add ----

    @Test
    void add_newElement_returnsTrue() {
        CSharpHashSet<String> set = new CSharpHashSet<>();
        assertTrue(set.add("apple"));
        assertTrue(set.add("banana"));
    }

    @Test
    void add_duplicateElement_returnsFalse() {
        CSharpHashSet<String> set = new CSharpHashSet<>();
        set.add("apple");
        assertFalse(set.add("apple"));
    }

    // ---- Contains ----

    @Test
    void contains_foundAndMissing() {
        CSharpHashSet<String> set = new CSharpHashSet<>();
        set.add("apple");
        assertTrue(set.contains("apple"));
        assertFalse(set.contains("cherry"));
    }

    // ---- Remove ----

    @Test
    void remove_existingElement() {
        CSharpHashSet<String> set = new CSharpHashSet<>();
        set.add("banana");
        assertTrue(set.remove("banana"));
        assertEquals(0, set.getCount());
    }

    @Test
    void remove_nonExistingElement() {
        CSharpHashSet<String> set = new CSharpHashSet<>();
        assertFalse(set.remove("missing"));
    }

    // ---- Count ----

    @Test
    void getCount_reflectsState() {
        CSharpHashSet<String> set = new CSharpHashSet<>();
        assertEquals(0, set.getCount());
        set.add("a");
        set.add("b");
        assertEquals(2, set.getCount());
        set.add("a"); // duplicate
        assertEquals(2, set.getCount());
    }

    // ---- ExceptWith ----

    @Test
    void exceptWith_removesElements() {
        CSharpHashSet<String> set = new CSharpHashSet<>(Arrays.asList("a", "b", "c"));
        set.exceptWith(Arrays.asList("b", "d"));
        assertEquals(2, set.getCount());
        assertTrue(set.contains("a"));
        assertFalse(set.contains("b"));
        assertTrue(set.contains("c"));
    }

    // ---- IntersectWith ----

    @Test
    void intersectWith_retainsCommonElements() {
        CSharpHashSet<String> set = new CSharpHashSet<>(Arrays.asList("a", "b", "c"));
        set.intersectWith(Arrays.asList("b", "c", "d"));
        assertEquals(2, set.getCount());
        assertFalse(set.contains("a"));
        assertTrue(set.contains("b"));
        assertTrue(set.contains("c"));
    }

    // ---- UnionWith ----

    @Test
    void unionWith_addsMissingElements() {
        CSharpHashSet<String> set = new CSharpHashSet<>(Arrays.asList("a", "b"));
        set.unionWith(Arrays.asList("b", "c"));
        assertEquals(3, set.getCount());
        assertTrue(set.contains("a"));
        assertTrue(set.contains("b"));
        assertTrue(set.contains("c"));
    }

    // ---- SymmetricExceptWith ----

    @Test
    void symmetricExceptWith_keepsOnlyDifferences() {
        CSharpHashSet<String> set = new CSharpHashSet<>(Arrays.asList("a", "b"));
        set.symmetricExceptWith(Arrays.asList("b", "c"));
        assertEquals(2, set.getCount());
        assertTrue(set.contains("a"));
        assertFalse(set.contains("b"));
        assertTrue(set.contains("c"));
    }

    // ---- IsSubsetOf ----

    @Test
    void isSubsetOf_true() {
        CSharpHashSet<String> set = new CSharpHashSet<>(Arrays.asList("a", "b"));
        assertTrue(set.isSubsetOf(Arrays.asList("a", "b", "c")));
    }

    @Test
    void isSubsetOf_false() {
        CSharpHashSet<String> set = new CSharpHashSet<>(Arrays.asList("a", "b", "c"));
        assertFalse(set.isSubsetOf(Arrays.asList("a", "b")));
    }

    // ---- IsSupersetOf ----

    @Test
    void isSupersetOf_true() {
        CSharpHashSet<String> set = new CSharpHashSet<>(Arrays.asList("a", "b", "c"));
        assertTrue(set.isSupersetOf(Arrays.asList("a", "b")));
    }

    @Test
    void isSupersetOf_false() {
        CSharpHashSet<String> set = new CSharpHashSet<>(Arrays.asList("a", "b"));
        assertFalse(set.isSupersetOf(Arrays.asList("a", "b", "c")));
    }

    // ---- IsProperSubsetOf ----

    @Test
    void isProperSubsetOf_true() {
        CSharpHashSet<String> set = new CSharpHashSet<>(Arrays.asList("a", "b"));
        assertTrue(set.isProperSubsetOf(Arrays.asList("a", "b", "c")));
    }

    @Test
    void isProperSubsetOf_sameSet_returnsFalse() {
        CSharpHashSet<String> set = new CSharpHashSet<>(Arrays.asList("a", "b"));
        assertFalse(set.isProperSubsetOf(Arrays.asList("a", "b")));
    }

    // ---- IsProperSupersetOf ----

    @Test
    void isProperSupersetOf_true() {
        CSharpHashSet<String> set = new CSharpHashSet<>(Arrays.asList("a", "b", "c"));
        assertTrue(set.isProperSupersetOf(Arrays.asList("a", "b")));
    }

    @Test
    void isProperSupersetOf_sameSet_returnsFalse() {
        CSharpHashSet<String> set = new CSharpHashSet<>(Arrays.asList("a", "b"));
        assertFalse(set.isProperSupersetOf(Arrays.asList("a", "b")));
    }

    // ---- SetEquals ----

    @Test
    void setEquals_sameElements() {
        CSharpHashSet<String> set = new CSharpHashSet<>(Arrays.asList("a", "b"));
        assertTrue(set.setEquals(Arrays.asList("b", "a")));
    }

    @Test
    void setEquals_differentElements() {
        CSharpHashSet<String> set = new CSharpHashSet<>(Arrays.asList("a", "b"));
        assertFalse(set.setEquals(Arrays.asList("a")));
    }

    // ---- Overlaps ----

    @Test
    void overlaps_hasCommonElements() {
        CSharpHashSet<String> set = new CSharpHashSet<>(Arrays.asList("a", "b"));
        assertTrue(set.overlaps(Arrays.asList("b", "c")));
    }

    @Test
    void overlaps_noCommonElements() {
        CSharpHashSet<String> set = new CSharpHashSet<>(Arrays.asList("a", "b"));
        assertFalse(set.overlaps(Arrays.asList("c", "d")));
    }

    // ---- CopyTo ----

    @Test
    void copyTo_copiesToIndex() {
        CSharpHashSet<String> set = new CSharpHashSet<>(Arrays.asList("a", "b"));
        String[] arr = new String[4];
        set.copyTo(arr, 1);
        // Exactly 2 elements copied starting at index 1
        assertNull(arr[0]);
        assertNotNull(arr[1]);
        assertNotNull(arr[2]);
    }

    // ---- TrimExcess ----

    @Test
    void trimExcess_doesNotThrow() {
        CSharpHashSet<String> set = new CSharpHashSet<>();
        set.add("a");
        set.trimExcess();
        assertEquals(1, set.getCount());
    }

    // ---- Clone ----

    @Test
    void clone_createsCopy() {
        CSharpHashSet<String> set = new CSharpHashSet<>(Arrays.asList("a", "b"));
        CSharpHashSet<String> cloned = set.clone();
        assertEquals(2, cloned.getCount());
        cloned.add("c");
        assertEquals(2, set.getCount());
    }

    // ---- Clear ----

    @Test
    void clear_emptiesSet() {
        CSharpHashSet<String> set = new CSharpHashSet<>(Arrays.asList("a", "b"));
        set.clear();
        assertEquals(0, set.getCount());
    }

    // ---- IsReadOnly ----

    @Test
    void isReadOnly_returnsFalse() {
        CSharpHashSet<String> set = new CSharpHashSet<>();
        assertFalse(set.getIsReadOnly());
    }

    // ---- Oracle data validation (HashSet<string>) ----

    @Test
    void oracleData_hashSetOperations() {
        CSharpHashSet<String> set = new CSharpHashSet<>();
        assertTrue(set.add("apple"));
        assertTrue(set.add("banana"));
        assertFalse(set.add("apple")); // duplicate
        assertEquals(2, set.getCount());
        assertTrue(set.contains("apple"));
        assertFalse(set.contains("cherry"));
        assertTrue(set.remove("banana"));
        assertEquals(1, set.getCount());
    }
}

package io.github.ningpp.compat;

import org.junit.jupiter.api.Test;
import static org.junit.jupiter.api.Assertions.*;

import java.util.Arrays;
import java.util.Comparator;
import java.util.List;

class CSharpSortedSetTest {

    // ---- Construction ----

    @Test
    void defaultConstructor_createsEmpty() {
        CSharpSortedSet<Integer> set = new CSharpSortedSet<>();
        assertEquals(0, set.getCount());
    }

    @Test
    void collectionConstructor_copiesAndSorts() {
        CSharpSortedSet<Integer> set = new CSharpSortedSet<>(Arrays.asList(30, 10, 20));
        assertEquals(3, set.getCount());
        assertEquals(10, set.getMin());
        assertEquals(30, set.getMax());
    }

    // ---- Add ----

    @Test
    void add_newElement_returnsTrue() {
        CSharpSortedSet<Integer> set = new CSharpSortedSet<>();
        assertTrue(set.add(30));
        assertTrue(set.add(10));
        assertTrue(set.add(20));
    }

    @Test
    void add_duplicateElement_returnsFalse() {
        CSharpSortedSet<Integer> set = new CSharpSortedSet<>();
        set.add(10);
        assertFalse(set.add(10));
    }

    // ---- Min / Max ----

    @Test
    void getMin_returnsSmallest() {
        CSharpSortedSet<Integer> set = new CSharpSortedSet<>(Arrays.asList(30, 10, 20));
        assertEquals(10, set.getMin());
    }

    @Test
    void getMax_returnsLargest() {
        CSharpSortedSet<Integer> set = new CSharpSortedSet<>(Arrays.asList(30, 10, 20));
        assertEquals(30, set.getMax());
    }

    @Test
    void getMin_emptySet_returnsNull() {
        CSharpSortedSet<Integer> set = new CSharpSortedSet<>();
        assertNull(set.getMin());
    }

    @Test
    void getMax_emptySet_returnsNull() {
        CSharpSortedSet<Integer> set = new CSharpSortedSet<>();
        assertNull(set.getMax());
    }

    // ---- Contains ----

    @Test
    void contains_foundAndMissing() {
        CSharpSortedSet<Integer> set = new CSharpSortedSet<>(Arrays.asList(10, 20, 30));
        assertTrue(set.contains(20));
        assertFalse(set.contains(99));
    }

    // ---- Remove ----

    @Test
    void remove_existingElement() {
        CSharpSortedSet<Integer> set = new CSharpSortedSet<>(Arrays.asList(10, 20, 30));
        assertTrue(set.remove(20));
        assertEquals(2, set.getCount());
    }

    // ---- GetViewBetween ----

    @Test
    void getViewBetween_returnsSubset() {
        CSharpSortedSet<Integer> set = new CSharpSortedSet<>(Arrays.asList(10, 20, 30, 40, 50));
        CSharpSortedSet<Integer> view = set.getViewBetween(20, 40);
        assertEquals(3, view.getCount());
        assertTrue(view.contains(20));
        assertTrue(view.contains(30));
        assertTrue(view.contains(40));
        assertFalse(view.contains(10));
    }

    // ---- GetReverse ----

    @Test
    void getReverse_returnsReversedView() {
        CSharpSortedSet<Integer> set = new CSharpSortedSet<>(Arrays.asList(10, 20, 30));
        CSharpSortedSet<Integer> reversed = set.getReverse();
        assertEquals(3, reversed.getCount());
        // The reversed set should iterate in reverse order
        assertEquals(30, reversed.iterator().next());
    }

    // ---- GetComparer ----

    @Test
    void getComparer_returnsComparator() {
        CSharpSortedSet<Integer> set = new CSharpSortedSet<>();
        assertNotNull(set.getComparer());
    }

    // ---- RemoveWhere ----

    @Test
    void removeWhere_removesMatching() {
        CSharpSortedSet<Integer> set = new CSharpSortedSet<>(Arrays.asList(1, 2, 3, 4, 5));
        int removed = set.removeWhere(x -> x % 2 == 0);
        assertEquals(2, removed);
        assertEquals(3, set.getCount());
    }

    // ---- TrimExcess ----

    @Test
    void trimExcess_doesNotThrow() {
        CSharpSortedSet<Integer> set = new CSharpSortedSet<>(Arrays.asList(1, 2, 3));
        set.trimExcess();
        assertEquals(3, set.getCount());
    }

    // ---- Clone ----

    @Test
    void clone_createsCopy() {
        CSharpSortedSet<Integer> set = new CSharpSortedSet<>(Arrays.asList(10, 20, 30));
        CSharpSortedSet<Integer> cloned = set.clone();
        assertEquals(3, cloned.getCount());
        cloned.add(40);
        assertEquals(3, set.getCount());
    }

    // ---- ISet operations ----

    @Test
    void exceptWith_removesElements() {
        CSharpSortedSet<Integer> set = new CSharpSortedSet<>(Arrays.asList(1, 2, 3));
        set.exceptWith(Arrays.asList(2, 4));
        assertEquals(2, set.getCount());
        assertFalse(set.contains(2));
    }

    @Test
    void intersectWith_retainsCommon() {
        CSharpSortedSet<Integer> set = new CSharpSortedSet<>(Arrays.asList(1, 2, 3));
        set.intersectWith(Arrays.asList(2, 3, 4));
        assertEquals(2, set.getCount());
        assertTrue(set.contains(2));
    }

    @Test
    void unionWith_addsMissing() {
        CSharpSortedSet<Integer> set = new CSharpSortedSet<>(Arrays.asList(1, 2));
        set.unionWith(Arrays.asList(2, 3));
        assertEquals(3, set.getCount());
    }

    @Test
    void setEquals_sameElements() {
        CSharpSortedSet<Integer> set = new CSharpSortedSet<>(Arrays.asList(1, 2, 3));
        assertTrue(set.setEquals(Arrays.asList(3, 2, 1)));
    }

    @Test
    void overlaps_hasCommonElements() {
        CSharpSortedSet<Integer> set = new CSharpSortedSet<>(Arrays.asList(1, 2, 3));
        assertTrue(set.overlaps(Arrays.asList(3, 4)));
    }

    @Test
    void isSubsetOf_true() {
        CSharpSortedSet<Integer> set = new CSharpSortedSet<>(Arrays.asList(1, 2));
        assertTrue(set.isSubsetOf(Arrays.asList(1, 2, 3)));
    }

    @Test
    void isSupersetOf_true() {
        CSharpSortedSet<Integer> set = new CSharpSortedSet<>(Arrays.asList(1, 2, 3));
        assertTrue(set.isSupersetOf(Arrays.asList(1, 2)));
    }

    // ---- Clear ----

    @Test
    void clear_emptiesSet() {
        CSharpSortedSet<Integer> set = new CSharpSortedSet<>(Arrays.asList(1, 2, 3));
        set.clear();
        assertEquals(0, set.getCount());
    }

    // ---- IsReadOnly ----

    @Test
    void isReadOnly_returnsFalse() {
        CSharpSortedSet<Integer> set = new CSharpSortedSet<>();
        assertFalse(set.getIsReadOnly());
    }

    // ---- Oracle data validation (SortedSet<int>) ----

    @Test
    void oracleData_sortedSetOperations() {
        CSharpSortedSet<Integer> set = new CSharpSortedSet<>();
        assertTrue(set.add(30));
        assertTrue(set.add(10));
        assertTrue(set.add(20));
        assertEquals(3, set.getCount());
        assertEquals(10, set.getMin());
        assertEquals(30, set.getMax());
        assertTrue(set.contains(20));
        assertTrue(set.remove(20));
        assertEquals(2, set.getCount());
    }
}

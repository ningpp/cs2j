package io.github.ningpp.compat;

import org.junit.jupiter.api.Test;
import static org.junit.jupiter.api.Assertions.*;

import java.util.Arrays;
import java.util.Collections;
import java.util.List;
import java.util.function.Predicate;

class CSharpListTest {

    // ---- Construction ----

    @Test
    void defaultConstructor_createsEmpty() {
        CSharpList<String> list = new CSharpList<>();
        assertEquals(0, list.getCount());
    }

    @Test
    void capacityConstructor_createsEmpty() {
        CSharpList<String> list = new CSharpList<>(10);
        assertEquals(0, list.getCount());
    }

    @Test
    void collectionConstructor_copiesElements() {
        CSharpList<String> list = new CSharpList<>(Arrays.asList("a", "b", "c"));
        assertEquals(3, list.getCount());
    }

    // ---- Add / Get / Set ----

    @Test
    void add_appendsElement() {
        CSharpList<String> list = new CSharpList<>();
        assertTrue(list.add("alpha"));
        list.add("beta");
        list.add("gamma");
        assertEquals(3, list.getCount());
        assertEquals("beta", list.get(1));
    }

    @Test
    void set_replacesElement() {
        CSharpList<String> list = new CSharpList<>();
        list.add("a");
        list.set(0, "b");
        assertEquals("b", list.get(0));
    }

    // ---- Contains ----

    @Test
    void contains_foundAndMissing() {
        CSharpList<String> list = new CSharpList<>();
        list.add("beta");
        assertTrue(list.contains("beta"));
        assertFalse(list.contains("missing"));
    }

    // ---- IndexOf ----

    @Test
    void indexOf_findsElement() {
        CSharpList<String> list = new CSharpList<>();
        list.add("alpha");
        list.add("beta");
        list.add("gamma");
        assertEquals(2, list.indexOf("gamma"));
        assertEquals(-1, list.indexOf("missing"));
    }

    @Test
    void indexOf_withStartAndCount() {
        CSharpList<String> list = new CSharpList<>();
        list.add("a");
        list.add("b");
        list.add("c");
        list.add("b");
        assertEquals(1, list.indexOf("b", 0));
        assertEquals(1, list.indexOf("b", 1));
        assertEquals(-1, list.indexOf("b", 2, 1));
    }

    // ---- Insert ----

    @Test
    void insert_addsAtPosition() {
        CSharpList<String> list = new CSharpList<>();
        list.add("alpha");
        list.add("gamma");
        list.insert(1, "beta");
        assertEquals(3, list.getCount());
        assertEquals("beta", list.get(1));
    }

    // ---- Remove / RemoveAt ----

    @Test
    void remove_existingElement() {
        CSharpList<String> list = new CSharpList<>();
        list.add("alpha");
        list.add("beta");
        list.add("gamma");
        assertTrue(list.remove("beta"));
        assertEquals(2, list.getCount());
    }

    @Test
    void remove_nonExistingElement() {
        CSharpList<String> list = new CSharpList<>();
        list.add("a");
        assertFalse(list.remove("z"));
    }

    @Test
    void removeAt_removesByIndex() {
        CSharpList<String> list = new CSharpList<>();
        list.add("alpha");
        list.add("beta");
        list.removeAt(0);
        assertEquals("beta", list.get(0));
    }

    // ---- Sort ----

    @Test
    void sort_sortsInPlace() {
        CSharpList<String> list = new CSharpList<>();
        list.add("gamma");
        list.add("alpha");
        list.add("beta");
        list.sort();
        assertEquals("alpha", list.get(0));
        assertEquals("beta", list.get(1));
        assertEquals("gamma", list.get(2));
    }

    // ---- Reverse ----

    @Test
    void reverse_reversesInPlace() {
        CSharpList<String> list = new CSharpList<>();
        list.add("a");
        list.add("b");
        list.add("c");
        list.reverse();
        assertEquals("c", list.get(0));
        assertEquals("b", list.get(1));
        assertEquals("a", list.get(2));
    }

    @Test
    void reverse_partialRange() {
        CSharpList<String> list = new CSharpList<>();
        list.add("a");
        list.add("b");
        list.add("c");
        list.add("d");
        list.reverse(1, 2);
        assertEquals("a", list.get(0));
        assertEquals("c", list.get(1));
        assertEquals("b", list.get(2));
        assertEquals("d", list.get(3));
    }

    // ---- Find / FindAll / FindIndex ----

    @Test
    void find_returnsFirstMatch() {
        CSharpList<Integer> list = new CSharpList<>();
        list.add(1);
        list.add(2);
        list.add(3);
        list.add(4);
        assertEquals(2, list.find(x -> x > 1));
        assertNull(list.find(x -> x > 10));
    }

    @Test
    void findAll_returnsAllMatches() {
        CSharpList<Integer> list = new CSharpList<>();
        list.add(1);
        list.add(2);
        list.add(3);
        list.add(4);
        CSharpList<Integer> even = list.findAll(x -> x % 2 == 0);
        assertEquals(2, even.getCount());
        assertTrue(even.contains(2));
        assertTrue(even.contains(4));
    }

    @Test
    void findIndex_returnsFirstMatchingIndex() {
        CSharpList<Integer> list = new CSharpList<>();
        list.add(1);
        list.add(2);
        list.add(3);
        assertEquals(1, list.findIndex(x -> x > 1));
        assertEquals(-1, list.findIndex(x -> x > 10));
    }

    // ---- ForEach ----

    @Test
    void forEach_iteratesAllElements() {
        CSharpList<String> list = new CSharpList<>();
        list.add("a");
        list.add("b");
        StringBuilder sb = new StringBuilder();
        list.forEach(sb::append);
        assertEquals("ab", sb.toString());
    }

    // ---- AddRange ----

    @Test
    void addRange_appendsCollection() {
        CSharpList<String> list = new CSharpList<>();
        list.add("a");
        list.addRange(Arrays.asList("b", "c"));
        assertEquals(3, list.getCount());
        assertEquals("b", list.get(1));
    }

    @Test
    void addRange_appendsArray() {
        CSharpList<String> list = new CSharpList<>();
        list.add("a");
        list.addRange(new String[]{"b", "c"});
        assertEquals(3, list.getCount());
        assertEquals("b", list.get(1));
        assertEquals("c", list.get(2));
    }

    @Test
    void addRange_appendsStringSplitResult() {
        CSharpList<String> list = new CSharpList<>();
        list.addRange("a b c".split(" "));
        assertEquals(3, list.getCount());
        assertEquals("a", list.get(0));
        assertEquals("b", list.get(1));
        assertEquals("c", list.get(2));
    }

    // ---- GetRange ----

    @Test
    void getRange_returnsSubList() {
        CSharpList<String> list = new CSharpList<>();
        list.add("a");
        list.add("b");
        list.add("c");
        list.add("d");
        CSharpList<String> sub = list.getRange(1, 2);
        assertEquals(2, sub.getCount());
        assertEquals("b", sub.get(0));
        assertEquals("c", sub.get(1));
    }

    // ---- InsertRange ----

    @Test
    void insertRange_insertsAtPosition() {
        CSharpList<String> list = new CSharpList<>();
        list.add("a");
        list.add("d");
        list.insertRange(1, Arrays.asList("b", "c"));
        assertEquals(4, list.getCount());
        assertEquals("a", list.get(0));
        assertEquals("b", list.get(1));
        assertEquals("c", list.get(2));
        assertEquals("d", list.get(3));
    }

    @Test
    void insertRange_insertsArrayAtPosition() {
        CSharpList<String> list = new CSharpList<>();
        list.add("a");
        list.add("d");
        list.insertRange(1, new String[]{"b", "c"});
        assertEquals(4, list.getCount());
        assertEquals("a", list.get(0));
        assertEquals("b", list.get(1));
        assertEquals("c", list.get(2));
        assertEquals("d", list.get(3));
    }

    // ---- LastIndexOf ----

    @Test
    void lastIndexOf_findsLastOccurrence() {
        CSharpList<String> list = new CSharpList<>();
        list.add("a");
        list.add("b");
        list.add("a");
        assertEquals(2, list.lastIndexOf("a"));
        assertEquals(-1, list.lastIndexOf("z"));
    }

    // ---- RemoveAllMatching ----

    @Test
    void removeAllMatching_removesAndReturnsCount() {
        CSharpList<Integer> list = new CSharpList<>();
        list.add(1);
        list.add(2);
        list.add(3);
        list.add(4);
        int removed = list.removeAllMatching(x -> x % 2 == 0);
        assertEquals(2, removed);
        assertEquals(2, list.getCount());
    }

    // ---- RemoveRange ----

    @Test
    void removeRange_removesPortion() {
        CSharpList<String> list = new CSharpList<>();
        list.add("a");
        list.add("b");
        list.add("c");
        list.add("d");
        list.removeRange(1, 2);
        assertEquals(2, list.getCount());
        assertEquals("a", list.get(0));
        assertEquals("d", list.get(1));
    }

    // ---- ToArray ----

    @Test
    void toArray_returnsElements() {
        CSharpList<String> list = new CSharpList<>();
        list.add("a");
        list.add("b");
        Object[] arr = list.toArray();
        assertArrayEquals(new Object[]{"a", "b"}, arr);
    }

    // ---- TrimExcess ----

    @Test
    void trimExcess_doesNotThrow() {
        CSharpList<String> list = new CSharpList<>();
        list.add("a");
        list.trimExcess();
        assertEquals(1, list.getCount());
    }

    // ---- TrueForAll ----

    @Test
    void trueForAll_allMatch() {
        CSharpList<Integer> list = new CSharpList<>();
        list.add(2);
        list.add(4);
        list.add(6);
        assertTrue(list.trueForAll(x -> x % 2 == 0));
    }

    @Test
    void trueForAll_notAllMatch() {
        CSharpList<Integer> list = new CSharpList<>();
        list.add(2);
        list.add(3);
        assertFalse(list.trueForAll(x -> x % 2 == 0));
    }

    // ---- EnsureCapacity ----

    @Test
    void ensureCapacity_doesNotThrow() {
        CSharpList<String> list = new CSharpList<>();
        list.ensureCapacity(100);
        assertEquals(0, list.getCount());
    }

    // ---- AsReadOnly ----

    @Test
    void asReadOnly_returnsReadOnlyView() {
        CSharpList<String> list = new CSharpList<>();
        list.add("a");
        list.add("b");
        CSharpReadOnlyList<String> ro = list.asReadOnly();
        assertEquals(2, ro.getCount());
        assertEquals("a", ro.get(0));
    }

    // ---- BinarySearch ----

    @Test
    void binarySearch_sortedList() {
        CSharpList<String> list = new CSharpList<>();
        list.add("apple");
        list.add("banana");
        list.add("cherry");
        assertEquals(1, list.binarySearch("banana"));
    }

    // ---- Clone ----

    @Test
    void clone_createsCopy() {
        CSharpList<String> list = new CSharpList<>();
        list.add("a");
        list.add("b");
        CSharpList<String> cloned = list.clone();
        assertEquals(2, cloned.getCount());
        cloned.add("c");
        assertEquals(2, list.getCount());
    }

    // ---- Clear ----

    @Test
    void clear_emptiesList() {
        CSharpList<String> list = new CSharpList<>();
        list.add("a");
        list.add("b");
        list.clear();
        assertEquals(0, list.getCount());
    }

    // ---- IsReadOnly ----

    @Test
    void isReadOnly_returnsFalse() {
        CSharpList<String> list = new CSharpList<>();
        assertFalse(list.getIsReadOnly());
    }

    // ---- CopyTo ----

    @Test
    void copyTo_copiesToIndex() {
        CSharpList<String> list = new CSharpList<>();
        list.add("a");
        list.add("b");
        String[] arr = new String[4];
        list.copyTo(arr, 1);
        assertNull(arr[0]);
        assertEquals("a", arr[1]);
        assertEquals("b", arr[2]);
    }

    // ---- Oracle data validation (List<string>) ----

    @Test
    void oracleData_listOperations() {
        CSharpList<String> list = new CSharpList<>();
        list.add("alpha");
        list.add("beta");
        list.add("gamma");
        assertEquals(3, list.getCount());
        assertEquals("beta", list.get(1));
        assertTrue(list.contains("beta"));
        assertEquals(2, list.indexOf("gamma"));
        list.insert(1, "delta");
        assertTrue(list.remove("beta"));
        list.removeAt(0);
        list.sort();
        list.reverse();
        Object[] arr = list.toArray();
        assertEquals(2, arr.length);
        assertEquals("gamma", arr[0]);
        assertEquals("delta", arr[1]);
    }
}

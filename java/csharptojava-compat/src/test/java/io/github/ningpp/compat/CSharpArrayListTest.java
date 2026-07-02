package io.github.ningpp.compat;

import org.junit.jupiter.api.Test;
import static org.junit.jupiter.api.Assertions.*;

import java.util.Arrays;
import java.util.Collections;
import java.util.List;
import java.util.NoSuchElementException;

class CSharpArrayListTest {

    // ---- Construction ----

    @Test
    void defaultConstructor_createsEmpty() {
        CSharpArrayList list = new CSharpArrayList();
        assertEquals(0, list.getCount());
    }

    @Test
    void capacityConstructor_createsEmpty() {
        CSharpArrayList list = new CSharpArrayList(10);
        assertEquals(0, list.getCount());
    }

    @Test
    void collectionConstructor_copiesElements() {
        CSharpArrayList list = new CSharpArrayList(Arrays.asList("a", "b", "c"));
        assertEquals(3, list.getCount());
        assertEquals("a", list.get(0));
        assertEquals("b", list.get(1));
        assertEquals("c", list.get(2));
    }

    // ---- Add / get / set ----

    @Test
    void add_returnsIndex() {
        CSharpArrayList list = new CSharpArrayList();
        assertEquals(0, list.add("cherry"));
        assertEquals(1, list.add("apple"));
        assertEquals(2, list.add("banana"));
    }

    @Test
    void get_retrievesElement() {
        CSharpArrayList list = new CSharpArrayList();
        list.add("cherry");
        list.add("apple");
        assertEquals("cherry", list.get(0));
        assertEquals("apple", list.get(1));
    }

    @Test
    void set_replacesElement() {
        CSharpArrayList list = new CSharpArrayList();
        list.add("a");
        list.set(0, "b");
        assertEquals("b", list.get(0));
    }

    // ---- Count ----

    @Test
    void getCount_reflectsState() {
        CSharpArrayList list = new CSharpArrayList();
        assertEquals(0, list.getCount());
        list.add("x");
        assertEquals(1, list.getCount());
        list.add("y");
        assertEquals(2, list.getCount());
    }

    // ---- Contains ----

    @Test
    void contains_foundAndMissing() {
        CSharpArrayList list = new CSharpArrayList();
        list.add("apple");
        list.add("banana");
        assertTrue(list.contains("apple"));
        assertFalse(list.contains("missing"));
    }

    // ---- IndexOf ----

    @Test
    void indexOf_findsElement() {
        CSharpArrayList list = new CSharpArrayList();
        list.add("cherry");
        list.add("apple");
        list.add("banana");
        assertEquals(2, list.indexOf("banana"));
        assertEquals(-1, list.indexOf("missing"));
    }

    // ---- Insert ----

    @Test
    void insert_addsAtPosition() {
        CSharpArrayList list = new CSharpArrayList();
        list.add("cherry");
        list.add("apple");
        list.add("banana");
        list.insert(1, "date");
        assertEquals(4, list.getCount());
        assertEquals("cherry", list.get(0));
        assertEquals("date", list.get(1));
        assertEquals("apple", list.get(2));
    }

    // ---- Remove / RemoveAt ----

    @Test
    void remove_removesFirstOccurrence() {
        CSharpArrayList list = new CSharpArrayList();
        list.add("cherry");
        list.add("apple");
        list.add("banana");
        list.remove("cherry");
        assertEquals(2, list.getCount());
        assertFalse(list.contains("cherry"));
    }

    @Test
    void removeAt_removesByIndex() {
        CSharpArrayList list = new CSharpArrayList();
        list.add("cherry");
        list.add("apple");
        list.add("banana");
        list.removeAt(0);
        assertEquals(2, list.getCount());
        assertEquals("apple", list.get(0));
    }

    // ---- Sort ----

    @Test
    void sort_sortsElements() {
        CSharpArrayList list = new CSharpArrayList();
        list.add("cherry");
        list.add("apple");
        list.add("banana");
        list.sort();
        assertEquals("apple", list.get(0));
        assertEquals("banana", list.get(1));
        assertEquals("cherry", list.get(2));
    }

    // ---- Reverse ----

    @Test
    void reverse_reversesElements() {
        CSharpArrayList list = new CSharpArrayList();
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
        CSharpArrayList list = new CSharpArrayList();
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

    // ---- ToArray ----

    @Test
    void toArray_returnsElements() {
        CSharpArrayList list = new CSharpArrayList();
        list.add("apple");
        list.add("banana");
        Object[] arr = list.toArray();
        assertArrayEquals(new Object[]{"apple", "banana"}, arr);
    }

    // ---- Clone ----

    @Test
    void clone_createsCopy() {
        CSharpArrayList list = new CSharpArrayList();
        list.add("a");
        list.add("b");
        CSharpArrayList cloned = list.clone();
        assertEquals(2, cloned.getCount());
        assertEquals("a", cloned.get(0));
        // Modifying clone should not affect original
        cloned.add("c");
        assertEquals(2, list.getCount());
    }

    // ---- AddRange ----

    @Test
    void addRange_appendsCollection() {
        CSharpArrayList list = new CSharpArrayList();
        list.add("a");
        list.addRange(Arrays.asList("b", "c"));
        assertEquals(3, list.getCount());
        assertEquals("a", list.get(0));
        assertEquals("b", list.get(1));
        assertEquals("c", list.get(2));
    }

    // ---- GetRange ----

    @Test
    void getRange_returnsSubList() {
        CSharpArrayList list = new CSharpArrayList();
        list.add("a");
        list.add("b");
        list.add("c");
        list.add("d");
        CSharpArrayList sub = list.getRange(1, 2);
        assertEquals(2, sub.getCount());
        assertEquals("b", sub.get(0));
        assertEquals("c", sub.get(1));
    }

    // ---- RemoveRange ----

    @Test
    void removeRange_removesPortion() {
        CSharpArrayList list = new CSharpArrayList();
        list.add("a");
        list.add("b");
        list.add("c");
        list.add("d");
        list.removeRange(1, 2);
        assertEquals(2, list.getCount());
        assertEquals("a", list.get(0));
        assertEquals("d", list.get(1));
    }

    // ---- BinarySearch ----

    @Test
    void binarySearch_sortedList() {
        CSharpArrayList list = new CSharpArrayList();
        list.add("apple");
        list.add("banana");
        list.add("cherry");
        // List must be sorted for binarySearch to work correctly
        int idx = list.binarySearch("banana");
        assertEquals(1, idx);
    }

    // ---- LastIndexOf ----

    @Test
    void lastIndexOf_findsLastOccurrence() {
        CSharpArrayList list = new CSharpArrayList();
        list.add("a");
        list.add("b");
        list.add("a");
        assertEquals(2, list.lastIndexOf("a"));
        assertEquals(1, list.lastIndexOf("b"));
        assertEquals(-1, list.lastIndexOf("z"));
    }

    // ---- CopyTo ----

    @Test
    void copyTo_copiesToIndex() {
        CSharpArrayList list = new CSharpArrayList();
        list.add("a");
        list.add("b");
        Object[] arr = new Object[5];
        list.copyTo(arr, 1);
        assertNull(arr[0]);
        assertEquals("a", arr[1]);
        assertEquals("b", arr[2]);
    }

    // ---- Clear ----

    @Test
    void clear_emptiesList() {
        CSharpArrayList list = new CSharpArrayList();
        list.add("a");
        list.add("b");
        list.clear();
        assertEquals(0, list.getCount());
    }

    // ---- IsFixedSize / IsReadOnly ----

    @Test
    void isFixedSize_returnsFalse() {
        CSharpArrayList list = new CSharpArrayList();
        assertFalse(list.getIsFixedSize());
    }

    @Test
    void isReadOnly_returnsFalse() {
        CSharpArrayList list = new CSharpArrayList();
        assertFalse(list.getIsReadOnly());
    }

    // ---- TrimToSize ----

    @Test
    void trimToSize_doesNotThrow() {
        CSharpArrayList list = new CSharpArrayList();
        list.add("a");
        list.add("b");
        list.trimToSize();
        assertEquals(2, list.getCount());
    }

    // ---- Repeat ----

    @Test
    void repeat_createsRepeatedElements() {
        CSharpArrayList list = CSharpArrayList.repeat("x", 3);
        assertEquals(3, list.getCount());
        assertEquals("x", list.get(0));
        assertEquals("x", list.get(1));
        assertEquals("x", list.get(2));
    }

    // ---- Oracle data validation (ArrayList) ----

    @Test
    void oracleData_arrayListOperations() {
        CSharpArrayList list = new CSharpArrayList();
        // From C# test data:
        list.add("cherry");   // output: 0
        list.add("apple");    // output: 1
        list.add("banana");   // output: 2
        assertEquals(3, list.getCount());
        assertEquals("cherry", list.get(0));
        assertTrue(list.contains("apple"));
        assertFalse(list.contains("missing"));
        assertEquals(2, list.indexOf("banana"));
        list.insert(1, "date");
        assertEquals(4, list.getCount());
        list.remove("cherry");
        list.removeAt(0);
        // After remove cherry then removeAt 0, we should have apple, banana
        Object[] arr = list.toArray();
        assertEquals(2, arr.length);
        // Sort then reverse for final state
        list.sort();
        list.reverse();
        assertEquals("banana", list.get(0));
        assertEquals("apple", list.get(1));
    }
}

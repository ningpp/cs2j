package io.github.ningpp.compat;

import org.junit.jupiter.api.Test;
import static org.junit.jupiter.api.Assertions.*;

import java.util.ArrayList;
import java.util.List;
import java.util.NoSuchElementException;

class CSharpLinkedListTest {

    // ---- Construction ----

    @Test
    void defaultConstructor_createsEmpty() {
        CSharpLinkedList<String> list = new CSharpLinkedList<>();
        assertEquals(0, list.getCount());
        assertNull(list.getFirst());
        assertNull(list.getLast());
    }

    // ---- AddFirst / AddLast ----

    @Test
    void addFirst_andAddLast() {
        CSharpLinkedList<String> list = new CSharpLinkedList<>();
        list.addLast("middle");
        list.addFirst("first");
        list.addLast("last");
        assertEquals(3, list.getCount());
        assertEquals("first", list.getFirst().getValue());
        assertEquals("last", list.getLast().getValue());
    }

    // ---- Add (ICollection) ----

    @Test
    void add_returnsTrue() {
        CSharpLinkedList<String> list = new CSharpLinkedList<>();
        assertTrue(list.add("a"));
        assertEquals(1, list.getCount());
    }

    // ---- Contains ----

    @Test
    void contains_foundAndMissing() {
        CSharpLinkedList<String> list = new CSharpLinkedList<>();
        list.addLast("middle");
        assertTrue(list.contains("middle"));
        assertFalse(list.contains("missing"));
    }

    // ---- Find / FindLast ----

    @Test
    void find_returnsNode() {
        CSharpLinkedList<String> list = new CSharpLinkedList<>();
        list.addLast("middle");
        list.addLast("other");
        CSharpLinkedListNode<String> node = list.find("middle");
        assertNotNull(node);
        assertEquals("middle", node.getValue());
    }

    @Test
    void find_notFound_returnsNull() {
        CSharpLinkedList<String> list = new CSharpLinkedList<>();
        assertNull(list.find("missing"));
    }

    @Test
    void findLast_returnsLastNode() {
        CSharpLinkedList<String> list = new CSharpLinkedList<>();
        list.addLast("middle");
        list.addLast("middle");
        CSharpLinkedListNode<String> node = list.findLast("middle");
        assertNotNull(node);
        // Should be the last "middle"
        assertNull(node.getNext());
    }

    // ---- Remove ----

    @Test
    void remove_existingValue() {
        CSharpLinkedList<String> list = new CSharpLinkedList<>();
        list.addLast("first");
        list.addLast("middle");
        list.addLast("last");
        assertTrue(list.remove("first"));
        assertEquals(2, list.getCount());
        assertEquals("middle", list.getFirst().getValue());
    }

    @Test
    void remove_nonExistingValue() {
        CSharpLinkedList<String> list = new CSharpLinkedList<>();
        list.addLast("a");
        assertFalse(list.remove("z"));
    }

    // ---- RemoveFirst / RemoveLast ----

    @Test
    void removeFirst() {
        CSharpLinkedList<String> list = new CSharpLinkedList<>();
        list.addLast("a");
        list.addLast("b");
        list.removeFirst();
        assertEquals(1, list.getCount());
        assertEquals("b", list.getFirst().getValue());
    }

    @Test
    void removeLast() {
        CSharpLinkedList<String> list = new CSharpLinkedList<>();
        list.addLast("a");
        list.addLast("b");
        list.removeLast();
        assertEquals(1, list.getCount());
        assertEquals("a", list.getLast().getValue());
    }

    @Test
    void removeFirst_empty_throws() {
        CSharpLinkedList<String> list = new CSharpLinkedList<>();
        assertThrows(NoSuchElementException.class, list::removeFirst);
    }

    @Test
    void removeLast_empty_throws() {
        CSharpLinkedList<String> list = new CSharpLinkedList<>();
        assertThrows(NoSuchElementException.class, list::removeLast);
    }

    // ---- AddBefore / AddAfter ----

    @Test
    void addBefore_insertsBeforeNode() {
        CSharpLinkedList<String> list = new CSharpLinkedList<>();
        CSharpLinkedListNode<String> nodeB = list.addLast("b");
        list.addBefore(nodeB, "a");
        assertEquals(2, list.getCount());
        assertEquals("a", list.getFirst().getValue());
    }

    @Test
    void addAfter_insertsAfterNode() {
        CSharpLinkedList<String> list = new CSharpLinkedList<>();
        CSharpLinkedListNode<String> nodeA = list.addLast("a");
        list.addAfter(nodeA, "b");
        assertEquals(2, list.getCount());
        assertEquals("b", list.getLast().getValue());
    }

    // ---- Node navigation ----

    @Test
    void node_getNext_getPrevious() {
        CSharpLinkedList<String> list = new CSharpLinkedList<>();
        list.addLast("a");
        list.addLast("b");
        list.addLast("c");
        CSharpLinkedListNode<String> first = list.getFirst();
        assertEquals("a", first.getValue());
        assertNull(first.getPrevious());
        assertEquals("b", first.getNext().getValue());
        CSharpLinkedListNode<String> last = list.getLast();
        assertEquals("c", last.getValue());
        assertNull(last.getNext());
        assertEquals("b", last.getPrevious().getValue());
    }

    @Test
    void node_setValue() {
        CSharpLinkedList<String> list = new CSharpLinkedList<>();
        list.addLast("a");
        CSharpLinkedListNode<String> node = list.getFirst();
        node.setValue("modified");
        assertEquals("modified", node.getValue());
        assertEquals("modified", list.getFirst().getValue());
    }

    @Test
    void node_getList() {
        CSharpLinkedList<String> list = new CSharpLinkedList<>();
        list.addLast("a");
        assertSame(list, list.getFirst().getList());
    }

    // ---- Clear ----

    @Test
    void clear_emptiesList() {
        CSharpLinkedList<String> list = new CSharpLinkedList<>();
        list.addLast("a");
        list.addLast("b");
        list.clear();
        assertEquals(0, list.getCount());
        assertNull(list.getFirst());
        assertNull(list.getLast());
    }

    // ---- GetFirst / GetLast ----

    @Test
    void getFirst_andGetLast() {
        CSharpLinkedList<String> list = new CSharpLinkedList<>();
        list.addLast("middle");
        assertEquals("middle", list.getFirst().getValue());
        assertEquals("middle", list.getLast().getValue());
    }

    // ---- CopyTo ----

    @Test
    void copyTo_copiesToIndex() {
        CSharpLinkedList<String> list = new CSharpLinkedList<>();
        list.addLast("a");
        list.addLast("b");
        list.addLast("c");
        String[] arr = new String[5];
        list.copyTo(arr, 1);
        assertNull(arr[0]);
        assertEquals("a", arr[1]);
        assertEquals("b", arr[2]);
        assertEquals("c", arr[3]);
    }

    // ---- Clone ----

    @Test
    void clone_createsCopy() {
        CSharpLinkedList<String> list = new CSharpLinkedList<>();
        list.addLast("a");
        list.addLast("b");
        CSharpLinkedList<String> cloned = list.clone();
        assertEquals(2, cloned.getCount());
        assertEquals("a", cloned.getFirst().getValue());
        cloned.remove("a");
        assertEquals(2, list.getCount());
    }

    // ---- Oracle data validation (LinkedList<string>) ----

    @Test
    void oracleData_linkedListOperations() {
        CSharpLinkedList<String> list = new CSharpLinkedList<>();
        list.addLast("middle");
        list.addFirst("first");
        list.addLast("last");
        assertEquals(3, list.getCount());
        assertEquals("first", list.getFirst().getValue());
        assertEquals("last", list.getLast().getValue());
        assertTrue(list.contains("middle"));
        assertNotNull(list.find("middle"));
        assertNotNull(list.findLast("middle"));
        assertTrue(list.remove("first"));
        assertEquals(2, list.getCount());
    }
}

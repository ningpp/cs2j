package io.github.ningpp.compat;

import org.junit.jupiter.api.Test;
import java.util.ArrayList;
import java.util.Collection;
import java.util.Iterator;
import java.util.List;
import java.util.NoSuchElementException;
import static org.junit.jupiter.api.Assertions.*;

/**
 * Tests for CSharpLinkedList and CSharpLinkedListNode.
 */
class LinkedListTest {

    // ---- Basic operations ----

    @Test
    void emptyList() {
        CSharpLinkedList<String> list = new CSharpLinkedList<>();
        assertEquals(0, list.getCount());
        assertNull(list.getFirst());
        assertNull(list.getLast());
    }

    @Test
    void addFirst_singleElement() {
        CSharpLinkedList<String> list = new CSharpLinkedList<>();
        list.addFirst("a");
        assertEquals(1, list.getCount());
        assertEquals("a", list.getFirst().getValue());
        assertEquals("a", list.getLast().getValue());
    }

    @Test
    void addLast_singleElement() {
        CSharpLinkedList<String> list = new CSharpLinkedList<>();
        list.addLast("a");
        assertEquals(1, list.getCount());
        assertEquals("a", list.getFirst().getValue());
        assertEquals("a", list.getLast().getValue());
    }

    @Test
    void addFirst_multipleElements() {
        CSharpLinkedList<String> list = new CSharpLinkedList<>();
        list.addFirst("c");
        list.addFirst("b");
        list.addFirst("a");
        assertEquals(3, list.getCount());
        assertEquals("a", list.getFirst().getValue());
        assertEquals("c", list.getLast().getValue());
    }

    @Test
    void addLast_multipleElements() {
        CSharpLinkedList<String> list = new CSharpLinkedList<>();
        list.addLast("a");
        list.addLast("b");
        list.addLast("c");
        assertEquals(3, list.getCount());
        assertEquals("a", list.getFirst().getValue());
        assertEquals("c", list.getLast().getValue());
    }

    // ---- add method (boolean) ----

    @Test
    void add_returnsTrue() {
        CSharpLinkedList<String> list = new CSharpLinkedList<>();
        assertTrue(list.add("a"));
        assertEquals(1, list.getCount());
    }

    // ---- contains ----

    @Test
    void contains_existing() {
        CSharpLinkedList<String> list = new CSharpLinkedList<>();
        list.addLast("a");
        list.addLast("b");
        assertTrue(list.contains("a"));
        assertTrue(list.contains("b"));
    }

    @Test
    void contains_nonExisting() {
        CSharpLinkedList<String> list = new CSharpLinkedList<>();
        list.addLast("a");
        assertFalse(list.contains("z"));
    }

    @Test
    void contains_null() {
        CSharpLinkedList<String> list = new CSharpLinkedList<>();
        list.addLast((String) null);
        assertTrue(list.contains(null));
    }

    // ---- remove by value ----

    @Test
    void remove_existingValue() {
        CSharpLinkedList<String> list = new CSharpLinkedList<>();
        list.addLast("a");
        list.addLast("b");
        list.addLast("c");
        assertTrue(list.remove("b"));
        assertEquals(2, list.getCount());
        assertFalse(list.contains("b"));
    }

    @Test
    void remove_nonExistingValue() {
        CSharpLinkedList<String> list = new CSharpLinkedList<>();
        list.addLast("a");
        assertFalse(list.remove("z"));
        assertEquals(1, list.getCount());
    }

    @Test
    void remove_firstElement() {
        CSharpLinkedList<String> list = new CSharpLinkedList<>();
        list.addLast("a");
        list.addLast("b");
        assertTrue(list.remove("a"));
        assertEquals("b", list.getFirst().getValue());
    }

    @Test
    void remove_lastElement() {
        CSharpLinkedList<String> list = new CSharpLinkedList<>();
        list.addLast("a");
        list.addLast("b");
        assertTrue(list.remove("b"));
        assertEquals("a", list.getLast().getValue());
    }

    @Test
    void remove_singleElement() {
        CSharpLinkedList<String> list = new CSharpLinkedList<>();
        list.addLast("a");
        assertTrue(list.remove("a"));
        assertEquals(0, list.getCount());
        assertNull(list.getFirst());
        assertNull(list.getLast());
    }

    // ---- remove by node ----

    @Test
    void removeNode_directly() {
        CSharpLinkedList<String> list = new CSharpLinkedList<>();
        list.addLast("a");
        CSharpLinkedListNode<String> nodeB = list.addLast("b");
        list.addLast("c");
        list.remove(nodeB);
        assertEquals(2, list.getCount());
        assertFalse(list.contains("b"));
    }

    // ---- removeFirst / removeLast ----

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

    // ---- Iteration ----

    @Test
    void iteration_correctOrder() {
        CSharpLinkedList<String> list = new CSharpLinkedList<>();
        list.addLast("a");
        list.addLast("b");
        list.addLast("c");

        List<String> result = new ArrayList<>();
        for (String s : list) {
            result.add(s);
        }
        assertEquals(List.of("a", "b", "c"), result);
    }

    @Test
    void iteration_emptyList() {
        CSharpLinkedList<String> list = new CSharpLinkedList<>();
        List<String> result = new ArrayList<>();
        for (String s : list) {
            result.add(s);
        }
        assertTrue(result.isEmpty());
    }

    @Test
    void iterator_throwsWhenExhausted() {
        CSharpLinkedList<String> list = new CSharpLinkedList<>();
        list.addLast("a");
        Iterator<String> it = list.iterator();
        assertEquals("a", it.next());
        assertThrows(NoSuchElementException.class, it::next);
    }

    @Test
    void iterator_hasNext_falseOnEmpty() {
        CSharpLinkedList<String> list = new CSharpLinkedList<>();
        Iterator<String> it = list.iterator();
        assertFalse(it.hasNext());
    }

    // ---- addAfter / addBefore ----

    @Test
    void addAfter_middle() {
        CSharpLinkedList<String> list = new CSharpLinkedList<>();
        list.addLast("a");
        CSharpLinkedListNode<String> nodeA = list.getFirst();
        list.addLast("c");
        list.addAfter(nodeA, "b");

        List<String> result = new ArrayList<>();
        for (String s : list) result.add(s);
        assertEquals(List.of("a", "b", "c"), result);
        assertEquals(3, list.getCount());
    }

    @Test
    void addAfter_last() {
        CSharpLinkedList<String> list = new CSharpLinkedList<>();
        list.addLast("a");
        CSharpLinkedListNode<String> nodeA = list.getFirst();
        list.addAfter(nodeA, "b");

        assertEquals("b", list.getLast().getValue());
        assertEquals(2, list.getCount());
    }

    @Test
    void addBefore_first() {
        CSharpLinkedList<String> list = new CSharpLinkedList<>();
        list.addLast("b");
        CSharpLinkedListNode<String> nodeB = list.getFirst();
        list.addBefore(nodeB, "a");

        assertEquals("a", list.getFirst().getValue());
        assertEquals(2, list.getCount());

        List<String> result = new ArrayList<>();
        for (String s : list) result.add(s);
        assertEquals(List.of("a", "b"), result);
    }

    @Test
    void addBefore_middle() {
        CSharpLinkedList<String> list = new CSharpLinkedList<>();
        list.addLast("a");
        list.addLast("c");
        CSharpLinkedListNode<String> nodeC = list.getLast();
        list.addBefore(nodeC, "b");

        List<String> result = new ArrayList<>();
        for (String s : list) result.add(s);
        assertEquals(List.of("a", "b", "c"), result);
        assertEquals(3, list.getCount());
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
        assertNotNull(first.getNext());
        assertEquals("b", first.getNext().getValue());

        CSharpLinkedListNode<String> middle = first.getNext();
        assertEquals("b", middle.getValue());
        assertEquals("a", middle.getPrevious().getValue());
        assertEquals("c", middle.getNext().getValue());

        CSharpLinkedListNode<String> last = list.getLast();
        assertEquals("c", last.getValue());
        assertNull(last.getNext());
        assertNotNull(last.getPrevious());
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
        CSharpLinkedListNode<String> node = list.getFirst();
        assertSame(list, node.getList());
    }

    // ---- addFirst/addLast with node ----

    @Test
    void addFirst_node() {
        CSharpLinkedList<String> list = new CSharpLinkedList<>();
        list.addLast("b");
        CSharpLinkedList<String> otherList = new CSharpLinkedList<>();
        CSharpLinkedListNode<String> nodeA = otherList.addLast("a");

        list.addFirst(nodeA);
        assertEquals(2, list.getCount());
        assertEquals("a", list.getFirst().getValue());
        // node should be removed from otherList
        assertEquals(0, otherList.getCount());
    }

    @Test
    void addLast_node() {
        CSharpLinkedList<String> list = new CSharpLinkedList<>();
        list.addLast("a");
        CSharpLinkedList<String> otherList = new CSharpLinkedList<>();
        CSharpLinkedListNode<String> nodeB = otherList.addLast("b");

        list.addLast(nodeB);
        assertEquals(2, list.getCount());
        assertEquals("b", list.getLast().getValue());
        assertEquals(0, otherList.getCount());
    }

    // ---- clear ----

    @Test
    void clear() {
        CSharpLinkedList<String> list = new CSharpLinkedList<>();
        list.addLast("a");
        list.addLast("b");
        list.clear();
        assertEquals(0, list.getCount());
        assertNull(list.getFirst());
        assertNull(list.getLast());
    }

    // ---- find / findLast ----

    @Test
    void find_existing() {
        CSharpLinkedList<String> list = new CSharpLinkedList<>();
        list.addLast("a");
        list.addLast("b");
        list.addLast("b");
        CSharpLinkedListNode<String> found = list.find("b");
        assertNotNull(found);
        assertEquals("b", found.getValue());
        // find returns first occurrence
        assertEquals("a", found.getPrevious().getValue());
    }

    @Test
    void find_nonExisting() {
        CSharpLinkedList<String> list = new CSharpLinkedList<>();
        list.addLast("a");
        assertNull(list.find("z"));
    }

    @Test
    void findLast_existing() {
        CSharpLinkedList<String> list = new CSharpLinkedList<>();
        list.addLast("b");
        list.addLast("a");
        list.addLast("b");
        CSharpLinkedListNode<String> found = list.findLast("b");
        assertNotNull(found);
        assertEquals("b", found.getValue());
        // findLast returns last occurrence
        assertEquals("a", found.getPrevious().getValue());
    }

    // ---- copyTo ----

    @Test
    void copyTo() {
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
        assertNull(arr[4]);
    }

    // ---- clone ----

    @Test
    void clone_createsCopy() {
        CSharpLinkedList<String> list = new CSharpLinkedList<>();
        list.addLast("a");
        list.addLast("b");
        CSharpLinkedList<String> cloned = list.clone();
        assertEquals(2, cloned.getCount());
        assertEquals("a", cloned.getFirst().getValue());
        // Modifying clone should not affect original
        cloned.remove("a");
        assertEquals(2, list.getCount());
    }

    // ---- collection constructor ----

    @Test
    void collectionConstructor() {
        Collection<String> c = List.of("a", "b", "c");
        CSharpLinkedList<String> list = new CSharpLinkedList<>(c);
        assertEquals(3, list.getCount());
        assertEquals("a", list.getFirst().getValue());
        assertEquals("c", list.getLast().getValue());
    }

    // ---- Generic with Integer ----

    @Test
    void integerLinkedList() {
        CSharpLinkedList<Integer> list = new CSharpLinkedList<>();
        list.addLast(1);
        list.addLast(2);
        list.addLast(3);
        assertTrue(list.contains(2));
        assertFalse(list.contains(4));
        assertEquals(3, list.getCount());
    }
}

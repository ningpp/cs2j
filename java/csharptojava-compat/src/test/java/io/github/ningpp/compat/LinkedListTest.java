package io.github.ningpp.compat;

import org.junit.jupiter.api.Test;
import java.util.ArrayList;
import java.util.Iterator;
import java.util.List;
import java.util.NoSuchElementException;
import static org.junit.jupiter.api.Assertions.*;

/**
 * Tests for LinkedListWithNodes and LinkedListNode.
 */
class LinkedListTest {

    // ---- Basic operations ----

    @Test
    void emptyList() {
        LinkedListWithNodes<String> list = new LinkedListWithNodes<>();
        assertEquals(0, list.size());
        assertTrue(list.isEmpty());
        assertNull(list.getFirst());
        assertNull(list.getLast());
    }

    @Test
    void addFirst_singleElement() {
        LinkedListWithNodes<String> list = new LinkedListWithNodes<>();
        list.addFirst("a");
        assertEquals(1, list.size());
        assertFalse(list.isEmpty());
        assertEquals("a", list.getFirst().getValue());
        assertEquals("a", list.getLast().getValue());
    }

    @Test
    void addLast_singleElement() {
        LinkedListWithNodes<String> list = new LinkedListWithNodes<>();
        list.addLast("a");
        assertEquals(1, list.size());
        assertEquals("a", list.getFirst().getValue());
        assertEquals("a", list.getLast().getValue());
    }

    @Test
    void addFirst_multipleElements() {
        LinkedListWithNodes<String> list = new LinkedListWithNodes<>();
        list.addFirst("c");
        list.addFirst("b");
        list.addFirst("a");
        assertEquals(3, list.size());
        assertEquals("a", list.getFirst().getValue());
        assertEquals("c", list.getLast().getValue());
    }

    @Test
    void addLast_multipleElements() {
        LinkedListWithNodes<String> list = new LinkedListWithNodes<>();
        list.addLast("a");
        list.addLast("b");
        list.addLast("c");
        assertEquals(3, list.size());
        assertEquals("a", list.getFirst().getValue());
        assertEquals("c", list.getLast().getValue());
    }

    // ---- add method (boolean) ----

    @Test
    void add_returnsTrue() {
        LinkedListWithNodes<String> list = new LinkedListWithNodes<>();
        assertTrue(list.add("a"));
        assertEquals(1, list.size());
    }

    // ---- contains ----

    @Test
    void contains_existing() {
        LinkedListWithNodes<String> list = new LinkedListWithNodes<>();
        list.addLast("a");
        list.addLast("b");
        assertTrue(list.contains("a"));
        assertTrue(list.contains("b"));
    }

    @Test
    void contains_nonExisting() {
        LinkedListWithNodes<String> list = new LinkedListWithNodes<>();
        list.addLast("a");
        assertFalse(list.contains("z"));
    }

    @Test
    void contains_null() {
        LinkedListWithNodes<String> list = new LinkedListWithNodes<>();
        list.addLast((String) null);
        assertTrue(list.contains(null));
    }

    // ---- remove by value ----

    @Test
    void remove_existingValue() {
        LinkedListWithNodes<String> list = new LinkedListWithNodes<>();
        list.addLast("a");
        list.addLast("b");
        list.addLast("c");
        assertTrue(list.remove("b"));
        assertEquals(2, list.size());
        assertFalse(list.contains("b"));
    }

    @Test
    void remove_nonExistingValue() {
        LinkedListWithNodes<String> list = new LinkedListWithNodes<>();
        list.addLast("a");
        assertFalse(list.remove("z"));
        assertEquals(1, list.size());
    }

    @Test
    void remove_firstElement() {
        LinkedListWithNodes<String> list = new LinkedListWithNodes<>();
        list.addLast("a");
        list.addLast("b");
        assertTrue(list.remove("a"));
        assertEquals("b", list.getFirst().getValue());
    }

    @Test
    void remove_lastElement() {
        LinkedListWithNodes<String> list = new LinkedListWithNodes<>();
        list.addLast("a");
        list.addLast("b");
        assertTrue(list.remove("b"));
        assertEquals("a", list.getLast().getValue());
    }

    @Test
    void remove_singleElement() {
        LinkedListWithNodes<String> list = new LinkedListWithNodes<>();
        list.addLast("a");
        assertTrue(list.remove("a"));
        assertTrue(list.isEmpty());
        assertNull(list.getFirst());
        assertNull(list.getLast());
    }

    // ---- remove by node ----

    @Test
    void removeNode_directly() {
        LinkedListWithNodes<String> list = new LinkedListWithNodes<>();
        list.addLast("a");
        LinkedListNode<String> nodeB = list.addLast("b");
        list.addLast("c");
        assertTrue(list.remove(nodeB));
        assertEquals(2, list.size());
        assertFalse(list.contains("b"));
    }

    // ---- Iteration ----

    @Test
    void iteration_correctOrder() {
        LinkedListWithNodes<String> list = new LinkedListWithNodes<>();
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
        LinkedListWithNodes<String> list = new LinkedListWithNodes<>();
        List<String> result = new ArrayList<>();
        for (String s : list) {
            result.add(s);
        }
        assertTrue(result.isEmpty());
    }

    @Test
    void iterator_throwsWhenExhausted() {
        LinkedListWithNodes<String> list = new LinkedListWithNodes<>();
        list.addLast("a");
        Iterator<String> it = list.iterator();
        assertEquals("a", it.next());
        assertThrows(NoSuchElementException.class, it::next);
    }

    @Test
    void iterator_hasNext_falseOnEmpty() {
        LinkedListWithNodes<String> list = new LinkedListWithNodes<>();
        Iterator<String> it = list.iterator();
        assertFalse(it.hasNext());
    }

    // ---- addAfter / addBefore ----

    @Test
    void addAfter_middle() {
        LinkedListWithNodes<String> list = new LinkedListWithNodes<>();
        list.addLast("a");
        LinkedListNode<String> nodeA = list.getFirst();
        list.addLast("c");
        list.addAfter(nodeA, "b");

        List<String> result = new ArrayList<>();
        for (String s : list) result.add(s);
        assertEquals(List.of("a", "b", "c"), result);
        assertEquals(3, list.size());
    }

    @Test
    void addAfter_last() {
        LinkedListWithNodes<String> list = new LinkedListWithNodes<>();
        list.addLast("a");
        LinkedListNode<String> nodeA = list.getFirst();
        list.addAfter(nodeA, "b");

        assertEquals("b", list.getLast().getValue());
        assertEquals(2, list.size());
    }

    @Test
    void addBefore_first() {
        LinkedListWithNodes<String> list = new LinkedListWithNodes<>();
        list.addLast("b");
        LinkedListNode<String> nodeB = list.getFirst();
        list.addBefore(nodeB, "a");

        assertEquals("a", list.getFirst().getValue());
        assertEquals(2, list.size());

        List<String> result = new ArrayList<>();
        for (String s : list) result.add(s);
        assertEquals(List.of("a", "b"), result);
    }

    @Test
    void addBefore_middle() {
        LinkedListWithNodes<String> list = new LinkedListWithNodes<>();
        list.addLast("a");
        list.addLast("c");
        LinkedListNode<String> nodeC = list.getLast();
        list.addBefore(nodeC, "b");

        List<String> result = new ArrayList<>();
        for (String s : list) result.add(s);
        assertEquals(List.of("a", "b", "c"), result);
        assertEquals(3, list.size());
    }

    // ---- Node navigation ----

    @Test
    void node_getNext_getPrevious() {
        LinkedListWithNodes<String> list = new LinkedListWithNodes<>();
        list.addLast("a");
        list.addLast("b");
        list.addLast("c");

        LinkedListNode<String> first = list.getFirst();
        assertEquals("a", first.getValue());
        assertNull(first.getPrevious());
        assertNotNull(first.getNext());
        assertEquals("b", first.getNext().getValue());

        LinkedListNode<String> middle = first.getNext();
        assertEquals("b", middle.getValue());
        assertEquals("a", middle.getPrevious().getValue());
        assertEquals("c", middle.getNext().getValue());

        LinkedListNode<String> last = list.getLast();
        assertEquals("c", last.getValue());
        assertNull(last.getNext());
        assertNotNull(last.getPrevious());
    }

    @Test
    void node_setValue() {
        LinkedListWithNodes<String> list = new LinkedListWithNodes<>();
        list.addLast("a");
        LinkedListNode<String> node = list.getFirst();
        node.setValue("modified");
        assertEquals("modified", node.getValue());
        assertEquals("modified", list.getFirst().getValue());
    }

    @Test
    void node_getList() {
        LinkedListWithNodes<String> list = new LinkedListWithNodes<>();
        list.addLast("a");
        LinkedListNode<String> node = list.getFirst();
        assertSame(list, node.getList());
    }

    // ---- addFirst/addLast with node ----

    @Test
    void addFirst_node() {
        LinkedListWithNodes<String> list = new LinkedListWithNodes<>();
        list.addLast("b");
        LinkedListWithNodes<String> otherList = new LinkedListWithNodes<>();
        LinkedListNode<String> nodeA = otherList.addLast("a");

        list.addFirst(nodeA);
        assertEquals(2, list.size());
        assertEquals("a", list.getFirst().getValue());
        // node should be removed from otherList
        assertEquals(0, otherList.size());
    }

    @Test
    void addLast_node() {
        LinkedListWithNodes<String> list = new LinkedListWithNodes<>();
        list.addLast("a");
        LinkedListWithNodes<String> otherList = new LinkedListWithNodes<>();
        LinkedListNode<String> nodeB = otherList.addLast("b");

        list.addLast(nodeB);
        assertEquals(2, list.size());
        assertEquals("b", list.getLast().getValue());
        assertEquals(0, otherList.size());
    }

    // ---- clear ----

    @Test
    void clear() {
        LinkedListWithNodes<String> list = new LinkedListWithNodes<>();
        list.addLast("a");
        list.addLast("b");
        list.clear();
        assertTrue(list.isEmpty());
        assertEquals(0, list.size());
        assertNull(list.getFirst());
        assertNull(list.getLast());
    }

    // ---- Generic with Integer ----

    @Test
    void integerLinkedList() {
        LinkedListWithNodes<Integer> list = new LinkedListWithNodes<>();
        list.addLast(1);
        list.addLast(2);
        list.addLast(3);
        assertTrue(list.contains(2));
        assertFalse(list.contains(4));
        assertEquals(3, list.size());
    }
}

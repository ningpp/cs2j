package io.github.ningpp.compat;

import org.junit.jupiter.api.Test;
import static org.junit.jupiter.api.Assertions.*;

import java.util.NoSuchElementException;

class CSharpPriorityQueueTest {

    // ---- Construction ----

    @Test
    void defaultConstructor_createsEmpty() {
        CSharpPriorityQueue<String, Integer> pq = new CSharpPriorityQueue<>();
        assertEquals(0, pq.getCount());
    }

    // ---- Enqueue / Dequeue / Peek ----

    @Test
    void enqueue_andDequeue_priorityOrder() {
        CSharpPriorityQueue<String, Integer> pq = new CSharpPriorityQueue<>();
        pq.enqueue("low", 3);
        pq.enqueue("high", 1);
        pq.enqueue("medium", 2);
        assertEquals(3, pq.getCount());
        assertEquals("high", pq.peek());
        assertEquals("high", pq.dequeue());
        assertEquals("medium", pq.peek());
    }

    @Test
    void dequeue_empty_throws() {
        CSharpPriorityQueue<String, Integer> pq = new CSharpPriorityQueue<>();
        assertThrows(NoSuchElementException.class, pq::dequeue);
    }

    @Test
    void peek_empty_throws() {
        CSharpPriorityQueue<String, Integer> pq = new CSharpPriorityQueue<>();
        assertThrows(NoSuchElementException.class, pq::peek);
    }

    // ---- TryDequeue / TryPeek ----

    @Test
    void tryDequeue_succeedsOnNonEmpty() {
        CSharpPriorityQueue<String, Integer> pq = new CSharpPriorityQueue<>();
        pq.enqueue("item", 1);
        assertEquals("item", pq.tryDequeue());
        assertEquals(0, pq.getCount());
    }

    @Test
    void tryDequeue_failsOnEmpty() {
        CSharpPriorityQueue<String, Integer> pq = new CSharpPriorityQueue<>();
        assertNull(pq.tryDequeue());
    }

    @Test
    void tryPeek_succeedsOnNonEmpty() {
        CSharpPriorityQueue<String, Integer> pq = new CSharpPriorityQueue<>();
        pq.enqueue("item", 1);
        assertEquals("item", pq.tryPeek());
        assertEquals(1, pq.getCount());
    }

    @Test
    void tryPeek_failsOnEmpty() {
        CSharpPriorityQueue<String, Integer> pq = new CSharpPriorityQueue<>();
        assertNull(pq.tryPeek());
    }

    // ---- Clear ----

    @Test
    void clear_emptiesQueue() {
        CSharpPriorityQueue<String, Integer> pq = new CSharpPriorityQueue<>();
        pq.enqueue("a", 1);
        pq.enqueue("b", 2);
        pq.clear();
        assertEquals(0, pq.getCount());
    }

    // ---- EnsureCapacity / TrimExcess ----

    @Test
    void ensureCapacity_doesNotThrow() {
        CSharpPriorityQueue<String, Integer> pq = new CSharpPriorityQueue<>();
        pq.ensureCapacity(100);
        assertEquals(0, pq.getCount());
    }

    @Test
    void trimExcess_doesNotThrow() {
        CSharpPriorityQueue<String, Integer> pq = new CSharpPriorityQueue<>();
        pq.enqueue("a", 1);
        pq.trimExcess();
        assertEquals(1, pq.getCount());
    }

    // ---- Iterator ----

    @Test
    void iterator_iteratesAllElements() {
        CSharpPriorityQueue<String, Integer> pq = new CSharpPriorityQueue<>();
        pq.enqueue("a", 1);
        pq.enqueue("b", 2);
        java.util.List<String> items = new java.util.ArrayList<>();
        var it = pq.iterator();
        while (it.hasNext()) {
            items.add(it.next());
        }
        assertEquals(2, items.size());
        assertTrue(items.contains("a"));
        assertTrue(items.contains("b"));
    }

    // ---- Oracle data validation (PriorityQueue<string,int>) ----

    @Test
    void oracleData_priorityQueueOperations() {
        CSharpPriorityQueue<String, Integer> pq = new CSharpPriorityQueue<>();
        pq.enqueue("low", 3);
        pq.enqueue("high", 1);
        pq.enqueue("medium", 2);
        assertEquals(3, pq.getCount());
        assertEquals("high", pq.peek());
        assertEquals("high", pq.dequeue());
        assertEquals("medium", pq.peek());
        assertEquals(2, pq.getCount());
    }
}

package io.github.ningpp.compat;

import org.junit.jupiter.api.Test;
import static org.junit.jupiter.api.Assertions.*;

import java.util.Arrays;
import java.util.NoSuchElementException;

class CSharpQueueTest {

    // ---- Construction ----

    @Test
    void defaultConstructor_createsEmpty() {
        CSharpQueue<String> q = new CSharpQueue<>();
        assertEquals(0, q.getCount());
    }

    @Test
    void capacityConstructor_createsEmpty() {
        CSharpQueue<String> q = new CSharpQueue<>(10);
        assertEquals(0, q.getCount());
    }

    @Test
    void collectionConstructor_copiesElements() {
        CSharpQueue<String> q = new CSharpQueue<>(Arrays.asList("a", "b"));
        assertEquals(2, q.getCount());
    }

    // ---- Enqueue / Dequeue / Peek ----

    @Test
    void enqueue_andDequeue() {
        CSharpQueue<String> q = new CSharpQueue<>();
        q.enqueue("first");
        q.enqueue("second");
        q.enqueue("third");
        assertEquals(3, q.getCount());
        assertEquals("first", q.peek());
        assertEquals("first", q.dequeue());
        assertEquals("second", q.peek());
    }

    @Test
    void dequeue_empty_throws() {
        CSharpQueue<String> q = new CSharpQueue<>();
        assertThrows(NoSuchElementException.class, q::dequeue);
    }

    @Test
    void peek_empty_throws() {
        CSharpQueue<String> q = new CSharpQueue<>();
        assertThrows(NoSuchElementException.class, q::peek);
    }

    // ---- TryDequeue / TryPeek ----

    @Test
    void tryDequeue_succeedsOnNonEmpty() {
        CSharpQueue<String> q = new CSharpQueue<>();
        q.enqueue("hello");
        assertEquals("hello", q.tryDequeue());
        assertEquals(0, q.getCount());
    }

    @Test
    void tryDequeue_failsOnEmpty() {
        CSharpQueue<String> q = new CSharpQueue<>();
        assertNull(q.tryDequeue());
    }

    @Test
    void tryPeek_succeedsOnNonEmpty() {
        CSharpQueue<String> q = new CSharpQueue<>();
        q.enqueue("hello");
        assertEquals("hello", q.tryPeek());
        assertEquals(1, q.getCount());
    }

    @Test
    void tryPeek_failsOnEmpty() {
        CSharpQueue<String> q = new CSharpQueue<>();
        assertNull(q.tryPeek());
    }

    // ---- Contains ----

    @Test
    void contains_foundAndMissing() {
        CSharpQueue<String> q = new CSharpQueue<>();
        q.enqueue("a");
        q.enqueue("b");
        assertTrue(q.contains("a"));
        assertFalse(q.contains("z"));
    }

    // ---- CopyTo ----

    @Test
    void copyTo_copiesToIndex() {
        CSharpQueue<String> q = new CSharpQueue<>();
        q.enqueue("a");
        q.enqueue("b");
        String[] arr = new String[4];
        q.copyTo(arr, 1);
        assertNull(arr[0]);
        assertEquals("a", arr[1]);
        assertEquals("b", arr[2]);
    }

    // ---- ToArray ----

    @Test
    void toArray_returnsElements() {
        CSharpQueue<String> q = new CSharpQueue<>();
        q.enqueue("a");
        q.enqueue("b");
        Object[] arr = q.toArray();
        assertArrayEquals(new Object[]{"a", "b"}, arr);
    }

    // ---- Clone ----

    @Test
    void clone_createsCopy() {
        CSharpQueue<String> q = new CSharpQueue<>();
        q.enqueue("a");
        q.enqueue("b");
        CSharpQueue<String> cloned = q.clone();
        assertEquals(2, cloned.getCount());
        assertEquals("a", cloned.peek());
        cloned.dequeue();
        assertEquals(2, q.getCount());
    }

    // ---- Clear ----

    @Test
    void clear_emptiesQueue() {
        CSharpQueue<String> q = new CSharpQueue<>();
        q.enqueue("a");
        q.enqueue("b");
        q.clear();
        assertEquals(0, q.getCount());
    }

    // ---- TrimExcess / EnsureCapacity ----

    @Test
    void trimExcess_doesNotThrow() {
        CSharpQueue<String> q = new CSharpQueue<>();
        q.enqueue("a");
        q.trimExcess();
        assertEquals(1, q.getCount());
    }

    @Test
    void ensureCapacity_doesNotThrow() {
        CSharpQueue<String> q = new CSharpQueue<>();
        q.ensureCapacity(100);
        assertEquals(0, q.getCount());
    }

    // ---- Oracle data validation (Queue<string>) ----

    @Test
    void oracleData_queueOperations() {
        CSharpQueue<String> q = new CSharpQueue<>();
        q.enqueue("first");
        q.enqueue("second");
        q.enqueue("third");
        assertEquals(3, q.getCount());
        assertEquals("first", q.peek());
        assertEquals("first", q.dequeue());
        assertTrue(q.contains("second"));
        Object[] arr = q.toArray();
        assertArrayEquals(new Object[]{"second", "third"}, arr);
    }
}

package io.github.ningpp.compat;

import org.junit.jupiter.api.Test;
import static org.junit.jupiter.api.Assertions.*;

import java.util.Arrays;
import java.util.NoSuchElementException;

class CSharpObjQueueTest {

    // ---- Construction ----

    @Test
    void defaultConstructor_createsEmpty() {
        CSharpObjQueue q = new CSharpObjQueue();
        assertEquals(0, q.getCount());
    }

    @Test
    void collectionConstructor_copiesElements() {
        CSharpObjQueue q = new CSharpObjQueue(Arrays.asList("a", "b"));
        assertEquals(2, q.getCount());
    }

    // ---- Enqueue / Dequeue / Peek ----

    @Test
    void enqueue_andDequeue() {
        CSharpObjQueue q = new CSharpObjQueue();
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
        CSharpObjQueue q = new CSharpObjQueue();
        assertThrows(NoSuchElementException.class, q::dequeue);
    }

    @Test
    void peek_empty_throws() {
        CSharpObjQueue q = new CSharpObjQueue();
        assertThrows(NoSuchElementException.class, q::peek);
    }

    // ---- Contains ----

    @Test
    void contains_foundAndMissing() {
        CSharpObjQueue q = new CSharpObjQueue();
        q.enqueue("a");
        q.enqueue("b");
        assertTrue(q.contains("a"));
        assertFalse(q.contains("z"));
    }

    // ---- CopyTo ----

    @Test
    void copyTo_copiesToIndex() {
        CSharpObjQueue q = new CSharpObjQueue();
        q.enqueue("a");
        q.enqueue("b");
        Object[] arr = new Object[4];
        q.copyTo(arr, 1);
        assertNull(arr[0]);
        assertEquals("a", arr[1]);
        assertEquals("b", arr[2]);
    }

    // ---- ToArray ----

    @Test
    void toArray_returnsElements() {
        CSharpObjQueue q = new CSharpObjQueue();
        q.enqueue("a");
        q.enqueue("b");
        Object[] arr = q.toArray();
        assertArrayEquals(new Object[]{"a", "b"}, arr);
    }

    // ---- Clone ----

    @Test
    void clone_createsCopy() {
        CSharpObjQueue q = new CSharpObjQueue();
        q.enqueue("a");
        q.enqueue("b");
        CSharpObjQueue cloned = q.clone();
        assertEquals(2, cloned.getCount());
        assertEquals("a", cloned.peek());
        // Modifying clone should not affect original
        cloned.dequeue();
        assertEquals(2, q.getCount());
    }

    // ---- Clear ----

    @Test
    void clear_emptiesQueue() {
        CSharpObjQueue q = new CSharpObjQueue();
        q.enqueue("a");
        q.enqueue("b");
        q.clear();
        assertEquals(0, q.getCount());
    }

    // ---- Oracle data validation (Queue) ----

    @Test
    void oracleData_queueOperations() {
        CSharpObjQueue q = new CSharpObjQueue();
        q.enqueue("x");
        q.enqueue(99);
        assertEquals(2, q.getCount());
        assertEquals("x", q.peek());
        assertEquals("x", q.dequeue());
        assertTrue(q.contains(99));
        Object[] arr = q.toArray();
        assertEquals(1, arr.length);
        assertEquals(99, arr[0]);
    }
}

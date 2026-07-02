package io.github.ningpp.compat;

import org.junit.jupiter.api.Test;
import static org.junit.jupiter.api.Assertions.*;

import java.util.Arrays;
import java.util.NoSuchElementException;

class CSharpObjStackTest {

    // ---- Construction ----

    @Test
    void defaultConstructor_createsEmpty() {
        CSharpObjStack s = new CSharpObjStack();
        assertEquals(0, s.getCount());
    }

    @Test
    void capacityConstructor_createsEmpty() {
        CSharpObjStack s = new CSharpObjStack(10);
        assertEquals(0, s.getCount());
    }

    @Test
    void collectionConstructor_copiesElements() {
        CSharpObjStack s = new CSharpObjStack(Arrays.asList("a", "b"));
        assertEquals(2, s.getCount());
    }

    // ---- Push / Pop / Peek ----

    @Test
    void push_andPop() {
        CSharpObjStack s = new CSharpObjStack();
        s.push("a");
        s.push(42);
        s.push(null);
        assertEquals(3, s.getCount());
        assertNull(s.peek());
        assertNull(s.pop());
        assertEquals(42, s.peek());
    }

    @Test
    void pop_empty_throws() {
        CSharpObjStack s = new CSharpObjStack();
        assertThrows(NoSuchElementException.class, s::pop);
    }

    @Test
    void peek_empty_throws() {
        CSharpObjStack s = new CSharpObjStack();
        assertThrows(NoSuchElementException.class, s::peek);
    }

    // ---- Contains ----

    @Test
    void contains_foundAndMissing() {
        CSharpObjStack s = new CSharpObjStack();
        s.push("a");
        s.push(42);
        assertTrue(s.contains("a"));
        assertFalse(s.contains("z"));
    }

    // ---- CopyTo ----

    @Test
    void copyTo_copiesLIFOOrder() {
        CSharpObjStack s = new CSharpObjStack();
        s.push("a");
        s.push("b");
        Object[] arr = new Object[4];
        s.copyTo(arr, 1);
        assertNull(arr[0]);
        assertEquals("b", arr[1]);
        assertEquals("a", arr[2]);
    }

    // ---- ToArray ----

    @Test
    void toArray_returnsLIFOOrder() {
        CSharpObjStack s = new CSharpObjStack();
        s.push("a");
        s.push(42);
        Object[] arr = s.toArray();
        assertEquals(2, arr.length);
        assertEquals(42, arr[0]);
        assertEquals("a", arr[1]);
    }

    // ---- Clone ----

    @Test
    void clone_createsCopy() {
        CSharpObjStack s = new CSharpObjStack();
        s.push("a");
        s.push("b");
        CSharpObjStack cloned = s.clone();
        assertEquals(2, cloned.getCount());
        assertEquals("b", cloned.peek());
        // Modifying clone should not affect original
        cloned.pop();
        assertEquals(2, s.getCount());
    }

    // ---- Clear ----

    @Test
    void clear_emptiesStack() {
        CSharpObjStack s = new CSharpObjStack();
        s.push("a");
        s.push("b");
        s.clear();
        assertEquals(0, s.getCount());
    }

    // ---- Oracle data validation (Stack - non-generic) ----

    @Test
    void oracleData_stackOperations() {
        CSharpObjStack s = new CSharpObjStack();
        s.push("a");
        s.push(42);
        s.push(null);
        assertEquals(3, s.getCount());
        assertNull(s.peek());
        assertNull(s.pop());
        assertTrue(s.contains("a"));
        Object[] arr = s.toArray();
        assertEquals(2, arr.length);
        assertEquals(42, arr[0]);
        assertEquals("a", arr[1]);
    }
}

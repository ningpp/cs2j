package io.github.ningpp.compat;

import org.junit.jupiter.api.Test;
import static org.junit.jupiter.api.Assertions.*;

import java.util.Arrays;
import java.util.List;
import java.util.NoSuchElementException;

class CSharpStackTest {

    @Test
    void defaultConstructor_createsEmptyStack() {
        CSharpStack<Integer> s = new CSharpStack<>();
        assertEquals(0, s.getCount());
    }

    @Test
    void capacityConstructor_createsEmptyStack() {
        CSharpStack<String> s = new CSharpStack<>(100);
        assertEquals(0, s.getCount());
    }

    @Test
    void collectionConstructor_pushesInOrder() {
        List<String> items = Arrays.asList("A", "B", "C");
        CSharpStack<String> s = new CSharpStack<>(items);
        assertEquals(3, s.getCount());
        assertEquals("C", s.peek());
    }

    @Test
    void pushPopPeek_basicOperations() {
        CSharpStack<Integer> s = new CSharpStack<>();
        s.push(10);
        s.push(20);
        s.push(30);
        assertEquals(30, s.peek());
        assertEquals(30, s.pop());
        assertEquals(20, s.peek());
        assertEquals(20, s.pop());
        assertEquals(10, s.pop());
        assertEquals(0, s.getCount());
    }

    @Test
    void pop_emptyStack_throws() {
        CSharpStack<Integer> s = new CSharpStack<>();
        assertThrows(NoSuchElementException.class, s::pop);
    }

    @Test
    void peek_emptyStack_throws() {
        CSharpStack<Integer> s = new CSharpStack<>();
        assertThrows(NoSuchElementException.class, s::peek);
    }

    @Test
    void getCount_reflectsState() {
        CSharpStack<String> s = new CSharpStack<>();
        assertEquals(0, s.getCount());
        s.push("x");
        assertEquals(1, s.getCount());
        s.pop();
        assertEquals(0, s.getCount());
    }

    @Test
    void contains_findsElements() {
        CSharpStack<String> s = new CSharpStack<>();
        s.push("hello");
        s.push("world");
        assertTrue(s.contains("hello"));
        assertTrue(s.contains("world"));
        assertFalse(s.contains("missing"));
        assertFalse(s.contains(null));
    }

    @Test
    void clear_emptiesStack() {
        CSharpStack<Integer> s = new CSharpStack<>();
        s.push(1);
        s.push(2);
        s.clear();
        assertEquals(0, s.getCount());
    }

    @Test
    void ensureCapacity_preservesElements() {
        CSharpStack<String> s = new CSharpStack<>();
        s.push("A");
        s.push("B");
        s.push("C");
        s.ensureCapacity(100);
        assertEquals(3, s.getCount());
        assertEquals("C", s.peek());
        assertEquals("C", s.pop());
        assertEquals("B", s.pop());
        assertEquals("A", s.pop());
    }

    @Test
    void ensureCapacity_negativeCapacity_throws() {
        CSharpStack<String> s = new CSharpStack<>();
        assertThrows(IllegalArgumentException.class, () -> s.ensureCapacity(-1));
    }

    @Test
    void ensureCapacity_zeroCapacity_preservesElements() {
        CSharpStack<Integer> s = new CSharpStack<>();
        s.push(42);
        s.ensureCapacity(0);
        assertEquals(1, s.getCount());
        assertEquals(42, s.peek());
    }

    @Test
    void iterator_returnsLIFOOrder() {
        CSharpStack<String> s = new CSharpStack<>();
        s.push("A");
        s.push("B");
        s.push("C");
        List<String> result = new java.util.ArrayList<>();
        for (String item : s) {
            result.add(item);
        }
        assertEquals(Arrays.asList("C", "B", "A"), result);
    }

    @Test
    void toArray_returnsLIFOOrder() {
        CSharpStack<String> s = new CSharpStack<>();
        s.push("A");
        s.push("B");
        s.push("C");
        Object[] arr = s.toArray();
        assertArrayEquals(new Object[]{"C", "B", "A"}, arr);
    }

    @Test
    void toArrayTyped_returnsLIFOOrder() {
        CSharpStack<String> s = new CSharpStack<>();
        s.push("A");
        s.push("B");
        s.push("C");
        String[] arr = s.toArray(new String[0]);
        assertArrayEquals(new String[]{"C", "B", "A"}, arr);
    }

    @Test
    void toArrayTyped_existingArrayUsed() {
        CSharpStack<Integer> s = new CSharpStack<>();
        s.push(1);
        s.push(2);
        Integer[] arr = new Integer[5];
        Integer[] result = s.toArray(arr);
        assertSame(arr, result);
        assertEquals(2, result[0]);
        assertEquals(1, result[1]);
        assertNull(result[2]);
    }

    @Test
    void copyTo_copiesLIFOOrder() {
        CSharpStack<String> s = new CSharpStack<>();
        s.push("A");
        s.push("B");
        s.push("C");
        String[] arr = new String[5];
        s.copyTo(arr, 1);
        assertNull(arr[0]);
        assertEquals("C", arr[1]);
        assertEquals("B", arr[2]);
        assertEquals("A", arr[3]);
        assertNull(arr[4]);
    }

    @Test
    void tryPeek_succeedsOnNonEmpty() {
        CSharpStack<String> s = new CSharpStack<>();
        s.push("hello");
        assertEquals("hello", s.tryPeek());
        assertEquals(1, s.getCount());
    }

    @Test
    void tryPeek_failsOnEmpty() {
        CSharpStack<String> s = new CSharpStack<>();
        assertNull(s.tryPeek());
    }

    @Test
    void tryPop_succeedsOnNonEmpty() {
        CSharpStack<Integer> s = new CSharpStack<>();
        s.push(42);
        assertEquals(42, s.tryPop());
        assertEquals(0, s.getCount());
    }

    @Test
    void tryPop_failsOnEmpty() {
        CSharpStack<Integer> s = new CSharpStack<>();
        assertNull(s.tryPop());
    }

    @Test
    void trimExcess_preservesElements() {
        CSharpStack<Integer> s = new CSharpStack<>();
        for (int i = 0; i < 100; i++) {
            s.push(i);
        }
        s.trimExcess();
        assertEquals(100, s.getCount());
        assertEquals(99, s.peek());
    }

    @Test
    void getIsReadOnly_returnsFalse() {
        CSharpStack<String> s = new CSharpStack<>();
        assertFalse(s.getIsReadOnly());
    }

    @Test
    void add_pushesToStack() {
        CSharpStack<String> s = new CSharpStack<>();
        s.add("A");
        s.add("B");
        assertEquals(2, s.getCount());
        assertEquals("B", s.peek());
    }

    @Test
    void remove_removesElement() {
        CSharpStack<String> s = new CSharpStack<>();
        s.push("A");
        s.push("B");
        s.push("C");
        assertTrue(s.remove("B"));
        assertEquals(2, s.getCount());
        assertFalse(s.contains("B"));
    }

    @Test
    void clone_createsCopy() {
        CSharpStack<String> s = new CSharpStack<>();
        s.push("A");
        s.push("B");
        CSharpStack<String> c = s.clone();
        assertEquals(2, c.getCount());
        assertEquals("B", c.peek());
        // Modifying clone should not affect original
        c.pop();
        assertEquals(2, s.getCount());
    }
}

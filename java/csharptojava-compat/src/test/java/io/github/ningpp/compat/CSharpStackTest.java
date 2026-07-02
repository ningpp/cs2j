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
        assertEquals(0, s.size());
        assertTrue(s.isEmpty());
    }

    @Test
    void capacityConstructor_createsEmptyStack() {
        CSharpStack<String> s = new CSharpStack<>(100);
        assertEquals(0, s.size());
        assertTrue(s.isEmpty());
    }

    @Test
    void collectionConstructor_pushesInOrder() {
        List<String> items = Arrays.asList("A", "B", "C");
        CSharpStack<String> s = new CSharpStack<>(items);
        assertEquals(3, s.size());
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
        assertTrue(s.isEmpty());
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
    void size_isEmpty_reflectState() {
        CSharpStack<String> s = new CSharpStack<>();
        assertTrue(s.isEmpty());
        assertEquals(0, s.size());
        s.push("x");
        assertFalse(s.isEmpty());
        assertEquals(1, s.size());
        s.pop();
        assertTrue(s.isEmpty());
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
        assertEquals(0, s.size());
        assertTrue(s.isEmpty());
    }

    @Test
    void ensureCapacity_preservesElements() {
        CSharpStack<String> s = new CSharpStack<>();
        s.push("A");
        s.push("B");
        s.push("C");
        s.ensureCapacity(100);
        assertEquals(3, s.size());
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
        assertEquals(1, s.size());
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
    void stream_returnsLIFOOrder() {
        CSharpStack<String> s = new CSharpStack<>();
        s.push("A");
        s.push("B");
        s.push("C");
        List<String> result = s.stream().toList();
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
        ObjectHolder<String> holder = new ObjectHolder<>();
        assertTrue(s.tryPeek(holder));
        assertEquals("hello", holder.value);
        assertEquals(1, s.size());
    }

    @Test
    void tryPeek_failsOnEmpty() {
        CSharpStack<String> s = new CSharpStack<>();
        ObjectHolder<String> holder = new ObjectHolder<>();
        assertFalse(s.tryPeek(holder));
    }

    @Test
    void tryPop_succeedsOnNonEmpty() {
        CSharpStack<Integer> s = new CSharpStack<>();
        s.push(42);
        ObjectHolder<Integer> holder = new ObjectHolder<>();
        assertTrue(s.tryPop(holder));
        assertEquals(42, holder.value);
        assertTrue(s.isEmpty());
    }

    @Test
    void tryPop_failsOnEmpty() {
        CSharpStack<Integer> s = new CSharpStack<>();
        ObjectHolder<Integer> holder = new ObjectHolder<>();
        assertFalse(s.tryPop(holder));
    }

    @Test
    void trimExcess_preservesElements() {
        CSharpStack<Integer> s = new CSharpStack<>();
        for (int i = 0; i < 100; i++) {
            s.push(i);
        }
        s.trimExcess();
        assertEquals(100, s.size());
        assertEquals(99, s.peek());
    }

    @Test
    void trimExcessWithCapacity_preservesElements() {
        CSharpStack<Integer> s = new CSharpStack<>();
        for (int i = 0; i < 10; i++) {
            s.push(i);
        }
        s.trimExcess(50);
        assertEquals(10, s.size());
        assertEquals(9, s.peek());
    }

    @Test
    void getEnumerator_returnsLIFOOrder() {
        CSharpStack<String> s = new CSharpStack<>();
        s.push("A");
        s.push("B");
        s.push("C");
        CSharpEnumerator e = s.getEnumerator();
        assertTrue(e.moveNext());
        assertEquals("C", e.getCurrent());
        assertTrue(e.moveNext());
        assertEquals("B", e.getCurrent());
        assertTrue(e.moveNext());
        assertEquals("A", e.getCurrent());
        assertFalse(e.moveNext());
    }

    @Test
    void equals_sameElementsSameOrder() {
        CSharpStack<String> s1 = new CSharpStack<>();
        s1.push("A");
        s1.push("B");
        CSharpStack<String> s2 = new CSharpStack<>();
        s2.push("A");
        s2.push("B");
        assertEquals(s1, s2);
    }

    @Test
    void equals_differentOrder_notEqual() {
        CSharpStack<String> s1 = new CSharpStack<>();
        s1.push("A");
        s1.push("B");
        CSharpStack<String> s2 = new CSharpStack<>();
        s2.push("B");
        s2.push("A");
        assertNotEquals(s1, s2);
    }

    @Test
    void equals_differentSize_notEqual() {
        CSharpStack<String> s1 = new CSharpStack<>();
        s1.push("A");
        CSharpStack<String> s2 = new CSharpStack<>();
        s2.push("A");
        s2.push("B");
        assertNotEquals(s1, s2);
    }

    @Test
    void hashCode_consistentWithEquals() {
        CSharpStack<String> s1 = new CSharpStack<>();
        s1.push("A");
        s1.push("B");
        CSharpStack<String> s2 = new CSharpStack<>();
        s2.push("A");
        s2.push("B");
        assertEquals(s1.hashCode(), s2.hashCode());
    }
}

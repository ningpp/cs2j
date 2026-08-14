package io.github.ningpp.compat;

import org.junit.jupiter.api.Test;
import static org.junit.jupiter.api.Assertions.*;

import java.util.Arrays;

class MemoryExtensionsTest {

    @Test
    void asMemory_byteArray_returnsIntegerMemory() {
        byte[] data = { 1, 2, 3, 4, 5 };
        ReadOnlyMemory<Integer> memory = MemoryExtensions.asMemory(data);
        assertEquals(5, memory.length());
        assertEquals(Integer.valueOf(2), memory.getSpan().get(1));
        assertArrayEquals(new byte[] { 1, 2, 3, 4, 5 }, memory.toByteArray());
    }

    @Test
    void asMemory_byteArray_slices() {
        byte[] data = { 1, 2, 3, 4, 5 };
        ReadOnlyMemory<Integer> fromStart = MemoryExtensions.asMemory(data, 2);
        assertEquals(3, fromStart.length());
        assertEquals(Integer.valueOf(3), fromStart.getSpan().get(0));

        ReadOnlyMemory<Integer> slice = MemoryExtensions.asMemory(data, 1, 3);
        assertEquals(3, slice.length());
        assertEquals(Integer.valueOf(4), slice.getSpan().get(2));
    }

    @Test
    void asMemory_charArray_sliceFromStart() {
        char[] data = { 'a', 'b', 'c', 'd' };
        ReadOnlyMemory<Character> memory = MemoryExtensions.asMemory(data, 1);
        assertEquals(3, memory.length());
        assertEquals(Character.valueOf('b'), memory.getSpan().get(0));
    }

    @Test
    void asMemory_genericArrayOverloads() {
        String[] data = { "a", "b", "c" };
        ReadOnlyMemory<String> whole = MemoryExtensions.asMemory(data);
        assertEquals(3, whole.length());

        ReadOnlyMemory<String> fromStart = MemoryExtensions.asMemory(data, 1);
        assertEquals(2, fromStart.length());
        assertEquals("c", fromStart.getSpan().get(1));

        ReadOnlyMemory<String> slice = MemoryExtensions.asMemory(data, 0, 2);
        assertEquals(2, slice.length());
    }

    @Test
    void asSpan_genericArraySlices() {
        String[] data = { "a", "b", "c", "d" };
        Span<String> fromStart = MemoryExtensions.asSpan(data, 1);
        assertEquals(3, fromStart.length());
        assertEquals("d", fromStart.get(2));

        Span<String> slice = MemoryExtensions.asSpan(data, 1, 2);
        assertEquals(2, slice.length());
        assertEquals("b", slice.get(0));
    }

    @Test
    void endsWith_matchesSuffix() {
        ReadOnlySpan<Character> span = MemoryExtensions.asSpan("hello");
        ReadOnlySpan<Character> suffix = MemoryExtensions.asSpan("llo");
        ReadOnlySpan<Character> nonSuffix = MemoryExtensions.asSpan("xyz");

        assertTrue(MemoryExtensions.endsWith(span, suffix));
        assertFalse(MemoryExtensions.endsWith(span, nonSuffix));

        Span<Character> mutableSpan = new Span<>(new Character[] { 'h', 'e', 'l', 'l', 'o' });
        assertTrue(MemoryExtensions.endsWith(mutableSpan, suffix));
    }

    @Test
    void lastIndexOf_findsLastOccurrence() {
        ReadOnlySpan<Integer> span = MemoryExtensions.asSpan(new byte[] { 1, 2, 3, 2, 1 });
        assertEquals(3, MemoryExtensions.lastIndexOf(span, 2));
        assertEquals(-1, MemoryExtensions.lastIndexOf(span, 9));

        Span<Integer> mutable = new Span<>(new Integer[] { 1, 2, 3, 2, 1 });
        assertEquals(3, MemoryExtensions.lastIndexOf(mutable, 2));
    }

    @Test
    void lastIndexOf_findsLastSubsequence() {
        ReadOnlySpan<Integer> span = MemoryExtensions.asSpan(new byte[] { 1, 2, 1, 2, 3 });
        ReadOnlySpan<Integer> sub = MemoryExtensions.asSpan(new byte[] { 1, 2 });
        assertEquals(2, MemoryExtensions.lastIndexOf(span, sub));

        Span<Integer> mutable = new Span<>(new Integer[] { 1, 2, 1, 2, 3 });
        assertEquals(2, MemoryExtensions.lastIndexOf(mutable, sub));
    }

    @Test
    void containsAny_findsValues() {
        ReadOnlySpan<Integer> span = MemoryExtensions.asSpan(new byte[] { 1, 2, 3 });
        assertTrue(MemoryExtensions.containsAny(span, 5, 2));
        assertFalse(MemoryExtensions.containsAny(span, 5, 6));
        assertTrue(MemoryExtensions.containsAny(span, 5, 6, 1));
        assertFalse(MemoryExtensions.containsAny(span, 5, 6, 7));

        ReadOnlySpan<Integer> values = MemoryExtensions.asSpan(new byte[] { 9, 2 });
        assertTrue(MemoryExtensions.containsAny(span, values));

        Span<Integer> mutable = new Span<>(new Integer[] { 1, 2, 3 });
        assertTrue(MemoryExtensions.containsAny(mutable, 5, 3));
        assertTrue(MemoryExtensions.containsAny(mutable, 5, 6, 3));
        assertTrue(MemoryExtensions.containsAny(mutable, values));
    }

    @Test
    void indexOfAny_spanAndValuesVariants() {
        Span<Integer> mutable = new Span<>(new Integer[] { 1, 2, 3, 4 });
        assertEquals(1, MemoryExtensions.indexOfAny(mutable, 9, 2));
        assertEquals(2, MemoryExtensions.indexOfAny(mutable, 9, 8, 3));
        assertEquals(-1, MemoryExtensions.indexOfAny(mutable, 9, 8));

        ReadOnlySpan<Integer> span = MemoryExtensions.asSpan(new byte[] { 1, 2, 3, 4 });
        ReadOnlySpan<Integer> values = MemoryExtensions.asSpan(new byte[] { 8, 4 });
        assertEquals(3, MemoryExtensions.indexOfAny(span, values));
        assertEquals(3, MemoryExtensions.indexOfAny(mutable, values));
    }

    @Test
    void count_countsOccurrences() {
        ReadOnlySpan<Integer> span = MemoryExtensions.asSpan(new byte[] { 1, 2, 1, 3, 1 });
        assertEquals(3, MemoryExtensions.count(span, 1));
        assertEquals(0, MemoryExtensions.count(span, 9));

        Span<Integer> mutable = new Span<>(new Integer[] { 1, 2, 1, 3, 1 });
        assertEquals(3, MemoryExtensions.count(mutable, 1));
    }

    @Test
    void toUpperAndInvariantVariants() {
        ReadOnlySpan<Character> source = MemoryExtensions.asSpan("abC");
        Span<Character> destination = new Span<>(new Character[3]);

        assertEquals(3, MemoryExtensions.toUpperCase(source, destination, CultureInfo.getInvariantCulture()));
        assertEquals("ABC", new String(new char[] { destination.get(0), destination.get(1), destination.get(2) }));

        assertEquals(3, MemoryExtensions.toUpperCaseInvariant(source, destination));
        assertEquals('A', destination.get(0));

        assertEquals(3, MemoryExtensions.toLowerCaseInvariant(source, destination));
        assertEquals('a', destination.get(0));
        assertEquals('c', destination.get(2));
    }

    @Test
    void compareTo_andEquals_withStringComparison() {
        ReadOnlySpan<Character> a = MemoryExtensions.asSpan("abc");
        ReadOnlySpan<Character> b = MemoryExtensions.asSpan("ABC");
        ReadOnlySpan<Character> c = MemoryExtensions.asSpan("abd");

        assertTrue(MemoryExtensions.compareTo(a, b, StringComparison.OrdinalIgnoreCase) == 0);
        assertTrue(MemoryExtensions.compareTo(a, b, StringComparison.Ordinal) != 0);
        assertTrue(MemoryExtensions.compareTo(a, c, StringComparison.Ordinal) < 0);
        assertTrue(MemoryExtensions.compareTo(c, a, StringComparison.Ordinal) > 0);

        assertTrue(MemoryExtensions.equals(a, b, StringComparison.InvariantCultureIgnoreCase));
        assertFalse(MemoryExtensions.equals(a, c, StringComparison.Ordinal));
    }

    @Test
    void isWhitespace_checksAllWhitespace() {
        assertTrue(MemoryExtensions.isWhitespace(MemoryExtensions.asSpan(" \t\n")));
        assertFalse(MemoryExtensions.isWhitespace(MemoryExtensions.asSpan(" a")));
    }

    @Test
    void sequenceCompareTo_lexicographic() {
        ReadOnlySpan<Integer> a = MemoryExtensions.asSpan(new byte[] { 1, 2, 3 });
        ReadOnlySpan<Integer> b = MemoryExtensions.asSpan(new byte[] { 1, 2, 4 });
        ReadOnlySpan<Integer> prefix = MemoryExtensions.asSpan(new byte[] { 1, 2 });

        assertTrue(MemoryExtensions.sequenceCompareTo(a, b) < 0);
        assertTrue(MemoryExtensions.sequenceCompareTo(b, a) > 0);
        assertTrue(MemoryExtensions.sequenceCompareTo(a, prefix) > 0);
        assertTrue(MemoryExtensions.sequenceCompareTo(a, a) == 0);

        Span<Integer> mutable = new Span<>(new Integer[] { 1, 2, 3 });
        assertTrue(MemoryExtensions.sequenceCompareTo(mutable, b) < 0);
    }

    @Test
    void commonPrefixLength_sharedPrefix() {
        ReadOnlySpan<Character> a = MemoryExtensions.asSpan("hello");
        ReadOnlySpan<Character> b = MemoryExtensions.asSpan("heaven");
        ReadOnlySpan<Character> c = MemoryExtensions.asSpan("zzz");

        assertEquals(2, MemoryExtensions.commonPrefixLength(a, b));
        assertEquals(0, MemoryExtensions.commonPrefixLength(a, c));
    }
}

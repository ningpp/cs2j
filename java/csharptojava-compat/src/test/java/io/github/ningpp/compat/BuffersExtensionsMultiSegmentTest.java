package io.github.ningpp.compat;

import org.junit.jupiter.api.Test;

import static org.junit.jupiter.api.Assertions.assertArrayEquals;
import static org.junit.jupiter.api.Assertions.assertEquals;

/**
 * BuffersExtensions.CopyTo/ToArray must cover the WHOLE sequence, not just its first
 * segment — .NET copies every segment. The generated Utf8JsonReader funnels every
 * Get* method of a multi-segment value token through
 * {@code BuffersExtensions.toByteArray(getValueSequence())}, so a first-segment-only
 * copy truncated multi-segment reads (e.g. allowCommentStackMismatchMultiSegment
 * produced "14" instead of "123456").
 */
public class BuffersExtensionsMultiSegmentTest {

    /** Builds a three-segment chain "ab" | "cd" | "efg" plus a trailing empty segment. */
    private static ReadOnlySequence<Integer> multiSegmentSequence() {
        ReadOnlySequenceSegment<Integer> first = new ReadOnlySequenceSegment<Integer>() {
        };
        first.setMemory(new ReadOnlyMemory<>(new byte[] { 'a', 'b' }));
        ReadOnlySequenceSegment<Integer> second = new ReadOnlySequenceSegment<Integer>() {
        };
        second.setMemory(new ReadOnlyMemory<>(new byte[] { 'c', 'd' }));
        ReadOnlySequenceSegment<Integer> third = new ReadOnlySequenceSegment<Integer>() {
        };
        third.setMemory(new ReadOnlyMemory<>(new byte[] { 'e', 'f', 'g' }));
        ReadOnlySequenceSegment<Integer> empty = new ReadOnlySequenceSegment<Integer>() {
        };
        empty.setMemory(ReadOnlyMemory.getEmpty());
        first.setNext(second);
        second.setRunningIndex(2);
        second.setNext(third);
        third.setRunningIndex(4);
        third.setNext(empty);
        empty.setRunningIndex(7);
        return new ReadOnlySequence<>(first, 0, empty, 0);
    }

    @Test
    public void toByteArray_joinsAllSegments() {
        assertArrayEquals(new byte[] { 'a', 'b', 'c', 'd', 'e', 'f', 'g' },
                BuffersExtensions.toByteArray(multiSegmentSequence()));
    }

    @Test
    public void toArray_joinsAllSegments() {
        Integer[] joined = BuffersExtensions.toArray(multiSegmentSequence());
        assertEquals(7, joined.length);
        assertEquals('a', joined[0].intValue());
        assertEquals('g', joined[6].intValue());
    }

    @Test
    public void copyTo_writesAllSegmentsIntoTheDestination() {
        Span<Integer> destination = new Span<>(new Integer[7]);
        BuffersExtensions.copyTo(multiSegmentSequence(), destination);
        for (int i = 0; i < 7; i++) {
            assertEquals('a' + i, destination.get(i).intValue(), "index " + i);
        }
    }

    @Test
    public void sliceOfMultiSegmentSequence_copiesTheSlicedRange() {
        ReadOnlySequence<Integer> sliced = multiSegmentSequence().slice(1, 4); // "bcde"
        assertArrayEquals(new byte[] { 'b', 'c', 'd', 'e' },
                BuffersExtensions.toByteArray(sliced));
    }
}

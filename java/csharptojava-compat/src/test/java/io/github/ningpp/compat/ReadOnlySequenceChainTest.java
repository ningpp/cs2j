package io.github.ningpp.compat;

import org.junit.jupiter.api.Test;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertTrue;

/**
 * ReadOnlySequence(segment, startIndex, segment, endIndex) must mirror the .NET
 * length semantics: the span runs from startIndex in the first segment to endIndex
 * in the last segment. A trailing EMPTY segment with endIndex=0 (the shape
 * produced by JsonTestHelper.GetSequence with segmentSize=1) must still expose
 * every byte before it.
 */
public class ReadOnlySequenceChainTest {

    private static final class Segment extends ReadOnlySequenceSegment<Integer> {
    }

    @Test
    public void chainWithEmptyLastSegment_exposesAllBytes() {
        Segment first = new Segment();
        first.setMemory(new ReadOnlyMemory<>(new byte[] { '{', 'a' }));
        Segment middle = new Segment();
        middle.setMemory(new ReadOnlyMemory<>(new byte[] { ':', '1' }));
        first.setNext(middle);
        middle.setRunningIndex(2);
        Segment emptyLast = new Segment();
        emptyLast.setMemory(new ReadOnlyMemory<>(new byte[0]));
        middle.setNext(emptyLast);
        emptyLast.setRunningIndex(4);

        ReadOnlySequence<Integer> sequence =
            new ReadOnlySequence<>(first, 0, emptyLast, 0);

        assertEquals(4, sequence.getLength());
        ReadOnlySpan<Integer> span = sequence.getFirst().getSpan();
        assertEquals(2, span.length());
        assertEquals('{', span.get(0));
        assertEquals('a', span.get(1));
    }

    @Test
    public void chainWithEndIndexInsideLastSegment_truncates() {
        Segment first = new Segment();
        first.setMemory(new ReadOnlyMemory<>(new byte[] { 'a', 'b', 'c' }));
        Segment last = new Segment();
        last.setMemory(new ReadOnlyMemory<>(new byte[] { 'd', 'e' }));
        first.setNext(last);
        last.setRunningIndex(3);

        ReadOnlySequence<Integer> sequence =
            new ReadOnlySequence<>(first, 0, last, 1);

        assertEquals(4, sequence.getLength());
        assertEquals(3, sequence.getFirst().getSpan().length());
    }

    @Test
    public void multiSegmentChain_reportsMultiSegmentAndIterates() {
        Segment first = new Segment();
        first.setMemory(new ReadOnlyMemory<>(new byte[] { '{', 'a' }));
        Segment last = new Segment();
        last.setMemory(new ReadOnlyMemory<>(new byte[] { ':', '1' }));
        first.setNext(last);
        last.setRunningIndex(2);

        ReadOnlySequence<Integer> sequence =
            new ReadOnlySequence<>(first, 0, last, 2);

        assertFalse(sequence.getIsSingleSegment());
        assertEquals(4, sequence.getLength());

        ObjectHolder<SequencePosition> pos = new ObjectHolder<>(sequence.getStart());
        ObjectHolder<ReadOnlyMemory<Integer>> mem = new ObjectHolder<>();
        assertTrue(sequence.tryGet(pos, mem, true));
        assertEquals(2, mem.value.getLength());
        assertEquals('{', mem.value.getSpan().get(0));
        assertTrue(sequence.tryGet(pos, mem, true));
        assertEquals(2, mem.value.getLength());
        assertEquals(':', mem.value.getSpan().get(0));
        assertFalse(sequence.tryGet(pos, mem, true));
    }

    @Test
    public void multiSegmentChain_sliceByPositionsUsesByteOffsets() {
        Segment first = new Segment();
        first.setMemory(new ReadOnlyMemory<>(new byte[] { '{', 'a' }));
        Segment last = new Segment();
        last.setMemory(new ReadOnlyMemory<>(new byte[] { ':', '1' }));
        first.setNext(last);
        last.setRunningIndex(2);

        ReadOnlySequence<Integer> sequence =
            new ReadOnlySequence<>(first, 0, last, 2);

        SequencePosition start = sequence.getStart();
        SequencePosition middle = sequence.getPosition(2, start);
        ReadOnlySequence<Integer> sliced = sequence.slice(start, middle);
        assertEquals(2, sliced.getLength());
        assertEquals('{', sliced.getFirst().getSpan().get(0));
        assertEquals('a', sliced.getFirst().getSpan().get(1));
    }

    @Test
    public void perByteChain_withReaderStylePositions_slicesCorrectly() {
        // JsonTestHelper.GetSequence(data, 1) produces one segment per byte plus a
        // trailing EMPTY segment. Utf8JsonReader builds slice positions as
        // new SequencePosition(sequence, segmentIndex + consumed/index).
        byte[] data = new byte[] { '{', '"', 'v', 'a', 'l', 'u', 'e', '1', '"' };
        Segment first = new Segment();
        first.setMemory(new ReadOnlyMemory<>(new byte[] { data[0] }));
        Segment current = first;
        for (int i = 1; i < data.length; i++) {
            Segment next = new Segment();
            next.setMemory(new ReadOnlyMemory<>(new byte[] { data[i] }));
            current.setNext(next);
            next.setRunningIndex(i);
            current = next;
        }
        Segment emptyLast = new Segment();
        emptyLast.setMemory(new ReadOnlyMemory<>(new byte[0]));
        current.setNext(emptyLast);
        emptyLast.setRunningIndex(data.length);

        ReadOnlySequence<Integer> sequence =
            new ReadOnlySequence<>(first, 0, emptyLast, 0);

        SequencePosition start = new SequencePosition(sequence, 2);
        SequencePosition end = new SequencePosition(sequence, 8);
        ReadOnlySequence<Integer> value = sequence.slice(start, end);
        assertFalse(value.getIsEmpty());
        assertEquals(6, value.getLength());
        assertEquals('v', value.getFirst().getSpan().get(0));
    }
}

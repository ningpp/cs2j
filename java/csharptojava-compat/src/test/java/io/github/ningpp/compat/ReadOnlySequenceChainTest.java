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

    /** Builds a three-segment chain "ab" | "cd" | "efg" (runningIndex on each successor). */
    private static ReadOnlySequence<Integer> threeSegmentChain() {
        Segment first = new Segment();
        first.setMemory(new ReadOnlyMemory<>(new byte[] { 'a', 'b' }));
        Segment second = new Segment();
        second.setMemory(new ReadOnlyMemory<>(new byte[] { 'c', 'd' }));
        Segment third = new Segment();
        third.setMemory(new ReadOnlyMemory<>(new byte[] { 'e', 'f', 'g' }));
        first.setNext(second);
        second.setRunningIndex(2);
        second.setNext(third);
        third.setRunningIndex(4);
        return new ReadOnlySequence<>(first, 0, third, 3);
    }

    /**
     * .NET Slice preserves the segment structure: a slice that starts and ends in
     * different segments stays multi-segment. Utf8JsonReader relies on this for
     * HasValueSequence = !slice.IsSingleSegment (skipOrConsumeCommentMultiSegmentWithRollback),
     * and on foreach visiting every segment piece (compareToSequence).
     */
    @Test
    public void sliceSpanningSegments_staysMultiSegment() {
        ReadOnlySequence<Integer> sequence = threeSegmentChain();

        ReadOnlySequence<Integer> sliced = sequence.slice(1, 4); // "bcde" spans all three segments

        assertFalse(sliced.getIsSingleSegment());
        assertEquals(4, sliced.getLength());
        assertEquals(1, sliced.getFirst().getSpan().length());
        assertEquals('b', sliced.getFirst().getSpan().get(0).intValue());

        // Foreach must visit every (non-empty) segment piece of the slice.
        StringBuilder joined = new StringBuilder();
        for (ReadOnlyMemory<Integer> memory : sliced) {
            ReadOnlySpan<Integer> span = memory.getSpan();
            for (int i = 0; i < span.length(); i++) {
                joined.append((char) span.get(i).intValue());
            }
        }
        assertEquals("bcde", joined.toString());

        // tryGet walks the pieces with positions relative to the slice.
        ObjectHolder<SequencePosition> pos = new ObjectHolder<>(sliced.getStart());
        ObjectHolder<ReadOnlyMemory<Integer>> mem = new ObjectHolder<>();
        assertTrue(sliced.tryGet(pos, mem, true));
        assertEquals(1, mem.value.getLength());
        assertTrue(sliced.tryGet(pos, mem, true));
        assertEquals(2, mem.value.getLength());
        assertEquals('c', mem.value.getSpan().get(0).intValue());
        assertTrue(sliced.tryGet(pos, mem, true));
        assertEquals(1, mem.value.getLength());
        assertEquals('e', mem.value.getSpan().get(0).intValue());
        assertFalse(sliced.tryGet(pos, mem, true));
    }

    /** unescapeSequenceAndCompare re-slices a value sequence mid-token; the structure must survive. */
    @Test
    public void reslicingASlice_keepsSegmentStructure() {
        ReadOnlySequence<Integer> sliced = threeSegmentChain().slice(1, 4); // "bcde"
        ReadOnlySequence<Integer> resliced = sliced.slice(2L); // "de", spans pieces 2|3

        assertFalse(resliced.getIsSingleSegment());
        assertEquals(2, resliced.getLength());
        assertEquals('d', resliced.getFirst().getSpan().get(0).intValue());
    }

    /** A slice fully inside one segment is single-segment, like .NET. */
    @Test
    public void sliceWithinOneSegment_reportsSingleSegment() {
        ReadOnlySequence<Integer> sequence = threeSegmentChain();

        ReadOnlySequence<Integer> sliced = sequence.slice(2, 2); // "cd" inside segment two

        assertTrue(sliced.getIsSingleSegment());
        assertEquals(2, sliced.getLength());
        assertEquals('c', sliced.getFirst().getSpan().get(0).intValue());
    }

    /** Foreach over an unsliced multi-segment chain visits every segment, not just the first. */
    @Test
    public void forEachVisitsEverySegmentOfAMultiSegmentSequence() {
        ReadOnlySequence<Integer> sequence = threeSegmentChain();

        assertFalse(sequence.getIsSingleSegment());
        StringBuilder joined = new StringBuilder();
        for (ReadOnlyMemory<Integer> memory : sequence) {
            ReadOnlySpan<Integer> span = memory.getSpan();
            for (int i = 0; i < span.length(); i++) {
                joined.append((char) span.get(i).intValue());
            }
        }
        assertEquals("abcdefg", joined.toString());
    }
}

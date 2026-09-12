package io.github.ningpp.compat;

import org.junit.jupiter.api.Test;

import java.nio.charset.StandardCharsets;
import java.util.Arrays;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertTrue;

/**
 * ReadOnlySequence.tryGet(ref position, out memory, advance) has to resolve a
 * SequencePosition back to the segment that contains it. The converter models a
 * position as a byte offset into the sequence, so the lookup must map
 * offset -> segment on every call.
 *
 * <p>Utf8JsonReader.getNextSpan calls tryGet once per segment advance. When the
 * lookup is a linear scan from segment 0, reading an N-byte payload that was split
 * into N one-byte segments (the shape JsonTestHelper.GetSequence(data, 1) builds)
 * costs O(N^2). That is exactly what happened to
 * Utf8JsonReaderTests.testJsonReaderLargeUtf8SegmentSizeOne: the 400KB resource
 * took 94s instead of ~0.1s, and the 4 slowest parameters of that one test method
 * accounted for 191s of a 345s test suite.
 *
 * <p>These tests pin both halves of the contract: the offset -> segment semantics
 * and the per-advance complexity.
 */
public class ReadOnlySequenceSegmentLookupTest {

    /**
     * Calibrated by measuring both implementations on this module at this exact size:
     * a linear scan needs 22,158ms for the walk (N^2/2 offset comparisons), a
     * logarithmic lookup needs 16ms. The 2s budget therefore fails the quadratic
     * behaviour by 11x while leaving the fixed implementation a ~125x margin, which
     * keeps the test stable on a slow or loaded machine.
     */
    private static final int TRAVERSAL_SEGMENT_COUNT = 300_000;
    private static final long TRAVERSAL_BUDGET_MILLIS = 2_000L;

    private static final class Segment extends ReadOnlySequenceSegment<Integer> {
    }

    /** Builds a segment chain from explicit buffers, mirroring BufferFactory/ReadOnlyBufferSegment. */
    private static ReadOnlySequence<Integer> chainOf(byte[]... buffers) {
        Segment first = null;
        Segment current = null;
        for (byte[] buffer : buffers) {
            Segment segment = new Segment();
            segment.setMemory(new ReadOnlyMemory<>(buffer));
            if (current == null) {
                first = segment;
            } else {
                current.setNext(segment);
                segment.setRunningIndex(current.getRunningIndex() + current.getMemory().getLength());
            }
            current = segment;
        }
        if (first == null) {
            first = current = new Segment();
        }
        return new ReadOnlySequence<>(first, 0, current, current.getMemory().getLength());
    }

    /**
     * Mirrors JsonTestHelper.GetSequence(data, 1): one segment per byte plus a
     * trailing EMPTY segment (the shape that produces the largest segment count).
     */
    private static ReadOnlySequence<Integer> perByteChain(byte[] data) {
        byte[][] buffers = new byte[data.length + 1][];
        for (int i = 0; i < data.length; i++) {
            buffers[i] = new byte[] { data[i] };
        }
        buffers[data.length] = new byte[0];
        return chainOf(buffers);
    }

    /** Builds a chain whose segments have the given sizes, followed by an empty segment. */
    private static ReadOnlySequence<Integer> chainWithSizes(byte[] data, int[] sizes) {
        byte[][] buffers = new byte[sizes.length + 1][];
        int position = 0;
        for (int i = 0; i < sizes.length; i++) {
            buffers[i] = Arrays.copyOfRange(data, position, position + sizes[i]);
            position += sizes[i];
        }
        buffers[sizes.length] = new byte[0];
        return chainOf(buffers);
    }

    /** Resolves the segment containing {@code offset}, as the reader does when it advances. */
    private static ReadOnlyMemory<Integer> resolve(ReadOnlySequence<Integer> sequence, int offset) {
        ObjectHolder<SequencePosition> position =
            new ObjectHolder<>(new SequencePosition(sequence, offset));
        ObjectHolder<ReadOnlyMemory<Integer>> memory = new ObjectHolder<>();
        assertTrue(sequence.tryGet(position, memory, true),
            "tryGet should resolve byte offset " + offset);
        return memory.value;
    }

    private static boolean resolves(ReadOnlySequence<Integer> sequence, int offset) {
        ObjectHolder<SequencePosition> position =
            new ObjectHolder<>(new SequencePosition(sequence, offset));
        return sequence.tryGet(position, new ObjectHolder<>(), true);
    }

    @Test
    public void tryGet_resolvesEveryByteOffsetInPerByteChain() {
        byte[] data = "{\"value1\":[1,22,333]}".getBytes(StandardCharsets.UTF_8);
        ReadOnlySequence<Integer> sequence = perByteChain(data);

        assertEquals(data.length, sequence.getLength());
        for (int offset = 0; offset < data.length; offset++) {
            ReadOnlyMemory<Integer> memory = resolve(sequence, offset);
            assertEquals(1, memory.getLength(), "offset " + offset);
            assertEquals(data[offset] & 0xFF, memory.getSpan().get(0).intValue(), "offset " + offset);
        }
    }

    @Test
    public void tryGet_atEndAndPastEndReturnsFalse() {
        byte[] data = "abcd".getBytes(StandardCharsets.UTF_8);
        ReadOnlySequence<Integer> sequence = perByteChain(data);

        assertFalse(resolves(sequence, data.length), "offset at the end of the sequence");
        assertFalse(resolves(sequence, data.length + 7), "offset past the end of the sequence");
    }

    @Test
    public void tryGet_resolvesEveryOffsetInMixedSizeChain() {
        byte[] data = "abcdefghijkl".getBytes(StandardCharsets.UTF_8);
        int[] sizes = { 3, 1, 0, 5, 2, 1 };
        ReadOnlySequence<Integer> sequence = chainWithSizes(data, sizes);

        int[] segmentStart = new int[sizes.length];
        int accumulated = 0;
        for (int i = 0; i < sizes.length; i++) {
            segmentStart[i] = accumulated;
            accumulated += sizes[i];
        }
        assertEquals(data.length, accumulated, "test fixture must consume the whole payload");
        assertEquals(data.length, sequence.getLength());

        for (int offset = 0; offset < data.length; offset++) {
            int expectedSegment = -1;
            for (int i = 0; i < sizes.length; i++) {
                if (offset >= segmentStart[i] && offset < segmentStart[i] + sizes[i]) {
                    expectedSegment = i;
                    break;
                }
            }
            ReadOnlyMemory<Integer> memory = resolve(sequence, offset);
            assertEquals(data[segmentStart[expectedSegment]] & 0xFF,
                memory.getSpan().get(0).intValue(), "offset " + offset);
        }
        assertFalse(resolves(sequence, data.length));
    }

    @Test
    public void tryGet_skipsEmptySegmentsBetweenNonEmptyOnes() {
        byte[] data = "xyz".getBytes(StandardCharsets.UTF_8);
        ReadOnlySequence<Integer> sequence = chainWithSizes(data, new int[] { 1, 0, 1, 0, 1 });

        assertEquals(data.length, sequence.getLength());
        for (int offset = 0; offset < data.length; offset++) {
            ReadOnlyMemory<Integer> memory = resolve(sequence, offset);
            assertEquals(1, memory.getLength(), "offset " + offset);
            assertEquals(data[offset] & 0xFF, memory.getSpan().get(0).intValue(), "offset " + offset);
        }
    }

    @Test
    public void tryGet_withLeadingEmptySegmentResolvesFirstNonEmptySegment() {
        byte[] data = "xy".getBytes(StandardCharsets.UTF_8);
        ReadOnlySequence<Integer> sequence = chainWithSizes(data, new int[] { 0, 1, 1 });

        assertEquals(data.length, sequence.getLength());
        ReadOnlyMemory<Integer> first = resolve(sequence, 0);
        assertEquals(1, first.getLength());
        assertEquals('x', first.getSpan().get(0).intValue());

        ReadOnlyMemory<Integer> second = resolve(sequence, 1);
        assertEquals(1, second.getLength());
        assertEquals('y', second.getSpan().get(0).intValue());
    }

    /**
     * The reader advances monotonically, but nothing in the tryGet contract requires
     * it. A lookup that only tracks a forward cursor would pass the traversal tests
     * and silently return the wrong segment here.
     */
    @Test
    public void tryGet_resolvesOffsetsOutOfOrder() {
        byte[] data = "abcdefghijkl".getBytes(StandardCharsets.UTF_8);
        int[] sizes = { 3, 1, 0, 5, 2, 1 };
        ReadOnlySequence<Integer> sequence = chainWithSizes(data, sizes);

        int[] offsets = { 11, 4, 0, 7, 4, 1, 9, 3, 11, 2, 8, 5 };
        for (int offset : offsets) {
            ReadOnlyMemory<Integer> memory = resolve(sequence, offset);
            // The payload uses distinct bytes, so the first byte of the returned
            // segment identifies where that segment starts.
            int segmentStart = indexOfByte(data, memory.getSpan().get(0).intValue());
            assertTrue(offset >= segmentStart && offset < segmentStart + memory.getLength(),
                "offset " + offset + " resolved to segment [" + segmentStart + ", "
                    + (segmentStart + memory.getLength()) + ")");
        }
    }

    @Test
    public void tryGet_withAdvanceFalseLeavesPositionUnchanged() {
        byte[] data = "abcdefghijkl".getBytes(StandardCharsets.UTF_8);
        ReadOnlySequence<Integer> sequence = chainWithSizes(data, new int[] { 3, 1, 0, 5, 2, 1 });

        ObjectHolder<SequencePosition> position =
            new ObjectHolder<>(new SequencePosition(sequence, 4));
        ObjectHolder<ReadOnlyMemory<Integer>> memory = new ObjectHolder<>();

        assertTrue(sequence.tryGet(position, memory, false));
        assertEquals(4, position.value.getInteger(), "advance=false must not move the position");
        assertEquals('e', memory.value.getSpan().get(0).intValue());
    }

    @Test
    public void tryGet_withPositionFromAnotherSequenceStartsAtFirstSegment() {
        byte[] data = "abcdefghijkl".getBytes(StandardCharsets.UTF_8);
        ReadOnlySequence<Integer> sequence = chainWithSizes(data, new int[] { 3, 1, 0, 5, 2, 1 });

        ObjectHolder<SequencePosition> foreign =
            new ObjectHolder<>(new SequencePosition(new Object(), 9_999));
        ObjectHolder<ReadOnlyMemory<Integer>> memory = new ObjectHolder<>();
        assertTrue(sequence.tryGet(foreign, memory, true));
        assertEquals('a', memory.value.getSpan().get(0).intValue());
    }

    @Test
    public void tryGet_withMissingPositionStartsAtFirstSegment() {
        byte[] data = "abcdefghijkl".getBytes(StandardCharsets.UTF_8);
        ReadOnlySequence<Integer> sequence = chainWithSizes(data, new int[] { 3, 1, 0, 5, 2, 1 });

        ObjectHolder<SequencePosition> missing = new ObjectHolder<>();
        ObjectHolder<ReadOnlyMemory<Integer>> memory = new ObjectHolder<>();
        assertTrue(sequence.tryGet(missing, memory, true));
        assertEquals('a', memory.value.getSpan().get(0).intValue());
    }

    @Test
    public void singleSegmentSequence_reportsWholeSequenceThenEnds() {
        byte[] data = "abc".getBytes(StandardCharsets.UTF_8);
        ReadOnlySequence<Integer> sequence = new ReadOnlySequence<>(data);

        assertTrue(sequence.getIsSingleSegment());
        assertEquals(3, sequence.getLength());

        ObjectHolder<SequencePosition> position = new ObjectHolder<>(sequence.getStart());
        ObjectHolder<ReadOnlyMemory<Integer>> memory = new ObjectHolder<>();
        assertTrue(sequence.tryGet(position, memory, true));
        assertEquals(3, memory.value.getLength());
        assertFalse(sequence.tryGet(position, memory, true));
    }

    /**
     * Regression guard for the O(N^2) segment lookup. Building the chain is excluded
     * from the measurement: only the per-advance tryGet walk is timed, which is the
     * part Utf8JsonReader.getNextSpan drives.
     */
    @Test
    public void fullTraversalOfPerByteChainStaysLinear() {
        byte[] warmupData = new byte[5_000];
        Arrays.fill(warmupData, (byte) 'a');
        walkFully(perByteChain(warmupData));

        byte[] data = new byte[TRAVERSAL_SEGMENT_COUNT];
        for (int i = 0; i < data.length; i++) {
            data[i] = (byte) ('a' + (i % 26));
        }
        ReadOnlySequence<Integer> sequence = perByteChain(data);

        long start = System.nanoTime();
        int visited = walkFully(sequence);
        long elapsedMillis = (System.nanoTime() - start) / 1_000_000L;

        // The trailing EMPTY segment carries no byte range, so findSegment never
        // reports it: one visit per byte.
        assertEquals(TRAVERSAL_SEGMENT_COUNT, visited);
        assertTrue(elapsedMillis < TRAVERSAL_BUDGET_MILLIS,
            "walking " + TRAVERSAL_SEGMENT_COUNT + " single-byte segments took " + elapsedMillis
                + "ms, budget is " + TRAVERSAL_BUDGET_MILLIS
                + "ms - the offset -> segment lookup has regressed to a linear scan");
    }

    private static int walkFully(ReadOnlySequence<Integer> sequence) {
        ObjectHolder<SequencePosition> position = new ObjectHolder<>(sequence.getStart());
        ObjectHolder<ReadOnlyMemory<Integer>> memory = new ObjectHolder<>();
        int visited = 0;
        while (sequence.tryGet(position, memory, true)) {
            visited++;
        }
        return visited;
    }

    private static int indexOfByte(byte[] data, int value) {
        for (int i = 0; i < data.length; i++) {
            if ((data[i] & 0xFF) == value) {
                return i;
            }
        }
        throw new AssertionError("byte " + value + " not present in fixture");
    }
}

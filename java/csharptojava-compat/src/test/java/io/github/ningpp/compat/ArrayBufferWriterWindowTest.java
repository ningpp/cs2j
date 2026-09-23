package io.github.ningpp.compat;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertTrue;

import org.junit.jupiter.api.Test;

/**
 * .NET {@code ArrayBufferWriter<T>.GetMemory} returns a window over the FULL
 * remaining capacity ({@code buffer.AsMemory(index)}); "at least sizeHint" is a
 * lower bound, not a cap. The generated {@code Utf8JsonWriter} relies on the
 * exact window size to decide when to call {@code GetMemory} again, and
 * Utf8JsonWriterTests.writeJsonWritesToIBWOnDemand_Dispose pins the byte counts
 * (Committed / BytesPending / WrittenCount) that only the full-window contract
 * reproduces.
 */
class ArrayBufferWriterWindowTest {

    @Test
    void getMemoryReturnsTheFullRemainingCapacity() {
        ArrayBufferWriter<Integer> writer = new ArrayBufferWriter<>();

        // First growth of a default writer: 256 + max(5000, 256) = 5256 elements.
        assertEquals(5256, writer.getMemory(5000).getLength(),
                "window must cover the full free capacity after growth");

        writer.advance(5000);
        // Second growth: 5256 + max(5000, 5256) = 10512, free = 10512 - 5000 = 5512.
        assertEquals(5512, writer.getMemory(5000).getLength(),
                "window must cover the full free capacity after the second growth");

        writer.advance(5000);
        // Small hint with more than 4096 elements free: 512 free after two writes.
        assertEquals(512, writer.getMemory(1).getLength());
    }

    @Test
    void windowLengthAlwaysEqualsFreeCapacity() {
        ArrayBufferWriter<Integer> writer = new ArrayBufferWriter<>(1024);
        int[] hints = { 0, 1, 2, 100, 4096, 8192 };

        for (int round = 0; round < 200; round++) {
            int hint = hints[round % hints.length];
            int minimum = Math.max(hint, 1);
            ReadOnlyMemory<Integer> memory = writer.getMemory(hint);
            // Measured AFTER the call: the hint may grow the buffer first, and the
            // returned window covers the full remaining capacity post-growth.
            assertEquals(writer.getCapacity() - writer.getWrittenCount(), memory.getLength(),
                    "round " + round + ", hint " + hint);
            assertTrue(memory.getLength() >= minimum,
                    "window must satisfy sizeHint: round " + round + ", hint " + hint);
            writer.advance(1 + (hint % 64));
        }
    }

    @Test
    void windowIsAViewAtTheWrittenOffsetAndExceedsTheHint() {
        ArrayBufferWriter<Integer> writer = new ArrayBufferWriter<>();
        writer.getMemory(1);
        writer.advance(1);

        // Grow well past 4096 so a capped window would be shorter than the free capacity.
        writer.getMemory(5000);
        writer.advance(100);

        ReadOnlyMemory<Integer> window = writer.getMemory(1);
        assertTrue(window.getLength() > 4096, "free capacity must be handed out beyond the hint");

        // Writing at the far end of the window (past any capped length) must work.
        new Span<>(window.getSpan()).set(window.getLength() - 1, 42);
        writer.advance(window.getLength());
        assertEquals(1 + 100 + window.getLength(), writer.getWrittenCount());
        assertEquals(42, writer.getWrittenSpan().get(writer.getWrittenCount() - 1).intValue());
    }
}

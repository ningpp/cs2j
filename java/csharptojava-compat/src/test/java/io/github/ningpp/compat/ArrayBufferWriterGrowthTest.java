package io.github.ningpp.compat;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertThrows;
import static org.junit.jupiter.api.Assertions.assertTrue;

import org.junit.jupiter.api.Test;

/**
 * Regression tests for the O(N^2) growth behaviour of {@link ArrayBufferWriter}.
 *
 * <p>Background: the transpiled System.Text.Json port writes multi-megabyte JSON documents
 * through this buffer writer. With a fixed +4096 growth step every {@code Advance} chunk
 * re-copied the whole backing array, so writing 8 MB copied ~61 GB of {@code Object[]}
 * (measured) and the {@code Utf8JsonWriterTests.writeNumbers} rows with a 50 character
 * escaped property name took 4.7-7.0 s instead of ~50 ms.
 */
class ArrayBufferWriterGrowthTest {

    @Test
    void getMemoryReturnsAViewAtTheWrittenOffset() {
        ArrayBufferWriter<Integer> writer = new ArrayBufferWriter<>(4);

        Span<Integer> first = new Span<>(writer.getMemory(2).getSpan());
        first.set(0, 10);
        first.set(1, 11);
        writer.advance(2);

        Span<Integer> second = new Span<>(writer.getMemory(2).getSpan());
        second.set(0, 12);
        second.set(1, 13);
        writer.advance(2);

        ReadOnlySpan<Integer> written = writer.getWrittenSpan();
        assertEquals(4, written.getLength());
        int[] expected = { 10, 11, 12, 13 };
        for (int i = 0; i < expected.length; i++) {
            int actual = written.get(i);
            assertEquals(expected[i], actual, "element " + i);
        }
    }

    @Test
    void contentsSurviveRepeatedGrowth() {
        ArrayBufferWriter<Integer> writer = new ArrayBufferWriter<>(256);
        final int chunk = 4096;
        final int chunks = 128; // 512 Ki elements, forces many reallocations

        for (int i = 0; i < chunks; i++) {
            Span<Integer> window = new Span<>(writer.getMemory(chunk).getSpan());
            for (int j = 0; j < chunk; j++) {
                window.set(j, (i + j) & 0xFF);
            }
            writer.advance(chunk);
        }

        ReadOnlySpan<Integer> written = writer.getWrittenSpan();
        assertEquals(chunks * chunk, written.getLength());
        for (int i = 0; i < chunks; i++) {
            for (int j = 0; j < chunk; j += 499) {
                int actual = written.get(i * chunk + j);
                assertEquals((i + j) & 0xFF, actual, "element " + (i * chunk + j));
            }
        }
    }

    /**
     * The .NET ArrayBufferWriter&lt;T&gt;.GetMemory hands back a view over the FULL
     * remaining capacity (buffer.AsMemory(index)); "at least sizeHint" is a lower
     * bound, not a cap. The generated Utf8JsonWriter depends on the exact window
     * size for its Committed/BytesPending bookkeeping
     * (writeJsonWritesToIBWOnDemand_Dispose pins those byte counts).
     */
    @Test
    void windowLengthIsTheFullFreeCapacity() {
        ArrayBufferWriter<Integer> writer = new ArrayBufferWriter<>(1024);
        int[] hints = { 0, 1, 2, 100, 4096, 8192 };

        for (int round = 0; round < 200; round++) {
            int hint = hints[round % hints.length];
            int minimum = Math.max(hint, 1);
            ReadOnlyMemory<Integer> memory = writer.getMemory(hint);

            assertTrue(memory.getLength() >= minimum,
                    "window must satisfy sizeHint: length=" + memory.getLength() + ", sizeHint=" + hint);
            // Measured AFTER the call: the hint may grow the buffer first.
            assertEquals(writer.getCapacity() - writer.getWrittenCount(), memory.getLength(),
                    "window must cover the full free capacity: sizeHint=" + hint);

            writer.advance(minimum);
        }
    }

    /**
     * Deterministic counterpart of the timing test: counts the elements moved by
     * {@code Arrays.copyOf} (the old capacity before every reallocation) and requires the
     * total to stay linear in the number of elements written.
     *
     * <p>Geometric growth copies ~2N elements. The fixed +4096 step copied
     * ~N^2 / (2 * 4096) elements, which for the 4 Mi elements below is ~500x the bound.
     */
    @Test
    void reallocationCopyingStaysLinearInWrittenElements() {
        ArrayBufferWriter<Integer> writer = new ArrayBufferWriter<>(1024);
        final int chunk = 4096;
        final int chunks = 1024; // 4 Mi elements
        final long totalWritten = (long) chunk * chunks;

        long copiedElements = 0;
        int lastCapacity = writer.getCapacity();
        for (int i = 0; i < chunks; i++) {
            writer.getMemory(chunk);
            writer.advance(chunk);
            int capacity = writer.getCapacity();
            if (capacity != lastCapacity) {
                copiedElements += lastCapacity; // Arrays.copyOf moves the whole old array
                lastCapacity = capacity;
            }
        }

        assertEquals(totalWritten, writer.getWrittenCount());
        assertTrue(copiedElements <= 8L * totalWritten,
                "growth is not geometric: copied " + copiedElements + " elements for " + totalWritten
                        + " written elements (fixed-step growth copies ~N^2/(2*step))");
    }

    /**
     * End-to-end shape of the reported bug: several megabytes pushed through the buffer in
     * the same 4096-element chunks the generated Utf8JsonWriter uses. With fixed +4096
     * growth this took ~10 s on the machine where the regression was measured; the bound
     * below is deliberately loose so the test does not depend on machine speed.
     */
    @Test
    void writingSeveralMegabytesStaysFast() {
        ArrayBufferWriter<Integer> writer = new ArrayBufferWriter<>(1024);
        final int chunk = 4096;
        final int chunks = 2048; // 8 Mi elements
        int maxWindow = 0;

        long start = System.nanoTime();
        for (int i = 0; i < chunks; i++) {
            ReadOnlyMemory<Integer> memory = writer.getMemory(chunk);
            maxWindow = Math.max(maxWindow, memory.getLength());
            Span<Integer> window = new Span<>(memory.getSpan());
            for (int j = 0; j < chunk; j++) {
                window.set(j, 65);
            }
            writer.advance(chunk);
        }
        long elapsedMs = (System.nanoTime() - start) / 1_000_000L;

        assertEquals((long) chunks * chunk, writer.getWrittenCount());
        assertTrue(maxWindow <= writer.getCapacity(),
                "window can never exceed the capacity: " + maxWindow + " vs " + writer.getCapacity());
        assertTrue(elapsedMs < 5000,
                "writing 8 Mi elements took " + elapsedMs + " ms (fixed-step growth regression took ~10 s)");
    }

    /**
     * Pins the .NET {@code ArrayBufferWriter<T>} capacity semantics that the transpiled
     * Utf8JsonWriter tests rely on: the first growth of a default writer adds exactly the
     * requested hint (256 + 4096 = 4352), and later growths double.
     *
     * <p>Writing more than the hint doubles instead of adding the hint again, which is what
     * keeps reallocation copying amortized O(N) for multi-megabyte documents.
     */
    @Test
    void capacityGrowsByTheHintFirstThenDoubles() {
        ArrayBufferWriter<Integer> writer = new ArrayBufferWriter<>();
        assertEquals(256, writer.getCapacity());

        writer.getMemory(256); // fits in the initial buffer
        assertEquals(256, writer.getCapacity());

        writer.advance(240);
        writer.getMemory(4096);
        assertEquals(4352, writer.getCapacity(), "first growth must be length + sizeHint");

        ArrayBufferWriter<Integer> growing = new ArrayBufferWriter<>(1024);
        java.util.List<Integer> capacities = new java.util.ArrayList<>();
        int last = growing.getCapacity();
        for (int i = 0; i < 64; i++) {
            growing.getMemory(4096);
            growing.advance(4096);
            if (growing.getCapacity() != last) {
                last = growing.getCapacity();
                capacities.add(last);
            }
        }
        assertEquals(java.util.List.of(5120, 10240, 20480, 40960), capacities.subList(0, 4));
    }

    @Test
    void advanceBeyondCapacityThrowsAndClearResetsTheWriter() {
        ArrayBufferWriter<Integer> writer = new ArrayBufferWriter<>(4);
        int capacity = writer.getCapacity();

        writer.getMemory(1);
        writer.advance(1);
        assertEquals(1, writer.getWrittenCount());

        assertThrows(IndexOutOfBoundsException.class, () -> writer.advance(capacity));
        assertThrows(IndexOutOfBoundsException.class, () -> writer.advance(-1));

        writer.clear();
        assertEquals(0, writer.getWrittenCount());

        writer.getMemory(1);
        writer.advance(1);
        assertEquals(1, writer.getWrittenCount());
        ReadOnlySpan<Integer> written = writer.getWrittenSpan();
        assertEquals(1, written.getLength());
    }
}

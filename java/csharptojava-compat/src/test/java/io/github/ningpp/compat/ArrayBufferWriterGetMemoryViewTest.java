package io.github.ningpp.compat;

import static org.junit.jupiter.api.Assertions.assertEquals;

import org.junit.jupiter.api.Test;

/// IBufferWriter.GetMemory must return a view of the internal buffer so writes
/// through the returned span are visible to WrittenMemory/WrittenSpan. A copy
/// would leave the committed bytes null (Utf8JsonWriter NPE on serialize).
class ArrayBufferWriterGetMemoryViewTest {
    @Test
    void writesThroughGetMemoryAreVisibleInWrittenSpan() {
        ArrayBufferWriter<Integer> writer = new ArrayBufferWriter<>(4);
        ReadOnlyMemory<Integer> memory = writer.getMemory(2);
        Span<Integer> span = new Span<>(memory.getSpan());
        span.set(0, 65);
        span.set(1, 66);
        writer.advance(2);

        ReadOnlySpan<Integer> written = writer.getWrittenSpan();
        assertEquals(2, written.getLength());
        assertEquals(65, written.get(0));
        assertEquals(66, written.get(1));
    }
}

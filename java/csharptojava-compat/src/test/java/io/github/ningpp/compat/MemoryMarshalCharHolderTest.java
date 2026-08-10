package io.github.ningpp.compat;

import org.junit.jupiter.api.Test;

import static org.junit.jupiter.api.Assertions.assertEquals;

/**
 * MemoryMarshal.createSpan must accept a CharHolder (the converter's lowering
 * of C# MemoryMarshal.CreateSpan(ref char value, 1)) and yield a char-typed
 * Span, so Utf8JsonWriter.writeStringValue(ReadOnlySpan&lt;Character&gt;) call
 * sites compile (CharConverter.Write/WriteWithQuotes).
 */
public class MemoryMarshalCharHolderTest {

    @Test
    public void createSpan_overCharHolder_yieldsCharacterSpan() {
        CharHolder holder = new CharHolder('A');
        Span<Character> span = MemoryMarshal.createSpan(holder, 1);
        assertEquals(Character.valueOf('A'), span.get(0));
        assertEquals(1, span.length());
    }
}

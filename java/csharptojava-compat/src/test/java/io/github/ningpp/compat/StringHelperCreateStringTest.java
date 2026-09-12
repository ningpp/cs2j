package io.github.ningpp.compat;

import org.junit.jupiter.api.Test;

import static org.junit.jupiter.api.Assertions.assertArrayEquals;
import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertInstanceOf;
import static org.junit.jupiter.api.Assertions.assertNotNull;
import static org.junit.jupiter.api.Assertions.assertThrows;
import static org.junit.jupiter.api.Assertions.assertTrue;

/**
 * Tests for {@link StringHelper#createString(int, Object, java.util.function.BiConsumer)},
 * the Java stand-in for C# {@code string.Create<TState>(int, TState, SpanAction<char, TState>)}.
 *
 * <p>The buffer handed to the action must be a primitive {@code char[]}. An earlier
 * revision used a boxed {@code Character[]} plus two extra full copies, which made
 * ValueTests.VeryLongInputString (1,073,741,791 chars) allocate an ~8.6 GB reference
 * array and spend ~27 s thrashing the GC.
 */
public class StringHelperCreateStringTest {

    @Test
    public void CreateString_FillsBufferAndReturnsStringOfRequestedLength() {
        String result = StringHelper.createString(5, 'x', (chars, fill) -> chars.fill(fill));

        assertEquals(5, result.length());
        assertEquals("xxxxx", result);
    }

    @Test
    public void CreateString_ExposesPrimitiveCharArrayBacking() {
        // White-box guard for the memory fix: the action must receive a Span over a
        // primitive char[] (2 bytes/element), never a boxed Character[] (8 bytes/element
        // for the reference array alone, plus one wrapper per element on write).
        Object[] seen = new Object[1];
        StringHelper.createString(4, 'a', (chars, fill) -> seen[0] = chars.getBackingArray());

        assertNotNull(seen[0], "action should have been invoked with a live span");
        assertInstanceOf(char[].class, seen[0],
                "createString must hand the action a primitive char[] backing, not a boxed Character[]");
    }

    @Test
    public void CreateString_ActionCanWriteIndividualPositions() {
        // Mirrors ValueTests.DeserializeLongJsonString: fill everything, then overwrite
        // the first and last position with a quote.
        String result = StringHelper.createString(6, 'x', (chars, fill) -> {
            chars.fill(fill);
            chars.set(0, '"');
            chars.set(chars.getLength() - 1, '"');
        });

        assertEquals("\"xxxx\"", result);
    }

    @Test
    public void CreateString_WritesFromSourceStringViaAsSpan() {
        // Mirrors JsonCamelCaseNamingPolicy.convertName: copy a source string in, then
        // mutate the buffer in place.
        String name = "webSocket";
        String result = StringHelper.createString(name.length(), name, (chars, source) -> {
            MemoryExtensions.asSpan(source).copyTo(chars);
            chars.set(0, Character.toUpperCase(chars.get(0)));
        });

        assertEquals("WebSocket", result);
    }

    @Test
    public void CreateString_ZeroLength_ReturnsEmptyString() {
        String result = StringHelper.createString(0, 'x', (chars, fill) -> {
            assertEquals(0, chars.getLength());
            chars.fill(fill);
        });

        assertEquals("", result);
    }

    @Test
    public void CreateString_ReadsBackNonNullCharactersForWholeBuffer() {
        // With a boxed Character[] buffer every slot is a reference that can be null;
        // with char[] every slot is a value. C#'s Span<char> cannot hold null, so the
        // primitive backing is the faithful mapping.
        String result = StringHelper.createString(3, 'q', (chars, fill) -> {
            chars.fill(fill);
            for (int i = 0; i < chars.getLength(); i++) {
                assertNotNull(chars.get(i));
            }
        });

        assertEquals("qqq", result);
    }

    @Test
    public void CreateString_NonBmpContentSurvivesRoundTrip() {
        // Surrogate pairs must be copied verbatim, not re-encoded.
        String emoji = "\uD83D\uDE00"; // U+1F600
        String result = StringHelper.createString(2, emoji, (chars, source) -> {
            chars.set(0, source.charAt(0));
            chars.set(1, source.charAt(1));
        });

        assertEquals(emoji, result);
    }

    @Test
    public void CreateString_NegativeLength_Throws() {
        assertThrows(NegativeArraySizeException.class,
                () -> StringHelper.createString(-1, 'x', (chars, fill) -> chars.fill(fill)));
    }

    @Test
    public void CreateString_LargeBuffer_CompletesWithPrimitiveBacking() {
        // Scale guard: the primitive backing keeps the buffer at 2 bytes/char, so a
        // multi-megabyte buffer allocates a plain char[] and completes quickly. The old
        // boxed path allocated 4x the memory here and would take orders of magnitude
        // longer once the intermediate stopped fitting in the young generation.
        int length = 4_000_000;
        String result = StringHelper.createString(length, 'z', (chars, fill) -> chars.fill(fill));

        assertEquals(length, result.length());
        assertEquals('z', result.charAt(0));
        assertEquals('z', result.charAt(length - 1));
    }

    @Test
    public void CreateString_LargeBufferContentMatchesSource() {
        int length = 1_000_000;
        char[] source = new char[length];
        java.util.Arrays.fill(source, 'k');

        String result = StringHelper.createString(length, source, (chars, src) -> {
            for (int i = 0; i < src.length; i++) {
                chars.set(i, src[i]);
            }
        });

        assertArrayEquals(source, result.toCharArray());
    }

    @Test
    public void CreateString_ReturnsIndependentStringFromBuffer() {
        // The returned String must be fully materialized: the local char[] is not
        // reachable afterwards, so later mutation of a caller-held array cannot leak in.
        char[] shared = { 'a', 'b', 'c' };
        String result = StringHelper.createString(3, shared, (chars, src) -> {
            for (int i = 0; i < src.length; i++) {
                chars.set(i, src[i]);
            }
        });
        shared[0] = 'Z';

        assertEquals("abc", result);
        assertTrue(result.startsWith("a"));
    }
}

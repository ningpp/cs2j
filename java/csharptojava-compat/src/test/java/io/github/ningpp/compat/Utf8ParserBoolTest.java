package io.github.ningpp.compat;

import org.junit.jupiter.api.Test;

import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertTrue;

/**
 * Utf8Parser must support out bool parsing: the converter emits
 * tryParse(ReadOnlySpan&lt;Integer&gt;, BoolHolder, IntHolder) for C#
 * Utf8Parser.TryParse(span, out bool value, out int bytesConsumed)
 * (BooleanConverter.ReadWithQuotes).
 */
public class Utf8ParserBoolTest {

    private static ReadOnlySpan<Integer> asSpan(String text) {
        Integer[] chars = new Integer[text.length()];
        for (int i = 0; i < text.length(); i++) {
            chars[i] = (int) text.charAt(i);
        }
        return new ReadOnlySpan<>(chars);
    }

    @Test
    public void tryParse_bool_true() {
        BoolHolder value = new BoolHolder();
        IntHolder bytesConsumed = new IntHolder();
        assertTrue(Utf8Parser.tryParse(asSpan("true"), value, bytesConsumed));
        assertTrue(value.value);
        assertTrue(bytesConsumed.value == 4);
    }

    @Test
    public void tryParse_bool_false() {
        BoolHolder value = new BoolHolder();
        IntHolder bytesConsumed = new IntHolder();
        assertTrue(Utf8Parser.tryParse(asSpan("false"), value, bytesConsumed));
        assertFalse(value.value);
        assertTrue(bytesConsumed.value == 5);
    }

    @Test
    public void tryParse_bool_invalid() {
        BoolHolder value = new BoolHolder();
        IntHolder bytesConsumed = new IntHolder();
        assertFalse(Utf8Parser.tryParse(asSpan("maybe"), value, bytesConsumed));
    }
}

package io.github.ningpp.compat;

import org.junit.jupiter.api.Test;
import static org.junit.jupiter.api.Assertions.assertArrayEquals;
import static org.junit.jupiter.api.Assertions.assertEquals;

/**
 * .NET's default encoder replacement fallback is U+FFFD. A lone surrogate must
 * encode as EF BF BD (3 bytes) in UTF-8, matching .NET's Encoding.UTF8.GetBytes.
 */
public class EncodingSurrogateTest {

    @Test
    public void loneHighSurrogate_EncodesAsReplacementChar() {
        byte[] bytes = Encoding.getUTF8().getBytes("\uD800");
        assertArrayEquals(new byte[]{(byte) 0xEF, (byte) 0xBF, (byte) 0xBD}, bytes);
        assertEquals(3, Encoding.getUTF8().getByteCount("\uD800"));
    }

    @Test
    public void validSurrogatePair_EncodesAsFourBytes() {
        byte[] bytes = Encoding.getUTF8().getBytes("\uD83D\uDE00");
        assertArrayEquals(new byte[]{(byte) 0xF0, (byte) 0x9F, (byte) 0x98, (byte) 0x80}, bytes);
    }
}

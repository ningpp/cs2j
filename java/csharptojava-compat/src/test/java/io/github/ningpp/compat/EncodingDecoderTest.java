package io.github.ningpp.compat;

import org.junit.jupiter.api.Test;

import java.nio.charset.StandardCharsets;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertFalse;

class EncodingDecoderTest {
    @Test
    void convertReportsOnlyBytesThatFitInDestinationChars() {
        Decoder decoder = Encoding.getUTF8().getDecoder();
        byte[] bytes = "abcdef".getBytes(StandardCharsets.UTF_8);
        char[] chars = new char[3];
        var bytesUsed = new IntHolder();
        var charsUsed = new IntHolder();
        var completed = new BoolHolder();

        decoder.convert(bytes, 0, bytes.length, chars, 0, chars.length, false, bytesUsed, charsUsed, completed);

        assertEquals(3, charsUsed.value);
        assertEquals(3, bytesUsed.value);
        assertFalse(completed.value);
        assertEquals("abc", new String(chars));
    }
}

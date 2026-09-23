package io.github.ningpp.compat;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertTrue;

import org.junit.jupiter.api.Test;

/**
 * UnsafeRelaxedJsonEscaping is an ALLOWLIST: printable BMP characters outside
 * its allowed UnicodeRanges are escaped. .NET's list excludes the Zs space
 * characters other than U+0020, so U+2000 (EN QUAD neighbourhood) must be
 * escaped as {@code \u2000} — pinned by
 * Utf8JsonWriterTests.escapingTestWhileWriting[59] and
 * Utf8JsonWriterTests.writeString_NonAscii[17].
 */
public class JavaScriptEncoderUnsafeRelaxedTest {

    @Test
    public void willEncode_escapesZsSpaceCharactersUnderUnsafeRelaxed() {
        JavaScriptEncoder encoder = JavaScriptEncoder.getUnsafeRelaxedJsonEscaping();
        assertTrue(encoder.willEncode(0x2000));
        assertTrue(encoder.willEncode(0x2007));
        assertTrue(encoder.willEncode(0x202F));
        // The plain space is the one Zs character the allowlist keeps.
        assertFalse(encoder.willEncode(0x0020));
    }

    @Test
    public void willEncode_unsafeRelaxedAllowsPrintableCharactersInsideRanges() {
        JavaScriptEncoder encoder = JavaScriptEncoder.getUnsafeRelaxedJsonEscaping();
        assertFalse(encoder.willEncode('a'));
        assertFalse(encoder.willEncode(0xE9));   // é — Latin-1 Supplement
        assertFalse(encoder.willEncode(0x4E2D)); // 中 — CJK Unified Ideographs
    }

    @Test
    public void willEncode_alwaysEscapesStructuralAndInvalidCharacters() {
        JavaScriptEncoder encoder = JavaScriptEncoder.getUnsafeRelaxedJsonEscaping();
        assertTrue(encoder.willEncode('"'));
        assertTrue(encoder.willEncode('\\'));
        assertTrue(encoder.willEncode(0x001F));
        assertTrue(encoder.willEncode(0xFFFD));
        assertTrue(encoder.willEncode(0x1F600)); // supplementary scalar
    }

    @Test
    public void utf8ScanFindsTheEscapableSpaceCharacter() {
        JavaScriptEncoder encoder = JavaScriptEncoder.getUnsafeRelaxedJsonEscaping();
        int first = encoder.findFirstCharacterToEncodeUtf8(
                new ReadOnlySpan<>(new byte[] { (byte) 0xE2, (byte) 0x80, (byte) 0x80 }));
        assertEquals(0, first);
    }

    @Test
    public void defaultEncoderEscapesNonAsciiAsBefore() {
        JavaScriptEncoder encoder = JavaScriptEncoder.getDefault();
        assertTrue(encoder.willEncode(0x2000));
        assertTrue(encoder.willEncode(0xE9));
    }
}

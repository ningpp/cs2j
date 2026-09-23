package io.github.ningpp.compat;

import java.lang.foreign.MemorySegment;

import org.junit.jupiter.api.Test;
import static org.junit.jupiter.api.Assertions.assertEquals;

/**
 * {@code JavaScriptEncoder.Create(allowedRanges)} allows ONLY the listed ranges — ASCII
 * letters outside them must be escaped, on the UTF-16 scanning path just as on the UTF-8
 * one. {@code JsonWriterHelper.needsEscaping_erasure_2} (the UTF-16 scan used for string
 * VALUES, e.g. OptionsTests.customEncoderAllowLatin1Supplement writing "age") goes through
 * {@link JavaScriptEncoder#findFirstCharacterToEncode(MemorySegment, int)}, while the
 * property-name path scans UTF-8 through {@link JavaScriptEncoder#willEncode(int)} — the
 * two answers must agree for range-created encoders.
 */
class JavaScriptEncoderAllowedRangesTest {

    private static MemorySegment utf16(String text) {
        return MemorySegment.ofArray(text.toCharArray());
    }

    @Test
    void utf16ScanEscapesAsciiOutsideAllowedRanges() {
        JavaScriptEncoder encoder = JavaScriptEncoder.create(UnicodeRanges.getLatin1Supplement());
        assertEquals(0, encoder.findFirstCharacterToEncode(utf16("age"), 3));
    }

    @Test
    void utf16ScanSkipsCharsInsideAllowedRanges() {
        JavaScriptEncoder encoder = JavaScriptEncoder.create(UnicodeRanges.getLatin1Supplement());
        // é (U+00E9) is inside Latin-1 Supplement; only the trailing 'a' needs escaping.
        assertEquals(1, encoder.findFirstCharacterToEncode(utf16("éa"), 2));
    }

    @Test
    void utf16ScanAllowsEverythingForAllRanges() {
        JavaScriptEncoder encoder = JavaScriptEncoder.create(UnicodeRanges.getAll());
        assertEquals(-1, encoder.findFirstCharacterToEncode(utf16("ageѧ"), 4));
    }

    @Test
    void utf16ScanAgreesWithWillEncodeForEveryAsciiChar() {
        JavaScriptEncoder encoder = JavaScriptEncoder.create(UnicodeRanges.getLatin1Supplement());
        for (int ch = 0x20; ch < 0x7F; ch++) {
            MemorySegment text = utf16(String.valueOf((char) ch));
            int firstIndexToEncode = encoder.findFirstCharacterToEncode(text, 1);
            if (encoder.willEncode(ch)) {
                assertEquals(0, firstIndexToEncode, "char U+" + Integer.toHexString(ch));
            } else {
                assertEquals(-1, firstIndexToEncode, "char U+" + Integer.toHexString(ch));
            }
        }
    }
}

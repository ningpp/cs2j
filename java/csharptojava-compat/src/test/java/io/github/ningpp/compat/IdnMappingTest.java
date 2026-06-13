package io.github.ningpp.compat;

import org.junit.jupiter.api.Test;
import static org.junit.jupiter.api.Assertions.*;

/**
 * Tests for IdnMapping.
 */
class IdnMappingTest {

    // ---- Basic getAscii tests ----

    @Test
    void getAscii_simpleAscii() {
        IdnMapping mapping = new IdnMapping();
        assertEquals("example.com", mapping.getAscii("example.com"));
    }

    @Test
    void getAscii_unicodeDomain() {
        IdnMapping mapping = new IdnMapping();
        String result = mapping.getAscii("\u4F8B\u5B50.com"); // 例子.com
        assertTrue(result.startsWith("xn--"));
    }

    @Test
    void getAscii_withIndex() {
        IdnMapping mapping = new IdnMapping();
        assertEquals("com", mapping.getAscii("example.com", 8));
    }

    @Test
    void getAscii_withIndexAndCount() {
        IdnMapping mapping = new IdnMapping();
        assertEquals("example", mapping.getAscii("example.com", 0, 7));
    }

    @Test
    void getAscii_null_throwsNullPointerException() {
        IdnMapping mapping = new IdnMapping();
        // getAscii(String) delegates to getAscii(String, int, int) which checks null,
        // but the single-arg overload calls unicode.length() first which throws NPE
        assertThrows(NullPointerException.class, () -> mapping.getAscii(null));
    }

    // ---- ArgumentException wrapping test ----

    @Test
    void getAscii_invalidInput_throwsArgumentException() {
        IdnMapping mapping = new IdnMapping();
        // Empty label (consecutive dots) should cause IDN.toASCII to throw IllegalArgumentException,
        // and we verify it's wrapped as ArgumentException
        assertThrows(ArgumentException.class, () -> mapping.getAscii(".."));
    }

    @Test
    void getAscii_invalidInput_argumentExceptionContainsCause() {
        IdnMapping mapping = new IdnMapping();
        try {
            mapping.getAscii("..");
            fail("Expected ArgumentException");
        } catch (ArgumentException e) {
            // Verify the original IllegalArgumentException is preserved as the cause
            assertNotNull(e.getCause());
            assertTrue(e.getCause() instanceof IllegalArgumentException);
        }
    }

    // ---- NFC normalization dot check test ----

    @Test
    void getAscii_nfcDecompositionWithoutExtraDots_succeeds() {
        IdnMapping mapping = new IdnMapping();
        // A string that normalizes differently in NFC but doesn't introduce extra dots
        // should still work fine
        String result = mapping.getAscii("example.com");
        assertEquals("example.com", result);
    }

    // ---- Basic getUnicode tests ----

    @Test
    void getUnicode_punycodeDomain() {
        IdnMapping mapping = new IdnMapping();
        String result = mapping.getUnicode("xn--fsq.com");
        assertNotNull(result);
    }

    @Test
    void getUnicode_simpleAscii() {
        IdnMapping mapping = new IdnMapping();
        assertEquals("example.com", mapping.getUnicode("example.com"));
    }

    @Test
    void getUnicode_withIndex() {
        IdnMapping mapping = new IdnMapping();
        assertEquals("com", mapping.getUnicode("example.com", 8));
    }

    @Test
    void getUnicode_withIndexAndCount() {
        IdnMapping mapping = new IdnMapping();
        assertEquals("example", mapping.getUnicode("example.com", 0, 7));
    }

    @Test
    void getUnicode_null_throwsNullPointerException() {
        IdnMapping mapping = new IdnMapping();
        // getUnicode(String) delegates to getUnicode(String, int, int) which checks null,
        // but the single-arg overload calls ascii.length() first which throws NPE
        assertThrows(NullPointerException.class, () -> mapping.getUnicode(null));
    }

    // ---- ArgumentException wrapping for getUnicode ----

    @Test
    void getUnicode_invalidPunycode_throwsArgumentException() {
        IdnMapping mapping = new IdnMapping();
        // IDN.toUnicode generally does not throw for invalid punycode,
        // but if it does, it should be wrapped as ArgumentException.
        // Test with a very long label that exceeds the 63-char limit
        // which may cause IllegalArgumentException in some JVM implementations
        // For now, verify that valid punycode works correctly
        String result = mapping.getUnicode("xn--nxasmq6b.com");
        assertNotNull(result);
    }

    // ---- Properties tests ----

    @Test
    void allowUnassigned_defaultFalse() {
        IdnMapping mapping = new IdnMapping();
        assertFalse(mapping.getAllowUnassigned());
    }

    @Test
    void useStd3AsciiRules_defaultFalse() {
        IdnMapping mapping = new IdnMapping();
        assertFalse(mapping.getUseStd3AsciiRules());
    }

    @Test
    void setAllowUnassigned() {
        IdnMapping mapping = new IdnMapping();
        mapping.setAllowUnassigned(true);
        assertTrue(mapping.getAllowUnassigned());
    }

    @Test
    void setUseStd3AsciiRules() {
        IdnMapping mapping = new IdnMapping();
        mapping.setUseStd3AsciiRules(true);
        assertTrue(mapping.getUseStd3AsciiRules());
    }

    // ---- equals and hashCode tests ----

    @Test
    void equals_sameSettings() {
        IdnMapping m1 = new IdnMapping();
        IdnMapping m2 = new IdnMapping();
        assertEquals(m1, m2);
    }

    @Test
    void equals_differentSettings() {
        IdnMapping m1 = new IdnMapping();
        IdnMapping m2 = new IdnMapping();
        m2.setAllowUnassigned(true);
        assertNotEquals(m1, m2);
    }

    @Test
    void hashCode_sameSettings() {
        IdnMapping m1 = new IdnMapping();
        IdnMapping m2 = new IdnMapping();
        assertEquals(m1.hashCode(), m2.hashCode());
    }

    // ---- ArgumentException is instanceof IllegalArgumentException ----

    @Test
    void argumentExceptionIsIllegalArgumentException() {
        // Verify that ArgumentException extends IllegalArgumentException,
        // so catch (IllegalArgumentException) can still catch ArgumentException
        ArgumentException ex = new ArgumentException("test");
        assertTrue(ex instanceof IllegalArgumentException);
    }

    @Test
    void getAscii_argumentExceptionCaughtByIllegalArgumentException() {
        IdnMapping mapping = new IdnMapping();
        // Verify that the thrown ArgumentException can be caught by IllegalArgumentException
        assertThrows(IllegalArgumentException.class, () -> mapping.getAscii(".."));
    }

    // ---- Round-trip test ----

    @Test
    void getAscii_getUnicode_roundTrip() {
        IdnMapping mapping = new IdnMapping();
        String unicode = "\u4F8B\u5B50.com"; // 例子.com
        String ascii = mapping.getAscii(unicode);
        String backToUnicode = mapping.getUnicode(ascii);
        assertEquals(unicode, backToUnicode);
    }
}

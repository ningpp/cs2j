package io.github.ningpp.compat;

import org.junit.jupiter.api.Test;
import static org.junit.jupiter.api.Assertions.*;

/**
 * Tests for StringHelper utility methods.
 */
class StringHelperTest {

    // ---- isNullOrEmpty ----

    @Test
    void isNullOrEmpty_null() {
        assertTrue(StringHelper.isNullOrEmpty(null));
    }

    @Test
    void isNullOrEmpty_empty() {
        assertTrue(StringHelper.isNullOrEmpty(""));
    }

    @Test
    void isNullOrEmpty_nonEmpty() {
        assertFalse(StringHelper.isNullOrEmpty("a"));
    }

    @Test
    void isNullOrEmpty_whitespace() {
        assertFalse(StringHelper.isNullOrEmpty(" "));
    }

    // ---- isNullOrWhiteSpace ----

    @Test
    void isNullOrWhiteSpace_null() {
        assertTrue(StringHelper.isNullOrWhiteSpace(null));
    }

    @Test
    void isNullOrWhiteSpace_empty() {
        assertTrue(StringHelper.isNullOrWhiteSpace(""));
    }

    @Test
    void isNullOrWhiteSpace_spaces() {
        assertTrue(StringHelper.isNullOrWhiteSpace("   "));
    }

    @Test
    void isNullOrWhiteSpace_tabs() {
        assertTrue(StringHelper.isNullOrWhiteSpace("\t\n\r"));
    }

    @Test
    void isNullOrWhiteSpace_nonEmpty() {
        assertFalse(StringHelper.isNullOrWhiteSpace("a"));
    }

    @Test
    void isNullOrWhiteSpace_surroundedBySpaces() {
        assertFalse(StringHelper.isNullOrWhiteSpace(" a "));
    }

    // ---- concat ----

    @Test
    void concat_basic() {
        assertEquals("abc", StringHelper.concat("a", "b", "c"));
    }

    @Test
    void concat_empty() {
        assertEquals("", StringHelper.concat());
    }

    @Test
    void concat_null() {
        assertEquals("", StringHelper.concat((Object[]) null));
    }

    @Test
    void concat_withNulls() {
        assertEquals("ac", StringHelper.concat("a", null, "c"));
    }

    @Test
    void concat_singleElement() {
        assertEquals("hello", StringHelper.concat("hello"));
    }

    @Test
    void concat_withNumbers() {
        assertEquals("12", StringHelper.concat(1, 2));
    }

    // ---- compare ----

    @Test
    void compare_nullNull() {
        assertEquals(0, StringHelper.compare(null, null));
    }

    @Test
    void compare_nullNonNull() {
        assertEquals(-1, StringHelper.compare(null, "a"));
    }

    @Test
    void compare_nonNullNull() {
        assertEquals(1, StringHelper.compare("a", null));
    }

    @Test
    void compare_equal() {
        assertEquals(0, StringHelper.compare("abc", "abc"));
    }

    @Test
    void compare_lessThan() {
        assertTrue(StringHelper.compare("a", "b") < 0);
    }

    @Test
    void compare_greaterThan() {
        assertTrue(StringHelper.compare("b", "a") > 0);
    }

    // C# String.Compare returns sign values (-1/0/1), not raw code point differences.
    // Reproduces testCompare bug: "http://www.contoso.com" vs "http://www.contosooo.com"
    // differ at position 18 ('.' ASCII 46 vs 'o' ASCII 111), raw diff = -65, expected -1.
    @Test
    void compare_returnsSignValue_notRawDifference() {
        assertEquals(-1, StringHelper.compare("http://www.contoso.com", "http://www.contosooo.com"));
    }

    @Test
    void compare_returnsSignValue_positive() {
        assertEquals(1, StringHelper.compare("http://www.contosooo.com", "http://www.contoso.com"));
    }

    @Test
    void compare_ignoreCase_returnsSignValue_notRawDifference() {
        assertEquals(-1, StringHelper.compare("http://www.contoso.com", "http://www.contosooo.com", false));
    }

    @Test
    void compareOrdinal_returnsSignValue_notRawDifference() {
        assertEquals(-1, StringHelper.compareOrdinal("http://www.contoso.com", "http://www.contosooo.com"));
    }

    @Test
    void compareOrdinal_substring_returnsSignValue_notRawDifference() {
        // substrings: "contoso.com" vs "contosooo.c" differ at position 7 ('.' vs 'o')
        assertEquals(-1, StringHelper.compareOrdinal("xxcontoso.com", 2, "xxcontosooo.com", 2, 11));
    }

    @Test
    void compare_ignoreCase_true() {
        assertEquals(0, StringHelper.compare("ABC", "abc", true));
    }

    @Test
    void compare_ignoreCase_false() {
        assertTrue(StringHelper.compare("ABC", "abc", false) != 0);
    }

    @Test
    void compare_ignoreCase_nullNull() {
        assertEquals(0, StringHelper.compare(null, null, true));
    }

    @Test
    void compare_ignoreCase_nullLeft() {
        assertEquals(-1, StringHelper.compare(null, "a", true));
    }

    @Test
    void compare_ignoreCase_nullRight() {
        assertEquals(1, StringHelper.compare("a", null, true));
    }

    // ---- equals ----

    @Test
    void equals_bothNull() {
        assertTrue(StringHelper.equals(null, null));
    }

    @Test
    void equals_leftNull() {
        assertFalse(StringHelper.equals(null, "a"));
    }

    @Test
    void equals_rightNull() {
        assertFalse(StringHelper.equals("a", null));
    }

    @Test
    void equals_same() {
        assertTrue(StringHelper.equals("abc", "abc"));
    }

    @Test
    void equals_different() {
        assertFalse(StringHelper.equals("abc", "def"));
    }

    @Test
    void equals_ignoreCase_true() {
        assertTrue(StringHelper.equals("ABC", "abc", true));
    }

    @Test
    void equals_ignoreCase_false() {
        assertFalse(StringHelper.equals("ABC", "abc", false));
    }

    @Test
    void equals_ignoreCase_nulls() {
        assertTrue(StringHelper.equals(null, null, true));
    }

    @Test
    void equals_ignoreCase_leftNull() {
        assertFalse(StringHelper.equals(null, "abc", true));
    }

    @Test
    void equals_ignoreCase_rightNull() {
        assertFalse(StringHelper.equals("abc", null, true));
    }

    // ---- startsWith ----

    @Test
    void startsWith_ignoreCase_true() {
        assertTrue(StringHelper.startsWith("Hello World", "hello", true));
    }

    @Test
    void startsWith_ignoreCase_false() {
        assertFalse(StringHelper.startsWith("Hello World", "hello", false));
    }

    @Test
    void startsWith_exactMatch() {
        assertTrue(StringHelper.startsWith("Hello", "Hello", false));
    }

    @Test
    void startsWith_prefixLongerThanString() {
        assertFalse(StringHelper.startsWith("Hi", "Hello", false));
    }

    @Test
    void startsWith_nullString() {
        assertFalse(StringHelper.startsWith(null, "a", false));
    }

    @Test
    void startsWith_nullPrefix() {
        assertFalse(StringHelper.startsWith("abc", null, false));
    }

    // ---- endsWith ----

    @Test
    void endsWith_ignoreCase_true() {
        assertTrue(StringHelper.endsWith("Hello World", "WORLD", true));
    }

    @Test
    void endsWith_ignoreCase_false() {
        assertFalse(StringHelper.endsWith("Hello World", "WORLD", false));
    }

    @Test
    void endsWith_exactMatch() {
        assertTrue(StringHelper.endsWith("Hello", "Hello", false));
    }

    @Test
    void endsWith_suffixLongerThanString() {
        assertFalse(StringHelper.endsWith("Hi", "Hello", false));
    }

    @Test
    void endsWith_nullString() {
        assertFalse(StringHelper.endsWith(null, "a", false));
    }

    @Test
    void endsWith_nullSuffix() {
        assertFalse(StringHelper.endsWith("abc", null, false));
    }

    // ---- indexOf ----

    @Test
    void indexOf_ignoreCase_found() {
        assertEquals(0, StringHelper.indexOf("Hello", "hello", true));
    }

    @Test
    void indexOf_ignoreCase_notFound() {
        assertEquals(-1, StringHelper.indexOf("Hello", "xyz", true));
    }

    @Test
    void indexOf_caseSensitive_found() {
        assertEquals(0, StringHelper.indexOf("hello", "hello", false));
    }

    @Test
    void indexOf_caseSensitive_notFound() {
        assertEquals(-1, StringHelper.indexOf("Hello", "hello", false));
    }

    @Test
    void indexOf_withStartIndex() {
        assertEquals(2, StringHelper.indexOf("abcabc", "c", 0, false));
    }

    @Test
    void indexOf_withStartIndex_ignoreCase() {
        assertEquals(2, StringHelper.indexOf("abcABC", "c", 0, true));
    }

    // ---- lastIndexOf ----

    @Test
    void lastIndexOf_ignoreCase() {
        assertEquals(6, StringHelper.lastIndexOf("Hello HELLO", "hello", true));
    }

    @Test
    void lastIndexOf_caseSensitive() {
        assertEquals(6, StringHelper.lastIndexOf("Hello HELLO", "HELLO", false));
    }

    @Test
    void lastIndexOf_notFound() {
        assertEquals(-1, StringHelper.lastIndexOf("Hello", "xyz", false));
    }

    // ---- contains ----

    @Test
    void contains_ignoreCase_true() {
        assertTrue(StringHelper.contains("Hello World", "WORLD", true));
    }

    @Test
    void contains_ignoreCase_false() {
        assertFalse(StringHelper.contains("Hello World", "xyz", true));
    }

    @Test
    void contains_caseSensitive_true() {
        assertTrue(StringHelper.contains("Hello World", "World", false));
    }

    @Test
    void contains_caseSensitive_false() {
        assertFalse(StringHelper.contains("Hello World", "world", false));
    }

    // ---- copyTo ----

    @Test
    void copyTo_basic() {
        String s = "Hello World";
        char[] dest = new char[11];
        StringHelper.copyTo(s, 0, dest, 0, 11);
        assertArrayEquals("Hello World".toCharArray(), dest);
    }

    @Test
    void copyTo_partialString() {
        String s = "Hello World";
        char[] dest = new char[5];
        StringHelper.copyTo(s, 0, dest, 0, 5);
        assertArrayEquals("Hello".toCharArray(), dest);
    }

    @Test
    void copyTo_withOffset() {
        String s = "Hello World";
        char[] dest = new char[5];
        StringHelper.copyTo(s, 6, dest, 0, 5);
        assertArrayEquals("World".toCharArray(), dest);
    }

    @Test
    void copyTo_withDestinationOffset() {
        String s = "Hello";
        char[] dest = new char[7];
        dest[0] = 'X';
        dest[1] = 'X';
        StringHelper.copyTo(s, 0, dest, 2, 5);
        assertEquals('X', dest[0]);
        assertEquals('X', dest[1]);
        assertArrayEquals(new char[]{'X', 'X', 'H', 'e', 'l', 'l', 'o'}, dest);
    }

    @Test
    void copyTo_emptyString() {
        String s = "";
        char[] dest = new char[0];
        StringHelper.copyTo(s, 0, dest, 0, 0);
        assertEquals(0, dest.length);
    }

    @Test
    void copyTo_singleCharacter() {
        String s = "A";
        char[] dest = new char[1];
        StringHelper.copyTo(s, 0, dest, 0, 1);
        assertEquals('A', dest[0]);
    }

    @Test
    void copyTo_overwriteDestination() {
        String s = "Hello";
        char[] dest = "XXXXXXXXXX".toCharArray();
        StringHelper.copyTo(s, 0, dest, 0, 5);
        assertArrayEquals("HelloXXXXX".toCharArray(), dest);
    }

    @Test
    void copyTo_nullSource() {
        char[] dest = new char[10];
        assertThrows(NullPointerException.class, () -> StringHelper.copyTo(null, 0, dest, 0, 5));
    }

    @Test
    void copyTo_nullDestination() {
        String s = "Hello";
        assertThrows(NullPointerException.class, () -> StringHelper.copyTo(s, 0, null, 0, 5));
    }

    @Test
    void copyTo_negativeSourceIndex() {
        String s = "Hello";
        char[] dest = new char[10];
        assertThrows(IndexOutOfBoundsException.class, () -> StringHelper.copyTo(s, -1, dest, 0, 5));
    }

    @Test
    void copyTo_negativeDestinationIndex() {
        String s = "Hello";
        char[] dest = new char[10];
        assertThrows(IndexOutOfBoundsException.class, () -> StringHelper.copyTo(s, 0, dest, -1, 5));
    }

    @Test
    void copyTo_negativeCount() {
        String s = "Hello";
        char[] dest = new char[10];
        assertThrows(IndexOutOfBoundsException.class, () -> StringHelper.copyTo(s, 0, dest, 0, -1));
    }

    @Test
    void copyTo_sourceIndexPlusCountExceedsLength() {
        String s = "Hello";
        char[] dest = new char[10];
        assertThrows(IndexOutOfBoundsException.class, () -> StringHelper.copyTo(s, 0, dest, 0, 10));
    }

    @Test
    void copyTo_destinationIndexPlusCountExceedsLength() {
        String s = "Hello";
        char[] dest = new char[3];
        assertThrows(IndexOutOfBoundsException.class, () -> StringHelper.copyTo(s, 0, dest, 0, 5));
    }

    // ---- formatCs ----

    @Test
    void formatCs_singlePlaceholder() {
        assertEquals("hello", StringHelper.formatCs(java.util.Locale.ROOT, "{0}", "hello"));
    }

    @Test
    void formatCs_multiplePlaceholders() {
        assertEquals("a.b.c", StringHelper.formatCs(java.util.Locale.ROOT, "{0}.{1}.{2}", "a", "b", "c"));
    }

    @Test
    void formatCs_withFormatSpecifier() {
        // {0:x} is a C# hex format string - should produce hex output
        assertEquals("ff", StringHelper.formatCs(java.util.Locale.ROOT, "{0:x}", 255));
    }

    @Test
    void formatCs_withUpperHexFormatSpecifier() {
        // {0:X} is a C# uppercase hex format string
        assertEquals("FF", StringHelper.formatCs(java.util.Locale.ROOT, "{0:X}", 255));
    }

    @Test
    void formatCs_withDecimalFormatSpecifier() {
        // {0:d} is a C# decimal format string
        assertEquals("255", StringHelper.formatCs(java.util.Locale.ROOT, "{0:d}", 255));
    }

    @Test
    void formatCs_withHexFormat_ushortValue() {
        // ushort 0xFFFF stored as Short(-1) should format as "ffff" not "ffffffff"
        assertEquals("ffff", StringHelper.formatCs(java.util.Locale.ROOT, "{0:x}", (short)-1));
    }

    @Test
    void formatCs_withHexFormat_ushortPositiveValue() {
        // ushort 0xFE08 stored as Short(-504) should format as "fe08"
        assertEquals("fe08", StringHelper.formatCs(java.util.Locale.ROOT, "{0:x}", (short)0xFE08));
    }

    @Test
    void formatCs_ipv4EmbeddedFormat() {
        // The pattern ":{0:d}.{1:d}.{2:d}.{3:d}" used in IPv6AddressHelper
        assertEquals(":192.168.1.1", StringHelper.formatCs(java.util.Locale.ROOT, ":{0:d}.{1:d}.{2:d}.{3:d}", 192, 168, 1, 1));
    }

    @Test
    void formatCs_repeatedPlaceholder() {
        // C# allows referencing the same argument multiple times
        assertEquals("aaa", StringHelper.formatCs(java.util.Locale.ROOT, "{0}{0}{0}", "a"));
    }

    @Test
    void formatCs_noPlaceholders() {
        assertEquals("hello world", StringHelper.formatCs(java.util.Locale.ROOT, "hello world"));
    }

    @Test
    void formatCs_percentSignInFormat() {
        // % in format string should be treated as literal
        assertEquals("100%", StringHelper.formatCs(java.util.Locale.ROOT, "{0}%", 100));
    }
}

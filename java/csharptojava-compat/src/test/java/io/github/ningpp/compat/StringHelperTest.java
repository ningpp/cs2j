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
}

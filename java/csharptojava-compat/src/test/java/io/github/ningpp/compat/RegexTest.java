package io.github.ningpp.compat;

import org.junit.jupiter.api.Test;
import static org.junit.jupiter.api.Assertions.*;

/**
 * Tests for Regex, Match, Group, GroupCollection, and RegexOptions.
 */
class RegexTest {

    // ---- Static Regex.match ----

    @Test
    void staticMatch_success() {
        Match m = Regex.match("hello world", "\\w+");
        assertTrue(m.Success);
        assertNotNull(m.Groups);
    }

    @Test
    void staticMatch_failure() {
        Match m = Regex.match("hello world", "\\d+");
        assertFalse(m.Success);
    }

    @Test
    void staticMatch_groupsGet0_fullMatch() {
        Match m = Regex.match("hello world", "\\w+");
        Group g = m.Groups.get(0);
        assertEquals("hello", g.Value);
    }

    // ---- Static Regex.isMatch ----

    @Test
    void staticIsMatch_true() {
        assertTrue(Regex.isMatch("abc123", "\\d+"));
    }

    @Test
    void staticIsMatch_false() {
        assertFalse(Regex.isMatch("abc", "\\d+"));
    }

    // ---- Static Regex.split ----

    @Test
    void staticSplit_basic() {
        String[] parts = Regex.split("a,b,c", ",");
        assertArrayEquals(new String[]{"a", "b", "c"}, parts);
    }

    @Test
    void staticSplit_noMatch() {
        String[] parts = Regex.split("abc", ",");
        assertArrayEquals(new String[]{"abc"}, parts);
    }

    @Test
    void staticSplit_emptyParts() {
        String[] parts = Regex.split("a,,c", ",");
        assertArrayEquals(new String[]{"a", "", "c"}, parts);
    }

    @Test
    void staticSplit_nullInput() {
        String[] parts = Regex.split(null, ",");
        assertArrayEquals(new String[]{""}, parts);
    }

    // ---- Instance Regex methods ----

    @Test
    void instanceMatch_success() {
        Regex re = new Regex("\\d+");
        Match m = re.match("abc 123 def");
        assertTrue(m.Success);
        assertEquals("123", m.Groups.get(0).Value);
    }

    @Test
    void instanceMatch_failure() {
        Regex re = new Regex("\\d+");
        Match m = re.match("no digits here");
        assertFalse(m.Success);
    }

    @Test
    void instanceIsMatch_true() {
        Regex re = new Regex("\\d+");
        assertTrue(re.isMatch("abc 123"));
    }

    @Test
    void instanceIsMatch_false() {
        Regex re = new Regex("\\d+");
        assertFalse(re.isMatch("no digits"));
    }

    @Test
    void instanceMatch_nullInput() {
        Regex re = new Regex(".*");
        Match m = re.match(null);
        // null is treated as "" which matches ".*"
        assertTrue(m.Success);
    }

    // ---- RegexOptions ----

    @Test
    void regexOptions_ignoreCase() {
        Regex re = new Regex("hello", RegexOptions.IgnoreCase);
        assertTrue(re.isMatch("HELLO"));
    }

    @Test
    void regexOptions_noIgnoreCase() {
        Regex re = new Regex("hello", RegexOptions.None);
        assertFalse(re.isMatch("HELLO"));
    }

    @Test
    void regexOptions_staticMatch_ignoreCase() {
        // Static match uses new Regex(pattern) without options, so case-sensitive
        Match m = Regex.match("HELLO", "hello");
        assertFalse(m.Success);
    }

    // ---- Match and Group details ----

    @Test
    void match_empty_hasSuccessFalse() {
        assertFalse(Match.Empty.Success);
    }

    @Test
    void match_emptyGroupsReturnEmptyGroup() {
        Group g = Match.Empty.Groups.get(0);
        assertEquals(Group.Empty.Value, g.Value);
        assertEquals("", g.Value);
    }

    @Test
    void group_properties() {
        Match m = Regex.match("test123", "\\w+");
        Group g = m.Groups.get(0);
        assertEquals("test123", g.Value);
        assertEquals(7, g.Length);
    }

    @Test
    void group_toString() {
        Group g = new Group("abc");
        assertEquals("abc", g.toString());
    }

    @Test
    void group_nullValue() {
        Group g = new Group(null);
        assertEquals("", g.Value);
        assertEquals(0, g.Length);
    }

    @Test
    void groupCollection_invalidIndex() {
        Match m = Regex.match("a", "\\w+");
        Group g = m.Groups.get(99);
        assertEquals(Group.Empty.Value, g.Value);
    }

    @Test
    void groupCollection_byName_noSuchGroup() {
        Match m = Regex.match("abc", "\\w+");
        Group g = m.Groups.get("nonexistent");
        assertEquals(Group.Empty.Value, g.Value);
    }

    // ---- Named group support (C# (?<name>...) syntax) ----

    @Test
    void namedGroup_singleGroup_returnsMatchedValue() {
        // C# syntax (?<name>...) is also valid Java regex syntax
        Regex re = new Regex("^Seed\\s+(?<seed>(0x)?\\S+)");
        Match m = re.match("Seed 12345");
        assertTrue(m.Success);
        Group g = m.getGroups().get("seed");
        assertEquals("12345", g.getValue());
        assertEquals("12345", g.toString());
    }

    @Test
    void namedGroup_withHexPrefix_returnsFullValue() {
        Regex re = new Regex("^Seed\\s+(?<seed>(0x)?\\S+)", RegexOptions.IgnoreCase);
        Match m = re.match("Seed 0xFF");
        assertTrue(m.Success);
        Group g = m.getGroups().get("seed");
        assertEquals("0xFF", g.getValue());
    }

    @Test
    void namedGroup_multipleGroups() {
        Regex re = new Regex("(?<word>\\w+)\\s+(?<num>\\d+)");
        Match m = re.match("hello 42");
        assertTrue(m.Success);
        assertEquals("hello", m.getGroups().get("word").getValue());
        assertEquals("42", m.getGroups().get("num").getValue());
    }

    @Test
    void namedGroup_simulatesTestFileReaderParseSeed() {
        // Reproduces the exact pattern used in TestFileStrings.ParseSeed
        // which causes FormatException: For input string: ""
        // because GroupCollection.get(String) returned Group.Empty instead
        // of the actual named group value.
        Regex re = new Regex("^Seed\\s+(?<seed>(0x)?\\S+)", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        Match m = re.match("Seed 12345");
        assertTrue(m.Success);
        String seedStr = m.getGroups().get("seed").toString();
        assertEquals("12345", seedStr);
        // This should not throw
        int seed = Integer.parseInt(seedStr);
        assertEquals(12345, seed);
    }

    @Test
    void regexOptions_constants() {
        assertEquals(0, RegexOptions.None);
        assertEquals(1, RegexOptions.Compiled);
        assertEquals(2, RegexOptions.CultureInvariant);
        assertEquals(4, RegexOptions.IgnoreCase);
        assertEquals(16, RegexOptions.Singleline);
    }

    @Test
    void regexOptions_singleline_allowsDotToMatchNewline() {
        Regex re = new Regex("a.b", RegexOptions.Singleline);

        assertTrue(re.isMatch("a\nb"));
    }

    // ---- Multiple matches ----

    @Test
    void instanceMatch_findSecondOccurrence() {
        Regex re = new Regex("\\d+");
        Match m = re.match("12 and 34");
        assertTrue(m.Success);
        assertEquals("12", m.Groups.get(0).Value);
    }

    // ---- Complex patterns ----

    @Test
    void match_emailPattern() {
        Match m = Regex.match("user@example.com", "[\\w.]+@[\\w.]+");
        assertTrue(m.Success);
        assertEquals("user@example.com", m.Groups.get(0).Value);
    }

    @Test
    void split_withRegexPattern() {
        String[] parts = Regex.split("one1two2three", "\\d+");
        assertArrayEquals(new String[]{"one", "two", "three"}, parts);
    }

    // ---- Static Regex.escape / Regex.unescape ----

    @Test
    void escape_plainText_unchanged() {
        assertEquals("Hello", Regex.escape("Hello"));
    }

    @Test
    void escape_metacharacters() {
        assertEquals("\\#\\$\\^\\*\\+\\(\\)\\{}<>\\\\\\|\\.\\ ",
            Regex.escape("#$^*+(){}<>\\|. "));
    }

    @Test
    void escape_controlCharacters() {
        assertEquals("\\n\\r\\t\\f", Regex.escape("\n\r\t\f"));
    }

    @Test
    void escape_backslash() {
        assertEquals("\\\\", Regex.escape("\\"));
    }

    @Test
    void escape_emptyString() {
        assertEquals("", Regex.escape(""));
    }

    @Test
    void escape_null_throwsArgumentNullException() {
        ArgumentNullException ex = assertThrows(ArgumentNullException.class, () -> Regex.escape(null));
        assertEquals("str", ex.getParamName());
    }

    @Test
    void unescape_plainText_unchanged() {
        assertEquals("Hello", Regex.unescape("Hello"));
    }

    @Test
    void unescape_metacharacters() {
        assertEquals("#$^*+(){}<>\\|. ",
            Regex.unescape("\\#\\$\\^\\*\\+\\(\\)\\{}<>\\\\\\|\\.\\ "));
    }

    @Test
    void unescape_controlCharacters() {
        assertEquals("\n\r\t\f", Regex.unescape("\\n\\r\\t\\f"));
    }

    @Test
    void unescape_backslash() {
        assertEquals("\\", Regex.unescape("\\\\"));
    }

    @Test
    void unescape_loneTrailingBackslash() {
        assertEquals("", Regex.unescape("\\"));
    }

    @Test
    void unescape_emptyString() {
        assertEquals("", Regex.unescape(""));
    }

    @Test
    void unescape_null_throwsArgumentNullException() {
        ArgumentNullException ex = assertThrows(ArgumentNullException.class, () -> Regex.unescape(null));
        assertEquals("str", ex.getParamName());
    }

    @Test
    void escapeAndUnescape_roundTrip() {
        String input = "a # $ ^ * + ( ) { } < > \\ | . \n\r\t\f 123";
        assertEquals(input, Regex.unescape(Regex.escape(input)));
    }

    @Test
    void unescape_hexOctalAndControlEscapes() {
        assertEquals("A", Regex.unescape("\\x41"));
        assertEquals("A", Regex.unescape("\\u0041"));
        assertEquals("A", Regex.unescape("\\101"));
        assertEquals("\u0007\b\u001B\f\n\r\t\u000B",
            Regex.unescape("\\a\\b\\e\\f\\n\\r\\t\\v"));
        assertEquals("\u0001", Regex.unescape("\\cA"));
        assertEquals("!", Regex.unescape("\\!"));
    }

    @Test
    void unescape_unrecognizedWordEscape_throws() {
        assertThrows(ArgumentException.class, () -> Regex.unescape("\\m"));
    }

    // ---- matches() with multiple results: index preservation ----

    @Test
    void matches_multipleMatchIndices_preservedAfterIteration() {
        // When Regex.matches returns multiple Match objects, each Match.getIndex()
        // must return the correct position of that specific match, not the position
        // of the last match (which happens if all Match objects share the same
        // Matcher reference).
        Regex re = new Regex("\\d+");
        MatchCollection mc = re.matches("a12b34c56");

        assertEquals(3, mc.getCount());

        // Collect all indices — they must be 1, 4, 7 (not all 7)
        int[] expected = {1, 4, 7};
        for (int i = 0; i < mc.getCount(); i++) {
            Match m = mc.get(i);
            assertEquals(expected[i], m.getIndex(), "Match " + i + " index");
        }
    }

    @Test
    void matches_lookbehindPattern_correctIndex() {
        // This mirrors the XmlConvert.encodeName pattern: (?<=_)[Xx]([0-9a-fA-F]{4})_
        // The match starts at [Xx], but matchPos = index - 1 points to the underscore.
        // The index must be the position of 'x', not affected by later matches.
        Regex re = new Regex("(?<=_)[Xx]([0-9a-fA-F]{4})_");
        MatchCollection mc = re.matches("Order_x0020__x0030_", 5);

        assertEquals(2, mc.getCount());

        // First match: "x0020_" starting at index 6 (after _ at index 5)
        Match m0 = mc.get(0);
        assertEquals(6, m0.getIndex());

        // Second match: "x0030_" starting at index 13 (after _ at index 12)
        Match m1 = mc.get(1);
        assertEquals(13, m1.getIndex());

        // Critical: after iterating, m0.getIndex() must still be 6 (not 13)
        assertEquals(6, m0.getIndex(), "First match index changed after iteration!");
    }
}

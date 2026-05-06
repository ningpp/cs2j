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

    @Test
    void regexOptions_constants() {
        assertEquals(0, RegexOptions.None);
        assertEquals(1, RegexOptions.Compiled);
        assertEquals(2, RegexOptions.CultureInvariant);
        assertEquals(4, RegexOptions.IgnoreCase);
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
}

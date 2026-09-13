package io.github.ningpp.compat;

import org.junit.jupiter.api.Test;
import static org.junit.jupiter.api.Assertions.*;

/**
 * Tests for StringHelper.join(String separator, ReadOnlySpan&lt;Character&gt;...) overload.
 *
 * <p>C# source that generates this call pattern:</p>
 * <pre>{@code
 * Console.WriteLine(string.Join("\\", asSpan(path1), asSpan(path2), asSpan(path3)));
 * }</pre>
 */
class StringHelperJoinReadOnlySpanTest {

    // ──────────────────── char[]-backed spans ────────────────────

    @Test
    void twoSpans_charBacked_joinWithColon() {
        ReadOnlySpan<Character> a = new ReadOnlySpan<>("foo".toCharArray());
        ReadOnlySpan<Character> b = new ReadOnlySpan<>("bar".toCharArray());
        assertEquals("foo:bar", StringHelper.join(":", a, b));
    }

    @Test
    void threeSpans_charBacked_joinWithDash() {
        ReadOnlySpan<Character> a = new ReadOnlySpan<>("abc".toCharArray());
        ReadOnlySpan<Character> b = new ReadOnlySpan<>("def".toCharArray());
        ReadOnlySpan<Character> c = new ReadOnlySpan<>("ghi".toCharArray());
        assertEquals("abc-def-ghi", StringHelper.join("-", a, b, c));
    }

    /**
     * Six ReadOnlySpan parameters — mirrors the original compilation error signature:
     * join(String, ReadOnlySpan&lt;Character&gt;, ..., ReadOnlySpan&lt;Character&gt;) (1 + 6 args).
     */
    @Test
    void sixSpans_charBacked_matchesOriginalCallSignature() {
        ReadOnlySpan<Character> s1 = new ReadOnlySpan<>("a".toCharArray());
        ReadOnlySpan<Character> s2 = new ReadOnlySpan<>("b".toCharArray());
        ReadOnlySpan<Character> s3 = new ReadOnlySpan<>("c".toCharArray());
        ReadOnlySpan<Character> s4 = new ReadOnlySpan<>("d".toCharArray());
        ReadOnlySpan<Character> s5 = new ReadOnlySpan<>("e".toCharArray());
        ReadOnlySpan<Character> s6 = new ReadOnlySpan<>("f".toCharArray());
        assertEquals("a.b.c.d.e.f", StringHelper.join(".", s1, s2, s3, s4, s5, s6));
    }

    @Test
    void twoSpans_charBacked_withBackslashSeparator() {
        // C# path-building pattern: string.Join("\\", span1, span2)
        // C# source uses "\\" → Java sees "C:\temp\file"; we build that separator
        // programmatically so the test source never contains an escaped backslash literal.
        String backslash = String.valueOf((char) 92);
        ReadOnlySpan<Character> dir  = new ReadOnlySpan<>("C:temp".replace('t', 't').toCharArray());
        // "C:" and "temp" are clearer constructed via concat to avoid any literal confusion:
        ReadOnlySpan<Character> part1 = new ReadOnlySpan<>("C:".toCharArray());
        ReadOnlySpan<Character> part2 = new ReadOnlySpan<>("temp".toCharArray());
        ReadOnlySpan<Character> part3 = new ReadOnlySpan<>("file".toCharArray());
        String sep = backslash;
        assertEquals("C:" + sep + "temp" + sep + "file", StringHelper.join(sep, part1, part2, part3));
    }

    // ──────────────────── String-backed spans (fromString) ────────────────────

    @Test
    void stringBackedSpans_joinPreservesContent() {
        String source = "hello world test";
        ReadOnlySpan<Character> hello = ReadOnlySpan.fromString(source, 0, 5);
        ReadOnlySpan<Character> world = ReadOnlySpan.fromString(source, 6, 5);
        ReadOnlySpan<Character> test  = ReadOnlySpan.fromString(source, 12, 4);
        assertEquals("hello,world,test", StringHelper.join(",", hello, world, test));
    }

    @Test
    void slicedSpan_joinUsesSubsectionOnly() {
        String data = "xxfooYYbarZZ";
        ReadOnlySpan<Character> foo = ReadOnlySpan.fromString(data, 2, 3);
        ReadOnlySpan<Character> bar = ReadOnlySpan.fromString(data, 7, 3);
        assertEquals("foo/bar", StringHelper.join("/", foo, bar));
    }

    // ──────────────────── empty / edge cases ────────────────────

    @Test
    void emptySpans_onlySeparators() {
        ReadOnlySpan<Character> e1 = new ReadOnlySpan<>(new char[0]);
        ReadOnlySpan<Character> e2 = new ReadOnlySpan<>("".toCharArray());
        ReadOnlySpan<Character> e3 = ReadOnlySpan.fromString("abc", 0, 0);
        assertEquals("--", StringHelper.join("-", e1, e2, e3));
    }

    @Test
    void singleSpan_noSeparatorAdded() {
        ReadOnlySpan<Character> only = new ReadOnlySpan<>("solo".toCharArray());
        assertEquals("solo", StringHelper.join("IGNORE", only));
    }

    @Test
    void mixedEmptyAndNonEmptySpans() {
        ReadOnlySpan<Character> a    = new ReadOnlySpan<>("a".toCharArray());
        ReadOnlySpan<Character> e    = new ReadOnlySpan<>(new char[0]);
        ReadOnlySpan<Character> c    = new ReadOnlySpan<>("c".toCharArray());
        assertEquals("a..c", StringHelper.join(".", a, e, c));
    }

    // ──────────────────── null handling ────────────────────

    @Test
    void nullSpanInArray_rendersAsEmpty() {
        ReadOnlySpan<Character> a = new ReadOnlySpan<>("first".toCharArray());
        ReadOnlySpan<Character> b = null;
        ReadOnlySpan<Character> c = new ReadOnlySpan<>("third".toCharArray());
        assertEquals("first--third", StringHelper.join("-", a, b, c));
    }

    @Test
    void allNullSpans_onlySeparators() {
        assertEquals("-", StringHelper.join("-", (ReadOnlySpan<Character>) null, (ReadOnlySpan<Character>) null));
    }

    @Test
    void nullSeparator_treatedAsEmptyConcat() {
        ReadOnlySpan<Character> a = new ReadOnlySpan<>("abc".toCharArray());
        ReadOnlySpan<Character> b = new ReadOnlySpan<>("def".toCharArray());
        assertEquals("abcdef", StringHelper.join(null, a, b));
    }

    // ──────────────────── String-backed + null separator (no-copy fast path) ────────────────────

    @Test
    void stringBackedSpans_nullSeparator_concatenatesBacking() {
        String source = "alpha beta gamma delta";
        ReadOnlySpan<Character> alpha = ReadOnlySpan.fromString(source, 0, 5);
        ReadOnlySpan<Character> beta  = ReadOnlySpan.fromString(source, 6, 4);
        ReadOnlySpan<Character> gamma = ReadOnlySpan.fromString(source, 11, 5);
        ReadOnlySpan<Character> delta = ReadOnlySpan.fromString(source, 17, 5);
        assertEquals("alphabetagammadelta", StringHelper.join(null, alpha, beta, gamma, delta));
    }
}

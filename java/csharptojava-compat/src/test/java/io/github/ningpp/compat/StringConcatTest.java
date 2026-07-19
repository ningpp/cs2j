package io.github.ningpp.compat;

import static org.junit.jupiter.api.Assertions.assertEquals;

import org.junit.jupiter.api.Test;

class StringConcatTest {
    @Test
    void joinReturnsConcatenatedStringAndResets() {
        StringConcat sc = new StringConcat();
        sc.append("a").append("b");
        assertEquals("ab", sc.join());
        assertEquals("", sc.join());
    }

    @Test
    void appendLineAddsNewline() {
        StringConcat sc = new StringConcat();
        sc.appendLine("a").appendLine("b");
        String sep = System.lineSeparator();
        assertEquals("a" + sep + "b" + sep, sc.toString());
    }
}

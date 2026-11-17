package io.github.ningpp.compat;

import dotnet.text.regularexpressions.Regex;
import org.junit.jupiter.api.Test;

import static org.junit.jupiter.api.Assertions.assertArrayEquals;

/**
 * Regression test for C# static Regex.Split(input, pattern) calls emitted by
 * the converter: the compat shim must expose the static two-argument overload,
 * not just the instance split(String).
 */
class RegexCompatTest {

    @Test
    void staticSplitUsesPatternArgument() {
        assertArrayEquals(new String[] {"a", "b"}, Regex.split("a  b", "\\s{2,}"));
    }
}

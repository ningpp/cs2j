package io.github.ningpp.compat;

import org.junit.jupiter.api.Test;

import java.util.Locale;

import static org.junit.jupiter.api.Assertions.assertEquals;

class TextInfoToUpperCaseTest {

    @Test
    void toUpperCaseAliasWorks() {
        TextInfo ti = new TextInfo(Locale.ROOT);
        assertEquals("ABC", ti.toUpperCase("abc"));
    }
}

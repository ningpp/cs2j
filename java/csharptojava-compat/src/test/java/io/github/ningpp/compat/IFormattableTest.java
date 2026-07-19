package io.github.ningpp.compat;

import org.junit.jupiter.api.Test;

import static org.junit.jupiter.api.Assertions.assertEquals;

class IFormattableTest {

    @Test
    void formattableInterfaceCanBeImplemented() {
        IFormattable formattable = (format, formatProvider) -> "formatted";
        assertEquals("formatted", formattable.toString("G", null));
    }
}

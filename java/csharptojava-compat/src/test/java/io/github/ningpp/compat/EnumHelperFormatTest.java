package io.github.ningpp.compat;

import org.junit.jupiter.api.Test;

import static org.junit.jupiter.api.Assertions.assertEquals;

class EnumHelperFormatTest {

    enum Color {
        RED, GREEN, BLUE
    }

    @Test
    void formatGeneral() {
        assertEquals("GREEN", EnumHelper.format(Color.class, Color.GREEN, "G"));
    }

    @Test
    void formatDecimal() {
        assertEquals("1", EnumHelper.format(Color.class, Color.GREEN, "D"));
    }

    @Test
    void formatHex() {
        assertEquals("00000001", EnumHelper.format(Color.class, Color.GREEN, "X"));
    }
}

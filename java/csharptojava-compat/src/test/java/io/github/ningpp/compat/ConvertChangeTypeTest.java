package io.github.ningpp.compat;

import org.junit.jupiter.api.Test;

import static org.junit.jupiter.api.Assertions.assertEquals;

class ConvertChangeTypeTest {

    @Test
    void changeTypeObjectToInt() {
        assertEquals(42, Convert.changeType(42L, int.class));
    }

    @Test
    void changeTypeStringToInt() {
        assertEquals(7, Convert.changeType("7", Integer.class));
    }

    @Test
    void changeTypeObjectToString() {
        assertEquals("123", Convert.changeType(123, String.class));
    }
}

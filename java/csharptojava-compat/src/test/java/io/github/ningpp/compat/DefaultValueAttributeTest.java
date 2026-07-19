package io.github.ningpp.compat;

import org.junit.jupiter.api.Test;

import static org.junit.jupiter.api.Assertions.assertEquals;

class DefaultValueAttributeTest {

    @Test
    void valueRoundTrip() {
        DefaultValueAttribute attr = new DefaultValueAttribute(42);
        assertEquals(42, attr.getValue());
    }
}

package io.github.ningpp.compat;

import org.junit.jupiter.api.Test;

import static org.junit.jupiter.api.Assertions.assertNotNull;

class ValueTypeTest {

    @Test
    void canExtendValueType() {
        ValueType vt = new ValueType() {};
        assertNotNull(vt);
    }
}

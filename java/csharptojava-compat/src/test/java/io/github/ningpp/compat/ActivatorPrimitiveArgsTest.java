package io.github.ningpp.compat;

import static org.junit.jupiter.api.Assertions.assertNotNull;

import org.junit.jupiter.api.Test;

class ActivatorPrimitiveArgsTest {
    static class C {
        public C(int value, String text) {
        }
    }

    @Test
    void boxedIntegerMatchesPrimitiveIntConstructorParam() {
        Object instance = Activator.createInstance(C.class, new Object[] { 1, "x" });
        assertNotNull(instance);
    }
}

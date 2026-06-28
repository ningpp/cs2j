package io.github.ningpp.compat;

import org.junit.jupiter.api.Test;

import java.util.concurrent.atomic.AtomicInteger;

import static org.junit.jupiter.api.Assertions.*;

class LazyTest {
    @Test
    void supplier_runsOnlyWhenValueIsRequested() {
        AtomicInteger calls = new AtomicInteger();
        Lazy<String> lazy = new Lazy<>(() -> {
            calls.incrementAndGet();
            return "created";
        });

        assertFalse(lazy.isValueCreated());
        assertEquals(0, calls.get());

        assertEquals("created", lazy.getValue());
        assertTrue(lazy.isValueCreated());
        assertEquals(1, calls.get());

        assertEquals("created", lazy.getValue());
        assertEquals(1, calls.get());
    }

    @Test
    void valueConstructorStartsCreated() {
        Lazy<Integer> lazy = new Lazy<>(42);

        assertTrue(lazy.isValueCreated());
        assertEquals(42, lazy.getValue());
    }

    @Test
    void booleanConstructorUsesSupplier() {
        Lazy<String> lazy = new Lazy<>(() -> "ok", true);

        assertEquals("ok", lazy.getValue());
        assertTrue(lazy.isValueCreated());
    }
}

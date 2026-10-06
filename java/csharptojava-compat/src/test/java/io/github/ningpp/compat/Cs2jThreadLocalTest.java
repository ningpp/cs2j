package io.github.ningpp.compat;

import org.junit.jupiter.api.Test;

import java.util.concurrent.atomic.AtomicInteger;

import static org.junit.jupiter.api.Assertions.*;

class Cs2jThreadLocalTest {

    @Test
    void eachThreadGetsIndependentFactoryValue() throws Exception {
        Cs2jThreadLocal<Integer> tl = new Cs2jThreadLocal<>(() -> 42);
        assertFalse(tl.isValueCreated());
        assertEquals(42, tl.getValue());
        assertTrue(tl.isValueCreated());
        tl.setValue(7);
        assertEquals(7, tl.getValue());

        int[] other = new int[1];
        Thread t = new Thread(() -> {
            assertFalse(tl.isValueCreated());
            other[0] = tl.getValue();
            assertTrue(tl.isValueCreated());
        });
        t.start();
        t.join();
        assertEquals(42, other[0]);
        // main thread's override is untouched by the other thread
        assertEquals(7, tl.getValue());
    }

    @Test
    void defaultCtorStartsUncreatedWithNull() {
        Cs2jThreadLocal<String> tl = new Cs2jThreadLocal<>();
        assertFalse(tl.isValueCreated());
        assertNull(tl.getValue());
        assertTrue(tl.isValueCreated());
        tl.setValue("x");
        assertEquals("x", tl.getValue());
    }

    @Test
    void factoryRunsOncePerThreadAndRetriesAfterRemove() {
        AtomicInteger calls = new AtomicInteger();
        Cs2jThreadLocal<String> tl = new Cs2jThreadLocal<>(() -> {
            calls.incrementAndGet();
            return "v" + calls.get();
        });
        assertEquals("v1", tl.getValue());
        assertEquals("v1", tl.getValue());
        assertEquals(1, calls.get());
        tl.dispose();
        assertFalse(tl.isValueCreated());
        assertEquals("v2", tl.getValue());
        assertEquals(2, calls.get());
    }

    @Test
    void nullValueStillMarksCreated() {
        Cs2jThreadLocal<Object> tl = new Cs2jThreadLocal<>(() -> null);
        assertNull(tl.getValue());
        assertTrue(tl.isValueCreated());
    }

    @Test
    void trackAllValuesAndValuesAreRejected() {
        assertThrows(UnsupportedOperationException.class,
                () -> new Cs2jThreadLocal<>(() -> 1, true));
        Cs2jThreadLocal<Integer> tl = new Cs2jThreadLocal<>(() -> 1);
        assertThrows(UnsupportedOperationException.class, tl::getValues);
    }
}

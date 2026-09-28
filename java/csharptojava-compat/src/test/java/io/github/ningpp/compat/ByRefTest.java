package io.github.ningpp.compat;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertNull;
import static org.junit.jupiter.api.Assertions.assertThrows;

import java.util.concurrent.atomic.AtomicInteger;
import org.junit.jupiter.api.Test;

class ByRefTest {
    @Test
    void slotDefaultNullAndSeeded() {
        ByRef<String> scratch = ByRef.create();
        assertNull(scratch.getValue());
        scratch.setValue("x");
        assertEquals("x", scratch.getValue());

        ByRef<Integer> seeded = ByRef.create(7);
        assertEquals(7, seeded.getValue());
    }

    @Test
    void arrayElementPassesThroughAndAliases() {
        Integer[] arr = { 1, 2, 3 };
        ByRef<Integer> a = ByRef.create(arr, 0);
        ByRef<Integer> b = ByRef.create(arr, 1);
        a.setValue(10);
        assertEquals(10, arr[0]);

        // alias swap through two proxies on the same storage
        ByRef<Integer> tmp = ByRef.create(arr[0]);
        tmp.setValue(a.getValue());
        a.setValue(b.getValue());
        b.setValue(tmp.getValue());
        assertEquals(2, arr[0]);
        assertEquals(10, arr[1]);
    }

    @Test
    void accessorFieldWriteThrough() {
        AtomicInteger field = new AtomicInteger(5);
        ByRef<Integer> proxy = ByRef.create(field::get, field::set);
        proxy.setValue(9);
        assertEquals(9, field.get());
        field.set(11);
        assertEquals(11, proxy.getValue());
    }

    @Test
    void writeSurvivesThrowingSetter() {
        AtomicInteger field = new AtomicInteger(0);
        ByRef<Integer> proxy = ByRef.create(field::get, v -> {
            field.set(v);
            if (v > 3) {
                throw new IllegalStateException("boom");
            }
        });
        assertThrows(IllegalStateException.class, () -> proxy.setValue(4));
        assertEquals(4, field.get());   // already-applied writes are not lost (D3 §3.2)
    }

    @Test
    void refStructByRefSlotModes() {
        RefStructByRef<Integer> seeded = RefStructByRef.create(3);
        assertEquals(3, seeded.getValue());
        seeded.setValue(4);
        assertEquals(4, seeded.getValue());

        RefStructByRef<Integer> scratch = RefStructByRef.create();
        assertNull(scratch.getValue());
    }
}

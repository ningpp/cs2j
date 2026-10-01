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
    void primitiveArrayElementSpecializations() {
        int[] ints = { 1, 2 };
        ByRef<Integer> i = ByRef.create(ints, 0);
        i.setValue(10);
        assertEquals(10, ints[0]);
        assertEquals(10, i.getValue());

        double[] ds = { 1.5, 2.5 };
        ByRef<Double> d = ByRef.create(ds, 1);
        d.setValue(9.5);
        assertEquals(9.5, ds[1]);
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

    @Test
    void readOnlyAccessorGetOnly() {
        Integer[] arr = { 1, 2, 3 };
        ReadOnlyByRef<Integer> ro = ReadOnlyByRef.create(() -> arr[1]);
        assertEquals(2, ro.getValue());
        arr[1] = 20;
        assertEquals(20, ro.getValue());   // read passes through
    }

    @Test
    void readOnlySpanAndReadOnlySpanAliases() {
        Span<Integer> span = new Span<>(new Integer[] { 5, 6, 7 });
        ReadOnlyByRef<Integer> ro = ReadOnlyByRef.create(span, 2);
        assertEquals(7, ro.getValue());

        ReadOnlySpan<Integer> ros = new ReadOnlySpan<>(new Integer[] { 8, 9 });
        ReadOnlyByRef<Integer> rro = ReadOnlyByRef.create(ros, 1);
        assertEquals(9, rro.getValue());
    }

    @Test
    void byRefSpanElementPassesThroughAndAliases() {
        Span<Integer> span = new Span<>(new Integer[] { 1, 2, 3 });
        ByRef<Integer> a = ByRef.create(span, 0);
        a.setValue(10);
        assertEquals(10, span.get(0).intValue());
        span.set(1, 20);
        assertEquals(20, a.getValue() == null ? null : a.getValue().intValue());

        // two proxies on the same span element stay aliased
        ByRef<Integer> b = ByRef.create(span, 2);
        b.setValue(30);
        assertEquals(30, ByRef.create(span, 2).getValue().intValue());
    }
}

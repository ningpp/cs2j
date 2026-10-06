package io.github.ningpp.compat;

import org.junit.jupiter.api.Test;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertNull;
import static org.junit.jupiter.api.Assertions.assertSame;
import static org.junit.jupiter.api.Assertions.assertThrows;

class VolatileTest {

    @Test
    void readWritePrimitiveHolders() {
        IntHolder i = new IntHolder(0);
        Volatile.write(i, 42);
        assertEquals(42, Volatile.read(i));

        LongHolder l = new LongHolder(0L);
        Volatile.write(l, 123_456_789_012L);
        assertEquals(123_456_789_012L, Volatile.read(l));

        BoolHolder b = new BoolHolder(false);
        Volatile.write(b, true);
        assertEquals(true, Volatile.read(b));

        DoubleHolder d = new DoubleHolder(0.0);
        Volatile.write(d, 3.5);
        assertEquals(3.5, Volatile.read(d), 0.0);

        ByteHolder by = new ByteHolder((byte) 0);
        Volatile.write(by, (byte) 7);
        assertEquals((byte) 7, Volatile.read(by));

        ShortHolder s = new ShortHolder((short) 0);
        Volatile.write(s, (short) 300);
        assertEquals((short) 300, Volatile.read(s));

        CharHolder c = new CharHolder((char) 0);
        Volatile.write(c, 'Z');
        assertEquals('Z', Volatile.read(c));

        FloatHolder f = new FloatHolder(0.0f);
        Volatile.write(f, 1.25f);
        assertEquals(1.25f, Volatile.read(f), 0.0f);
    }

    @Test
    void readWriteObjectHolder() {
        ObjectHolder<String> h = new ObjectHolder<>();
        assertNull(Volatile.read(h));
        Volatile.write(h, "publish-me");
        assertSame("publish-me", Volatile.read(h));
    }

    @Test
    void nullLocationIsRejected() {
        assertThrows(NullPointerException.class, () -> Volatile.read((IntHolder) null));
        assertThrows(NullPointerException.class, () -> Volatile.write((IntHolder) null, 1));
        assertThrows(NullPointerException.class, () -> Volatile.read((ObjectHolder<String>) null));
    }
}

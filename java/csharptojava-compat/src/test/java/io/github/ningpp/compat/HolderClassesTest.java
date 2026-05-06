package io.github.ningpp.compat;

import org.junit.jupiter.api.Test;
import static org.junit.jupiter.api.Assertions.*;

/**
 * Tests for all holder types: IntHolder, LongHolder, DoubleHolder, FloatHolder,
 * BoolHolder, CharHolder, ShortHolder, ByteHolder, ObjectHolder.
 */
class HolderClassesTest {

    // ---- IntHolder ----

    @Test
    void intHolder_defaultValue() {
        IntHolder h = new IntHolder();
        assertEquals(0, h.value);
    }

    @Test
    void intHolder_constructor() {
        IntHolder h = new IntHolder(42);
        assertEquals(42, h.value);
    }

    @Test
    void intHolder_assignment() {
        IntHolder h = new IntHolder();
        h.value = -7;
        assertEquals(-7, h.value);
    }

    // ---- LongHolder ----

    @Test
    void longHolder_defaultValue() {
        LongHolder h = new LongHolder();
        assertEquals(0L, h.value);
    }

    @Test
    void longHolder_constructor() {
        LongHolder h = new LongHolder(999999999999L);
        assertEquals(999999999999L, h.value);
    }

    @Test
    void longHolder_assignment() {
        LongHolder h = new LongHolder();
        h.value = Long.MAX_VALUE;
        assertEquals(Long.MAX_VALUE, h.value);
    }

    // ---- DoubleHolder ----

    @Test
    void doubleHolder_defaultValue() {
        DoubleHolder h = new DoubleHolder();
        assertEquals(0.0, h.value);
    }

    @Test
    void doubleHolder_constructor() {
        DoubleHolder h = new DoubleHolder(3.14);
        assertEquals(3.14, h.value, 0.0001);
    }

    @Test
    void doubleHolder_assignment() {
        DoubleHolder h = new DoubleHolder();
        h.value = -1.5;
        assertEquals(-1.5, h.value, 0.0001);
    }

    // ---- FloatHolder ----

    @Test
    void floatHolder_defaultValue() {
        FloatHolder h = new FloatHolder();
        assertEquals(0.0f, h.value);
    }

    @Test
    void floatHolder_constructor() {
        FloatHolder h = new FloatHolder(2.5f);
        assertEquals(2.5f, h.value, 0.0001f);
    }

    @Test
    void floatHolder_assignment() {
        FloatHolder h = new FloatHolder();
        h.value = -0.75f;
        assertEquals(-0.75f, h.value, 0.0001f);
    }

    // ---- BoolHolder ----

    @Test
    void boolHolder_defaultValue() {
        BoolHolder h = new BoolHolder();
        assertFalse(h.value);
    }

    @Test
    void boolHolder_constructorTrue() {
        BoolHolder h = new BoolHolder(true);
        assertTrue(h.value);
    }

    @Test
    void boolHolder_constructorFalse() {
        BoolHolder h = new BoolHolder(false);
        assertFalse(h.value);
    }

    @Test
    void boolHolder_assignment() {
        BoolHolder h = new BoolHolder();
        h.value = true;
        assertTrue(h.value);
    }

    // ---- CharHolder ----

    @Test
    void charHolder_defaultValue() {
        CharHolder h = new CharHolder();
        assertEquals('\0', h.value);
    }

    @Test
    void charHolder_constructor() {
        CharHolder h = new CharHolder('A');
        assertEquals('A', h.value);
    }

    @Test
    void charHolder_assignment() {
        CharHolder h = new CharHolder();
        h.value = 'z';
        assertEquals('z', h.value);
    }

    // ---- ShortHolder ----

    @Test
    void shortHolder_defaultValue() {
        ShortHolder h = new ShortHolder();
        assertEquals((short) 0, h.value);
    }

    @Test
    void shortHolder_constructor() {
        ShortHolder h = new ShortHolder((short) 12345);
        assertEquals((short) 12345, h.value);
    }

    @Test
    void shortHolder_assignment() {
        ShortHolder h = new ShortHolder();
        h.value = Short.MIN_VALUE;
        assertEquals(Short.MIN_VALUE, h.value);
    }

    // ---- ByteHolder ----

    @Test
    void byteHolder_defaultValue() {
        ByteHolder h = new ByteHolder();
        assertEquals((byte) 0, h.value);
    }

    @Test
    void byteHolder_constructor() {
        ByteHolder h = new ByteHolder((byte) 255);
        assertEquals((byte) 255, h.value);
    }

    @Test
    void byteHolder_assignment() {
        ByteHolder h = new ByteHolder();
        h.value = (byte) 42;
        assertEquals((byte) 42, h.value);
    }

    // ---- ObjectHolder ----

    @Test
    void objectHolder_defaultValue() {
        ObjectHolder<String> h = new ObjectHolder<>();
        assertNull(h.value);
    }

    @Test
    void objectHolder_constructor() {
        ObjectHolder<String> h = new ObjectHolder<>("hello");
        assertEquals("hello", h.value);
    }

    @Test
    void objectHolder_assignment() {
        ObjectHolder<String> h = new ObjectHolder<>();
        h.value = "world";
        assertEquals("world", h.value);
    }

    @Test
    void objectHolder_withInteger() {
        ObjectHolder<Integer> h = new ObjectHolder<>(100);
        assertEquals(100, h.value);
    }

    @Test
    void objectHolder_withNull() {
        ObjectHolder<Object> h = new ObjectHolder<>(new Object());
        h.value = null;
        assertNull(h.value);
    }
}

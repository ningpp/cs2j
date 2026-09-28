package io.github.ningpp.compat;

import java.util.function.Consumer;
import java.util.function.Supplier;

/**
 * C# ByRef&lt;T&gt; position model (internal calling convention only — never across ABI).
 * Mirrors src/Cs2jRefOutIn/ByRef.cs: same factories, slot/array-element/accessor kinds.
 * The converter maps reads to {@link #getValue()} and writes to {@link #setValue(Object)}
 * through its property conventions; it never emits the private implementation classes.
 */
public abstract class ByRef<T> {
    public abstract T getValue();
    public abstract void setValue(T value);

    /** out scratch slot (no seed). */
    public static <T> ByRef<T> create() { return new SlotByRef<>(null); }

    /** ref scratch slot (seeded). */
    public static <T> ByRef<T> create(T initial) { return new SlotByRef<>(initial); }

    /** aliasing array element: reads/writes pass through array[index]. */
    public static <T> ByRef<T> create(T[] array, int index) {
        return new ArrayElementByRef<>(array, index);
    }

    /** field/member-chain alias: accessor closures. */
    public static <T> ByRef<T> create(Supplier<T> getter, Consumer<T> setter) {
        return new AccessorByRef<>(getter, setter);
    }

    // Primitive-array specializations: Java generics cannot bind int[] to T[]
    // (spec §5.2 #5 — primitive types accept boxing), so each primitive array
    // element position gets a dedicated overload producing ByRef<Boxed>.

    public static ByRef<Integer> create(int[] array, int index) { return new IntArrayByRef(array, index); }
    public static ByRef<Long> create(long[] array, int index) { return new LongArrayByRef(array, index); }
    public static ByRef<Double> create(double[] array, int index) { return new DoubleArrayByRef(array, index); }
    public static ByRef<Float> create(float[] array, int index) { return new FloatArrayByRef(array, index); }
    public static ByRef<Boolean> create(boolean[] array, int index) { return new BooleanArrayByRef(array, index); }
    public static ByRef<Byte> create(byte[] array, int index) { return new ByteArrayByRef(array, index); }
    public static ByRef<Short> create(short[] array, int index) { return new ShortArrayByRef(array, index); }
    public static ByRef<Character> create(char[] array, int index) { return new CharArrayByRef(array, index); }

    private static final class IntArrayByRef extends ByRef<Integer> {
        private final int[] array;
        private final int index;
        IntArrayByRef(int[] array, int index) { this.array = array; this.index = index; }
        @Override public Integer getValue() { return array[index]; }
        @Override public void setValue(Integer v) { array[index] = v; }
    }
    private static final class LongArrayByRef extends ByRef<Long> {
        private final long[] array;
        private final int index;
        LongArrayByRef(long[] array, int index) { this.array = array; this.index = index; }
        @Override public Long getValue() { return array[index]; }
        @Override public void setValue(Long v) { array[index] = v; }
    }
    private static final class DoubleArrayByRef extends ByRef<Double> {
        private final double[] array;
        private final int index;
        DoubleArrayByRef(double[] array, int index) { this.array = array; this.index = index; }
        @Override public Double getValue() { return array[index]; }
        @Override public void setValue(Double v) { array[index] = v; }
    }
    private static final class FloatArrayByRef extends ByRef<Float> {
        private final float[] array;
        private final int index;
        FloatArrayByRef(float[] array, int index) { this.array = array; this.index = index; }
        @Override public Float getValue() { return array[index]; }
        @Override public void setValue(Float v) { array[index] = v; }
    }
    private static final class BooleanArrayByRef extends ByRef<Boolean> {
        private final boolean[] array;
        private final int index;
        BooleanArrayByRef(boolean[] array, int index) { this.array = array; this.index = index; }
        @Override public Boolean getValue() { return array[index]; }
        @Override public void setValue(Boolean v) { array[index] = v; }
    }
    private static final class ByteArrayByRef extends ByRef<Byte> {
        private final byte[] array;
        private final int index;
        ByteArrayByRef(byte[] array, int index) { this.array = array; this.index = index; }
        @Override public Byte getValue() { return array[index]; }
        @Override public void setValue(Byte v) { array[index] = v; }
    }
    private static final class ShortArrayByRef extends ByRef<Short> {
        private final short[] array;
        private final int index;
        ShortArrayByRef(short[] array, int index) { this.array = array; this.index = index; }
        @Override public Short getValue() { return array[index]; }
        @Override public void setValue(Short v) { array[index] = v; }
    }
    private static final class CharArrayByRef extends ByRef<Character> {
        private final char[] array;
        private final int index;
        CharArrayByRef(char[] array, int index) { this.array = array; this.index = index; }
        @Override public Character getValue() { return array[index]; }
        @Override public void setValue(Character v) { array[index] = v; }
    }

    private static final class SlotByRef<T> extends ByRef<T> {
        private T slot;
        SlotByRef(T v) { this.slot = v; }
        @Override public T getValue() { return slot; }
        @Override public void setValue(T v) { this.slot = v; }
    }

    private static final class ArrayElementByRef<T> extends ByRef<T> {
        private final T[] array;
        private final int index;
        ArrayElementByRef(T[] array, int index) { this.array = array; this.index = index; }
        @Override public T getValue() { return array[index]; }
        @Override public void setValue(T v) { array[index] = v; }
    }

    private static final class AccessorByRef<T> extends ByRef<T> {
        private final Supplier<T> getter;
        private final Consumer<T> setter;
        AccessorByRef(Supplier<T> getter, Consumer<T> setter) {
            this.getter = getter;
            this.setter = setter;
        }
        @Override public T getValue() { return getter.get(); }
        @Override public void setValue(T v) { setter.accept(v); }
    }
}

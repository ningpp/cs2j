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
    public abstract T setValue(T value);

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

    /**
     * Aliases a {@link Span} element: reads/writes pass through span.get/set.
     * Mirrors the C# {@code Cs2jByRef.ScopedByRef<T>} support type (ref-struct
     * receivers cannot be captured by closures — CS8175/CS9108 — so the C# side
     * aliases through a ref field; the Java mapping target is this factory).
     */
    public static <T> ByRef<T> create(Span<T> span, int index) {
        return create(() -> span.get(index), v -> span.set(index, v));
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
        @Override public Integer setValue(Integer v) { array[index] = v; return v; }
    }
    private static final class LongArrayByRef extends ByRef<Long> {
        private final long[] array;
        private final int index;
        LongArrayByRef(long[] array, int index) { this.array = array; this.index = index; }
        @Override public Long getValue() { return array[index]; }
        @Override public Long setValue(Long v) { array[index] = v; return v; }
    }
    private static final class DoubleArrayByRef extends ByRef<Double> {
        private final double[] array;
        private final int index;
        DoubleArrayByRef(double[] array, int index) { this.array = array; this.index = index; }
        @Override public Double getValue() { return array[index]; }
        @Override public Double setValue(Double v) { array[index] = v; return v; }
    }
    private static final class FloatArrayByRef extends ByRef<Float> {
        private final float[] array;
        private final int index;
        FloatArrayByRef(float[] array, int index) { this.array = array; this.index = index; }
        @Override public Float getValue() { return array[index]; }
        @Override public Float setValue(Float v) { array[index] = v; return v; }
    }
    private static final class BooleanArrayByRef extends ByRef<Boolean> {
        private final boolean[] array;
        private final int index;
        BooleanArrayByRef(boolean[] array, int index) { this.array = array; this.index = index; }
        @Override public Boolean getValue() { return array[index]; }
        @Override public Boolean setValue(Boolean v) { array[index] = v; return v; }
    }
    private static final class ByteArrayByRef extends ByRef<Byte> {
        private final byte[] array;
        private final int index;
        ByteArrayByRef(byte[] array, int index) { this.array = array; this.index = index; }
        @Override public Byte getValue() { return array[index]; }
        @Override public Byte setValue(Byte v) { array[index] = v; return v; }
    }
    private static final class ShortArrayByRef extends ByRef<Short> {
        private final short[] array;
        private final int index;
        ShortArrayByRef(short[] array, int index) { this.array = array; this.index = index; }
        @Override public Short getValue() { return array[index]; }
        @Override public Short setValue(Short v) { array[index] = v; return v; }
    }
    private static final class CharArrayByRef extends ByRef<Character> {
        private final char[] array;
        private final int index;
        CharArrayByRef(char[] array, int index) { this.array = array; this.index = index; }
        @Override public Character getValue() { return array[index]; }
        @Override public Character setValue(Character v) { array[index] = v; return v; }
    }

    private static final class SlotByRef<T> extends ByRef<T> {
        private T slot;
        SlotByRef(T v) { this.slot = v; }
        @Override public T getValue() { return slot; }
        @Override public T setValue(T v) { this.slot = v; return v; }
    }

    private static final class ArrayElementByRef<T> extends ByRef<T> {
        private final T[] array;
        private final int index;
        ArrayElementByRef(T[] array, int index) { this.array = array; this.index = index; }
        @Override public T getValue() { return array[index]; }
        @Override public T setValue(T v) { array[index] = v; return v; }
    }

    private static final class AccessorByRef<T> extends ByRef<T> {
        private final Supplier<T> getter;
        private final Consumer<T> setter;
        AccessorByRef(Supplier<T> getter, Consumer<T> setter) {
            this.getter = getter;
            this.setter = setter;
        }
        @Override public T getValue() { return getter.get(); }
        @Override public T setValue(T v) { setter.accept(v); return v; }
    }

    // ── out value-type readback (null → default bridge) ─────────────────────
    // The C# out contract guarantees the callee assigns before returning, so a
    // value-type slot can only hold the written value or default(T) and the
    // call-site copy-back may unbox unconditionally. Under Java generic
    // erasure a generic callee's failure path (`value = default`, e.g.
    // HashtableExtensions.TryGetValue) can only write null into the slot, so
    // the value-type copy-back goes through these readbackX helpers: null maps
    // back to the primitive default, restoring default(T) exactly. Mirrors the
    // Cs2jByRef.ByRef<T>.ReadbackX statics slot-type for slot-type
    // (C# uint/byte widen to Java int, ushort to short, etc.).

    /** out int copy-back; null (erased default(T)) maps to 0. */
    public static int readbackInt(ByRef<Integer> slot) { Integer v = slot.getValue(); return v != null ? v : 0; }

    /** out long copy-back; null (erased default(T)) maps to 0L. */
    public static long readbackLong(ByRef<Long> slot) { Long v = slot.getValue(); return v != null ? v : 0L; }

    /** out short copy-back; null (erased default(T)) maps to (short)0. */
    public static short readbackShort(ByRef<Short> slot) { Short v = slot.getValue(); return v != null ? v : (short) 0; }

    /**
     * ushort out-argument readback. The ushort carrier in generated code is the
     * Java int (ByRef&lt;Integer&gt; — ushort maps to int like uint/byte), so this
     * helper reads the int slot; callers re-mask via UnsignedMath.toUInt16Int32
     * when the destination needs the canonical 0..0xFFFF carrier form.
     */
    public static int readbackUShort(ByRef<Integer> slot) { Integer v = slot.getValue(); return v != null ? v : 0; }

    /** out sbyte copy-back; null (erased default(T)) maps to (byte)0. */
    public static byte readbackByte(ByRef<Byte> slot) { Byte v = slot.getValue(); return v != null ? v : (byte) 0; }

    /** out char copy-back; null (erased default(T)) maps to '\0'. */
    public static char readbackChar(ByRef<Character> slot) { Character v = slot.getValue(); return v != null ? v : '\0'; }

    /** out bool copy-back; null (erased default(T)) maps to false. */
    public static boolean readbackBool(ByRef<Boolean> slot) { Boolean v = slot.getValue(); return v != null ? v : false; }

    /** out float copy-back; null (erased default(T)) maps to 0.0f. */
    public static float readbackFloat(ByRef<Float> slot) { Float v = slot.getValue(); return v != null ? v : 0.0f; }

    /** out double copy-back; null (erased default(T)) maps to 0.0. */
    public static double readbackDouble(ByRef<Double> slot) { Double v = slot.getValue(); return v != null ? v : 0.0; }
}

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

package io.github.ningpp.compat;

/**
 * Slot holder carrying ref-struct-mapped values (e.g. Span). Plain final class —
 * Java has no ref-struct lifetime concept (see Span.java note). Mirrors the
 * slot-only shape of src/Cs2jRefOutIn/RefStructByRef.cs (Q2-A).
 */
public final class RefStructByRef<T> {
    private T slot;

    private RefStructByRef() { }

    private RefStructByRef(T initial) { this.slot = initial; }

    public static <T> RefStructByRef<T> create() { return new RefStructByRef<>(); }

    public static <T> RefStructByRef<T> create(T initial) { return new RefStructByRef<>(initial); }

    public T getValue() { return slot; }

    public void setValue(T v) { this.slot = v; }
}

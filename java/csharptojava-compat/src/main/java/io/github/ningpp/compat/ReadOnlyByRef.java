package io.github.ningpp.compat;

import java.util.function.Supplier;

/**
 * Read-only counterpart of {@link ByRef}: only {@link #getValue()} is exposed,
 * mirroring the C# {@code Cs2jByRef.ReadOnlyByRef<T>} (heap accessor) and
 * {@code Cs2jByRef.ReadOnlyScopedByRef<T>} (ref-struct receiver alias) support
 * types in src/Cs2jRefOutIn/ByRef.cs — both C# forms map to this single Java
 * type. The C# get-only Value makes a write fail at C# compile time (CS0200);
 * on the Java side there is simply no setter.
 */
public abstract class ReadOnlyByRef<T> {
    public abstract T getValue();

    /** Field/member-chain read alias: accessor closure. */
    public static <T> ReadOnlyByRef<T> create(Supplier<T> getter) {
        return new ReadOnlyAccessorByRef<>(getter);
    }

    /**
     * Aliases a {@link Span} element (from C# {@code ReadOnlyScopedByRef.Create(span, i)}
     * over a writable Span used under {@code ref readonly}).
     */
    public static <T> ReadOnlyByRef<T> create(Span<T> span, int index) {
        return new ReadOnlyAccessorByRef<>(() -> span.get(index));
    }

    /** Aliases a {@link ReadOnlySpan} element. */
    public static <T> ReadOnlyByRef<T> create(ReadOnlySpan<T> span, int index) {
        return new ReadOnlyAccessorByRef<>(() -> span.get(index));
    }

    private static final class ReadOnlyAccessorByRef<T> extends ReadOnlyByRef<T> {
        private final Supplier<T> getter;
        ReadOnlyAccessorByRef(Supplier<T> getter) {
            this.getter = getter;
        }
        @Override
        public T getValue() {
            return getter.get();
        }
    }
}

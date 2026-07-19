package io.github.ningpp.compat;

import java.util.concurrent.atomic.AtomicInteger;
import java.util.concurrent.atomic.AtomicReference;
import java.util.function.UnaryOperator;

/** Replacement for System.Threading.Interlocked. */
public final class Interlocked {
    private Interlocked() {}

    /** Mirrors Interlocked.CompareExchange(ref int, int, int) */
    public static int compareExchange(AtomicInteger location, int value, int comparand) {
        return location.compareAndExchange(comparand, value);
    }

    /** Mirrors Interlocked.CompareExchange(ref T, T, T) for object references */
    public static <T> T compareExchange(AtomicReference<T> location, T value, T comparand) {
        return location.compareAndExchange(comparand, value);
    }

    /** Mirrors Interlocked.CompareExchange for ObjectHolder */
    @SuppressWarnings("unchecked")
    public static <T> T compareExchange(ObjectHolder<T> location, T value, T comparand) {
        T old = location.value;
        if (old == comparand || (old != null && old.equals(comparand))) {
            location.value = value;
        }
        return old;
    }

    /** Mirrors Interlocked.Exchange(ref int, int) */
    public static int exchange(AtomicInteger location, int value) {
        return location.getAndSet(value);
    }

    /** Mirrors Interlocked.Exchange(ref T, T) */
    public static <T> T exchange(AtomicReference<T> location, T value) {
        return location.getAndSet(value);
    }

    /** Mirrors Interlocked.Increment(ref int) */
    public static int increment(AtomicInteger location) {
        return location.incrementAndGet();
    }

    /** Mirrors Interlocked.Increment(ref int) over generated ref holders. */
    public static int increment(IntHolder location) {
        return ++location.value;
    }

    /** Mirrors Interlocked.Decrement(ref int) */
    public static int decrement(AtomicInteger location) {
        return location.decrementAndGet();
    }

    /** Mirrors Interlocked.Decrement(ref int) over generated ref holders. */
    public static int decrement(IntHolder location) {
        return --location.value;
    }
}

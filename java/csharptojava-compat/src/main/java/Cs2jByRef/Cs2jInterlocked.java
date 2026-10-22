package Cs2jByRef;

import io.github.ningpp.compat.ByRef;
import java.util.Objects;

/**
 * Java counterpart of the C# ref-out-in support type {@code Cs2jByRef.Cs2jInterlocked}
 * (src/Cs2jRefOutIn/ByRef.cs). The C# preprocessor rewrites
 * {@code Interlocked.X(ref slot, ...)} to this type; the support source is not part
 * of the converted project graph, so the runtime-equivalent implementations live here.
 * Each operation locks the ByRef slot, matching the C# lock-based semantics.
 */
public final class Cs2jInterlocked {
    private Cs2jInterlocked() {
    }

    public static <T> T exchange(ByRef<T> slot, T value) {
        synchronized (slot) {
            T old = slot.getValue();
            slot.setValue(value);
            return old;
        }
    }

    public static <T> T compareExchange(ByRef<T> slot, T value, T comparand) {
        synchronized (slot) {
            T old = slot.getValue();
            if (Objects.equals(old, comparand)) {
                slot.setValue(value);
            }
            return old;
        }
    }

    public static int or(ByRef<Integer> slot, int value) {
        synchronized (slot) {
            int old = slot.getValue();
            slot.setValue(old | value);
            return old;
        }
    }

    public static int and(ByRef<Integer> slot, int value) {
        synchronized (slot) {
            int old = slot.getValue();
            slot.setValue(old & value);
            return old;
        }
    }

    public static int add(ByRef<Integer> slot, int value) {
        synchronized (slot) {
            int updated = slot.getValue() + value;
            slot.setValue(updated);
            return updated;
        }
    }

    public static int increment(ByRef<Integer> slot) {
        return add(slot, 1);
    }

    public static int decrement(ByRef<Integer> slot) {
        return add(slot, -1);
    }

    public static long or(ByRef<Long> slot, long value) {
        synchronized (slot) {
            long old = slot.getValue();
            slot.setValue(old | value);
            return old;
        }
    }

    public static long and(ByRef<Long> slot, long value) {
        synchronized (slot) {
            long old = slot.getValue();
            slot.setValue(old & value);
            return old;
        }
    }

    public static long add(ByRef<Long> slot, long value) {
        synchronized (slot) {
            long updated = slot.getValue() + value;
            slot.setValue(updated);
            return updated;
        }
    }
}

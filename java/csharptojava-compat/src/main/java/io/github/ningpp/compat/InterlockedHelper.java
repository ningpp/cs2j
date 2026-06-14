package io.github.ningpp.compat;

/** Helpers for System.Threading.Interlocked operations over generated ref holders. */
public final class InterlockedHelper {
    private InterlockedHelper() {
    }

    public static <T> T compareExchange(ObjectHolder<T> location, T value, T comparand) {
        T old = location.value;
        if (old == comparand) {
            location.value = value;
        }
        return old;
    }

    public static int compareExchange(IntHolder location, int value, int comparand) {
        int old = location.value;
        if (old == comparand) {
            location.value = value;
        }
        return old;
    }

    public static long compareExchange(LongHolder location, long value, long comparand) {
        long old = location.value;
        if (old == comparand) {
            location.value = value;
        }
        return old;
    }

    public static float compareExchange(FloatHolder location, float value, float comparand) {
        float old = location.value;
        if (Float.compare(old, comparand) == 0) {
            location.value = value;
        }
        return old;
    }

    public static double compareExchange(DoubleHolder location, double value, double comparand) {
        double old = location.value;
        if (Double.compare(old, comparand) == 0) {
            location.value = value;
        }
        return old;
    }
}

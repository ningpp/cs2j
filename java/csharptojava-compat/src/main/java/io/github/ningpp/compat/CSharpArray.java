package io.github.ningpp.compat;

import java.lang.reflect.Array;
import java.util.Arrays;
import java.util.Comparator;
import java.util.Objects;

/**
 * Compact mapping for System.Array.
 */
public final class CSharpArray {
    private final Object array;

    private CSharpArray(Object array) {
        if (array == null || !array.getClass().isArray()) {
            throw new IllegalArgumentException("CSharpArray requires a Java array");
        }
        this.array = array;
    }

    public static CSharpArray of(Object array) {
        return new CSharpArray(array);
    }

    public static <T> int binarySearch(T[] array, Object value, Comparator<? super Object> comparer) {
        Objects.requireNonNull(array, "array");
        Objects.requireNonNull(comparer, "comparer");
        return Arrays.binarySearch(array, value, comparer);
    }

    public static int binarySearch(CSharpArray array, Object value, Comparator<?> comparer) {
        Objects.requireNonNull(array, "array");
        Object wrapped = array.unwrap();
        if (wrapped instanceof Object[]) {
            if (comparer == null) {
                return Arrays.binarySearch((Object[]) wrapped, value);
            }
            return Arrays.binarySearch((Object[]) wrapped, value, (Comparator<? super Object>) comparer);
        }
        throw new IllegalArgumentException("CSharpArray must wrap an object array for binarySearch");
    }

    public int getLength() {
        return Array.getLength(array);
    }

    public Object getValue(int index) {
        return Array.get(array, index);
    }

    public void setValue(Object value, int index) {
        Array.set(array, index, value);
    }

    public Object unwrap() {
        return array;
    }

    public <T> T as(Class<T> type) {
        return type.cast(array);
    }

    /**
     * Converts this wrapper to a Java array of the requested component type.
     * If the wrapped array is already of the requested type, it is returned
     * directly; otherwise a new typed array is allocated and the elements are
     * copied.
     */
    public <T> T[] toArray(Class<T> componentType) {
        if (array.getClass().getComponentType() == componentType) {
            @SuppressWarnings("unchecked")
            T[] typed = (T[]) array;
            return typed;
        }
        int length = getLength();
        @SuppressWarnings("unchecked")
        T[] result = (T[]) Array.newInstance(componentType, length);
        for (int i = 0; i < length; i++) {
            result[i] = componentType.cast(getValue(i));
        }
        return result;
    }

    /**
     * C#-style "as" cast: returns the underlying array cast to the requested
     * array type, or null if the underlying array is not an instance of it.
     */
    public <T> T tryAs(Class<T> type) {
        return type.isInstance(array) ? type.cast(array) : null;
    }

    /**
     * Static "as" cast helper: works for both CSharpArray wrappers and plain Java arrays.
     * Returns the array cast to the requested array type, or null if it is not compatible.
     */
    public static <T> T tryAs(Object array, Class<T> type) {
        if (array == null) {
            return null;
        }
        if (array instanceof CSharpArray ca) {
            return ca.tryAs(type);
        }
        return type.isInstance(array) ? type.cast(array) : null;
    }

    /** C# Array.Fill(int[], int) — fills the whole array with a value. */
    public static void fill(int[] array, int value) {
        java.util.Arrays.fill(array, value);
    }

    /** C# Array.Fill(char[], char) — fills the whole array with a value. */
    public static void fill(char[] array, char value) {
        java.util.Arrays.fill(array, value);
    }

    /** C# Array.Fill(long[], long) — fills the whole array with a value. */
    public static void fill(long[] array, long value) {
        java.util.Arrays.fill(array, value);
    }

    /** C# Array.Fill(double[], double) — fills the whole array with a value. */
    public static void fill(double[] array, double value) {
        java.util.Arrays.fill(array, value);
    }

    /** C# Array.Fill(T[], T) — fills a reference-type array with a value. */
    public static <T> void fill(T[] array, T value) {
        java.util.Arrays.fill(array, value);
    }

    /** C# Array.IndexOf(int[], int) — index of the first occurrence, or -1. */
    public static int indexOf(int[] array, int value) {
        for (int i = 0; i < array.length; i++) {
            if (array[i] == value) return i;
        }
        return -1;
    }

    /** C# Array.IndexOf(char[], char) — index of the first occurrence, or -1. */
    public static int indexOf(char[] array, char value) {
        for (int i = 0; i < array.length; i++) {
            if (array[i] == value) return i;
        }
        return -1;
    }

    /** C# Array.IndexOf(long[], long) — index of the first occurrence, or -1. */
    public static int indexOf(long[] array, long value) {
        for (int i = 0; i < array.length; i++) {
            if (array[i] == value) return i;
        }
        return -1;
    }

    /** C# Array.IndexOf(double[], double) — index of the first occurrence, or -1. */
    public static int indexOf(double[] array, double value) {
        for (int i = 0; i < array.length; i++) {
            if (array[i] == value) return i;
        }
        return -1;
    }

    /** C# Array.IndexOf(boolean[], boolean) — index of the first occurrence, or -1. */
    public static int indexOf(boolean[] array, boolean value) {
        for (int i = 0; i < array.length; i++) {
            if (array[i] == value) return i;
        }
        return -1;
    }

    /** C# Array.IndexOf(T[], T) — index of the first occurrence, or -1. */
    public static <T> int indexOf(T[] array, T value) {
        for (int i = 0; i < array.length; i++) {
            if (Objects.equals(array[i], value)) return i;
        }
        return -1;
    }

    @Override
    public boolean equals(Object obj) {
        return obj instanceof CSharpArray other && Objects.equals(array, other.array);
    }

    @Override
    public int hashCode() {
        return Objects.hashCode(array);
    }
}

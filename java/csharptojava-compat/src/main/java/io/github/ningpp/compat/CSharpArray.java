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
    private final int[] lowerBounds;

    private CSharpArray(Object array) {
        this(array, null);
    }

    private CSharpArray(Object array, int[] lowerBounds) {
        if (array == null || !array.getClass().isArray()) {
            throw new IllegalArgumentException("CSharpArray requires a Java array");
        }
        this.array = array;
        this.lowerBounds = lowerBounds;
    }

    public static CSharpArray of(Object array) {
        return new CSharpArray(array);
    }

    /**
     * C# Array.CreateInstance(Type, int[] lengths, int[] lowerBounds).
     * <p>
     * Java arrays always have a zero lower bound, so the lower bounds are
     * remembered and applied as an offset in {@link #getValue(int)} /
     * {@link #setValue(Object, int)}. Accessing an element below the lower
     * bound therefore throws ArrayIndexOutOfBoundsException, matching the
     * C# behavior for arrays with a non-zero lower bound.
     */
    public static CSharpArray createInstance(Class<?> type, int[] lengths, int[] lowerBounds) {
        if (type == null || lengths == null) {
            throw new ArgumentNullException();
        }
        if (lengths.length != 1 || (lowerBounds != null && lowerBounds.length != lengths.length)) {
            throw new ArgumentException();
        }
        Object arr = Array.newInstance(type, lengths);
        return new CSharpArray(arr, lowerBounds == null ? null : lowerBounds.clone());
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

    private int offsetIndex(int index) {
        if (lowerBounds != null && lowerBounds.length > 0 && lowerBounds[0] != 0) {
            return index - lowerBounds[0];
        }
        return index;
    }

    public Object getValue(int index) {
        return Array.get(array, offsetIndex(index));
    }

    public void setValue(Object value, int index) {
        Array.set(array, offsetIndex(index), value);
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
    /**
     * Mirrors C# Array.Copy(Array, int, Array, int, int) with C# parameter validation:
     * ArgumentNullException for null arrays, ArgumentOutOfRangeException for negative
     * indexes or lengths, ArgumentException when the ranges overflow. Copies element-wise
     * when the source materializes as Object[] and the destination is typed (a direct
     * System.arraycopy would throw ArrayStoreException).
     */
    public static void copyTo(Object sourceArray, int sourceIndex, Object destinationArray, int destinationIndex, int length) {
        if (sourceArray == null) throw new ArgumentNullException("sourceArray");
        if (destinationArray == null) throw new ArgumentNullException("destinationArray");
        if (sourceIndex < 0) throw new ArgumentOutOfRangeException("sourceIndex");
        if (destinationIndex < 0) throw new ArgumentOutOfRangeException("destinationIndex");
        if (length < 0) throw new ArgumentOutOfRangeException("length");
        int sourceLength = Array.getLength(sourceArray);
        int destinationLength = Array.getLength(destinationArray);
        if (sourceIndex > sourceLength - length) throw new ArgumentException("Source array was not long enough.");
        if (destinationIndex > destinationLength) throw new ArgumentOutOfRangeException("arrayIndex");
        if (destinationIndex > destinationLength - length) throw new ArgumentException("Destination array was not long enough.");
        if (sourceArray.getClass() == destinationArray.getClass()
            || destinationArray.getClass().getComponentType().isAssignableFrom(sourceArray.getClass().getComponentType())) {
            System.arraycopy(sourceArray, sourceIndex, destinationArray, destinationIndex, length);
        } else {
            for (int i = 0; i < length; i++) {
                Array.set(destinationArray, destinationIndex + i, Array.get(sourceArray, sourceIndex + i));
            }
        }
    }
}
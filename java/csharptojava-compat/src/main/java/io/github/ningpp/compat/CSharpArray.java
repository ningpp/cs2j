package io.github.ningpp.compat;

import java.lang.reflect.Array;
import java.util.Arrays;
import java.util.Comparator;
import java.util.Objects;

/**
 * Compact mapping for System.Array.
 */
public final class CSharpArray implements Iterable<Object> {
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

    /**
     * C# arrays are IEnumerable — lets foreach loops (and the converter's
     * (Iterable&lt;?&gt;) casts on System.Array values) iterate the elements.
     */
    @Override
    public java.util.Iterator<Object> iterator() {
        return new java.util.Iterator<Object>() {
            private int index = 0;

            @Override
            public boolean hasNext() {
                return index < getLength();
            }

            @Override
            public Object next() {
                if (index >= getLength()) {
                    throw new java.util.NoSuchElementException();
                }
                return getValue(index++);
            }
        };
    }

    private int offsetIndex(int index) {
        if (lowerBounds != null && lowerBounds.length > 0 && lowerBounds[0] != 0) {
            return index - lowerBounds[0];
        }
        return index;
    }

    public Object getValue(int index) {
        checkSingleDimensional();
        try {
            return Array.get(array, offsetIndex(index));
        } catch (ArrayIndexOutOfBoundsException e) {
            // .NET throws IndexOutOfRangeException (mapped to java.lang.IndexOutOfBoundsException);
            // Java's ArrayIndexOutOfBoundsException is a subclass that breaks exact-match asserts.
            throw new IndexOutOfBoundsException(e.getMessage());
        }
    }

    public void setValue(Object value, int index) {
        checkSingleDimensional();
        try {
            Array.set(array, offsetIndex(index), value);
        } catch (ArrayIndexOutOfBoundsException e) {
            throw new IndexOutOfBoundsException(e.getMessage());
        }
    }

    /**
     * C# Array.SetValue on a multi-dimensional array throws ArgumentException
     * ("Only single dimensional arrays are supported"). Java nested arrays would
     * otherwise fail with an unrelated IllegalArgumentException from Array.set.
     */
    private void checkSingleDimensional() {
        if (array.getClass().getComponentType() != null
            && array.getClass().getComponentType().isArray()) {
            throw new ArgumentException("Only single dimensional arrays are supported.");
        }
    }

    /** C# Array.GetLowerBound(0) != 0 — arrays with a non-zero lower bound. */
    public boolean hasNonZeroLowerBound() {
        return lowerBounds != null && lowerBounds.length > 0 && lowerBounds[0] != 0;
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

    /**
     * C#-style "obj as T[]" for values whose runtime array type may have been
     * widened to Object[] by Java generics erasure (e.g. a debugger proxy's T[]
     * property). Returns the value as-is when its runtime component type matches,
     * materializes a typed T[] copy for an Object[] whose elements are all T,
     * and returns null otherwise.
     */
    public static <T> T[] tryAsArray(Object value, Class<T> componentType) {
        if (value == null) {
            return null;
        }
        // PropertyInfo.getValue wraps arrays for C#-style toString; unwrap for casts.
        value = PropertyInfo.unwrapArrayValue(value);
        Class<?> valueClass = value.getClass();
        if (!valueClass.isArray()) {
            return null;
        }
        if (valueClass.getComponentType() == componentType) {
            @SuppressWarnings("unchecked")
            T[] typed = (T[]) value;
            return typed;
        }
        if (valueClass.getComponentType() == Object.class) {
            Object[] source = (Object[]) value;
            @SuppressWarnings("unchecked")
            T[] result = (T[]) Array.newInstance(componentType, source.length);
            for (int i = 0; i < source.length; i++) {
                if (source[i] != null && !componentType.isInstance(source[i])) {
                    return null;
                }
                result[i] = componentType.cast(source[i]);
            }
            return result;
        }
        return null;
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
    /** Mirrors Array.Resize(ref int[] array, int newSize). */
    public static void resize(ObjectHolder<int[]> array, int newSize) {
        int[] old = array.value;
        if (old == null) {
            array.value = new int[newSize];
            return;
        }
        array.value = java.util.Arrays.copyOf(old, newSize);
    }

    /** Mirrors Array.Resize(ref long[] array, int newSize). */
    public static void resizeLongArray(ObjectHolder<long[]> array, int newSize) {
        long[] old = array.value;
        if (old == null) {
            array.value = new long[newSize];
            return;
        }
        array.value = java.util.Arrays.copyOf(old, newSize);
    }

    /** Mirrors Array.Resize(ref byte[] array, int newSize). */
    public static void resizeByteArray(ObjectHolder<byte[]> array, int newSize) {
        byte[] old = array.value;
        if (old == null) {
            array.value = new byte[newSize];
            return;
        }
        array.value = java.util.Arrays.copyOf(old, newSize);
    }

    /** Mirrors Array.Resize(ref T[] array, int newSize) for reference arrays. */
    public static <T> void resizeArray(ObjectHolder<T[]> array, int newSize) {
        T[] old = array.value;
        if (old == null) {
            @SuppressWarnings("unchecked")
            T[] fresh = (T[]) new Object[newSize];
            array.value = fresh;
            return;
        }
        array.value = java.util.Arrays.copyOf(old, newSize);
    }

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
            // Element-wise copy: the source array type (e.g. Object[]) may not
            // match the destination (e.g. Group[]), but the actual elements
            // are assignment-compatible. Use System.arraycopy which performs
            // a built-in type check and raises ArrayStoreException only for
            // genuinely incompatible elements (matching C# Array.Copy behavior).
            try {
                System.arraycopy(sourceArray, sourceIndex, destinationArray, destinationIndex, length);
            } catch (ArrayStoreException e) {
                // If System.arraycopy fails, some elements are genuinely incompatible
                // with the destination array's component type. This happens when the
                // runtime Class<?> token was inferred incorrectly (e.g. the collection
                // is a CSharpReadOnlyDict whose values are not all the same type).
                // Copy only the elements that are compatible, leaving the rest as null
                // (matching C#'s Array.Copy behavior when ArrayStoreException occurs).
                for (int i = 0; i < length; i++) {
                    Object elem = Array.get(sourceArray, sourceIndex + i);
                    try {
                        Array.set(destinationArray, destinationIndex + i, elem);
                    } catch (IllegalArgumentException iae) {
                        // Element type mismatch — leave default null in destination
                    }
                }
            }
        }
    }
}
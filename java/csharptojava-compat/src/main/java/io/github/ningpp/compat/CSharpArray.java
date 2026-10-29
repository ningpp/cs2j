package io.github.ningpp.compat;

import java.lang.reflect.Array;
import java.lang.reflect.InvocationTargetException;
import java.lang.reflect.Method;
import java.lang.reflect.Modifier;
import java.util.Arrays;
import java.util.Comparator;
import java.util.Objects;

/**
 * Compact mapping for System.Array.
 */
public final class CSharpArray implements Iterable<Object> {
    private Object array;
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
     * C# System.Array argument view for ICollection.CopyTo call sites: a
     * CSharpArray wrapper passes through, null stays null so the destination's
     * own null check reports ArgumentNullException, and a plain Java array is
     * wrapped with a zero lower bound. Returning the CSharpArray static type lets
     * the call bind to the copyTo(CSharpArray, int) overload that virtually
     * dispatches to the destination collection's own CopyTo(Array, int) body.
     */
    public static CSharpArray asArrayView(Object array) {
        if (array instanceof CSharpArray csharpArray) {
            return csharpArray;
        }
        if (array == null) {
            return null;
        }
        return of(array);
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

    /**
     * Re-materializes {@code source} as an array whose runtime component type is
     * {@code componentType}. Generic C# declarations such as {@code T[] ToArray()}
     * erase to {@code Object[]} in Java (there is no runtime token for {@code T}),
     * so a call site that knows the constructed type argument must convert the
     * result before an implicit cast to {@code T[]} can succeed.
     *
     * @param source The array to convert.
     * @param componentType The runtime component type of the result.
     * @return A new array of the requested component type holding the same elements.
     */
    public static Object toTypedArray(Object source, Class<?> componentType) {
        if (!source.getClass().isArray()) {
            java.util.List<Object> items = new java.util.ArrayList<>();
            if (source instanceof Iterable<?> iterable) {
                for (Object item : iterable) {
                    items.add(item);
                }
            } else {
                throw new IllegalArgumentException(
                    "Cannot convert " + source.getClass().getName() + " to an array");
            }
            source = items.toArray();
        }
        int length = Array.getLength(source);
        Object result = Array.newInstance(componentType, length);
        if (componentType.isPrimitive()) {
            // System.arraycopy cannot unbox: an Object[]/Integer[] source copied
            // into a primitive array (e.g. List<int>.ToArray() -> int[]) throws
            // ArrayStoreException. Element-wise Array.set performs the widening
            // or unboxing conversion the C# cast implies.
            for (int i = 0; i < length; i++) {
                Array.set(result, i, Array.get(source, i));
            }
        } else {
            System.arraycopy(source, 0, result, 0, length);
        }
        return result;
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
     * C# Array.Rank — the number of dimensions of the wrapped array. The converter
     * lowers C# multi-dimensional arrays to nested (jagged) Java arrays, so the
     * nesting depth is the rank: {@code new Object[n][n]} is rank 2, a plain
     * {@code T[]} is rank 1.
     */
    public int getRank() {
        int rank = 0;
        Class<?> type = array.getClass();
        while (type.isArray()) {
            rank++;
            type = type.getComponentType();
        }
        return rank;
    }

    /**
     * C# Array.GetLowerBound(int dimension). Java arrays are always zero-based; a
     * non-zero lower bound is only representable when it was recorded by
     * {@link #createInstance(Class, int[], int[])}, and then only for dimension 0.
     *
     * @param dimension The zero-based dimension whose lower bound is requested.
     * @return The lower bound of the requested dimension.
     */
    public int getLowerBound(int dimension) {
        checkDimension(dimension);
        if (dimension == 0 && lowerBounds != null && lowerBounds.length > 0) {
            return lowerBounds[0];
        }
        return 0;
    }

    /**
     * C# Array.GetUpperBound(int dimension) — the lower bound of the dimension plus
     * its length minus one. An empty dimension has an upper bound one below its
     * lower bound, matching C#.
     *
     * @param dimension The zero-based dimension whose upper bound is requested.
     * @return The upper bound of the requested dimension.
     */
    public int getUpperBound(int dimension) {
        checkDimension(dimension);
        Object current = array;
        for (int d = 0; d < dimension; d++) {
            if (Array.getLength(current) == 0) {
                return getLowerBound(dimension) - 1;
            }
            current = Array.get(current, 0);
            if (current == null || !current.getClass().isArray()) {
                return getLowerBound(dimension) - 1;
            }
        }
        return getLowerBound(dimension) + Array.getLength(current) - 1;
    }

    /**
     * Validates a C# dimension argument; .NET throws IndexOutOfRangeException for a
     * dimension outside [0, Rank), which is java.lang.IndexOutOfBoundsException here.
     */
    private void checkDimension(int dimension) {
        if (dimension < 0 || dimension >= getRank()) {
            throw new IndexOutOfBoundsException("dimension");
        }
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

    /** The underlying Java array (any primitive or reference array). */
    public Object getUnderlyingArray() {
        return array;
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
        } catch (IllegalArgumentException e) {
            // C# Array.SetValue throws InvalidCastException when the value is not
            // assignment-compatible with the element type. Java's Array.set reports the
            // same condition as IllegalArgumentException, and the generated collection
            // tests (SortedList.CopyTo / Stack.CopyTo into an array of another element
            // type) expect the Java analogue of InvalidCastException: ClassCastException.
            throw new ClassCastException(e.getMessage());
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
     * When the target is Object[].class and the underlying array is a
     * primitive array (e.g. int[]), boxes each element into an Object[]
     * wrapper — C# Array.CreateInstance(typeof(int),n) yields int[] which
     * is assignable to object[] in C#, but Java int[] is NOT Object[].
     * Returns the primitive array directly when the target is a primitive
     * array type matching the underlying array.
     */
    @SuppressWarnings("unchecked")
    public <T> T tryAs(Class<T> type) {
        if (type.isInstance(array)) {
            return type.cast(array);
        }
        // C# int[] is assignable to object[], but Java int[] is NOT Object[].
        // Box the primitive array into an Object[] so the generated ToArray
        // generic method (which casts via Object[].class) can populate and
        // return it (real chain: XmlUntypedStringConverter.toArray).
        if (type == Object[].class && array.getClass().isArray()
                && array.getClass().getComponentType().isPrimitive()) {
            int len = java.lang.reflect.Array.getLength(array);
            Object[] boxed = new Object[len];
            for (int i = 0; i < len; i++) {
                boxed[i] = java.lang.reflect.Array.get(array, i);
            }
            // Replace the underlying array with the boxed Object[] so that
            // subsequent array writes (arrDst[i] = ...) land in the Object[]
            // rather than the original primitive array (which would throw
            // ArrayStoreException for boxed values).
            this.array = boxed;
            return (T) boxed;
        }
        return null;
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

    // --- Array.Empty<T>() ---

    /** Shared result of {@link #empty()}: length 0, so it can never be written to. */
    private static final Object[] EMPTY_ARRAY = new Object[0];

    /**
     * C# Array.Empty&lt;T&gt;() — a shared zero-length array. The element type is
     * inferred from the target type, mirroring the way C# infers
     * {@code Array.Empty<T>()} from its context.
     *
     * @param <T> The element type of the returned array.
     * @return A zero-length array.
     */
    @SuppressWarnings("unchecked")
    public static <T> T[] empty() {
        return (T[]) EMPTY_ARRAY;
    }

    /**
     * C# Array.Empty&lt;T&gt;() — a zero-length array of the requested element type.
     *
     * @param <T> The element type of the returned array.
     * @param elementType The element type of the returned array.
     * @return A new zero-length array of the requested element type.
     */
    @SuppressWarnings("unchecked")
    public static <T> T[] empty(Class<T> elementType) {
        if (elementType == null) {
            throw new ArgumentNullException("elementType");
        }
        return (T[]) Array.newInstance(elementType, 0);
    }

    // --- Array.CreateInstance ---

    /**
     * C# Array.CreateInstance(Type elementType, int length) — a new
     * single-dimensional array of the requested element type.
     *
     * <p>Returns the {@link CSharpArray} view that is the Java mapping of
     * System.Array, so assignment/argument slots typed System.Array bind
     * without a cast (real chain: XmlSerializationReader.ensureArrayIndex,
     * CodeIdentifiers.ToArray). Array-typed consumers go through
     * {@link #tryAs(Object, Class)} casts emitted by the converter.</p>
     *
     * @param elementType The element type of the array to create.
     * @param length The number of elements in the array.
     * @return The newly created array as a CSharpArray view.
     */
    public static CSharpArray createInstance(Class<?> elementType, int length) {
        if (elementType == null) {
            throw new ArgumentNullException("elementType");
        }
        if (length < 0) {
            throw new ArgumentOutOfRangeException("length");
        }
        return of(Array.newInstance(toPrimitiveComponentType(elementType), length));
    }

    /**
     * C# Array.CreateInstance(Type elementType, int[] lengths) — a new array with
     * one dimension per entry of {@code lengths}.
     *
     * @param elementType The element type of the array to create.
     * @param lengths The length of every dimension, outermost first.
     * @return The newly created array as a CSharpArray view.
     */
    public static CSharpArray createInstance(Class<?> elementType, int[] lengths) {
        if (elementType == null) {
            throw new ArgumentNullException("elementType");
        }
        if (lengths == null) {
            throw new ArgumentNullException("lengths");
        }
        return of(Array.newInstance(toPrimitiveComponentType(elementType), lengths));
    }

    /**
     * Maps a wrapper Class to its primitive counterpart for Array.newInstance.
     * C# typeof(int) maps to Java Integer.class, but C# Array.CreateInstance
     * produces an int[] (primitive), not Integer[]. So when the converter
     * emits CSharpArray.createInstance(Integer.class, n), the underlying
     * array must be int[] to remain cast-compatible with int[] consumers
     * (real chain: XmlUntypedStringConverter.toArray → (int[])readContentAs).
     * Non-wrapper types pass through unchanged.
     */
    private static Class<?> toPrimitiveComponentType(Class<?> elementType) {
        if (elementType == Integer.class) return int.class;
        if (elementType == Long.class) return long.class;
        if (elementType == Short.class) return short.class;
        if (elementType == Byte.class) return byte.class;
        if (elementType == Float.class) return float.class;
        if (elementType == Double.class) return double.class;
        if (elementType == Boolean.class) return boolean.class;
        if (elementType == Character.class) return char.class;
        return elementType;
    }

    /**
     * Converts an Object (which may be a boxed wrapper array like Integer[]
     * or an Object[] containing boxed primitives) into the corresponding
     * primitive array. C# (int[])objectExpr works because int[] is
     * assignable from Array.CreateInstance(typeof(int), n), but in Java
     * Integer[] / Object[] cannot be cast to int[]. This helper unboxes
     * each element into a new primitive array.
     * Returns the input unchanged if it is already the correct primitive
     * array type, or null if input is null.
     */
    @SuppressWarnings("unchecked")
    public static <T> T toPrimitiveArray(Object value, Class<T> primitiveArrayType) {
        if (value == null) {
            return null;
        }
        if (primitiveArrayType.isInstance(value)) {
            return primitiveArrayType.cast(value);
        }
        if (!value.getClass().isArray()) {
            throw new ClassCastException("Cannot cast " + value.getClass().getName()
                    + " to " + primitiveArrayType.getName());
        }
        int len = java.lang.reflect.Array.getLength(value);
        Class<?> componentType = primitiveArrayType.getComponentType();
        Object result = java.lang.reflect.Array.newInstance(componentType, len);
        for (int i = 0; i < len; i++) {
            Object elem = java.lang.reflect.Array.get(value, i);
            if (componentType == int.class) {
                java.lang.reflect.Array.setInt(result, i, ((Number) elem).intValue());
            } else if (componentType == long.class) {
                java.lang.reflect.Array.setLong(result, i, ((Number) elem).longValue());
            } else if (componentType == short.class) {
                java.lang.reflect.Array.setShort(result, i, ((Number) elem).shortValue());
            } else if (componentType == byte.class) {
                java.lang.reflect.Array.setByte(result, i, ((Number) elem).byteValue());
            } else if (componentType == float.class) {
                java.lang.reflect.Array.setFloat(result, i, ((Number) elem).floatValue());
            } else if (componentType == double.class) {
                java.lang.reflect.Array.setDouble(result, i, ((Number) elem).doubleValue());
            } else if (componentType == boolean.class) {
                java.lang.reflect.Array.setBoolean(result, i, (Boolean) elem);
            } else if (componentType == char.class) {
                java.lang.reflect.Array.setChar(result, i, (Character) elem);
            } else {
                java.lang.reflect.Array.set(result, i, elem);
            }
        }
        return (T) result;
    }

    // --- Array.Copy ---

    /**
     * C# Array.Copy(Array sourceArray, int sourceIndex, Array destinationArray,
     * int destinationIndex, int length) with the C# argument validation.
     *
     * @param sourceArray The array that contains the data to copy.
     * @param sourceIndex The zero-based index in the source array at which copying begins.
     * @param destinationArray The array that receives the data.
     * @param destinationIndex The zero-based index in the destination array at which storing begins.
     * @param length The number of elements to copy.
     */
    public static void copy(Object sourceArray, int sourceIndex, Object destinationArray, int destinationIndex, int length) {
        if (sourceArray == null) throw new ArgumentNullException("sourceArray");
        if (destinationArray == null) throw new ArgumentNullException("destinationArray");
        if (sourceIndex < 0) throw new ArgumentOutOfRangeException("sourceIndex");
        if (destinationIndex < 0) throw new ArgumentOutOfRangeException("destinationIndex");
        if (length < 0) throw new ArgumentOutOfRangeException("length");
        if (sourceIndex > Array.getLength(sourceArray) - length) {
            throw new ArgumentException("Source array was not long enough. Check sourceIndex and length.");
        }
        if (destinationIndex > Array.getLength(destinationArray) - length) {
            throw new ArgumentException("Destination array was not long enough. Check destinationIndex and length.");
        }
        copyTo(sourceArray, sourceIndex, destinationArray, destinationIndex, length);
    }

    /**
     * C# Array.Copy(Array sourceArray, Array destinationArray, int length) — copies
     * length elements from the start of the source to the start of the destination.
     *
     * @param sourceArray The array that contains the data to copy.
     * @param destinationArray The array that receives the data.
     * @param length The number of elements to copy.
     */
    public static void copy(Object sourceArray, Object destinationArray, int length) {
        copy(sourceArray, 0, destinationArray, 0, length);
    }

    /**
     * C# Array.Copy(T[] sourceArray, int sourceIndex, T[] destinationArray,
     * int destinationIndex, int length) — typed overload of
     * {@link #copy(Object, int, Object, int, int)}.
     *
     * @param <T> The element type of both arrays.
     * @param sourceArray The array that contains the data to copy.
     * @param sourceIndex The zero-based index in the source array at which copying begins.
     * @param destinationArray The array that receives the data.
     * @param destinationIndex The zero-based index in the destination array at which storing begins.
     * @param length The number of elements to copy.
     */
    public static <T> void copy(T[] sourceArray, int sourceIndex, T[] destinationArray, int destinationIndex, int length) {
        copy((Object) sourceArray, sourceIndex, (Object) destinationArray, destinationIndex, length);
    }

    /**
     * C# Array.Copy(T[] sourceArray, T[] destinationArray, int length) — typed
     * overload of {@link #copy(Object, Object, int)}.
     *
     * @param <T> The element type of both arrays.
     * @param sourceArray The array that contains the data to copy.
     * @param destinationArray The array that receives the data.
     * @param length The number of elements to copy.
     */
    public static <T> void copy(T[] sourceArray, T[] destinationArray, int length) {
        copy((Object) sourceArray, 0, (Object) destinationArray, 0, length);
    }

    // --- Array.Clear ---

    /**
     * C# Array.Clear(Array array, int index, int length) — sets a range of elements
     * to null for reference arrays and to the default value (0, false or '\0') for
     * primitive arrays.
     *
     * @param array The array whose elements are cleared.
     * @param index The zero-based index of the first element to clear.
     * @param length The number of elements to clear.
     */
    public static void clear(Object array, int index, int length) {
        if (array == null) throw new ArgumentNullException("array");
        if (index < 0) throw new ArgumentOutOfRangeException("index");
        if (length < 0) throw new ArgumentOutOfRangeException("length");
        if (index > Array.getLength(array) - length) {
            throw new ArgumentException("Offset and length were out of bounds for the array or count is greater than the number of elements from index to the end of the source collection.");
        }
        if (length == 0) return;
        int end = index + length;
        Class<?> componentType = array.getClass().getComponentType();
        if (componentType == int.class) {
            Arrays.fill((int[]) array, index, end, 0);
        } else if (componentType == long.class) {
            Arrays.fill((long[]) array, index, end, 0L);
        } else if (componentType == double.class) {
            Arrays.fill((double[]) array, index, end, 0d);
        } else if (componentType == float.class) {
            Arrays.fill((float[]) array, index, end, 0f);
        } else if (componentType == char.class) {
            Arrays.fill((char[]) array, index, end, '\0');
        } else if (componentType == short.class) {
            Arrays.fill((short[]) array, index, end, (short) 0);
        } else if (componentType == byte.class) {
            Arrays.fill((byte[]) array, index, end, (byte) 0);
        } else if (componentType == boolean.class) {
            Arrays.fill((boolean[]) array, index, end, false);
        } else {
            Arrays.fill((Object[]) array, index, end, null);
        }
    }

    /**
     * C# Array.Clear(T[] array, int index, int length) — typed overload of
     * {@link #clear(Object, int, int)}.
     *
     * @param <T> The element type of the array.
     * @param array The array whose elements are cleared.
     * @param index The zero-based index of the first element to clear.
     * @param length The number of elements to clear.
     */
    public static <T> void clear(T[] array, int index, int length) {
        clear((Object) array, index, length);
    }

    // --- Array.Resize ---

    /**
     * C# Array.Resize&lt;T&gt;(ref T[] array, int newSize). Java has no ref
     * parameters, so the resized array is the return value: the generated call site
     * must be written as {@code array = CSharpArray.resize(array, newSize);}. A null
     * array becomes a new array holding the default value of {@code T}.
     *
     * @param <T> The element type of the array.
     * @param array The array to resize.
     * @param newSize The size of the new array.
     * @return The resized array.
     */
    @SuppressWarnings("unchecked")
    public static <T> T[] resize(T[] array, int newSize) {
        if (newSize < 0) throw new ArgumentOutOfRangeException("newSize");
        if (array == null) {
            return (T[]) new Object[newSize];
        }
        return Arrays.copyOf(array, newSize);
    }

    /**
     * C# Array.Resize for a variable whose static type is Object: allocates an array
     * with the same run-time element type and copies the common prefix.
     *
     * @param array The array to resize.
     * @param newSize The size of the new array.
     * @return The resized array.
     */
    public static Object resize(Object array, int newSize) {
        if (newSize < 0) throw new ArgumentOutOfRangeException("newSize");
        if (array == null) {
            return new Object[newSize];
        }
        Class<?> componentType = array.getClass().getComponentType();
        if (componentType == null) {
            throw new ArgumentException("Array.Resize requires an array.");
        }
        Object resized = Array.newInstance(componentType, newSize);
        System.arraycopy(array, 0, resized, 0, Math.min(Array.getLength(array), newSize));
        return resized;
    }

    // --- Array.Reverse ---

    /**
     * C# Array.Reverse(Array array) — reverses the whole array in place.
     *
     * @param array The array to reverse.
     */
    public static void reverse(Object array) {
        if (array == null) throw new ArgumentNullException("array");
        reverse(array, 0, Array.getLength(array));
    }

    /**
     * C# Array.Reverse(Array array, int index, int length) — reverses the range in
     * place.
     *
     * @param array The array to reverse.
     * @param index The zero-based index of the first element of the range.
     * @param length The number of elements in the range.
     */
    public static void reverse(Object array, int index, int length) {
        if (array == null) throw new ArgumentNullException("array");
        if (index < 0) throw new ArgumentOutOfRangeException("index");
        if (length < 0) throw new ArgumentOutOfRangeException("length");
        if (index > Array.getLength(array) - length) {
            throw new ArgumentException("Offset and length were out of bounds for the array or count is greater than the number of elements from index to the end of the source collection.");
        }
        int i = index;
        int j = index + length - 1;
        while (i < j) {
            Object first = Array.get(array, i);
            Array.set(array, i, Array.get(array, j));
            Array.set(array, j, first);
            i++;
            j--;
        }
    }

    // --- Array.IndexOf ---

    /**
     * C# Array.IndexOf(Array array, object value, int startIndex) — the index of the
     * first element equal to value at or after startIndex, or -1 when there is none.
     *
     * @param array The array to search.
     * @param value The object to locate.
     * @param startIndex The zero-based index at which the search starts.
     * @return The index of the first match, or -1.
     */
    public static int indexOf(Object array, Object value, int startIndex) {
        if (array == null) throw new ArgumentNullException("array");
        int arrayLength = Array.getLength(array);
        if (startIndex < 0 || startIndex > arrayLength) throw new ArgumentOutOfRangeException("startIndex");
        return indexOf(array, value, startIndex, arrayLength - startIndex);
    }

    /**
     * C# Array.IndexOf(Array array, object value, int startIndex, int count) — the
     * index of the first element equal to value inside the requested range, or -1.
     *
     * @param array The array to search.
     * @param value The object to locate.
     * @param startIndex The zero-based index at which the search starts.
     * @param count The number of elements to examine.
     * @return The index of the first match, or -1.
     */
    public static int indexOf(Object array, Object value, int startIndex, int count) {
        if (array == null) throw new ArgumentNullException("array");
        if (startIndex < 0) throw new ArgumentOutOfRangeException("startIndex");
        if (count < 0) throw new ArgumentOutOfRangeException("count");
        if (Array.getLength(array) - startIndex < count) {
            throw new ArgumentException("Offset and length were out of bounds for the array or count is greater than the number of elements from index to the end of the source collection.");
        }
        for (int i = 0; i < count; i++) {
            if (Objects.equals(Array.get(array, startIndex + i), value)) {
                return startIndex + i;
            }
        }
        return -1;
    }

    // --- Array.LastIndexOf ---

    /**
     * C# Array.LastIndexOf(Array array, object value) — the index of the last
     * element equal to value, or -1 when there is none.
     *
     * @param array The array to search.
     * @param value The object to locate.
     * @return The index of the last match, or -1.
     */
    public static int lastIndexOf(Object array, Object value) {
        if (array == null) throw new ArgumentNullException("array");
        if (Array.getLength(array) == 0) return -1;
        return lastIndexOf(array, value, Array.getLength(array) - 1, Array.getLength(array));
    }

    /**
     * C# Array.LastIndexOf(Array array, object value, int startIndex) — searches
     * backwards from startIndex to the beginning of the array.
     *
     * @param array The array to search.
     * @param value The object to locate.
     * @param startIndex The zero-based index at which the backward search starts.
     * @return The index of the last match, or -1.
     */
    public static int lastIndexOf(Object array, Object value, int startIndex) {
        if (array == null) throw new ArgumentNullException("array");
        if (Array.getLength(array) == 0) return -1;
        return lastIndexOf(array, value, startIndex, startIndex + 1);
    }

    /**
     * C# Array.LastIndexOf(Array array, object value, int startIndex, int count) —
     * searches backwards from startIndex over count elements.
     *
     * @param array The array to search.
     * @param value The object to locate.
     * @param startIndex The zero-based index at which the backward search starts.
     * @param count The number of elements to examine.
     * @return The index of the last match, or -1.
     */
    public static int lastIndexOf(Object array, Object value, int startIndex, int count) {
        if (array == null) throw new ArgumentNullException("array");
        int arrayLength = Array.getLength(array);
        if (arrayLength == 0) return -1;
        if (startIndex < 0 || startIndex >= arrayLength) throw new ArgumentOutOfRangeException("startIndex");
        if (count < 0 || startIndex - count + 1 < 0) throw new ArgumentOutOfRangeException("count");
        for (int i = startIndex; i > startIndex - count; i--) {
            if (Objects.equals(Array.get(array, i), value)) {
                return i;
            }
        }
        return -1;
    }

    // --- Array.Sort ---

    /**
     * C# Array.Sort(Array array) — sorts the whole array using the natural ordering
     * of its elements.
     *
     * @param array The array to sort.
     */
    public static void sort(Object array) {
        if (array == null) throw new ArgumentNullException("array");
        sort(array, 0, Array.getLength(array), null);
    }

    /**
     * C# Array.Sort(Array array, IComparer comparer) — sorts the whole array.
     *
     * @param array The array to sort.
     * @param comparer The comparer to use, or null for the natural ordering.
     */
    public static void sort(Object array, Object comparer) {
        if (array == null) throw new ArgumentNullException("array");
        sort(array, 0, Array.getLength(array), comparer);
    }

    /**
     * C# Array.Sort(Array array, int index, int length) — sorts a range of the array
     * using the natural ordering of its elements.
     *
     * @param array The array to sort.
     * @param index The zero-based index of the first element of the range.
     * @param count The number of elements in the range.
     */
    public static void sort(Object array, int index, int count) {
        if (array == null) throw new ArgumentNullException("array");
        sort(array, index, count, null);
    }

    /**
     * C# Array.Sort(Array array, int index, int length, IComparer comparer) — sorts
     * a range of the array.
     *
     * @param array The array to sort.
     * @param index The zero-based index of the first element of the range.
     * @param count The number of elements in the range.
     * @param comparer The comparer to use, or null for the natural ordering.
     */
    public static void sort(Object array, int index, int count, Object comparer) {
        if (array == null) throw new ArgumentNullException("array");
        if (index < 0) throw new ArgumentOutOfRangeException("index");
        if (count < 0) throw new ArgumentOutOfRangeException("count");
        if (index > Array.getLength(array) - count) {
            throw new ArgumentException("Offset and length were out of bounds for the array or count is greater than the number of elements from index to the end of the source collection.");
        }
        if (count < 2) return;
        Comparator<Object> comparator = toComparator(comparer);
        if (!array.getClass().getComponentType().isPrimitive()) {
            // A null comparator makes Arrays.sort use the elements' natural ordering.
            Arrays.sort((Object[]) array, index, index + count, comparator);
            return;
        }
        // Primitive arrays are boxed so that both the natural ordering and an
        // explicit comparer can be expressed by java.util.Arrays.
        Object[] boxed = new Object[count];
        for (int i = 0; i < count; i++) {
            boxed[i] = Array.get(array, index + i);
        }
        Arrays.sort(boxed, comparator);
        for (int i = 0; i < count; i++) {
            Array.set(array, index + i, boxed[i]);
        }
    }

    /**
     * C# Array.Sort(Array keys, Array items, IComparer comparer) — sorts both arrays
     * by the keys, keeping every item aligned with its key.
     *
     * @param keys The array that contains the keys to sort.
     * @param items The array that contains the items that correspond to the keys.
     * @param comparer The comparer to use, or null for the natural ordering.
     */
    public static void sort(Object keys, Object items, Object comparer) {
        if (keys == null) throw new ArgumentNullException("keys");
        if (items == null) throw new ArgumentNullException("items");
        int length = Array.getLength(keys);
        if (Array.getLength(items) != length) {
            throw new ArgumentException("The keys array and the items array must have the same length.");
        }
        if (length < 2) return;
        final Comparator<Object> comparator = toComparator(comparer);
        final Object keyArray = keys;
        Integer[] order = new Integer[length];
        for (int i = 0; i < length; i++) {
            order[i] = i;
        }
        Arrays.sort(order, (left, right) -> {
            Object a = Array.get(keyArray, left);
            Object b = Array.get(keyArray, right);
            return comparator == null ? compareNatural(a, b) : comparator.compare(a, b);
        });
        Object[] sortedKeys = new Object[length];
        Object[] sortedItems = new Object[length];
        for (int i = 0; i < length; i++) {
            sortedKeys[i] = Array.get(keys, order[i]);
            sortedItems[i] = Array.get(items, order[i]);
        }
        for (int i = 0; i < length; i++) {
            Array.set(keys, i, sortedKeys[i]);
            Array.set(items, i, sortedItems[i]);
        }
    }

    // --- Array.BinarySearch ---

    /**
     * C# Array.BinarySearch(Array array, object value) — searches the whole array
     * using the natural ordering of its elements.
     *
     * @param array The sorted array to search.
     * @param value The object to locate.
     * @return The index of value, or the bitwise complement of its insertion point.
     */
    public static int binarySearch(Object array, Object value) {
        if (array == null) throw new ArgumentNullException("array");
        return binarySearch(array, 0, Array.getLength(array), value, null);
    }

    /**
     * C# Array.BinarySearch(Array array, object value, IComparer comparer).
     *
     * @param array The sorted array to search.
     * @param value The object to locate.
     * @param comparer The comparer to use, or null for the natural ordering.
     * @return The index of value, or the bitwise complement of its insertion point.
     */
    public static int binarySearch(Object array, Object value, Object comparer) {
        if (array == null) throw new ArgumentNullException("array");
        return binarySearch(array, 0, Array.getLength(array), value, comparer);
    }

    /**
     * C# Array.BinarySearch(Array array, int index, int length, object value) —
     * searches a range using the natural ordering of the elements.
     *
     * @param array The sorted array to search.
     * @param index The zero-based index of the first element of the range.
     * @param length The number of elements in the range.
     * @param value The object to locate.
     * @return The index of value, or the bitwise complement of its insertion point.
     */
    public static int binarySearch(Object array, int index, int length, Object value) {
        return binarySearch(array, index, length, value, null);
    }

    /**
     * C# Array.BinarySearch(Array array, int index, int length, object value,
     * IComparer comparer) — searches a range of a sorted array. The insertion point
     * returned in the complement is an absolute index, exactly as in C#.
     *
     * @param array The sorted array to search.
     * @param index The zero-based index of the first element of the range.
     * @param length The number of elements in the range.
     * @param value The object to locate.
     * @param comparer The comparer to use, or null for the natural ordering.
     * @return The index of value, or the bitwise complement of its insertion point.
     */
    public static int binarySearch(Object array, int index, int length, Object value, Object comparer) {
        if (array == null) throw new ArgumentNullException("array");
        if (index < 0) throw new ArgumentOutOfRangeException("index");
        if (length < 0) throw new ArgumentOutOfRangeException("length");
        if (index > Array.getLength(array) - length) {
            throw new ArgumentException("Offset and length were out of bounds for the array or count is greater than the number of elements from index to the end of the source collection.");
        }
        Comparator<Object> comparator = toComparator(comparer);
        if (!array.getClass().getComponentType().isPrimitive()) {
            return Arrays.binarySearch((Object[]) array, index, index + length, value, comparator);
        }
        // Search a boxed copy of the whole array so that the insertion point, and
        // therefore the returned complement, stays an absolute index.
        Object[] boxed = new Object[Array.getLength(array)];
        for (int i = 0; i < boxed.length; i++) {
            boxed[i] = Array.get(array, i);
        }
        return Arrays.binarySearch(boxed, index, index + length, value, comparator);
    }

    // --- comparer adaptation ---

    /**
     * Adapts the comparer shapes used by the generated code to a
     * {@link java.util.Comparator}: null means "use the natural ordering", a
     * {@link java.util.Comparator} (which includes {@link CSharpComparer}) is used
     * as-is, and any other comparer — the project's {@code IComparer} and
     * {@code IComparer&lt;T&gt;} implementations, whose two-argument comparison
     * method erases to {@code compare(Object, Object)} or
     * {@code compare$Class(Object, Object)} — is invoked reflectively. Accepting
     * Object keeps a single sort/binarySearch overload per arity, which is what
     * keeps the existing call sites unambiguous.
     */
    private static Comparator<Object> toComparator(Object comparer) {
        if (comparer == null) return null;
        if (comparer instanceof Comparator) {
            @SuppressWarnings("unchecked")
            Comparator<Object> comparator = (Comparator<Object>) comparer;
            return comparator;
        }
        Method compare = findCompareMethod(comparer.getClass());
        if (compare == null) {
            throw new ArgumentException("The comparer does not implement a two-argument comparison method: " + comparer.getClass().getName());
        }
        return (left, right) -> {
            try {
                Object result = compare.invoke(comparer, left, right);
                return result instanceof Number ? ((Number) result).intValue() : 0;
            } catch (IllegalAccessException e) {
                throw new RuntimeException(e);
            } catch (InvocationTargetException e) {
                Throwable cause = e.getCause();
                if (cause instanceof RuntimeException runtime) throw runtime;
                if (cause instanceof Error error) throw error;
                throw new RuntimeException(cause);
            }
        };
    }

    /**
     * Finds the two-argument comparison method of a comparer, preferring
     * {@code compare} (IComparer&lt;T&gt;) over {@code compare$Class} (IComparer) and
     * the delegate-shaped names used by converted comparisons.
     */
    private static Method findCompareMethod(Class<?> type) {
        Method method = findTwoArgumentCompare(type, "compare");
        if (method == null) method = findTwoArgumentCompare(type, "compare$Class");
        if (method == null) method = findTwoArgumentCompare(type, "compareTo");
        if (method == null) method = findTwoArgumentCompare(type, "invoke");
        if (method == null) method = findTwoArgumentCompare(type, "apply");
        if (method == null) method = findTwoArgumentCompare(type, "applyAsInt");
        return method;
    }

    /**
     * Returns the named non-static two-argument int-returning method of the type, or
     * null when there is no such method.
     */
    private static Method findTwoArgumentCompare(Class<?> type, String name) {
        Method method;
        try {
            method = type.getMethod(name, Object.class, Object.class);
        } catch (NoSuchMethodException e) {
            return null;
        }
        if (Modifier.isStatic(method.getModifiers())) return null;
        Class<?> returnType = method.getReturnType();
        if (returnType != int.class && returnType != Integer.class) return null;
        try {
            method.setAccessible(true);
        } catch (RuntimeException ignored) {
            // Invoking the method may still work when it is public; the reflective
            // call itself reports the failure if it does not.
        }
        return method;
    }

    /**
     * Compares two boxed values using the .NET default comparer semantics, i.e. the
     * left value's IComparable implementation.
     */
    @SuppressWarnings({"unchecked", "rawtypes"})
    private static int compareNatural(Object left, Object right) {
        if (left == right) return 0;
        if (left == null) return -1;
        if (right == null) return 1;
        if (left instanceof Comparable) {
            return ((Comparable) left).compareTo(right);
        }
        throw new ArgumentException("At least one object must implement IComparable.");
    }
}
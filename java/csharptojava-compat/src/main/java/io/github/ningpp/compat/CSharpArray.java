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

    @Override
    public boolean equals(Object obj) {
        return obj instanceof CSharpArray other && Objects.equals(array, other.array);
    }

    @Override
    public int hashCode() {
        return Objects.hashCode(array);
    }
}

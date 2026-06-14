package io.github.ningpp.compat;

import java.lang.reflect.Array;
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

    @Override
    public boolean equals(Object obj) {
        return obj instanceof CSharpArray other && Objects.equals(array, other.array);
    }

    @Override
    public int hashCode() {
        return Objects.hashCode(array);
    }
}

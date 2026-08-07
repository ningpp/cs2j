package io.github.ningpp.compat;

import java.lang.reflect.Array;
import java.util.ArrayDeque;

/**
 * Lightweight stand-in for System.Buffers.ArrayPool&lt;T&gt; (converted from C#).
 * Buckets arrays by power-of-two length; oversized requests allocate directly.
 */
public final class ArrayPool<T> {
    private static final ArrayPool<Object> SHARED = new ArrayPool<>(Object.class);
    private static final ArrayDeque<char[]> CHAR_BUCKET = new ArrayDeque<>();

    private final Class<?> componentType;
    private final ArrayDeque<Object[]> buckets = new ArrayDeque<>();

    private ArrayPool(Class<?> componentType) {
        this.componentType = componentType;
    }

    /**
     * Returns a shared pool instance. The generic parameter is erased at runtime
     * (mirroring C#'s ArrayPool&lt;T&gt;.Shared), so the cast is unchecked by design.
     */
    @SuppressWarnings("unchecked")
    public static <T> ArrayPool<T> getShared() {
        return (ArrayPool<T>) SHARED;
    }

    /**
     * Rents an array with at least {@code minimumLength} elements.
     */
    @SuppressWarnings("unchecked")
    public T[] rent(int minimumLength) {
        if (minimumLength < 0) {
            throw new IllegalArgumentException("minimumLength=" + minimumLength);
        }
        int size = Math.max(1, Integer.highestOneBit(Math.max(minimumLength, 1) - 1) << 1);
        if (size < minimumLength) {
            size = minimumLength;
        }
        Object[] pooled = buckets.pollFirst();
        if (pooled != null && pooled.length >= minimumLength) {
            return (T[]) pooled;
        }
        return (T[]) Array.newInstance(componentType, size);
    }

    /**
     * Returns an array to the pool.
     */
    public void returnValue(T[] array) {
        returnValue(array, false);
    }

    /**
     * Returns an array to the pool, optionally clearing its contents first.
     */
    public void returnValue(T[] array, boolean clearArray) {
        if (array == null) {
            throw new NullPointerException("array");
        }
        if (clearArray) {
            java.util.Arrays.fill(array, null);
        }
        if (buckets.size() < 32) {
            buckets.addLast(array);
        }
    }

    /**
     * Rents a char[] from the shared char pool. Java generics cannot express
     * ArrayPool&lt;char&gt;, so C# ArrayPool&lt;char&gt;.Shared.Rent routes here.
     */
    public static char[] rentSharedCharArray(int minimumLength) {
        if (minimumLength < 0) {
            throw new IllegalArgumentException("minimumLength=" + minimumLength);
        }
        int size = Math.max(1, Integer.highestOneBit(Math.max(minimumLength, 1) - 1) << 1);
        if (size < minimumLength) {
            size = minimumLength;
        }
        char[] pooled = CHAR_BUCKET.pollFirst();
        if (pooled != null && pooled.length >= minimumLength) {
            return pooled;
        }
        return new char[size];
    }

    /**
     * Returns a char[] to the shared char pool.
     */
    public static void returnSharedCharArray(char[] array) {
        returnSharedCharArray(array, false);
    }

    /**
     * Returns a char[] to the shared char pool, optionally clearing it first.
     */
    public static void returnSharedCharArray(char[] array, boolean clearArray) {
        if (array == null) {
            throw new NullPointerException("array");
        }
        if (clearArray) {
            java.util.Arrays.fill(array, '\0');
        }
        if (CHAR_BUCKET.size() < 32) {
            CHAR_BUCKET.addLast(array);
        }
    }
}

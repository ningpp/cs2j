package io.github.ningpp.compat;

import java.lang.foreign.MemorySegment;

/**
 * Mirrors System.Runtime.InteropServices.GCHandle for converted C# code.
 *
 * <p>.NET's {@code GCHandle} pins a managed object and provides an address
 * through {@code AddrOfPinnedObject()}.  In Java, pinned memory is represented
 * by {@link MemorySegment}, so this class provides static helper methods that
 * operate on {@code Object} references (since the C# source may declare the
 * handle variable as {@code object}).</p>
 */
public final class GCHandle {

    private GCHandle() { /* utility class */ }

    /**
     * Allocates a pinned handle for the given array.
     * Mirrors {@code GCHandle.Alloc(obj, GCHandleType.Pinned)}.
     *
     * @param array the array to pin (char[], byte[], int[], long[], double[], float[])
     * @return a MemorySegment representing the pinned array
     */
    public static MemorySegment alloc(Object array) {
        if (array instanceof MemorySegment ms) {
            return ms;
        }
        if (array instanceof char[] arr) {
            return MemorySegment.ofArray(arr);
        }
        if (array instanceof byte[] arr) {
            return MemorySegment.ofArray(arr);
        }
        if (array instanceof int[] arr) {
            return MemorySegment.ofArray(arr);
        }
        if (array instanceof long[] arr) {
            return MemorySegment.ofArray(arr);
        }
        if (array instanceof double[] arr) {
            return MemorySegment.ofArray(arr);
        }
        if (array instanceof float[] arr) {
            return MemorySegment.ofArray(arr);
        }
        if (array instanceof short[] arr) {
            return MemorySegment.ofArray(arr);
        }
        throw new IllegalArgumentException("Unsupported array type for GCHandle.alloc: " + array.getClass().getName());
    }

    /**
     * Returns the address of a pinned object as a MemorySegment.
     * If the handle is {@code null} (not allocated), returns {@code null}.
     *
     * @param handle the GCHandle variable (may be {@code null})
     * @return the MemorySegment for the pinned object, or {@code null}
     */
    public static MemorySegment addrOfPinnedObject(Object handle) {
        if (handle == null) {
            return null;
        }
        if (handle instanceof MemorySegment ms) {
            return ms;
        }
        if (handle instanceof char[] arr) {
            return MemorySegment.ofArray(arr);
        }
        if (handle instanceof byte[] arr) {
            return MemorySegment.ofArray(arr);
        }
        if (handle instanceof int[] arr) {
            return MemorySegment.ofArray(arr);
        }
        if (handle instanceof long[] arr) {
            return MemorySegment.ofArray(arr);
        }
        if (handle instanceof double[] arr) {
            return MemorySegment.ofArray(arr);
        }
        if (handle instanceof float[] arr) {
            return MemorySegment.ofArray(arr);
        }
        return null;
    }

    /**
     * Returns whether the GCHandle is allocated (not {@code null}).
     *
     * @param handle the GCHandle variable (may be {@code null})
     * @return {@code true} if the handle is not {@code null}
     */
    public static boolean isAllocated(Object handle) {
        return handle != null;
    }

    /**
     * Frees the GCHandle by setting it to {@code null}.
     * This is a no-op in Java since GCHandle pinning is handled by MemorySegment.
     *
     * @param handle the GCHandle variable (may be {@code null})
     */
    public static void free(Object handle) {
        // No-op: in Java, MemorySegment handles its own lifecycle.
        // The caller should set the variable to null if needed.
    }
}

package io.github.ningpp.compat;

import java.lang.foreign.MemorySegment;

/**
 * Mirrors System.Runtime.InteropServices.GCHandle for converted C# code.
 *
 * <p>.NET's {@code GCHandle} is a struct that keeps a strong/pinned reference
 * to a managed object and exposes its address through
 * {@code AddrOfPinnedObject()}.  In Java there is no moving GC handle
 * equivalent: a direct reference already keeps the object alive, and pinned
 * memory is represented by {@link MemorySegment#ofArray(Object[])} wrappers,
 * so this class simply wraps the target reference and derives the
 * {@code MemorySegment} on demand.</p>
 *
 * <p>Static {@code isAllocated(Object)} / {@code addrOfPinnedObject(Object)} /
 * {@code free(Object)} overloads are kept for converted code whose handle
 * variable was declared {@code object} (or {@code null} from
 * {@code default(GCHandle)}) — the converter emits
 * {@code GCHandle.isAllocated(handle)} for those receivers.</p>
 */
public final class GCHandle {

    private Object target;
    private boolean allocated;

    private GCHandle(Object target) {
        this.target = target;
        this.allocated = true;
    }

    /**
     * Mirrors {@code GCHandle.Alloc(object)} — allocates a Normal handle.
     */
    public static GCHandle alloc(Object value) {
        return alloc(value, GCHandleType.Normal);
    }

    /**
     * Mirrors {@code GCHandle.Alloc(object, GCHandleType)}.
     */
    public static GCHandle alloc(Object value, GCHandleType type) {
        if (value == null) {
            throw new ArgumentNullException("value");
        }
        return new GCHandle(value);
    }

    /**
     * Mirrors the {@code IsAllocated} property.
     */
    public boolean isAllocated() {
        return allocated;
    }

    /**
     * Mirrors {@code AddrOfPinnedObject()}.  Returns the pinned array as a
     * {@link MemorySegment}; {@code null} when the target is not a Java array
     * (or a {@code MemorySegment} already).
     */
    public MemorySegment addrOfPinnedObject() {
        return asSegment(target);
    }

    /**
     * Mirrors the {@code Target} property getter.
     */
    public Object getTarget() {
        return target;
    }

    /**
     * Mirrors the {@code Target} property setter.
     */
    public void setTarget(Object value) {
        this.target = value;
    }

    /**
     * Mirrors {@code Free()}: releases the target reference and reports
     * whether the handle had been allocated.
     */
    public boolean free() {
        boolean wasAllocated = allocated;
        allocated = false;
        target = null;
        return wasAllocated;
    }

    /**
     * Static helper mirroring {@code IsAllocated} for {@code object}-typed or
     * possibly-null handle variables.
     */
    public static boolean isAllocated(Object handle) {
        if (handle instanceof GCHandle h) {
            return h.isAllocated();
        }
        return handle != null;
    }

    /**
     * Static helper mirroring {@code AddrOfPinnedObject()} for
     * {@code object}-typed handle variables.
     */
    public static MemorySegment addrOfPinnedObject(Object handle) {
        if (handle instanceof GCHandle h) {
            return h.addrOfPinnedObject();
        }
        return asSegment(handle);
    }

    /**
     * Static helper mirroring {@code Free()} for {@code object}-typed handle
     * variables.  Returns whether the handle was allocated.
     */
    public static boolean free(Object handle) {
        if (handle instanceof GCHandle h) {
            return h.free();
        }
        return handle != null;
    }

    private static MemorySegment asSegment(Object o) {
        if (o instanceof MemorySegment ms) {
            return ms;
        }
        if (o instanceof char[] arr) {
            return MemorySegment.ofArray(arr);
        }
        if (o instanceof byte[] arr) {
            return MemorySegment.ofArray(arr);
        }
        if (o instanceof int[] arr) {
            return MemorySegment.ofArray(arr);
        }
        if (o instanceof long[] arr) {
            return MemorySegment.ofArray(arr);
        }
        if (o instanceof double[] arr) {
            return MemorySegment.ofArray(arr);
        }
        if (o instanceof float[] arr) {
            return MemorySegment.ofArray(arr);
        }
        if (o instanceof short[] arr) {
            return MemorySegment.ofArray(arr);
        }
        return null;
    }
}

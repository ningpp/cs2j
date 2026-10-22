package io.github.ningpp.compat;

/**
 * Minimal compat stub for System.GC (garbage collector).
 * Mirrors the API surface used by converted C# code.
 */
public final class GC {

    private GC() { /* utility class */ }

    /**
     * Returns the best approximation of the number of bytes currently allocated
     * in managed memory.
     *
     * <p>If {@code forceFullCollection} is {@code true}, this method triggers
     * a garbage collection before measuring memory (mirrors C#'s behavior where
     * {@code true} indicates the method can wait for GC to occur before returning).</p>
     *
     * @param forceFullCollection if {@code true}, trigger GC before measuring
     * @return the approximate number of bytes allocated
     */
    public static long getTotalMemory(boolean forceFullCollection) {
        if (forceFullCollection) {
            System.gc();
        }
        Runtime rt = Runtime.getRuntime();
        return rt.totalMemory() - rt.freeMemory();
    }

    /**
     * Forces a garbage collection of all generations.
     * Mirrors {@code System.GC.Collect()}.
     */
    public static void collect() {
        System.gc();
    }

    /**
     * Forces a garbage collection of the specified generation.
     * Mirrors {@code System.GC.Collect(int)}.
     *
     * @param generation the generation number to collect
     */
    public static void collect(int generation) {
        System.gc();
    }

    /**
     * Forces a garbage collection using the specified mode.
     * Mirrors {@code System.GC.Collect(int, GCCollectionMode)}.
     *
     * @param generation the generation number to collect
     * @param mode       the collection mode (Default or Forced)
     */
    public static void collect(int generation, int mode) {
        System.gc();
    }

    /**
     * Waits for all finalizers to finish executing.
     * Mirrors {@code System.GC.WaitForPendingFinalizers()}.
     */
    public static void waitForPendingFinalizers() {
        try {
            System.runFinalization();
        } catch (Throwable ignored) {
            // no-op
        }
    }

    /**
     * Keeps the reference alive until this point (mirrors {@code GC.KeepAlive}).
     * The JIT can no longer reclaim {@code obj} before this call; the empty
     * synchronized block forces a black-box use of the reference.
     *
     * @param obj the reference to keep alive (including boxed primitives)
     */
    public static void keepAlive(Object obj) {
        synchronized (GC.class) {
            // Black-box use: the argument must remain live up to here.
            if (obj == GC.class) {
                throw new AssertionError("unreachable identity");
            }
        }
    }

    /**
     * Mirrors {@code System.GC.SuppressFinalize(Object)}: converted C# Dispose
     * patterns call it after {@code dispose(true)}, but converted classes have
     * no JVM finalizers to suppress — a no-op preserves the control flow.
     *
     * @param obj the object whose finalizer would have been suppressed
     */
    public static void suppressFinalize(Object obj) {
        // no-op: converted types carry no finalizers.
    }

    /**
     * Returns the generation number of the target object.
     * In this compat implementation, always returns 0 since Java
     * does not expose object generation the same way .NET does.
     *
     * @param obj the target object
     * @return always 0
     */
    public static int getGeneration(Object obj) {
        return 0;
    }

    /**
     * Returns the number of times garbage collection has occurred for the specified generation.
     * Mirrors {@code System.GC.CollectionCount(int)}.
     *
     * @param generation the generation number
     * @return 0 (not tracked in this compat stub)
     */
    public static int collectionCount(int generation) {
        return 0;
    }
}

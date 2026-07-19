package io.github.ningpp.compat;

/**
 * Minimal compat stub for System.Threading.Monitor.
 * Provides enough API surface to let converted Java code compile.
 * Concurrency semantics are approximated via intrinsic locks where possible.
 */
public final class Monitor {

    private Monitor() {
    }

    public static void enter(Object obj) {
        if (obj == null) {
            throw new NullPointerException("obj");
        }
        // Intrinsic lock acquisition happens in the caller's synchronized block.
        // This method exists only to satisfy the translated call site.
    }

    public static void enter(Object obj, BoolHolder lockTaken) {
        if (obj == null) {
            throw new NullPointerException("obj");
        }
        if (lockTaken != null) {
            lockTaken.value = true;
        }
    }

    public static void exit(Object obj) {
        if (obj == null) {
            throw new NullPointerException("obj");
        }
        // Intrinsic lock release happens when the caller's synchronized block ends.
    }

    public static boolean tryEnter(Object obj) {
        return tryEnter(obj, 0);
    }

    public static boolean tryEnter(Object obj, BoolHolder lockTaken) {
        if (obj == null) {
            throw new NullPointerException("obj");
        }
        if (lockTaken != null) {
            lockTaken.value = true;
        }
        return true;
    }

    public static boolean tryEnter(Object obj, int millisecondsTimeout) {
        if (obj == null) {
            throw new NullPointerException("obj");
        }
        // Best-effort: always report success for translated code that does not rely on real contention.
        return true;
    }

    public static boolean tryEnter(Object obj, CSharpTimeSpan timeout) {
        return tryEnter(obj, (int) timeout.getTotalMilliseconds());
    }

    public static boolean isEntered(Object obj) {
        if (obj == null) {
            throw new NullPointerException("obj");
        }
        return Thread.holdsLock(obj);
    }

    public static boolean wait(Object obj) throws InterruptedException {
        obj.wait();
        return true;
    }

    public static boolean wait(Object obj, int millisecondsTimeout) throws InterruptedException {
        obj.wait(millisecondsTimeout);
        return true;
    }

    public static boolean wait(Object obj, CSharpTimeSpan timeout) throws InterruptedException {
        return wait(obj, (int) timeout.getTotalMilliseconds());
    }

    public static void pulse(Object obj) {
        obj.notify();
    }

    public static void pulseAll(Object obj) {
        obj.notifyAll();
    }
}

package io.github.ningpp.compat;

import java.util.Collections;
import java.util.Map;
import java.util.WeakHashMap;
import java.util.concurrent.locks.ReentrantLock;

/**
 * Compat bridge for {@code System.Threading.Monitor}.
 *
 * <p>Two lock disciplines coexist in this bridge:</p>
 * <ul>
 *   <li><b>Intrinsic mode (C29 诚实化, 设计 §5.4 路线 (a)).</b> {@code lock (obj)}
 *       statements are emitted directly as Java {@code synchronized (obj)} blocks and
 *       never pass through this bridge. The single-argument {@link #enter(Object)} /
 *       {@link #exit(Object)} pair only serves explicit C29 residual calls (CS2J2910
 *       sites that could not be pair-rewritten): they acquire nothing — Java intrinsic
 *       monitors cannot be acquired by an ordinary method call — and instead fail fast
 *       unless the current thread already holds the object's intrinsic monitor inside a
 *       surrounding {@code synchronized (obj)} block.</li>
 *   <li><b>Pooled-lock mode (iterator lockTaken pattern).</b> The canonical C# pattern
 *       {@code bool taken=false; Monitor.Enter(obj, ref taken); try { … } finally {
 *       if (taken) Monitor.Exit(obj); }} — emitted by the iterator-lowering pass for
 *       {@code lock} bodies that cross {@code yield} points — cannot use an intrinsic
 *       monitor, because an intrinsic lock acquired in one {@code MoveNext()} call is
 *       not retained across the next call. {@link #enter(Object, BoolHolder)} therefore
 *       acquires a per-object {@link ReentrantLock} from a weak-keyed pool, and
 *       {@link #exit(Object)} releases it. The pooled lock is reentrant and thread-bound
 *       exactly like {@code System.Threading.Monitor}: cross-thread release and
 *       release-without-enter throw {@link SynchronizationLockException}, matching the
 *       CLR behaviour. The {@code lockTaken} flag is set only after successful
 *       acquisition, preserving the exception-safety contract of the C# pattern.</li>
 *   <li>{@code TryEnter} overloads: Java intrinsic monitors have no tryLock and the
 *       iterator pass never emits them; CS2J2911 forbids always-succeed stubs, so they
 *       unconditionally throw {@link UnsupportedOperationException} and leave
 *       {@code lockTaken} at its caller-initialized {@code false}.</li>
 *   <li>{@link #wait}/{@link #pulse}/{@link #pulseAll} delegate to
 *       {@link Object#wait(long)}/{@link Object#notify()}/{@link Object#notifyAll()} and
 *       therefore require the intrinsic monitor (mixing them with a pooled-lock region
 *       is a CS2J2910 residual shape and fails fast). {@code wait}'s boolean result is
 *       the documented §10-1 approximation: Java cannot distinguish timeout from
 *       notification, so a wake within a 5ms slack of the timeout window reports false.</li>
 * </ul>
 */
public final class Monitor {

    private static final int INFINITE = -1;

    /** Tolerance for classifying timeout wakes vs pulse wakes (§10-1 approximation). */
    private static final long TIMER_SLACK_NANOS = 5_000_000L;

    /**
     * Per-object reentrant locks backing the lockTaken-pattern Enter/Exit pair.
     * Weak keys let lock objects be collected once users drop them; the map (not the
     * values) keeps keys reachable, and pooled {@link ReentrantLock} instances never
     * reference their key, so there is no strong-reference retention cycle. All map
     * access goes through the synchronized-map wrapper; {@code computeIfAbsent} runs
     * under the map monitor but never under a pooled lock, so there is no lock-ordering
     * hazard.
     */
    private static final Map<Object, ReentrantLock> POOLED_LOCKS =
            Collections.synchronizedMap(new WeakHashMap<>());

    private Monitor() {
    }

    /**
     * Residual single-argument {@code Monitor.Enter(obj)} site (CS2J2910). The
     * intrinsic lock must already be held by a surrounding {@code synchronized (obj)}
     * block emitted by the converter; this method only verifies that precondition —
     * it never acquires.
     */
    public static void enter(Object obj) {
        requireLockObject(obj);
        requireHoldsIntrinsicLock(obj, "Monitor.enter");
    }

    /**
     * Canonical lockTaken-pattern enter. Acquires the per-object pooled
     * {@link ReentrantLock} (reentrant, thread-bound — cross-{@code MoveNext()} safe
     * inside lowered iterators) and sets {@code lockTaken.value = true} only after
     * acquisition has completed, so a failure during acquisition leaves the finally
     * guard at false exactly as the CLR contract requires.
     */
    public static void enter(Object obj, BoolHolder lockTaken) {
        requireLockObject(obj);
        if (lockTaken == null) {
            throw new NullPointerException("lockTaken");
        }
        pooledLockFor(obj).lock();
        lockTaken.value = true;
    }

    /**
     * Releases the lock most recently entered for {@code obj} by this thread: if the
     * per-object pooled lock exists and is held by the current thread (lockTaken
     * pattern), it is unlocked; otherwise this is a C29 residual intrinsic-mode site
     * and the intrinsic-monitor precondition is verified. Releasing without a matching
     * enter throws {@link SynchronizationLockException} on both paths, mirroring the CLR.
     */
    public static void exit(Object obj) {
        requireLockObject(obj);
        ReentrantLock pooled = POOLED_LOCKS.get(obj);
        if (pooled != null && pooled.isHeldByCurrentThread()) {
            pooled.unlock();
            return;
        }
        requireHoldsIntrinsicLock(obj, "Monitor.exit");
    }

    /**
     * @throws UnsupportedOperationException always — Java intrinsic monitors have no
     *     tryLock; C29 CS2J2911 forbids wiring this site to an always-succeed stub.
     */
    public static boolean tryEnter(Object obj) {
        requireLockObject(obj);
        throw tryEnterUnsupported();
    }

    /**
     * @throws UnsupportedOperationException always — same contract as
     *     {@link #tryEnter(Object)}; {@code lockTaken} is deliberately left untouched
     *     (callers initialize it to false).
     */
    public static boolean tryEnter(Object obj, BoolHolder lockTaken) {
        requireLockObject(obj);
        throw tryEnterUnsupported();
    }

    /**
     * @throws UnsupportedOperationException always — Java intrinsic monitors have no
     *     timed tryLock (C29 CS2J2911 explicit-unsupported contract).
     */
    public static boolean tryEnter(Object obj, int millisecondsTimeout) {
        requireLockObject(obj);
        throw tryEnterUnsupported();
    }

    /**
     * @throws UnsupportedOperationException always — same contract as
     *     {@link #tryEnter(Object, int)}.
     */
    public static boolean tryEnter(Object obj, CSharpTimeSpan timeout) {
        requireLockObject(obj);
        if (timeout == null) {
            throw new NullPointerException("timeout");
        }
        throw tryEnterUnsupported();
    }

    public static boolean isEntered(Object obj) {
        requireLockObject(obj);
        ReentrantLock pooled = POOLED_LOCKS.get(obj);
        return (pooled != null && pooled.isHeldByCurrentThread()) || Thread.holdsLock(obj);
    }

    /**
     * Mirrors {@code Monitor.Wait(obj)}: must be called inside {@code synchronized}.
     * Thread interruption is surfaced as the unchecked compat
     * {@link ThreadInterruptedException} with the interrupt flag restored.
     */
    public static boolean wait(Object obj) {
        return wait(obj, 0L);
    }

    /**
     * Mirrors {@code Monitor.Wait(obj, int millisecondsTimeout)}; {@code -1} means
     * infinite (maps to {@code wait(0)}). The boolean result is the documented §10-1
     * approximation: false only when the wait consumed at least the requested timeout.
     */
    public static boolean wait(Object obj, int millisecondsTimeout) {
        return wait(obj, normalizeTimeout(millisecondsTimeout));
    }

    /** Mirrors {@code Monitor.Wait(obj, TimeSpan)} via the int-millisecond overload. */
    public static boolean wait(Object obj, CSharpTimeSpan timeout) {
        if (timeout == null) {
            throw new NullPointerException("timeout");
        }
        return wait(obj, (long) timeout.getTotalMilliseconds());
    }

    public static void pulse(Object obj) {
        requireLockObject(obj);
        requireHoldsIntrinsicLock(obj, "Monitor.pulse");
        obj.notify();
    }

    public static void pulseAll(Object obj) {
        requireLockObject(obj);
        requireHoldsIntrinsicLock(obj, "Monitor.pulseAll");
        obj.notifyAll();
    }

    private static boolean wait(Object obj, long timeoutMillis) {
        requireLockObject(obj);
        requireHoldsIntrinsicLock(obj, "Monitor.wait");
        long startNanos = System.nanoTime();
        try {
            obj.wait(timeoutMillis);
        } catch (InterruptedException e) {
            Thread.currentThread().interrupt();
            throw new ThreadInterruptedException(
                "Monitor.wait interrupted while waiting on the object monitor", e);
        }
        if (timeoutMillis == 0L) {
            return true;
        }
        long elapsedNanos = System.nanoTime() - startNanos;
        // §10-1 approximation: Java cannot distinguish a timeout from a notification;
        // classify a wake within TIMER_SLACK_NANOS of the requested deadline as a
        // timeout (covers millisecond truncation and OS timer granularity), and any
        // earlier wake as a pulse notification.
        return elapsedNanos < timeoutMillis * 1_000_000L - TIMER_SLACK_NANOS;
    }

    private static long normalizeTimeout(int millisecondsTimeout) {
        if (millisecondsTimeout == INFINITE) {
            return 0L;
        }
        if (millisecondsTimeout < 0) {
            throw new ArgumentOutOfRangeException(
                "millisecondsTimeout",
                "millisecondsTimeout must be non-negative, or -1 for infinite wait.");
        }
        return millisecondsTimeout;
    }

    /**
     * Returns the pooled reentrant lock for {@code obj}, creating one on first use.
     * Runs (briefly) under the synchronized map's monitor; the mapping function only
     * constructs a fresh {@link ReentrantLock} and never re-enters any pooled lock.
     */
    private static ReentrantLock pooledLockFor(Object obj) {
        return POOLED_LOCKS.computeIfAbsent(obj, key -> new ReentrantLock());
    }

    private static void requireLockObject(Object obj) {
        if (obj == null) {
            throw new NullPointerException("obj");
        }
    }

    private static void requireHoldsIntrinsicLock(Object obj, String site) {
        if (!Thread.holdsLock(obj)) {
            throw new SynchronizationLockException(
                site + " reached the Java compat bridge without the current thread "
                + "holding the intrinsic monitor of obj. Java intrinsic locks can only "
                + "be acquired structurally via 'synchronized (obj)'; a C29 residual "
                + "site (CS2J2910) escaped lock-pair rewrite and must be resolved "
                + "manually instead of silently running unprotected.");
        }
    }

    private static UnsupportedOperationException tryEnterUnsupported() {
        return new UnsupportedOperationException(
            "System.Threading.Monitor.TryEnter has no honest Java equivalent: intrinsic "
            + "monitors only support blocking acquisition inside 'synchronized' blocks "
            + "(no tryLock / no timed acquisition). C29 gates this site as CS2J2911; "
            + "restructure to 'lock (obj)' or java.util.concurrent.locks.ReentrantLock "
            + "instead of relying on an always-succeed stub.");
    }
}

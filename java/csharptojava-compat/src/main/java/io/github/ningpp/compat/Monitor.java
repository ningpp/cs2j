package io.github.ningpp.compat;

/**
 * Compat bridge for {@code System.Threading.Monitor}.
 *
 * <p>C29 诚实化（设计 §5.4 路线 (a) intrinsic 桥）后的契约：</p>
 * <ul>
 *   <li>{@code lock (obj)} 语句由转换器直接发为 Java {@code synchronized (obj)}
 *       块，不经过本桥；因此 {@link #enter(Object)}/{@link #exit(Object)} 只承接
 *       C29 无法配对改写而残留的显式调用（CS2J2910 挂账站点）。这些方法<b>不获取
 *       任何锁</b>——Java intrinsic monitor 无法由普通方法调用获取——而是校验
 *       调用线程确已持有该对象的 intrinsic 监视器（即调用点位于
 *       {@code synchronized (obj)} 内），违例立即 fail-fast，绝不静默空跑。</li>
 *   <li>{@code TryEnter} 全部重载：Java intrinsic 监视器不存在 tryLock，
 *       桥接层禁止恒真桩（CS2J2911），一律抛 {@link UnsupportedOperationException}
 *       显式拒绝；{@code lockTaken} out 形参保持调用方初始化的 {@code false}。</li>
 *   <li>{@link #wait}/{@link #pulse}/{@link #pulseAll} 委托
 *       {@link Object#wait(long)}/{@link Object#notify()}/{@link Object#notifyAll()}，
 *       仅在 {@code synchronized (obj)} 内合法（与 C# Monitor 的归属要求一致）。
 *       {@code wait} 的 bool 返回值按设计 §10-1 为近似值：Java 无法区分超时与
 *       通知唤醒，按 5ms 计时容差分类，耗时达到超时窗口即返回 false。</li>
 * </ul>
 */
public final class Monitor {

    private static final int INFINITE = -1;

    /** Tolerance for classifying timeout wakes vs pulse wakes (§10-1 approximation). */
    private static final long TIMER_SLACK_NANOS = 5_000_000L;

    private Monitor() {
    }

    /**
     * Residual {@code Monitor.Enter(obj)} site (CS2J2910). The intrinsic lock must
     * already be held by a surrounding {@code synchronized (obj)} block emitted by
     * the converter; this method only verifies that precondition — it never acquires.
     */
    public static void enter(Object obj) {
        requireLockObject(obj);
        requireHoldsLock(obj, "Monitor.enter");
    }

    /**
     * Residual {@code Monitor.Enter(obj, ref lockTaken)} site (CS2J2910). Same
     * precondition as {@link #enter(Object)}; {@code lockTaken} is set to true only
     * after the precondition is verified.
     */
    public static void enter(Object obj, BoolHolder lockTaken) {
        requireLockObject(obj);
        requireHoldsLock(obj, "Monitor.enter");
        if (lockTaken != null) {
            lockTaken.value = true;
        }
    }

    /**
     * Residual {@code Monitor.Exit(obj)} site (CS2J2910). Verifies the intrinsic
     * monitor is held by the current thread; it never releases anything itself —
     * release happens structurally when the caller's {@code synchronized} block ends.
     */
    public static void exit(Object obj) {
        requireLockObject(obj);
        requireHoldsLock(obj, "Monitor.exit");
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
        return Thread.holdsLock(obj);
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
        requireHoldsLock(obj, "Monitor.pulse");
        obj.notify();
    }

    public static void pulseAll(Object obj) {
        requireLockObject(obj);
        requireHoldsLock(obj, "Monitor.pulseAll");
        obj.notifyAll();
    }

    private static boolean wait(Object obj, long timeoutMillis) {
        requireLockObject(obj);
        requireHoldsLock(obj, "Monitor.wait");
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

    private static void requireLockObject(Object obj) {
        if (obj == null) {
            throw new NullPointerException("obj");
        }
    }

    private static void requireHoldsLock(Object obj, String site) {
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

package io.github.ningpp.compat;

import java.util.concurrent.TimeUnit;
import java.util.concurrent.locks.ReentrantLock;

/**
 * Compat replacement for {@code System.Threading.Lock} (.NET 9 dedicated lock object).
 *
 * <p>C29 lock 分型：当 C# {@code lock (x)} 的 {@code x} 静态类型为
 * {@code System.Threading.Lock} 时，转换器不发 {@code synchronized}（那会锁
 * wrapper 实例的 intrinsic monitor，与本锁是两把锁），而发
 * {@code x.enter(); try { ... } finally { x.exit(); }}，与本类的实例方法对接。
 * 直接调用 {@code Enter/Exit/TryEnter} 的代码经成员映射走同样的方法。</p>
 *
 * <p>语义委托给可重入的 {@link ReentrantLock}（与 C# System.Threading.Lock 的
 * 可重入、线程亲和语义一致）。中断策略：{@code enter} 不可中断（对齐 C# Enter
 * 不响应 Thread.Interrupt 的获取语义）；{@code tryEnter} 被中断时复位 interrupt
 * 标志并抛非检查的 {@link ThreadInterruptedException}。</p>
 */
public final class Lock {

    /** C# Lock.TryEnter(TimeSpan/-1ms): -1 milliseconds means infinite. */
    private static final int INFINITE = -1;

    private final ReentrantLock sync = new ReentrantLock();

    public void enter() {
        sync.lock();
    }

    public void exit() {
        if (!sync.isHeldByCurrentThread()) {
            throw new SynchronizationLockException(
                "System.Threading.Lock.exit requires the current thread to hold the lock.");
        }
        sync.unlock();
    }

    public boolean tryEnter() {
        return sync.tryLock();
    }

    public boolean tryEnter(int millisecondsTimeout) {
        if (millisecondsTimeout == INFINITE) {
            sync.lock();
            return true;
        }
        if (millisecondsTimeout < 0) {
            throw new ArgumentOutOfRangeException(
                "millisecondsTimeout",
                "millisecondsTimeout must be non-negative, or -1 for infinite wait.");
        }
        try {
            return sync.tryLock(millisecondsTimeout, TimeUnit.MILLISECONDS);
        } catch (InterruptedException e) {
            Thread.currentThread().interrupt();
            throw new ThreadInterruptedException(
                "System.Threading.Lock.tryEnter interrupted while waiting", e);
        }
    }

    public boolean tryEnter(CSharpTimeSpan timeout) {
        if (timeout == null) {
            throw new NullPointerException("timeout");
        }
        return tryEnter((int) timeout.getTotalMilliseconds());
    }

    /** Test/diagnostic aid; System.Threading.Lock itself exposes no such member in C#. */
    boolean isHeldByCurrentThread() {
        return sync.isHeldByCurrentThread();
    }
}

package io.github.ningpp.compat;

import java.util.concurrent.atomic.AtomicBoolean;

import org.junit.jupiter.api.Test;

import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertThrows;
import static org.junit.jupiter.api.Assertions.assertTrue;

class MonitorTest {

    @Test
    void enterAndExitOutsideSynchronizedAreRejected() {
        Object lock = new Object();
        assertThrows(SynchronizationLockException.class, () -> Monitor.enter(lock));
        assertThrows(SynchronizationLockException.class, () -> Monitor.exit(lock));
    }

    @Test
    void enterAndExitInsideSynchronizedPass() {
        Object lock = new Object();
        synchronized (lock) {
            Monitor.enter(lock);
            Monitor.exit(lock);
        }
    }

    @Test
    void holderPatternAcquiresPooledLockAcrossIndependentCalls() {
        // Models a lowered iterator: enter happens in one MoveNext() invocation and
        // exit in a later one — an intrinsic monitor could not span that gap.
        Object lock = new Object();
        BoolHolder holder = new BoolHolder();

        Monitor.enter(lock, holder);
        assertTrue(holder.value, "lockTaken is set only after acquisition");
        assertTrue(Monitor.isEntered(lock));
        Monitor.exit(lock);
        assertFalse(Monitor.isEntered(lock));
    }

    @Test
    void holderPatternIsReentrantOnTheSameThread() {
        Object lock = new Object();
        BoolHolder outer = new BoolHolder();
        BoolHolder inner = new BoolHolder();

        Monitor.enter(lock, outer);
        Monitor.enter(lock, inner);
        assertTrue(Monitor.isEntered(lock));
        Monitor.exit(lock);
        assertTrue(Monitor.isEntered(lock), "one exit must not release the reentrant outer enter");
        Monitor.exit(lock);
        assertFalse(Monitor.isEntered(lock));
    }

    @Test
    void holderPatternRejectsUnbalancedExit() {
        Object lock = new Object();
        assertThrows(SynchronizationLockException.class, () -> Monitor.exit(lock));
    }

    @Test
    void holderPatternNullArgumentsAreRejected() {
        assertThrows(NullPointerException.class,
            () -> Monitor.enter(null, new BoolHolder()));
        assertThrows(NullPointerException.class,
            () -> Monitor.enter(new Object(), null));
    }

    @Test
    void pooledLockIsExclusiveAcrossThreads() throws Exception {
        Object lock = new Object();
        BoolHolder holder = new BoolHolder();
        AtomicBoolean enteredCriticalSection = new AtomicBoolean(false);
        AtomicBoolean leftCriticalSection = new AtomicBoolean(false);

        Monitor.enter(lock, holder);
        Thread other = new Thread(() -> {
            BoolHolder its = new BoolHolder();
            Monitor.enter(lock, its);
            enteredCriticalSection.set(true);
            try {
                assertTrue(Monitor.isEntered(lock), "isEntered is true on the owning thread");
            } finally {
                Monitor.exit(lock);
                leftCriticalSection.set(true);
            }
        }, "monitor-test-pooled");
        other.start();

        Thread.sleep(100);
        assertTrue(other.isAlive(), "pooled enter must block while the lock is held");
        assertFalse(enteredCriticalSection.get(), "peer must not run the critical section yet");
        Monitor.exit(lock);
        other.join(5_000);
        assertFalse(other.isAlive(), "peer must complete once the pooled lock is released");
        assertTrue(leftCriticalSection.get());
    }

    @Test
    void tryEnterOverloadsAreExplicitlyUnsupportedAndNeverFakeSuccess() {
        Object lock = new Object();
        assertThrows(UnsupportedOperationException.class, () -> Monitor.tryEnter(lock));
        assertThrows(UnsupportedOperationException.class,
            () -> Monitor.tryEnter(lock, new BoolHolder()));
        assertThrows(UnsupportedOperationException.class, () -> Monitor.tryEnter(lock, 0));
        assertThrows(UnsupportedOperationException.class, () -> Monitor.tryEnter(lock, 100));
        assertThrows(UnsupportedOperationException.class,
            () -> Monitor.tryEnter(lock, CSharpTimeSpan.fromMilliseconds(100)));

        BoolHolder holder = new BoolHolder();
        assertThrows(UnsupportedOperationException.class, () -> Monitor.tryEnter(lock, holder));
        assertFalse(holder.value, "lockTaken must stay false on the unsupported path");
    }

    @Test
    void isEnteredReflectsSynchronizedState() {
        Object lock = new Object();
        assertFalse(Monitor.isEntered(lock));
        synchronized (lock) {
            assertTrue(Monitor.isEntered(lock));
        }
    }

    @Test
    void waitAndPulseHandOffInsideSynchronized() throws Exception {
        Object lock = new Object();
        AtomicBoolean produced = new AtomicBoolean(false);

        Thread consumer = new Thread(() -> {
            synchronized (lock) {
                while (!produced.get()) {
                    boolean notified = Monitor.wait(lock, 5_000);
                    assertTrue(notified, "wait should report notification before timeout");
                }
            }
        }, "monitor-test-consumer");
        consumer.start();

        Thread.sleep(100);
        synchronized (lock) {
            produced.set(true);
            Monitor.pulse(lock);
        }
        consumer.join(5_000);
        assertFalse(consumer.isAlive(), "pulse must wake the waiting consumer");
        assertTrue(produced.get());
    }

    @Test
    void timedWaitOutsideSynchronizedIsRejected() {
        Object lock = new Object();
        assertThrows(SynchronizationLockException.class, () -> Monitor.wait(lock, 10));
        assertThrows(SynchronizationLockException.class, () -> Monitor.pulse(lock));
        assertThrows(SynchronizationLockException.class, () -> Monitor.pulseAll(lock));
    }

    @Test
    void waitTimeoutReportsFalseApproximation() {
        Object lock = new Object();
        synchronized (lock) {
            long start = System.nanoTime();
            boolean notified = Monitor.wait(lock, 80);
            long elapsedMs = (System.nanoTime() - start) / 1_000_000L;
            assertFalse(notified, "elapsed wait must report the §10-1 timeout approximation");
            assertTrue(elapsedMs >= 70, "wait must actually block, not return immediately");
        }
    }

    @Test
    void nullObjectIsRejected() {
        assertThrows(NullPointerException.class, () -> Monitor.enter(null));
        assertThrows(NullPointerException.class, () -> Monitor.exit(null));
        assertThrows(NullPointerException.class, () -> Monitor.tryEnter((Object) null));
    }
}

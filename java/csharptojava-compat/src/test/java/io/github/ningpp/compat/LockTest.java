package io.github.ningpp.compat;

import java.util.concurrent.atomic.AtomicBoolean;

import org.junit.jupiter.api.Test;

import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertThrows;
import static org.junit.jupiter.api.Assertions.assertTrue;

class LockTest {

    @Test
    void enterExitIsReentrantAndFinallyShaped() {
        Lock csLock = new Lock();
        csLock.enter();
        assertTrue(csLock.isHeldByCurrentThread());
        csLock.enter();
        csLock.exit();
        csLock.exit();
        assertFalse(csLock.isHeldByCurrentThread());
    }

    @Test
    void exitWithoutEnterIsRejected() {
        assertThrows(SynchronizationLockException.class, () -> new Lock().exit());
    }

    @Test
    void tryEnterWithoutContentionSucceedsAndUnlockWorks() {
        Lock csLock = new Lock();
        assertTrue(csLock.tryEnter());
        csLock.exit();

        assertTrue(csLock.tryEnter(0));
        csLock.exit();

        assertTrue(csLock.tryEnter(50));
        csLock.exit();

        assertTrue(csLock.tryEnter(CSharpTimeSpan.fromMilliseconds(50)));
        csLock.exit();
    }

    @Test
    void tryEnterTimesOutWhenHeldByAnotherThread() throws Exception {
        Lock csLock = new Lock();
        AtomicBoolean entered = new AtomicBoolean(false);
        AtomicBoolean release = new AtomicBoolean(false);

        Thread owner = new Thread(() -> {
            csLock.enter();
            entered.set(true);
            try {
                while (!release.get()) {
                    Thread.sleep(5);
                }
            } catch (InterruptedException e) {
                Thread.currentThread().interrupt();
            } finally {
                csLock.exit();
            }
        }, "lock-test-owner");
        owner.start();
        while (!entered.get()) {
            Thread.sleep(5);
        }

        long start = System.nanoTime();
        assertFalse(csLock.tryEnter(60), "timed tryEnter must honestly report timeout");
        long elapsedMs = (System.nanoTime() - start) / 1_000_000L;
        assertTrue(elapsedMs >= 50, "tryEnter must really wait the timeout, not stub true");

        release.set(true);
        owner.join(5_000);
        assertFalse(owner.isAlive());
        assertTrue(csLock.tryEnter(1_000), "lock must be acquirable after release");
        csLock.exit();
    }

    @Test
    void negativeTimeoutIsRejected() {
        assertThrows(ArgumentOutOfRangeException.class, () -> new Lock().tryEnter(-2));
    }
}

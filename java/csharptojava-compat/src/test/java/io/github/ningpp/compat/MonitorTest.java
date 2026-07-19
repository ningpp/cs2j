package io.github.ningpp.compat;

import org.junit.jupiter.api.Test;

import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertTrue;

class MonitorTest {

    @Test
    void enterAndExitDoNotThrow() {
        Object lock = new Object();
        Monitor.enter(lock);
        Monitor.exit(lock);
    }

    @Test
    void enterWithHolderSetsLockTaken() {
        Object lock = new Object();
        BoolHolder holder = new BoolHolder();
        Monitor.enter(lock, holder);
        assertTrue(holder.value);
    }

    @Test
    void tryEnterReturnsTrue() {
        Object lock = new Object();
        assertTrue(Monitor.tryEnter(lock));
        assertTrue(Monitor.tryEnter(lock, 100));
    }

    @Test
    void isEnteredReflectsSynchronizedState() {
        Object lock = new Object();
        assertFalse(Monitor.isEntered(lock));
        synchronized (lock) {
            assertTrue(Monitor.isEntered(lock));
        }
    }
}

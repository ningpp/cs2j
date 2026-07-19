package io.github.ningpp.compat;

import org.junit.jupiter.api.Test;

import java.util.concurrent.atomic.AtomicBoolean;

import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertTrue;

class SpinWaitTest {

    @Test
    void spinOnceIncreasesCount() {
        SpinWait spin = new SpinWait();
        assertFalse(spin.nextSpinWillYield());
        spin.spinOnce();
        assertTrue(spin.getCount() > 0);
    }

    @Test
    void spinUntilCompletes() {
        AtomicBoolean flag = new AtomicBoolean(false);
        new Thread(() -> {
            try {
                Thread.sleep(50);
            } catch (InterruptedException ignored) {
            }
            flag.set(true);
        }).start();
        SpinWait.spinUntil(flag::get, 5000);
        assertTrue(flag.get());
    }
}

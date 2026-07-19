package io.github.ningpp.compat;

import java.util.function.Supplier;

/**
 * Compat stub for System.Threading.SpinWait.
 */
public final class SpinWait {

    private int count;

    public void spinOnce() {
        spinOnce(1);
    }

    public void spinOnce(int sleep1Threshold) {
        if (count > 10 || (sleep1Threshold >= 0 && count > sleep1Threshold)) {
            Thread.yield();
        } else {
            // Busy spin for a few iterations
            for (int i = 0; i < (1 << Math.min(count, 8)); i++) {
                Thread.onSpinWait();
            }
        }
        count++;
    }

    public void reset() {
        count = 0;
    }

    public int getCount() {
        return count;
    }

    public boolean nextSpinWillYield() {
        return count > 10;
    }

    public static void spinUntil(Supplier<Boolean> condition) {
        while (!Boolean.TRUE.equals(condition.get())) {
            Thread.yield();
        }
    }

    public static boolean spinUntil(Supplier<Boolean> condition, int millisecondsTimeout) {
        long deadline = System.currentTimeMillis() + millisecondsTimeout;
        while (!Boolean.TRUE.equals(condition.get())) {
            if (System.currentTimeMillis() > deadline) {
                return false;
            }
            Thread.yield();
        }
        return true;
    }
}

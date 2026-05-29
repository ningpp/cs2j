package io.github.ningpp.compat;

import org.junit.jupiter.api.Test;
import static org.junit.jupiter.api.Assertions.*;

class CSharpRandomTest {

    @Test
    void next_defaultConstructor_rangeValid() {
        CSharpRandom r = new CSharpRandom();
        for (int i = 0; i < 10000; i++) {
            int v = r.next();
            assertTrue(v >= 0 && v < Integer.MAX_VALUE,
                "next() returned " + v + " which is out of [0, MAX_VALUE)");
        }
    }

    @Test
    void next_seeded_repeatable() {
        CSharpRandom r1 = new CSharpRandom(42);
        CSharpRandom r2 = new CSharpRandom(42);
        for (int i = 0; i < 100; i++) {
            assertEquals(r1.next(), r2.next());
        }
    }

    @Test
    void next_maxValue1_alwaysZero() {
        CSharpRandom r = new CSharpRandom(123);
        for (int i = 0; i < 1000; i++) {
            assertEquals(0, r.next(1));
        }
    }

    @Test
    void next_maxValueNegative_throws() {
        CSharpRandom r = new CSharpRandom();
        assertThrows(IllegalArgumentException.class, () -> r.next(-1));
    }

    @Test
    void next_maxValueZero_throws() {
        CSharpRandom r = new CSharpRandom();
        assertThrows(IllegalArgumentException.class, () -> r.next(0));
    }

    @Test
    void next_maxValue_rangeValid() {
        CSharpRandom r = new CSharpRandom();
        int max = 100;
        for (int i = 0; i < 10000; i++) {
            int v = r.next(max);
            assertTrue(v >= 0 && v < max,
                "next(maxValue) returned " + v + " out of [0, " + max + ")");
        }
    }

    @Test
    void next_minMax_equal_returnsMin() {
        CSharpRandom r = new CSharpRandom();
        for (int i = 0; i < 100; i++) {
            int v = r.next(5, 5);
            assertEquals(5, v);
        }
    }

    @Test
    void next_minMax_inverted_throws() {
        CSharpRandom r = new CSharpRandom();
        assertThrows(IllegalArgumentException.class, () -> r.next(10, 5));
    }

    @Test
    void next_minMax_rangeValid() {
        CSharpRandom r = new CSharpRandom();
        int min = 10, max = 20;
        for (int i = 0; i < 10000; i++) {
            int v = r.next(min, max);
            assertTrue(v >= min && v < max,
                "next(min,max) returned " + v + " out of [10, 20)");
        }
    }

    @Test
    void next_minMax_largeRange() {
        CSharpRandom r = new CSharpRandom();
        int min = Integer.MIN_VALUE;
        int max = Integer.MAX_VALUE;
        for (int i = 0; i < 10000; i++) {
            int v = r.next(min, max);
            assertTrue(v >= min && v < max,
                "next(MIN,MAX) returned " + v + " out of range");
        }
    }

    @Test
    void nextDouble_rangeValid() {
        CSharpRandom r = new CSharpRandom();
        for (int i = 0; i < 10000; i++) {
            double v = r.nextDouble();
            assertTrue(v >= 0.0 && v < 1.0,
                "nextDouble() returned " + v + " out of [0.0, 1.0)");
        }
    }

    @Test
    void nextSingle_rangeValid() {
        CSharpRandom r = new CSharpRandom();
        for (int i = 0; i < 10000; i++) {
            float v = r.nextSingle();
            assertTrue(v >= 0.0f && v < 1.0f,
                "nextSingle() returned " + v + " out of [0.0f, 1.0f)");
        }
    }

    @Test
    void nextInt64_rangeValid() {
        CSharpRandom r = new CSharpRandom();
        for (int i = 0; i < 10000; i++) {
            long v = r.nextInt64();
            assertTrue(v >= 0L && v < Long.MAX_VALUE,
                "nextInt64() returned " + v + " out of [0, MAX_VALUE)");
        }
    }

    @Test
    void nextBytes_fillsBuffer() {
        CSharpRandom r = new CSharpRandom(42);
        byte[] buf = new byte[100];
        r.nextBytes(buf);
        boolean allZero = true;
        for (byte b : buf) {
            if (b != 0) allZero = false;
        }
        assertFalse(allZero, "nextBytes should produce non-trivial values");
    }

    @Test
    void nextBytes_nullBuffer_throws() {
        CSharpRandom r = new CSharpRandom();
        assertThrows(NullPointerException.class, () -> r.nextBytes(null));
    }
}

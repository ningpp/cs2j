package io.github.ningpp.compat;

public class CSharpRandom {

    private final java.util.Random random;

    public CSharpRandom() {
        this.random = new java.util.Random();
    }

    public CSharpRandom(int seed) {
        this.random = new java.util.Random(seed);
    }

    public int next() {
        return random.nextInt(Integer.MAX_VALUE);
    }

    public int next(int maxValue) {
        if (maxValue < 0) {
            throw new IllegalArgumentException(
                "maxValue must be positive, was: " + maxValue);
        }
        return random.nextInt(maxValue);
    }

    public int next(int minValue, int maxValue) {
        if (minValue > maxValue) {
            throw new IllegalArgumentException(
                "minValue must be less than or equal to maxValue. minValue: "
                + minValue + ", maxValue: " + maxValue);
        }
        long range = (long) maxValue - minValue;
        if (range <= Integer.MAX_VALUE) {
            return ((int) (sample() * range) + minValue);
        } else {
            return (int) ((long) (getSampleForLargeRange() * range) + minValue);
        }
    }

    protected double sample() {
        return random.nextDouble();
    }

    private double getSampleForLargeRange() {
        int result = next();
        boolean negative = (next() % 2 == 0);
        if (negative) {
            result = -result;
        }
        double d = result;
        d += (Integer.MAX_VALUE - 1);
        d /= 2.0 * (long) Integer.MAX_VALUE - 1;
        return d;
    }

    public double nextDouble() {
        return random.nextDouble();
    }

    public float nextSingle() {
        return (float) random.nextDouble();
    }

    public long nextInt64() {
        return (long) (sample() * Long.MAX_VALUE);
    }

    public int nextInt() {
        return next();
    }

    public int nextInt(int maxValue) {
        return next(maxValue);
    }

    public int nextInt(int minValue, int maxValue) {
        return next(minValue, maxValue);
    }

    public void nextBytes(byte[] buffer) {
        if (buffer == null) {
            throw new NullPointerException("buffer");
        }
        for (int i = 0; i < buffer.length; i++) {
            buffer[i] = (byte) next();
        }
    }
}

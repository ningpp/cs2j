package io.github.ningpp.compat;

public class CSharpRandom {

    private static final int MBIG = java.lang.Integer.MAX_VALUE;
    private static final int MSEED = 161803398;

    private final int[] seedArray = new int[56];
    private int inext;
    private int inextp;

    public CSharpRandom() {
        this((int) System.currentTimeMillis());
    }

    public CSharpRandom(int seed) {
        int subtraction = seed == java.lang.Integer.MIN_VALUE ? java.lang.Integer.MAX_VALUE : Math.abs(seed);
        int mj = MSEED - subtraction;
        seedArray[55] = mj;
        int mk = 1;

        for (int i = 1; i < 55; i++) {
            int ii = (21 * i) % 55;
            seedArray[ii] = mk;
            mk = mj - mk;
            if (mk < 0) {
                mk += MBIG;
            }
            mj = seedArray[ii];
        }

        for (int k = 1; k < 5; k++) {
            for (int i = 1; i < 56; i++) {
                seedArray[i] -= seedArray[1 + (i + 30) % 55];
                if (seedArray[i] < 0) {
                    seedArray[i] += MBIG;
                }
            }
        }

        inext = 0;
        inextp = 21;
    }

    public int next() {
        return internalSample();
    }

    public int next(int maxValue) {
        if (maxValue < 0) {
            throw new IllegalArgumentException(
                "maxValue must be positive, was: " + maxValue);
        }
        return (int) (sample() * maxValue);
    }

    public int next(int minValue, int maxValue) {
        if (minValue > maxValue) {
            throw new IllegalArgumentException(
                "minValue must be less than or equal to maxValue. minValue: "
                + minValue + ", maxValue: " + maxValue);
        }
        long range = (long) maxValue - minValue;
        if (range <= java.lang.Integer.MAX_VALUE) {
            return ((int) (sample() * range) + minValue);
        } else {
            return (int) ((long) (getSampleForLargeRange() * range) + minValue);
        }
    }

    protected double sample() {
        return internalSample() * (1.0 / MBIG);
    }

    private double getSampleForLargeRange() {
        int result = next();
        boolean negative = (next() % 2 == 0);
        if (negative) {
            result = -result;
        }
        double d = result;
        d += (java.lang.Integer.MAX_VALUE - 1);
        d /= 2.0 * (long) java.lang.Integer.MAX_VALUE - 1;
        return d;
    }

    public double nextDouble() {
        return sample();
    }

    public float nextSingle() {
        return (float) sample();
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

    private int internalSample() {
        int locINext = inext;
        int locINextp = inextp;

        if (++locINext >= 56) {
            locINext = 1;
        }
        if (++locINextp >= 56) {
            locINextp = 1;
        }

        int retVal = seedArray[locINext] - seedArray[locINextp];
        if (retVal == MBIG) {
            retVal--;
        }
        if (retVal < 0) {
            retVal += MBIG;
        }

        seedArray[locINext] = retVal;
        inext = locINext;
        inextp = locINextp;
        return retVal;
    }
}

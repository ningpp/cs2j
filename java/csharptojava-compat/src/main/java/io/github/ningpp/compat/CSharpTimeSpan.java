package io.github.ningpp.compat;

/**
 * C# System.TimeSpan compatibility class.
 * Internally stores ticks where 1 tick = 100 nanoseconds.
 */
public final class CSharpTimeSpan implements Comparable<CSharpTimeSpan> {

    public static final long TICKS_PER_MILLISECOND = 10000L;
    public static final long TICKS_PER_SECOND = 10000000L;
    public static final long TICKS_PER_MINUTE = 600000000L;
    public static final long TICKS_PER_HOUR = 36000000000L;
    public static final long TICKS_PER_DAY = 864000000000L;

    public static final CSharpTimeSpan ZERO = new CSharpTimeSpan(0L);
    public static final CSharpTimeSpan MAX_VALUE = new CSharpTimeSpan(Long.MAX_VALUE);
    public static final CSharpTimeSpan MIN_VALUE = new CSharpTimeSpan(Long.MIN_VALUE);

    private final long ticks;

    public CSharpTimeSpan(long ticks) {
        this.ticks = ticks;
    }

    public CSharpTimeSpan(int hours, int minutes, int seconds) {
        this.ticks = calculateTicks(0, hours, minutes, seconds, 0);
    }

    public CSharpTimeSpan(int days, int hours, int minutes, int seconds) {
        this.ticks = calculateTicks(days, hours, minutes, seconds, 0);
    }

    public CSharpTimeSpan(int days, int hours, int minutes, int seconds, int milliseconds) {
        this.ticks = calculateTicks(days, hours, minutes, seconds, milliseconds);
    }

    private static long calculateTicks(int days, int hours, int minutes, int seconds, int milliseconds) {
        long totalMs = ((long) days * 3600 * 24 + (long) hours * 3600 + (long) minutes * 60 + seconds) * 1000L + milliseconds;
        return totalMs * TICKS_PER_MILLISECOND;
    }

    // --- Instance Properties ---

    public long getTicks() { return ticks; }

    public int getDays() { return (int) (ticks / TICKS_PER_DAY); }
    public int getHours() { return (int) ((ticks % TICKS_PER_DAY) / TICKS_PER_HOUR); }
    public int getMinutes() { return (int) ((ticks % TICKS_PER_HOUR) / TICKS_PER_MINUTE); }
    public int getSeconds() { return (int) ((ticks % TICKS_PER_MINUTE) / TICKS_PER_SECOND); }
    public int getMilliseconds() { return (int) ((ticks % TICKS_PER_SECOND) / TICKS_PER_MILLISECOND); }

    public double getTotalDays() { return (double) ticks / TICKS_PER_DAY; }
    public double getTotalHours() { return (double) ticks / TICKS_PER_HOUR; }
    public double getTotalMinutes() { return (double) ticks / TICKS_PER_MINUTE; }
    public double getTotalSeconds() { return (double) ticks / TICKS_PER_SECOND; }
    public double getTotalMilliseconds() { return (double) ticks / TICKS_PER_MILLISECOND; }

    public CSharpTimeSpan duration() { return new CSharpTimeSpan(Math.abs(ticks)); }
    public CSharpTimeSpan negate() { return new CSharpTimeSpan(-ticks); }

    // --- Instance Methods ---

    public CSharpTimeSpan add(CSharpTimeSpan ts) { return new CSharpTimeSpan(ticks + ts.ticks); }
    public CSharpTimeSpan subtract(CSharpTimeSpan ts) { return new CSharpTimeSpan(ticks - ts.ticks); }

    // --- Static operator methods (C# operator overloads are static) ---

    public static CSharpTimeSpan add(CSharpTimeSpan left, CSharpTimeSpan right) { return left.add(right); }
    public static CSharpTimeSpan subtract(CSharpTimeSpan left, CSharpTimeSpan right) { return left.subtract(right); }

    @Override
    public int compareTo(CSharpTimeSpan other) { return Long.compare(ticks, other.ticks); }

    @Override
    public boolean equals(Object obj) {
        if (this == obj) return true;
        if (!(obj instanceof CSharpTimeSpan)) return false;
        return ticks == ((CSharpTimeSpan) obj).ticks;
    }

    @Override
    public int hashCode() { return Long.hashCode(ticks); }

    // --- toString matching C# format ---
    // C# format: [-][d.]hh:mm:ss[.fffffff]
    @Override
    public String toString() {
        StringBuilder sb = new StringBuilder();
        if (ticks < 0) {
            sb.append('-');
        }
        long absTicks = Math.abs(ticks);
        int days = (int) (absTicks / TICKS_PER_DAY);
        long remaining = absTicks % TICKS_PER_DAY;
        int hours = (int) (remaining / TICKS_PER_HOUR);
        remaining %= TICKS_PER_HOUR;
        int minutes = (int) (remaining / TICKS_PER_MINUTE);
        remaining %= TICKS_PER_MINUTE;
        int seconds = (int) (remaining / TICKS_PER_SECOND);
        long fracTicks = remaining % TICKS_PER_SECOND;

        if (days != 0) {
            sb.append(days).append('.');
        }
        sb.append(String.format("%02d:%02d:%02d", hours, minutes, seconds));
        if (fracTicks != 0) {
            // C# always outputs 7 fractional digits
            String frac = String.format("%07d", fracTicks);
            sb.append('.').append(frac);
        }
        return sb.toString();
    }

    // --- Static Factory Methods ---

    public static CSharpTimeSpan fromDays(double value) {
        return interval(value, TICKS_PER_DAY);
    }
    public static CSharpTimeSpan fromHours(double value) {
        return interval(value, TICKS_PER_HOUR);
    }
    public static CSharpTimeSpan fromMinutes(double value) {
        return interval(value, TICKS_PER_MINUTE);
    }
    public static CSharpTimeSpan fromSeconds(double value) {
        return interval(value, TICKS_PER_SECOND);
    }
    public static CSharpTimeSpan fromMilliseconds(double value) {
        return interval(value, TICKS_PER_MILLISECOND);
    }
    public static CSharpTimeSpan fromTicks(long value) {
        return new CSharpTimeSpan(value);
    }

    private static CSharpTimeSpan interval(double value, long scale) {
        if (Double.isNaN(value)) {
            throw new IllegalArgumentException("Value cannot be NaN.");
        }
        double ticks = value * scale;
        if (ticks > Long.MAX_VALUE || ticks < Long.MIN_VALUE) {
            throw new ArithmeticException("TimeSpan overflowed because the duration is too long.");
        }
        return new CSharpTimeSpan((long) ticks);
    }

    // --- Parse ---
    // C# format: [-][d.]hh:mm:ss[.fffffff]
    public static CSharpTimeSpan parse(String s) {
        try {
            boolean negative = false;
            String input = s.trim();
            if (input.startsWith("-")) {
                negative = true;
                input = input.substring(1);
            }

            // Split on first dot to determine if it's days separator or fractional seconds
            int firstDot = input.indexOf('.');
            int days = 0;
            String timePart;
            String fracPart = null;

            if (firstDot >= 0) {
                String beforeDot = input.substring(0, firstDot);
                String afterDot = input.substring(firstDot + 1);
                // If beforeDot contains ':', the dot is fractional seconds
                if (beforeDot.contains(":")) {
                    timePart = beforeDot;
                    fracPart = afterDot;
                } else {
                    // beforeDot is days count, afterDot must contain ':'
                    days = Integer.parseInt(beforeDot);
                    int nextDot = afterDot.indexOf('.');
                    if (nextDot >= 0) {
                        timePart = afterDot.substring(0, nextDot);
                        fracPart = afterDot.substring(nextDot + 1);
                    } else {
                        timePart = afterDot;
                    }
                }
            } else {
                timePart = input;
            }

            String[] parts = timePart.split(":");
            if (parts.length < 2 || parts.length > 3) {
                throw new IllegalArgumentException("Invalid TimeSpan format: " + s);
            }
            int hours = Integer.parseInt(parts[0]);
            int minutes = Integer.parseInt(parts[1]);
            int seconds = parts.length >= 3 ? Integer.parseInt(parts[2]) : 0;

            long fracTicks = 0;
            if (fracPart != null && !fracPart.isEmpty()) {
                // Pad or trim to 7 digits
                if (fracPart.length() < 7) {
                    fracPart = fracPart + "0000000".substring(0, 7 - fracPart.length());
                }
                fracPart = fracPart.substring(0, 7);
                fracTicks = Long.parseLong(fracPart);
            }

            long totalTicks = (long) days * TICKS_PER_DAY
                + (long) hours * TICKS_PER_HOUR
                + (long) minutes * TICKS_PER_MINUTE
                + (long) seconds * TICKS_PER_SECOND
                + fracTicks;

            return new CSharpTimeSpan(negative ? -totalTicks : totalTicks);
        } catch (Exception e) {
            if (e instanceof IllegalArgumentException) throw (IllegalArgumentException) e;
            throw new IllegalArgumentException("Invalid TimeSpan format: " + s, e);
        }
    }

    public static boolean tryParse(String s, ObjectHolder<CSharpTimeSpan> result) {
        try {
            result.value = parse(s);
            return true;
        } catch (Exception e) {
            result.value = ZERO;
            return false;
        }
    }
}

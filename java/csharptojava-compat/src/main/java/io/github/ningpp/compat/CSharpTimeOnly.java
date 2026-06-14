package io.github.ningpp.compat;

import java.time.LocalTime;

/**
 * C# System.TimeOnly compatibility class.
 * Internally stores ticks (100ns resolution) since midnight.
 */
public final class CSharpTimeOnly implements Comparable<CSharpTimeOnly> {

    public static final CSharpTimeOnly MIN_VALUE = new CSharpTimeOnly(0L);
    public static final CSharpTimeOnly MAX_VALUE = new CSharpTimeOnly(863999999999L);

    private static final long TICKS_PER_MILLISECOND = 10000L;
    private static final long TICKS_PER_SECOND = 10000000L;
    private static final long TICKS_PER_MINUTE = 600000000L;
    private static final long TICKS_PER_HOUR = 36000000000L;
    private static final long TICKS_PER_DAY = 864000000000L;

    private final long ticks;

    public CSharpTimeOnly(long ticks) {
        if (ticks < 0 || ticks >= TICKS_PER_DAY) {
            throw new IllegalArgumentException("ticks must be between 0 and 863999999999.");
        }
        this.ticks = ticks;
    }

    public CSharpTimeOnly(int hour, int minute, int second) {
        this((long) hour * TICKS_PER_HOUR + (long) minute * TICKS_PER_MINUTE + (long) second * TICKS_PER_SECOND);
    }

    public CSharpTimeOnly(int hour, int minute, int second, int millisecond) {
        this((long) hour * TICKS_PER_HOUR + (long) minute * TICKS_PER_MINUTE
            + (long) second * TICKS_PER_SECOND + (long) millisecond * TICKS_PER_MILLISECOND);
    }

    // --- Properties ---

    public long getTicks() { return ticks; }
    public int getHour() { return (int) (ticks / TICKS_PER_HOUR); }
    public int getMinute() { return (int) ((ticks % TICKS_PER_HOUR) / TICKS_PER_MINUTE); }
    public int getSecond() { return (int) ((ticks % TICKS_PER_MINUTE) / TICKS_PER_SECOND); }
    public int getMillisecond() { return (int) ((ticks % TICKS_PER_SECOND) / TICKS_PER_MILLISECOND); }

    // --- Add methods ---

    public CSharpTimeOnly add(CSharpTimeSpan ts) {
        long newTicks = (ticks + ts.getTicks()) % TICKS_PER_DAY;
        if (newTicks < 0) newTicks += TICKS_PER_DAY;
        return new CSharpTimeOnly(newTicks);
    }

    public CSharpTimeOnly addHours(double hours) {
        long addTicks = (long) (hours * TICKS_PER_HOUR);
        long newTicks = (ticks + addTicks) % TICKS_PER_DAY;
        if (newTicks < 0) newTicks += TICKS_PER_DAY;
        return new CSharpTimeOnly(newTicks);
    }

    public CSharpTimeOnly addMinutes(double minutes) {
        long addTicks = (long) (minutes * TICKS_PER_MINUTE);
        long newTicks = (ticks + addTicks) % TICKS_PER_DAY;
        if (newTicks < 0) newTicks += TICKS_PER_DAY;
        return new CSharpTimeOnly(newTicks);
    }

    // --- Static Methods ---

    public static CSharpTimeOnly fromDateTime(CSharpDateTime dateTime) {
        return new CSharpTimeOnly(dateTime.getTimeOfDay().getTicks());
    }

    public static CSharpTimeOnly fromTimeSpan(CSharpTimeSpan ts) {
        long t = ts.getTicks() % TICKS_PER_DAY;
        if (t < 0) t += TICKS_PER_DAY;
        return new CSharpTimeOnly(t);
    }

    public static CSharpTimeOnly parse(String s) {
        LocalTime lt = LocalTime.parse(s.trim());
        return new CSharpTimeOnly(lt.toNanoOfDay() / 100);
    }

    public static boolean tryParse(String s, ObjectHolder<CSharpTimeOnly> result) {
        try {
            result.value = parse(s);
            return true;
        } catch (Exception e) {
            result.value = MIN_VALUE;
            return false;
        }
    }

    // --- toString ---
    // C# TimeOnly.ToString() default format is short time: "h:mm" (no seconds)
    // This matches C# behavior where default ToString uses short time pattern
    @Override
    public String toString() {
        int h = getHour();
        int m = getMinute();
        return String.format("%d:%02d", h, m);
    }

    @Override
    public int compareTo(CSharpTimeOnly other) { return Long.compare(ticks, other.ticks); }

    @Override
    public boolean equals(Object obj) {
        if (this == obj) return true;
        if (!(obj instanceof CSharpTimeOnly)) return false;
        return ticks == ((CSharpTimeOnly) obj).ticks;
    }

    @Override
    public int hashCode() { return Long.hashCode(ticks); }
}

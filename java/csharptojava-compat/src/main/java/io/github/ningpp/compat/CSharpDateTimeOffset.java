package io.github.ningpp.compat;

import java.time.*;

/**
 * C# System.DateTimeOffset compatibility class.
 * Represents a point in time with an offset from UTC.
 */
public final class CSharpDateTimeOffset implements Comparable<CSharpDateTimeOffset> {

    private final CSharpDateTime dateTime;
    private final CSharpTimeSpan offset;

    public static final CSharpDateTimeOffset MIN_VALUE =
        new CSharpDateTimeOffset(CSharpDateTime.MIN_VALUE, CSharpTimeSpan.ZERO);
    public static final CSharpDateTimeOffset MAX_VALUE =
        new CSharpDateTimeOffset(CSharpDateTime.MAX_VALUE, CSharpTimeSpan.ZERO);

    public CSharpDateTimeOffset(CSharpDateTime dateTime, CSharpTimeSpan offset) {
        this.dateTime = dateTime;
        this.offset = offset;
    }

    public CSharpDateTimeOffset(int year, int month, int day, int hour, int minute, int second, int millisecond, CSharpTimeSpan offset) {
        this.dateTime = new CSharpDateTime(year, month, day, hour, minute, second, millisecond, DateTimeKind.Unspecified);
        this.offset = offset;
    }

    public CSharpDateTimeOffset(int year, int month, int day, int hour, int minute, int second, CSharpTimeSpan offset) {
        this(year, month, day, hour, minute, second, 0, offset);
    }

    public CSharpDateTimeOffset(CSharpDateTime dateTime) {
        this.dateTime = dateTime;
        this.offset = CSharpTimeSpan.ZERO;
    }

    // --- Properties ---

    public CSharpDateTime getDateTime() { return dateTime; }
    public CSharpTimeSpan getOffset() { return offset; }
    public int getYear() { return dateTime.getYear(); }
    public int getMonth() { return dateTime.getMonth(); }
    public int getDay() { return dateTime.getDay(); }
    public int getHour() { return dateTime.getHour(); }
    public int getMinute() { return dateTime.getMinute(); }
    public int getSecond() { return dateTime.getSecond(); }
    public int getMillisecond() { return dateTime.getMillisecond(); }
    public DayOfWeek getDayOfWeek() { return dateTime.getDayOfWeek(); }
    public int getDayOfYear() { return dateTime.getDayOfYear(); }

    public CSharpDateTime getLocalDateTime() {
        return dateTime;
    }

    public CSharpDateTime getUtcDateTime() {
        return dateTime.subtract(offset);
    }

    public long getTicks() { return dateTime.getTicks(); }
    public long getUtcTicks() { return dateTime.subtract(offset).getTicks(); }

    // --- Add/Subtract ---

    public CSharpDateTimeOffset add(CSharpTimeSpan ts) {
        return new CSharpDateTimeOffset(dateTime.add(ts), offset);
    }

    public CSharpDateTimeOffset addDays(double days) {
        return new CSharpDateTimeOffset(dateTime.addDays(days), offset);
    }

    public CSharpDateTimeOffset addHours(double hours) {
        return new CSharpDateTimeOffset(dateTime.addHours(hours), offset);
    }

    public CSharpDateTimeOffset addMinutes(double minutes) {
        return new CSharpDateTimeOffset(dateTime.addMinutes(minutes), offset);
    }

    public CSharpDateTimeOffset addMonths(int months) {
        return new CSharpDateTimeOffset(dateTime.addMonths(months), offset);
    }

    public CSharpDateTimeOffset addSeconds(double seconds) {
        return new CSharpDateTimeOffset(dateTime.addSeconds(seconds), offset);
    }

    public CSharpDateTimeOffset addYears(int years) {
        return new CSharpDateTimeOffset(dateTime.addYears(years), offset);
    }

    public CSharpDateTimeOffset subtract(CSharpTimeSpan ts) {
        return new CSharpDateTimeOffset(dateTime.subtract(ts), offset);
    }

    public CSharpTimeSpan subtract(CSharpDateTimeOffset other) {
        return new CSharpTimeSpan(getUtcTicks() - other.getUtcTicks());
    }

    // --- Static Methods ---

    public static CSharpDateTimeOffset getNow() {
        OffsetDateTime odt = OffsetDateTime.now();
        CSharpDateTime dt = new CSharpDateTime(odt.getYear(), odt.getMonthValue(), odt.getDayOfMonth(),
            odt.getHour(), odt.getMinute(), odt.getSecond(), odt.getNano() / 1_000_000, DateTimeKind.Local);
        int offsetSeconds = odt.getOffset().getTotalSeconds();
        CSharpTimeSpan offset = CSharpTimeSpan.fromSeconds(offsetSeconds);
        return new CSharpDateTimeOffset(dt, offset);
    }

    public static CSharpDateTimeOffset getUtcNow() {
        Instant instant = Instant.now();
        OffsetDateTime odt = instant.atOffset(ZoneOffset.UTC);
        CSharpDateTime dt = new CSharpDateTime(odt.getYear(), odt.getMonthValue(), odt.getDayOfMonth(),
            odt.getHour(), odt.getMinute(), odt.getSecond(), odt.getNano() / 1_000_000, DateTimeKind.Utc);
        return new CSharpDateTimeOffset(dt, CSharpTimeSpan.ZERO);
    }

    // --- toString ---
    // C# format: "M/d/yyyy h:mm:ss +HH:mm:ss"
    @Override
    public String toString() {
        String dtStr = dateTime.toString();
        long offsetTicks = offset.getTicks();
        if (offsetTicks == 0) {
            return dtStr + " +00:00:00";
        }
        boolean negative = offsetTicks < 0;
        long absTicks = Math.abs(offsetTicks);
        int h = (int) (absTicks / CSharpTimeSpan.TICKS_PER_HOUR);
        long rem = absTicks % CSharpTimeSpan.TICKS_PER_HOUR;
        int m = (int) (rem / CSharpTimeSpan.TICKS_PER_MINUTE);
        int s = (int) (rem % CSharpTimeSpan.TICKS_PER_MINUTE / CSharpTimeSpan.TICKS_PER_SECOND);
        return dtStr + " " + (negative ? "-" : "+") + String.format("%02d:%02d:%02d", h, m, s);
    }

    @Override
    public int compareTo(CSharpDateTimeOffset other) {
        return Long.compare(getUtcTicks(), other.getUtcTicks());
    }

    @Override
    public boolean equals(Object obj) {
        if (this == obj) return true;
        if (!(obj instanceof CSharpDateTimeOffset)) return false;
        return getUtcTicks() == ((CSharpDateTimeOffset) obj).getUtcTicks();
    }

    @Override
    public int hashCode() { return Long.hashCode(getUtcTicks()); }
}

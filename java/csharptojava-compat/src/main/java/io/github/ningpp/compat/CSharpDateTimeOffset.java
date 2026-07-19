package io.github.ningpp.compat;

import java.time.*;
import java.time.format.DateTimeFormatter;

/**
 * C# System.DateTimeOffset compatibility class.
 * Represents a point in time with an offset from UTC.
 */
public final class CSharpDateTimeOffset implements Comparable<CSharpDateTimeOffset> {

    private static final long MAX_OFFSET_TICKS = 14L * CSharpTimeSpan.TICKS_PER_HOUR;

    private final CSharpDateTime dateTime;
    private final CSharpTimeSpan offset;

    public static final CSharpDateTimeOffset MIN_VALUE =
        new CSharpDateTimeOffset(CSharpDateTime.MIN_VALUE, CSharpTimeSpan.ZERO);
    public static final CSharpDateTimeOffset MAX_VALUE =
        new CSharpDateTimeOffset(CSharpDateTime.MAX_VALUE, CSharpTimeSpan.ZERO);

    public CSharpDateTimeOffset(CSharpDateTime dateTime, CSharpTimeSpan offset) {
        validateOffset(dateTime, offset);
        this.dateTime = dateTime;
        this.offset = offset;
    }

    public CSharpDateTimeOffset(int year, int month, int day, int hour, int minute, int second, int millisecond, CSharpTimeSpan offset) {
        this.dateTime = new CSharpDateTime(year, month, day, hour, minute, second, millisecond, DateTimeKind.Unspecified);
        validateOffset(this.dateTime, offset);
        this.offset = offset;
    }

    public CSharpDateTimeOffset(int year, int month, int day, int hour, int minute, int second, CSharpTimeSpan offset) {
        this(year, month, day, hour, minute, second, 0, offset);
    }

    public CSharpDateTimeOffset(int year, int month, int day, int hour, int minute, int second, int millisecond, DateTimeKind kind) {
        this.dateTime = new CSharpDateTime(year, month, day, hour, minute, second, millisecond, kind);
        this.offset = offsetFromKind(kind);
    }

    public CSharpDateTimeOffset(int year, int month, int day, int hour, int minute, int second, DateTimeKind kind) {
        this(year, month, day, hour, minute, second, 0, kind);
    }

    public CSharpDateTimeOffset(CSharpDateTime dateTime) {
        this.dateTime = dateTime;
        this.offset = offsetFromKind(dateTime.getKind());
    }

    public static CSharpDateTimeOffset toCSharpDateTimeOffset(CSharpDateTime dateTime) {
        return new CSharpDateTimeOffset(dateTime);
    }

    private static CSharpTimeSpan offsetFromKind(DateTimeKind kind) {
        if (kind == DateTimeKind.Utc) {
            return CSharpTimeSpan.ZERO;
        }
        // Local and Unspecified both use the local timezone offset, matching .NET behavior
        int offsetSeconds = OffsetDateTime.now().getOffset().getTotalSeconds();
        return CSharpTimeSpan.fromSeconds(offsetSeconds);
    }

    private static void validateOffset(CSharpDateTime dateTime, CSharpTimeSpan offset) {
        if (dateTime == null) {
            throw new NullPointerException("dateTime");
        }
        if (offset == null) {
            throw new NullPointerException("offset");
        }

        long offsetTicks = offset.getTicks();
        if (Math.abs(offsetTicks) > MAX_OFFSET_TICKS) {
            throw new IllegalArgumentException("Offset must be within plus or minus 14 hours.");
        }
        if (offsetTicks % CSharpTimeSpan.TICKS_PER_MINUTE != 0) {
            throw new IllegalArgumentException("Offset must be specified in whole minutes.");
        }

        long utcTicks;
        try {
            utcTicks = Math.subtractExact(dateTime.getTicks(), offsetTicks);
        } catch (ArithmeticException ex) {
            throw new IllegalArgumentException("The UTC time represented by dateTime and offset is out of range.", ex);
        }

        if (utcTicks < CSharpDateTime.MIN_VALUE.getTicks() || utcTicks > CSharpDateTime.MAX_VALUE.getTicks()) {
            throw new IllegalArgumentException("The UTC time represented by dateTime and offset is out of range.");
        }
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

    public CSharpDateTimeOffset addTicks(long value) {
        return new CSharpDateTimeOffset(dateTime.addTicks(value), offset);
    }

    public CSharpDateTimeOffset subtract(CSharpTimeSpan ts) {
        return new CSharpDateTimeOffset(dateTime.subtract(ts), offset);
    }

    public CSharpTimeSpan subtract(CSharpDateTimeOffset other) {
        return new CSharpTimeSpan(getUtcTicks() - other.getUtcTicks());
    }

    // --- Static operator methods (C# operator overloads are static) ---

    public static CSharpDateTimeOffset subtract(CSharpDateTimeOffset left, CSharpTimeSpan right) {
        return left.subtract(right);
    }

    public static CSharpTimeSpan subtract(CSharpDateTimeOffset left, CSharpDateTimeOffset right) {
        return left.subtract(right);
    }

    public static CSharpDateTimeOffset add(CSharpDateTimeOffset left, CSharpTimeSpan right) {
        return left.add(right);
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

    public static CSharpDateTimeOffset parse(String s) {
        String trimmed = s.trim();
        try {
            OffsetDateTime odt;
            try {
                odt = OffsetDateTime.parse(trimmed);
            } catch (Exception ignored) {
                LocalDateTime ldt = trimmed.contains("T")
                    ? LocalDateTime.parse(trimmed)
                    : trimmed.contains(":")
                        ? LocalDateTime.parse(trimmed, DateTimeFormatter.ISO_LOCAL_DATE_TIME)
                        : LocalDate.parse(trimmed).atStartOfDay();
                odt = OffsetDateTime.of(ldt, ZoneOffset.UTC);
            }

            CSharpDateTime dt = new CSharpDateTime(odt.getYear(), odt.getMonthValue(), odt.getDayOfMonth(),
                odt.getHour(), odt.getMinute(), odt.getSecond(), odt.getNano() / 1_000_000, DateTimeKind.Unspecified);
            return new CSharpDateTimeOffset(dt, CSharpTimeSpan.fromSeconds(odt.getOffset().getTotalSeconds()));
        } catch (Exception e) {
            throw new IllegalArgumentException("Invalid DateTimeOffset format: " + s, e);
        }
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

    public String toString(String format) {
        if (format == null || format.isEmpty()) {
            return toString();
        }

        if ("o".equals(format) || "O".equals(format)) {
            long offsetTicks = offset.getTicks();
            long offsetSeconds = offsetTicks / CSharpTimeSpan.TICKS_PER_SECOND;
            ZoneOffset zoneOffset = ZoneOffset.ofTotalSeconds((int) offsetSeconds);
            LocalDateTime ldt = ticksToLocalDateTime(dateTime.getTicks());
            OffsetDateTime odt = OffsetDateTime.of(ldt, zoneOffset);
            return odt.format(DateTimeFormatter.ofPattern("yyyy-MM-dd'T'HH:mm:ss.SSSSSSSXXX"));
        }

        return ticksToLocalDateTime(dateTime.getTicks()).format(DateTimeFormatter.ofPattern(format));
    }

    public String toString(String format, IFormatProvider provider) {
        return toString(format);
    }

    public static CSharpDateTimeOffset parseExact(String s, String format) {
        try {
            return parse(s);
        } catch (Exception e) {
            throw new IllegalArgumentException("Invalid DateTimeOffset format: " + s, e);
        }
    }

    public static CSharpDateTimeOffset parseExact(String s, String[] formats) {
        for (String fmt : formats) {
            try {
                return parseExact(s, fmt);
            } catch (Exception ignored) {
            }
        }
        throw new IllegalArgumentException("Invalid DateTimeOffset format: " + s);
    }

    public static CSharpDateTimeOffset parseExact(String s, String format, IFormatProvider provider) {
        return parseExact(s, format, provider, 0);
    }

    public static CSharpDateTimeOffset parseExact(String s, String format, IFormatProvider provider, int style) {
        try {
            return parse(s);
        } catch (Exception e) {
            throw new IllegalArgumentException("Invalid DateTimeOffset format: " + s, e);
        }
    }

    public static CSharpDateTimeOffset parseExact(String s, String[] formats, IFormatProvider provider, int style) {
        for (String fmt : formats) {
            try {
                return parseExact(s, fmt, provider, style);
            } catch (Exception ignored) {
            }
        }
        throw new IllegalArgumentException("Invalid DateTimeOffset format: " + s);
    }

    private static LocalDateTime ticksToLocalDateTime(long ticks) {
        long javaEpochTicks = ticks - 621355968000000000L;
        long epochDay = javaEpochTicks / CSharpTimeSpan.TICKS_PER_DAY;
        long nanoOfDay = (javaEpochTicks % CSharpTimeSpan.TICKS_PER_DAY) * 100;
        if (nanoOfDay < 0) {
            epochDay--;
            nanoOfDay += 24L * 3600 * 1_000_000_000L;
        }

        return LocalDateTime.of(LocalDate.ofEpochDay(epochDay), LocalTime.ofNanoOfDay(nanoOfDay));
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

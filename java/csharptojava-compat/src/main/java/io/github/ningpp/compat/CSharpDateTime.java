package io.github.ningpp.compat;

import java.time.*;
import java.time.format.DateTimeFormatter;
import java.time.temporal.ChronoField;

/**
 * C# System.DateTime compatibility class.
 * Internally stores ticks (100ns resolution) since 0001-01-01 00:00:00.
 */
public final class CSharpDateTime implements Comparable<CSharpDateTime> {

    // Java epoch (1970-01-01) in C# ticks
    private static final long EPOCH_DIFF_TICKS = 621355968000000000L;

    public static final CSharpDateTime MIN_VALUE = new CSharpDateTime(0L);
    public static final CSharpDateTime MAX_VALUE = new CSharpDateTime(3155378975999999999L);

    private final long ticks;
    private final DateTimeKind kind;

    public CSharpDateTime(long ticks) {
        if (ticks < 0 || ticks > 3155378975999999999L) {
            throw new IllegalArgumentException("Ticks must be between 0 and 3155378975999999999.");
        }
        this.ticks = ticks;
        this.kind = DateTimeKind.Unspecified;
    }

    public CSharpDateTime(long ticks, DateTimeKind kind) {
        if (ticks < 0 || ticks > 3155378975999999999L) {
            throw new IllegalArgumentException("Ticks must be between 0 and 3155378975999999999.");
        }
        this.ticks = ticks;
        this.kind = kind;
    }

    public CSharpDateTime(int year, int month, int day) {
        this(year, month, day, 0, 0, 0, 0, DateTimeKind.Unspecified);
    }

    public CSharpDateTime(int year, int month, int day, int hour, int minute, int second) {
        this(year, month, day, hour, minute, second, 0, DateTimeKind.Unspecified);
    }

    public CSharpDateTime(int year, int month, int day, int hour, int minute, int second, int millisecond) {
        this(year, month, day, hour, minute, second, millisecond, DateTimeKind.Unspecified);
    }

    public CSharpDateTime(int year, int month, int day, int hour, int minute, int second, int millisecond, DateTimeKind kind) {
        LocalDateTime ldt = LocalDateTime.of(year, month, day, hour, minute, second, millisecond * 1_000_000);
        this.ticks = ldtToTicks(ldt);
        this.kind = kind;
    }

    // --- Conversion helpers ---

    private static long ldtToTicks(LocalDateTime ldt) {
        long epochDay = ldt.toLocalDate().toEpochDay();
        long nanoOfDay = ldt.toLocalTime().toNanoOfDay();
        return epochDay * CSharpTimeSpan.TICKS_PER_DAY + nanoOfDay / 100 + EPOCH_DIFF_TICKS;
    }

    private LocalDateTime ticksToLdt() {
        long javaEpochTicks = ticks - EPOCH_DIFF_TICKS;
        long epochDay = javaEpochTicks / CSharpTimeSpan.TICKS_PER_DAY;
        long nanoOfDay = (javaEpochTicks % CSharpTimeSpan.TICKS_PER_DAY) * 100;
        if (nanoOfDay < 0) {
            epochDay--;
            nanoOfDay += 24L * 3600 * 1_000_000_000L;
        }
        return LocalDateTime.of(LocalDate.ofEpochDay(epochDay), LocalTime.ofNanoOfDay(nanoOfDay));
    }

    // --- Instance Properties ---

    public long getTicks() { return ticks; }
    public DateTimeKind getKind() { return kind; }
    public int getYear() { return ticksToLdt().getYear(); }
    public int getMonth() { return ticksToLdt().getMonthValue(); }
    public int getDay() { return ticksToLdt().getDayOfMonth(); }
    public int getHour() { return ticksToLdt().getHour(); }
    public int getMinute() { return ticksToLdt().getMinute(); }
    public int getSecond() { return ticksToLdt().getSecond(); }
    public int getMillisecond() { return ticksToLdt().getNano() / 1_000_000; }
    public DayOfWeek getDayOfWeek() { return DayOfWeek.fromJava(ticksToLdt().getDayOfWeek()); }
    public int getDayOfYear() { return ticksToLdt().getDayOfYear(); }

    public CSharpDateTime getDate() {
        LocalDateTime ldt = ticksToLdt();
        return new CSharpDateTime(ldtToTicks(ldt.toLocalDate().atStartOfDay()), kind);
    }

    public CSharpTimeSpan getTimeOfDay() {
        LocalDateTime ldt = ticksToLdt();
        long nanoOfDay = ldt.toLocalTime().toNanoOfDay();
        return new CSharpTimeSpan(nanoOfDay / 100);
    }

    // --- Add methods ---

    public CSharpDateTime add(CSharpTimeSpan ts) {
        return new CSharpDateTime(ticks + ts.getTicks(), kind);
    }

    public CSharpDateTime addDays(double value) {
        return new CSharpDateTime(ticks + (long)(value * CSharpTimeSpan.TICKS_PER_DAY), kind);
    }

    public CSharpDateTime addHours(double value) {
        return new CSharpDateTime(ticks + (long)(value * CSharpTimeSpan.TICKS_PER_HOUR), kind);
    }

    public CSharpDateTime addMinutes(double value) {
        return new CSharpDateTime(ticks + (long)(value * CSharpTimeSpan.TICKS_PER_MINUTE), kind);
    }

    public CSharpDateTime addSeconds(double value) {
        return new CSharpDateTime(ticks + (long)(value * CSharpTimeSpan.TICKS_PER_SECOND), kind);
    }

    public CSharpDateTime addMilliseconds(double value) {
        return new CSharpDateTime(ticks + (long)(value * CSharpTimeSpan.TICKS_PER_MILLISECOND), kind);
    }

    public CSharpDateTime addMonths(int months) {
        LocalDateTime ldt = ticksToLdt().plusMonths(months);
        return new CSharpDateTime(ldtToTicks(ldt), kind);
    }

    public CSharpDateTime addYears(int years) {
        LocalDateTime ldt = ticksToLdt().plusYears(years);
        return new CSharpDateTime(ldtToTicks(ldt), kind);
    }

    public CSharpDateTime addTicks(long value) {
        return new CSharpDateTime(ticks + value, kind);
    }

    // --- Subtract ---

    public CSharpTimeSpan subtract(CSharpDateTime dt) {
        return new CSharpTimeSpan(ticks - dt.ticks);
    }

    public CSharpDateTime subtract(CSharpTimeSpan ts) {
        return new CSharpDateTime(ticks - ts.getTicks(), kind);
    }

    // --- Static Methods ---

    public static CSharpDateTime getNow() {
        return new CSharpDateTime(ldtToTicks(LocalDateTime.now()), DateTimeKind.Local);
    }

    public static CSharpDateTime getUtcNow() {
        return new CSharpDateTime(ldtToTicks(LocalDateTime.ofInstant(Instant.now(), ZoneOffset.UTC)), DateTimeKind.Utc);
    }

    public static CSharpDateTime getToday() {
        return getNow().getDate();
    }

    public static int daysInMonth(int year, int month) {
        return YearMonth.of(year, month).lengthOfMonth();
    }

    public static boolean isLeapYear(int year) {
        return Year.of(year).isLeap();
    }

    public static CSharpDateTime parse(String s) {
        String trimmed = s.trim();
        try {
            LocalDateTime ldt;
            if (trimmed.contains("T")) {
                ldt = LocalDateTime.parse(trimmed);
            } else if (trimmed.contains(":")) {
                ldt = LocalDateTime.parse(trimmed, DateTimeFormatter.ISO_LOCAL_DATE_TIME);
            } else {
                ldt = LocalDate.parse(trimmed).atStartOfDay();
            }
            return new CSharpDateTime(ldtToTicks(ldt), DateTimeKind.Unspecified);
        } catch (Exception e) {
            throw new IllegalArgumentException("Invalid DateTime format: " + s, e);
        }
    }

    public static boolean tryParse(String s, ObjectHolder<CSharpDateTime> result) {
        try {
            result.value = parse(s);
            return true;
        } catch (Exception e) {
            result.value = MIN_VALUE;
            return false;
        }
    }

    // --- toString ---
    // Match C# format: "M/d/yyyy h:mm:ss" (short date + long time)
    @Override
    public String toString() {
        LocalDateTime ldt = ticksToLdt();
        int h = ldt.getHour();
        int m = ldt.getMinute();
        int s = ldt.getSecond();
        // C# format: M/d/yyyy h:mm:ss (no leading zero for month/day, 24-hour)
        return ldt.getMonthValue() + "/" + ldt.getDayOfMonth() + "/" + ldt.getYear()
            + " " + h + ":" + String.format("%02d", m) + ":" + String.format("%02d", s);
    }

    public String toString(String format) {
        if (format == null || format.isEmpty()) {
            return toString();
        }

        LocalDateTime ldt = ticksToLdt();
        if ("o".equals(format) || "O".equals(format)) {
            String base = ldt.format(DateTimeFormatter.ofPattern("yyyy-MM-dd'T'HH:mm:ss"));
            String fraction = String.format("%07d", ldt.getNano() / 100);
            return switch (kind) {
                case Utc -> base + "." + fraction + "Z";
                case Local -> base + "." + fraction + getLocalOffsetText();
                default -> base + "." + fraction;
            };
        }

        return ldt.format(DateTimeFormatter.ofPattern(format));
    }

    public String toString(String format, IFormatProvider provider) {
        return toString(format);
    }

    public static CSharpDateTime parseExact(String s, String format) {
        try {
            return parse(s);
        } catch (Exception e) {
            throw new IllegalArgumentException("Invalid DateTime format: " + s, e);
        }
    }

    public static CSharpDateTime parseExact(String s, String[] formats) {
        for (String fmt : formats) {
            try {
                return parseExact(s, fmt);
            } catch (Exception ignored) {
            }
        }
        throw new IllegalArgumentException("Invalid DateTime format: " + s);
    }

    public static CSharpDateTime parseExact(String s, String format, IFormatProvider provider) {
        return parseExact(s, format, provider, 0);
    }

    public static CSharpDateTime parseExact(String s, String format, IFormatProvider provider, int style) {
        try {
            return parse(s);
        } catch (Exception e) {
            throw new IllegalArgumentException("Invalid DateTime format: " + s, e);
        }
    }

    public static CSharpDateTime parseExact(String s, String[] formats, IFormatProvider provider, int style) {
        for (String fmt : formats) {
            try {
                return parseExact(s, fmt, provider, style);
            } catch (Exception ignored) {
            }
        }
        throw new IllegalArgumentException("Invalid DateTime format: " + s);
    }

    private static String getLocalOffsetText() {
        ZoneOffset offset = OffsetDateTime.now().getOffset();
        int totalSeconds = offset.getTotalSeconds();
        char sign = totalSeconds < 0 ? '-' : '+';
        int absSeconds = Math.abs(totalSeconds);
        int hours = absSeconds / 3600;
        int minutes = (absSeconds % 3600) / 60;
        return String.format("%c%02d:%02d", sign, hours, minutes);
    }

    // --- Comparable / equals / hashCode ---

    @Override
    public int compareTo(CSharpDateTime other) { return Long.compare(ticks, other.ticks); }

    @Override
    public boolean equals(Object obj) {
        if (this == obj) return true;
        if (!(obj instanceof CSharpDateTime)) return false;
        return ticks == ((CSharpDateTime) obj).ticks;
    }

    @Override
    public int hashCode() { return Long.hashCode(ticks); }
}

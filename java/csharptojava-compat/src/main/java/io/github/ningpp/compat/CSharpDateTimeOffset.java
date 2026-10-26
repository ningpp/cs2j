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

    /** PascalCase aliases for converter output (C# DateTimeOffset.MaxValue). */
    public static final CSharpDateTimeOffset MinValue = MIN_VALUE;
    public static final CSharpDateTimeOffset MaxValue = MAX_VALUE;

    /** Default (unix epoch) offset — mirrors default(DateTimeOffset). */
    public CSharpDateTimeOffset() {
        this.dateTime = CSharpDateTime.MIN_VALUE;
        this.offset = CSharpTimeSpan.ZERO;
    }

    public CSharpDateTimeOffset(CSharpDateTime dateTime, CSharpTimeSpan offset) {
        validateOffset(dateTime, offset);
        this.dateTime = dateTime;
        this.offset = offset;
    }

    public CSharpDateTimeOffset(long ticks, CSharpTimeSpan offset) {
        this(new CSharpDateTime(ticks), offset);
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
        this.offset = offsetFromKind(kind, this.dateTime);
    }

    public CSharpDateTimeOffset(int year, int month, int day, int hour, int minute, int second, DateTimeKind kind) {
        this(year, month, day, hour, minute, second, 0, kind);
    }

    public CSharpDateTimeOffset(CSharpDateTime dateTime) {
        this.dateTime = dateTime;
        this.offset = offsetFromKind(dateTime.getKind(), dateTime);
    }

    /**
     * C# DateTimeOffset ← DateTime implicit conversion (op_Implicit) rendered as
     * a factory: Java has no user-defined conversions. Delegates to the existing
     * (CSharpDateTime) constructor.
     */
    public static CSharpDateTimeOffset fromDateTime(CSharpDateTime dateTime) {
        return new CSharpDateTimeOffset(dateTime);
    }

    public static CSharpDateTimeOffset toCSharpDateTimeOffset(CSharpDateTime dateTime) {
        return new CSharpDateTimeOffset(dateTime);
    }

    private static CSharpTimeSpan offsetFromKind(DateTimeKind kind, CSharpDateTime dateTime) {
        if (kind == DateTimeKind.Utc) {
            return CSharpTimeSpan.ZERO;
        }
        // .NET: new DateTimeOffset(dateTime) with Kind == Local or Unspecified uses
        // TimeZoneInfo.Local.GetUtcOffset(dateTime) — the offset for that specific
        // wall-clock date, not the offset at the current instant. This matters for
        // zones with sub-minute historical offsets (e.g. Asia/Shanghai before
        // 1901 is +08:05:43, rounded to +08:06).
        return CSharpTimeZoneInfo.getLocal().getUtcOffset(dateTime);
    }

    /**
     * Current whole-minute offset of the process default zone, matching the
     * offsets .NET's {@code TimeZoneInfo} exposes (see
     * {@link CSharpTimeZoneInfo#roundToWholeMinutes}).
     */
    private static long localOffsetSeconds() {
        ZoneId zoneId = CSharpTimeZoneInfo.localZoneId();
        return CSharpTimeZoneInfo.roundToWholeMinutes(
            zoneId.getRules().getOffset(Instant.now()).getTotalSeconds());
    }

    /**
     * Whole-minute offset of the process default zone at the wall-clock
     * instant {@code dateTime} represents, matching .NET's
     * {@code TimeZoneInfo.Local.GetUtcOffset(DateTime)}.
     */
    private static long localOffsetSecondsFor(CSharpDateTime dateTime) {
        return CSharpTimeZoneInfo.wholeMinuteOffsetSeconds(
            CSharpTimeZoneInfo.localZoneId(), dateTime);
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
            throw new ArgumentOutOfRangeException("offset", "Offset must be within plus or minus 14 hours.");
        }
        if (offsetTicks % CSharpTimeSpan.TICKS_PER_MINUTE != 0) {
            throw new ArgumentOutOfRangeException("offset", "Offset must be specified in whole minutes.");
        }

        long utcTicks;
        try {
            utcTicks = Math.subtractExact(dateTime.getTicks(), offsetTicks);
        } catch (ArithmeticException ex) {
            throw new ArgumentOutOfRangeException("offset", "The UTC time represented by dateTime and offset is out of range.", ex);
        }

        if (utcTicks < CSharpDateTime.MIN_VALUE.getTicks() || utcTicks > CSharpDateTime.MAX_VALUE.getTicks()) {
            throw new ArgumentOutOfRangeException("offset", "The UTC time represented by dateTime and offset is out of range.");
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
        // .NET DateTimeOffset.LocalDateTime converts the instant to the local
        // time zone; returning the stored wall time made Utf8JsonReader
        // GetDateTime() disagree with DateTime.Parse on offset strings.
        //
        // The local offset must be re-evaluated for the converted instant so
        // that zones with historical sub-minute offsets (e.g. Asia/Shanghai
        // before 1901) use the correct offset for that date.
        long offsetSeconds = offset.getTicks() / CSharpTimeSpan.TICKS_PER_SECOND;
        CSharpDateTime utcInstant = dateTime.addSeconds(-offsetSeconds);
        long localOffset = localOffsetSecondsFor(utcInstant);
        return dateTime.addSeconds(localOffset - offsetSeconds);
    }

    public CSharpDateTimeOffset toLocalTime() {
        long offsetSeconds = offset.getTicks() / CSharpTimeSpan.TICKS_PER_SECOND;
        CSharpDateTime utcInstant = dateTime.addSeconds(-offsetSeconds);
        long localOffset = localOffsetSecondsFor(utcInstant);
        CSharpDateTime localDt = dateTime.addSeconds(localOffset - offsetSeconds);
        return new CSharpDateTimeOffset(localDt, CSharpTimeSpan.fromSeconds(localOffset));
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
            // First try OffsetDateTime.parse which handles full ISO-8601
            // date-time strings with offset (e.g. 2002-01-09T04:02:08Z,
            // 2002-01-09T04:02:08+08:00).
            try {
                OffsetDateTime odt = OffsetDateTime.parse(trimmed);
                CSharpDateTime dt = CSharpDateTime.fromLocalDateTime(odt.toLocalDateTime(), DateTimeKind.Unspecified);
                return new CSharpDateTimeOffset(dt, CSharpTimeSpan.fromSeconds(odt.getOffset().getTotalSeconds()));
            } catch (Exception ignored) {
            }

            // Strip a trailing 'Z' and treat as UTC. Covers date-only+Z ("2002-01-09Z")
            // and year-only+Z ("9999Z").
            String core = trimmed;
            boolean utcSuffix = false;
            if (core.endsWith("Z") || core.endsWith("z")) {
                core = core.substring(0, core.length() - 1);
                utcSuffix = true;
            }

            LocalDateTime ldt;
            if (core.contains("T")) {
                ldt = LocalDateTime.parse(core);
            } else if (core.contains(":")) {
                // Time-only string like "23:15:55" — C# uses today's date.
                if (core.contains("-")) {
                    ldt = LocalDateTime.parse(core, DateTimeFormatter.ISO_LOCAL_DATE_TIME);
                } else {
                    // Time-only string like "23:15:55" — C# uses today's date.
                    // Require at least HH:mm:ss (two colons); reject "04:08"
                    // which is not a valid DateTimeOffset format.
                    long colonCount = core.chars().filter(c -> c == ':').count();
                    if (colonCount < 2) {
                        throw new IllegalArgumentException(
                            "Time-only string must include seconds: " + s);
                    }
                    java.time.LocalTime lt = java.time.LocalTime.parse(core);
                    java.time.LocalDate today = java.time.LocalDate.now();
                    ldt = LocalDateTime.of(today, lt);
                }
            } else if (core.contains("-")) {
                // Date string like "2002-01-09" or "2002-02"
                if (core.length() >= 10) {
                    ldt = LocalDate.parse(core).atStartOfDay();
                } else {
                    // Year-month like "2002-02" — parse as YearMonth
                    java.time.YearMonth ym = java.time.YearMonth.parse(core);
                    ldt = ym.atDay(1).atStartOfDay();
                }
            } else {
                // Year-only like "9999" or "0002"
                int year = Integer.parseInt(core);
                ldt = LocalDate.of(year, 1, 1).atStartOfDay();
            }

            CSharpDateTime dt = CSharpDateTime.fromLocalDateTime(ldt, DateTimeKind.Unspecified);
            CSharpTimeSpan tsOffset;
            if (utcSuffix) {
                tsOffset = CSharpTimeSpan.ZERO;
            } else {
                // .NET DateTimeOffset.Parse uses TimeZoneInfo.Local.GetUtcOffset(dateTime)
                // for the parsed wall-clock date — not the offset at the current instant.
                // This matters for zones with historical sub-minute offsets such as
                // Asia/Shanghai before 1901 (+08:05:43, rounded to +08:06).
                tsOffset = CSharpTimeZoneInfo.getLocal().getUtcOffset(dt);
            }
            return new CSharpDateTimeOffset(dt, tsOffset);
        } catch (Exception e) {
            throw new IllegalArgumentException("Invalid DateTimeOffset format: " + s, e);
        }
    }

    // --- toString ---
    // C# DateTimeOffset.ToString() with InvariantCulture uses "G" format:
    // "MM/dd/yyyy HH:mm:ss zzz" (zero-padded, 24-hour, offset without seconds)
    @Override
    public String toString() {
        LocalDateTime ldt = ticksToLocalDateTime(dateTime.getTicks());
        String dtStr = String.format("%02d/%02d/%04d %02d:%02d:%02d",
            ldt.getMonthValue(), ldt.getDayOfMonth(), ldt.getYear(),
            ldt.getHour(), ldt.getMinute(), ldt.getSecond());
        long offsetTicks = offset.getTicks();
        if (offsetTicks == 0) {
            return dtStr + " +00:00";
        }
        boolean negative = offsetTicks < 0;
        long absTicks = Math.abs(offsetTicks);
        int h = (int) (absTicks / CSharpTimeSpan.TICKS_PER_HOUR);
        long rem = absTicks % CSharpTimeSpan.TICKS_PER_HOUR;
        int m = (int) (rem / CSharpTimeSpan.TICKS_PER_MINUTE);
        return dtStr + " " + (negative ? "-" : "+") + String.format("%02d:%02d", h, m);
    }

    /**
     * C#'s signed offset text ({@code +00:00}, {@code -05:00}, {@code +05:30}).
     *
     * <p>Java's {@code X}/{@code x} pattern letters answer {@code Z} for a zero offset, which is
     * only correct for a C# {@code DateTime} of kind Utc — a {@code DateTimeOffset} carries its own
     * offset and always prints the sign.</p>
     */
    private static String formatOffset(ZoneOffset zoneOffset) {
        int totalSeconds = zoneOffset.getTotalSeconds();
        int absolute = Math.abs(totalSeconds);
        return String.format("%c%02d:%02d", totalSeconds < 0 ? '-' : '+',
            absolute / 3600, (absolute % 3600) / 60);
    }

    public String toString(String format) {
        if (format == null || format.isEmpty()) {
            return toString();
        }

        long offsetSeconds = offset.getTicks() / CSharpTimeSpan.TICKS_PER_SECOND;
        ZoneOffset zoneOffset = ZoneOffset.ofTotalSeconds((int) offsetSeconds);

        if ("o".equals(format) || "O".equals(format)) {
            LocalDateTime ldt = ticksToLocalDateTime(dateTime.getTicks());
            OffsetDateTime odt = OffsetDateTime.of(ldt, zoneOffset);
            // C#'s "O" for a DateTimeOffset always writes the offset WITH a sign — "+00:00" for
            // UTC — because the value carries its own offset; Java's "XXX" renders a zero offset as
            // "Z". Measured: Utf8JsonWriterTests expected "0001-01-01T00:00:00+00:00" and
            // DateTimeConverterTests expected "2000-12-15T22:11:03.055+00:00", both got "…Z".
            // The trimmed form the JSON writer builds expects the ±hh:mm layout as well.
            return odt.format(DateTimeFormatter.ofPattern("yyyy-MM-dd'T'HH:mm:ss.SSSSSSS"))
                + formatOffset(zoneOffset);
        }

        // Detect C# 'F' fraction-of-second tokens (no trailing zeros).
        // Java's 'S' always pads to the requested width, so we format with
        // 'S' and then strip trailing zeros from the fractional part.
        boolean hasFractionNoTrailingZeros = format.indexOf('F') >= 0;

        String javaPattern = toJavaDateTimePattern(format, zoneOffset);
        // If the pattern still contains C# 'z'/'K' offset tokens that were
        // replaced with a literal, LocalDateTime won't be able to resolve
        // them; fall back to OffsetDateTime whenever the pattern carries
        // offset information (xxx / Z / XXX etc.).
        boolean needsOffset = javaPattern.contains("xxx")
            || javaPattern.contains("XXX")
            || javaPattern.contains("'Z'");
        String result;
        if (needsOffset) {
            OffsetDateTime odt = OffsetDateTime.of(ticksToLocalDateTime(dateTime.getTicks()), zoneOffset);
            result = odt.format(DateTimeFormatter.ofPattern(javaPattern));
        } else {
            result = ticksToLocalDateTime(dateTime.getTicks()).format(DateTimeFormatter.ofPattern(javaPattern));
        }
        if (hasFractionNoTrailingZeros) {
            result = stripFractionTrailingZeros(result);
        }
        return result;
    }

    /**
     * Remove trailing zeros from the fractional-seconds portion of a
     * formatted date/time string. If the fraction becomes empty (i.e. the
     * value had only zeros), the leading dot is also removed.
     */
    private static String stripFractionTrailingZeros(String s) {
        int dotPos = s.indexOf('.');
        if (dotPos < 0) {
            return s;
        }
        // Find the end of the fractional digit run (digits only).
        int end = dotPos + 1;
        while (end < s.length() && Character.isDigit(s.charAt(end))) {
            end++;
        }
        if (end == dotPos + 1) {
            return s; // no digits after dot
        }
        String fraction = s.substring(dotPos + 1, end);
        // Strip trailing zeros
        int lastNonZero = fraction.length() - 1;
        while (lastNonZero >= 0 && fraction.charAt(lastNonZero) == '0') {
            lastNonZero--;
        }
        String prefix = s.substring(0, dotPos);
        String suffix = s.substring(end);
        if (lastNonZero < 0) {
            // All zeros: remove the dot entirely
            return prefix + suffix;
        }
        return prefix + "." + fraction.substring(0, lastNonZero + 1) + suffix;
    }

    /**
     * Translate a C# custom date/time format string into the equivalent Java
     * {@link DateTimeFormatter} pattern.
     *
     * <p>The main differences handled here:
     * <ul>
     *   <li>C# {@code z}..{@code zzzzzz} emit the UTC offset (e.g. {@code +08:00});
     *       Java {@code z} is a zone name and rejects more than 3 letters.
     *       We map any run of {@code z} (length >= 3) to Java {@code xxx}
     *       ({@code +HH:mm}), {@code zz} to {@code xx}, {@code z} to {@code x}.</li>
     *   <li>C# {@code K} for DateTimeOffset emits the offset; we substitute the
     *       literal offset string.</li>
     *   <li>C# treats a standalone uppercase {@code T} as a literal separator;
     *       Java treats it as a pattern letter, so we quote it.</li>
     * </ul>
     */
    private String toJavaDateTimePattern(String format, ZoneOffset zoneOffset) {
        String javaPattern = format;
        // C# custom formats treat uppercase 'T' as a literal separator; Java treats
        // 'T' as a pattern letter (time), so quote it unless already quoted.
        if (javaPattern.indexOf('T') >= 0 && !javaPattern.contains("'T'")) {
            javaPattern = javaPattern.replace("T", "'T'");
        }
        // C# 'K' for DateTimeOffset always emits the offset (e.g. +08:00); Java's
        // 'K' means clock-hour-of-am-pm, so emit the literal offset instead.
        if (javaPattern.indexOf('K') >= 0) {
            javaPattern = javaPattern.replace("K", "'" + zoneOffset + "'");
        }
        // C# 'Z' literal (uppercase, outside quotes) in custom format means
        // a literal 'Z' character; Java's 'Z' is an offset pattern. Replace
        // bare 'Z' (not inside quotes) with the literal 'Z'.
        // This is handled below together with 'z' runs.
        javaPattern = convertCSharpOffsetPatterns(javaPattern, zoneOffset);
        return javaPattern;
    }

    /**
     * Convert C# custom date/time pattern letters that differ from Java's
     * {@link DateTimeFormatter} into their Java equivalents.
     *
     * <p>Currently handles:
     * <ul>
     *   <li>{@code z}/{@code zz}/{@code zzz+} → Java offset patterns
     *       {@code x}/{@code xx}/{@code xxx}</li>
     *   <li>{@code Z} (bare, outside quotes) → literal {@code 'Z'}</li>
     *   <li>{@code F}/{@code FF}/.../{@code FFFFFFF} (fraction-of-second,
     *       up to 7 digits, no trailing zeros) → Java {@code S} with the
     *       same digit count. Java's {@code F} is week-of-month and rejects
     *       more than one letter, so we must translate it.</li>
     * </ul>
     */
    private String convertCSharpOffsetPatterns(String pattern, ZoneOffset zoneOffset) {
        // We process the pattern character-by-character to respect quoted
        // regions (single quotes in Java DateTimeFormatter patterns).
        StringBuilder sb = new StringBuilder();
        int i = 0;
        int len = pattern.length();
        boolean inQuote = false;
        while (i < len) {
            char c = pattern.charAt(i);
            if (c == '\'') {
                if (!inQuote && i + 1 < len && pattern.charAt(i + 1) == '\'') {
                    // escaped single quote
                    sb.append("''");
                    i += 2;
                    continue;
                }
                inQuote = !inQuote;
                sb.append(c);
                i++;
                continue;
            }
            if (inQuote) {
                sb.append(c);
                i++;
                continue;
            }
            // Outside quotes: collect runs of identical pattern letters.
            if (c == 'z' || c == 'Z' || c == 'F') {
                int start = i;
                while (i < len && pattern.charAt(i) == c) {
                    i++;
                }
                int count = i - start;
                if (c == 'Z') {
                    // C# uppercase 'Z' in custom format is a literal 'Z' char.
                    sb.append("'Z'");
                } else if (c == 'F') {
                    // C# 'F'..'FFFFFFF' = fraction of second (1-7 digits,
                    // no trailing zeros). Java's 'F' is week-of-month and
                    // rejects more than one letter; translate to 'S' with
                    // the same digit count (fraction-of-second).
                    sb.append("S".repeat(Math.min(count, 7)));
                } else {
                    // C# lowercase 'z' = UTC offset.
                    // z   -> +H      (Java 'x')
                    // zz  -> +HH     (Java 'xx')
                    // zzz+ -> +HH:mm (Java 'xxx')
                    if (count >= 3) {
                        sb.append("xxx");
                    } else if (count == 2) {
                        sb.append("xx");
                    } else {
                        sb.append("x");
                    }
                }
                continue;
            }
            sb.append(c);
            i++;
        }
        return sb.toString();
    }

    public String toString(String format, IFormatProvider provider) {
        return toString(format);
    }

    public static CSharpDateTimeOffset parseExact(String s, String format) {
        try {
            return parse(s);
        } catch (Exception e) {
            throw new FormatException("Invalid DateTimeOffset format: " + s, e);
        }
    }

    public static CSharpDateTimeOffset parseExact(String s, String[] formats) {
        for (String fmt : formats) {
            try {
                return parseExact(s, fmt);
            } catch (Exception ignored) {
            }
        }
        throw new FormatException("Invalid DateTimeOffset format: " + s);
    }

    public static CSharpDateTimeOffset parseExact(String s, String format, IFormatProvider provider) {
        return parseExact(s, format, provider, 0);
    }

    public static CSharpDateTimeOffset parseExact(String s, String format, IFormatProvider provider, int style) {
        if (format == null) {
            throw new ArgumentNullException("format");
        }
        try {
            return parse(s);
        } catch (Exception e) {
            throw new FormatException("Invalid DateTimeOffset format: " + s, e);
        }
    }

    public static CSharpDateTimeOffset parseExact(String s, String[] formats, IFormatProvider provider, int style) {
        if (formats == null) {
            throw new ArgumentNullException("formats");
        }
        for (String fmt : formats) {
            try {
                return parseExact(s, fmt, provider, style);
            } catch (Exception ignored) {
            }
        }
        throw new FormatException("Invalid DateTimeOffset format: " + s);
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

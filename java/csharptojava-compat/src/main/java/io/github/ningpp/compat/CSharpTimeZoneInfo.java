package io.github.ningpp.compat;

import java.time.*;

/**
 * C# System.TimeZoneInfo compatibility class.
 * Provides Local timezone access and UTC offset calculation.
 *
 * <p>Java's IANA database exposes historical "local mean time" offsets with
 * sub-minute precision (Asia/Shanghai reports +08:05:43 before 1901), while
 * .NET never surfaces such offsets: {@code TimeZoneInfo.GetUtcOffset} and
 * {@code TimeZoneInfo.ConvertTime} round the zone offset to the nearest whole
 * minute (see {@code TimeZoneInfo.GetUtcOffsetFromUtc}). Every offset this
 * class reports — and every wall-clock conversion it performs — therefore goes
 * through {@link #roundToWholeMinutes}, so that all conversion paths agree and
 * Local/UTC round trips stay exact.
 */
public final class CSharpTimeZoneInfo {

    public static final CSharpTimeZoneInfo LOCAL = new CSharpTimeZoneInfo(ZoneId.systemDefault());

    public static final CSharpTimeZoneInfo UTC = new CSharpTimeZoneInfo(ZoneOffset.UTC);

    private final ZoneId zoneId;

    public CSharpTimeZoneInfo(ZoneId zoneId) {
        this.zoneId = zoneId;
    }

    public static CSharpTimeZoneInfo getLocal() {
        return LOCAL;
    }

    public static CSharpTimeZoneInfo getUtc() {
        return UTC;
    }

    public String getDisplayName() {
        return zoneId.toString();
    }

    public String getStandardName() {
        return zoneId.toString();
    }

    public CSharpTimeSpan getUtcOffset(CSharpDateTime dateTime) {
        return CSharpTimeSpan.fromSeconds(wholeMinuteOffsetSeconds(zoneId, dateTime));
    }

    /**
     * Rounds a UTC offset in seconds to the nearest whole minute, which is what
     * .NET's {@code TimeZoneInfo} reports for zones whose IANA rules carry
     * sub-minute historical offsets.
     */
    public static long roundToWholeMinutes(long offsetSeconds) {
        return Math.round(offsetSeconds / 60.0) * 60;
    }

    /**
     * Whole-minute offset of {@code zoneId} at the instant {@code dateTime}
     * represents as local wall-clock time in that zone.
     */
    public static long wholeMinuteOffsetSeconds(ZoneId zoneId, CSharpDateTime dateTime) {
        return roundToWholeMinutes(dateTime.ticksToLdtPublic().atZone(zoneId).getOffset().getTotalSeconds());
    }

    /**
     * Zone used for {@code DateTimeKind.Local} conversions. The process default
     * zone can change at runtime (see {@code TimeZone.setDefault}), so it is
     * resolved on every call instead of being cached.
     */
    public static ZoneId localZoneId() {
        return ZoneId.systemDefault();
    }

    /**
     * Wall-clock time in {@code targetZoneId} for the instant {@code dateTime}
     * represents in the zone whose whole-minute offset is
     * {@code sourceOffsetSeconds}. The target offset is re-evaluated for the
     * converted instant so DST transitions are honored.
     */
    public static CSharpDateTime convertWallTime(
            CSharpDateTime dateTime, long sourceOffsetSeconds, ZoneId targetZoneId, DateTimeKind kind) {
        long sourceSeconds = roundToWholeMinutes(sourceOffsetSeconds);
        LocalDateTime ldt = dateTime.ticksToLdtPublic();
        Instant instant = ldt.toInstant(ZoneOffset.ofTotalSeconds((int) sourceSeconds));
        long targetSeconds = roundToWholeMinutes(targetZoneId.getRules().getOffset(instant).getTotalSeconds());
        return new CSharpDateTime(ldtToTicks(ldt.plusSeconds(targetSeconds - sourceSeconds)), kind);
    }

    /**
     * Converts {@code dateTime} to {@code destinationTimeZone}, mirroring
     * {@code TimeZoneInfo.ConvertTime(DateTime, TimeZoneInfo)}:
     * <ul>
     *   <li>{@code Kind == Utc}: {@code ConvertTimeFromUtc} — the ticks already
     *       are the UTC instant.</li>
     *   <li>otherwise: {@code ConvertTimeToUtc} (the value is read as local time
     *       in the process default zone) followed by {@code ConvertTimeFromUtc}.
     *       </li>
     * </ul>
     * The result carries the destination zone's {@link DateTimeKind}: converting
     * a {@code DateTimeKind.Utc} value into a custom zone yields
     * {@code Unspecified}, not {@code Local}.
     */
    public static CSharpDateTime convertTime(CSharpDateTime dateTime, CSharpTimeZoneInfo destinationTimeZone) {
        if (dateTime == null) throw new NullPointerException("dateTime");
        if (destinationTimeZone == null) throw new NullPointerException("destinationTimeZone");
        if (isUtcZone(destinationTimeZone.zoneId)) {
            // ConvertTimeToUtc: the ticks are local wall-clock time.
            return convertWallTime(dateTime, wholeMinuteOffsetSeconds(localZoneId(), dateTime),
                destinationTimeZone.zoneId, DateTimeKind.Utc);
        }
        long sourceOffsetSeconds = dateTime.getKind() == DateTimeKind.Utc
            ? 0
            : wholeMinuteOffsetSeconds(localZoneId(), dateTime);
        return convertWallTime(dateTime, sourceOffsetSeconds, destinationTimeZone.zoneId,
            destinationTimeZone.kind());
    }

    private static boolean isUtcZone(ZoneId zoneId) {
        return ZoneOffset.UTC.equals(zoneId) || "UTC".equals(zoneId.getId());
    }

    /** {@link DateTimeKind} that .NET assigns to values returned in this zone. */
    public DateTimeKind kind() {
        if (isUtcZone(zoneId)) {
            return DateTimeKind.Utc;
        }
        if (localZoneId().equals(zoneId)) {
            return DateTimeKind.Local;
        }
        return DateTimeKind.Unspecified;
    }

    private static long ldtToTicks(LocalDateTime ldt) {
        long epochDay = ldt.toLocalDate().toEpochDay();
        long nanoOfDay = ldt.toLocalTime().toNanoOfDay();
        return epochDay * CSharpTimeSpan.TICKS_PER_DAY + nanoOfDay / 100 + 621355968000000000L;
    }

    public ZoneId getZoneId() {
        return zoneId;
    }

    public boolean isDaylightSavingTime(CSharpDateTime dateTime) {
        ZonedDateTime zdt = dateTime.ticksToLdtPublic().atZone(zoneId);
        return zoneId.getRules().isDaylightSavings(zdt.toInstant());
    }
}

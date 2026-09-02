package io.github.ningpp.compat;

import java.time.*;

/**
 * C# System.TimeZoneInfo compatibility class.
 * Provides Local timezone access and UTC offset calculation.
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
        LocalDateTime ldt = dateTime.ticksToLdtPublic();
        ZonedDateTime zdt = ldt.atZone(zoneId);
        ZoneOffset offset = zdt.getOffset();
        return new CSharpTimeSpan((long) offset.getTotalSeconds() * CSharpTimeSpan.TICKS_PER_SECOND);
    }

    public static CSharpDateTime convertTime(CSharpDateTime dateTime, CSharpTimeZoneInfo destinationTimeZone) {
        if (dateTime == null) throw new NullPointerException("dateTime");
        if (destinationTimeZone == null) throw new NullPointerException("destinationTimeZone");
        LocalDateTime ldt = dateTime.ticksToLdtPublic();
        ZonedDateTime sourceZdt = ldt.atZone(ZoneId.systemDefault());
        ZonedDateTime destZdt = sourceZdt.withZoneSameInstant(destinationTimeZone.zoneId);
        return new CSharpDateTime(ldtToTicks(destZdt.toLocalDateTime()), dateTime.getKind());
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
        LocalDateTime ldt = dateTime.ticksToLdtPublic();
        ZonedDateTime zdt = ldt.atZone(zoneId);
        return zoneId.getRules().isDaylightSavings(zdt.toInstant());
    }
}

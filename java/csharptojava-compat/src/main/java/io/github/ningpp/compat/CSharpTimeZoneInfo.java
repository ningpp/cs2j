package io.github.ningpp.compat;

import java.time.*;

/**
 * C# System.TimeZoneInfo compatibility class.
 * Provides Local timezone access and UTC offset calculation.
 */
public final class CSharpTimeZoneInfo {

    public static final CSharpTimeZoneInfo LOCAL = new CSharpTimeZoneInfo(ZoneId.systemDefault());

    private final ZoneId zoneId;

    public CSharpTimeZoneInfo(ZoneId zoneId) {
        this.zoneId = zoneId;
    }

    public static CSharpTimeZoneInfo getLocal() {
        return LOCAL;
    }

    public CSharpTimeSpan getUtcOffset(CSharpDateTime dateTime) {
        LocalDateTime ldt = dateTime.ticksToLdtPublic();
        ZonedDateTime zdt = ldt.atZone(zoneId);
        ZoneOffset offset = zdt.getOffset();
        return new CSharpTimeSpan((long) offset.getTotalSeconds() * CSharpTimeSpan.TICKS_PER_SECOND);
    }

    public ZoneId getZoneId() {
        return zoneId;
    }
}

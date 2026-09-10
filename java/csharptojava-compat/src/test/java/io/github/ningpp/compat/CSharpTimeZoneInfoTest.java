package io.github.ningpp.compat;

import java.time.ZoneId;
import java.time.ZonedDateTime;
import java.util.TimeZone;
import org.junit.jupiter.api.Test;
import static org.junit.jupiter.api.Assertions.*;

/**
 * Regression tests for the "historical local mean time" offset problem.
 *
 * <p>Java exposes the IANA time zone database, which carries sub-minute
 * offsets for eras before time zones were standardized (for example
 * {@code Asia/Shanghai} is {@code +08:05:43} before 1901). C#/.NET never
 * exposes such offsets: {@code TimeZoneInfo.GetUtcOffset} / {@code ConvertTime}
 * round the base UTC offset to the nearest whole minute (see
 * {@code TimeZoneInfo.GetUtcOffsetFromUtc} and {@code TimeZoneInfo.ConvertTime}).
 *
 * <p>{@link CSharpTimeZoneInfo#getUtcOffset} already rounds, but the
 * wall-clock conversion helpers used to fall back to
 * {@code LocalDateTime.atZone(zone)} / {@code withZoneSameInstant(zone)} and
 * therefore added the raw {@code +08:05:43} offset. The two paths then
 * disagreed by 17 seconds, which broke the converted SDK tests (for example
 * {@code DateTimeAttributeTests.readContentAsDateTimeAttribute1}, which expects
 * {@code 0002-01-01 08:06:00} but observed {@code 0002-01-01 08:05:43}).
 */
class CSharpTimeZoneInfoTest {

    /** Base UTC offset a .NET TimeZoneInfo may expose for the zone at that instant. */
    private static long roundedOffsetSeconds(ZoneId zoneId, CSharpDateTime dateTime) {
        ZonedDateTime zdt = dateTime.ticksToLdtPublic().atZone(zoneId);
        return Math.round(zdt.getOffset().getTotalSeconds() / 60.0) * 60;
    }

    // --- getUtcOffset rounds to whole minutes ---

    @Test
    void getUtcOffset_roundsHistoricalSubMinuteOffset_up() {
        // Asia/Shanghai before 1901 is +08:05:43 -> +08:06:00 (nearest minute).
        CSharpTimeZoneInfo shanghai = new CSharpTimeZoneInfo(ZoneId.of("Asia/Shanghai"));
        CSharpTimeSpan offset = shanghai.getUtcOffset(new CSharpDateTime(2, 1, 1));
        assertEquals(8 * 3600L + 6 * 60L, offset.getTicks() / CSharpTimeSpan.TICKS_PER_SECOND);
        assertEquals(8, offset.getHours());
        assertEquals(6, offset.getMinutes());
        assertEquals(0, offset.getSeconds());
    }

    @Test
    void getUtcOffset_roundsHistoricalSubMinuteOffset_down() {
        // Europe/Amsterdam before 1835 is +00:17:30 (exactly half a minute) and
        // Africa/Monrovia before 1919 is -00:43:08 -> -00:43:00.
        CSharpTimeZoneInfo amsterdam = new CSharpTimeZoneInfo(ZoneId.of("Europe/Amsterdam"));
        assertEquals(18 * 60L, amsterdam.getUtcOffset(new CSharpDateTime(2, 1, 1)).getTicks()
            / CSharpTimeSpan.TICKS_PER_SECOND);

        CSharpTimeZoneInfo monrovia = new CSharpTimeZoneInfo(ZoneId.of("Africa/Monrovia"));
        CSharpTimeSpan offset = monrovia.getUtcOffset(new CSharpDateTime(2, 1, 1));
        assertEquals(-(43 * 60L), offset.getTicks() / CSharpTimeSpan.TICKS_PER_SECOND);
        assertEquals(-0, offset.getHours());
        assertEquals(-43, offset.getMinutes());
    }

    @Test
    void getUtcOffset_keepsModernWholeMinuteOffsets() {
        CSharpTimeZoneInfo shanghai = new CSharpTimeZoneInfo(ZoneId.of("Asia/Shanghai"));
        assertEquals(8 * 3600L, shanghai.getUtcOffset(new CSharpDateTime(2024, 6, 15)).getTicks()
            / CSharpTimeSpan.TICKS_PER_SECOND);
    }

    // --- convertTime uses the rounded offset ---

    @Test
    void convertTime_localToUtcAndBack_roundTripsWithoutOffsetDrift() {
        // Environment-independent: whatever sub-minute offset the process
        // default zone carries for an early date, converting local -> UTC ->
        // local must not drift, which is what happened when the two directions
        // rounded differently.
        CSharpDateTime original = new CSharpDateTime(2, 1, 1, 8, 6, 0, 0, DateTimeKind.Local);
        CSharpDateTime utc = CSharpTimeZoneInfo.convertTime(original, CSharpTimeZoneInfo.getUtc());
        assertEquals(DateTimeKind.Utc, utc.getKind());
        CSharpDateTime back = CSharpTimeZoneInfo.convertTime(utc, CSharpTimeZoneInfo.getLocal());
        assertEquals(original.toString(), back.toString());
    }

    @Test
    void convertTime_nullArguments_throw() {
        CSharpDateTime dt = new CSharpDateTime(2024, 6, 15, 10, 30, 45, 0, DateTimeKind.Local);
        assertThrows(NullPointerException.class, () -> CSharpTimeZoneInfo.convertTime(null, CSharpTimeZoneInfo.getUtc()));
        assertThrows(NullPointerException.class, () -> CSharpTimeZoneInfo.convertTime(dt, null));
    }

    // --- zone conversions driven by the process default zone ---

    @Test
    void toLocalTime_usesRoundedHistoricalOffset_withShanghaiAsLocalZone() {
        withDefaultTimeZone("Asia/Shanghai", () -> {
            // Raw IANA offset is +08:05:43; .NET rounds it to +08:06.
            CSharpDateTime utc = new CSharpDateTime(2, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc);
            assertEquals("1/1/2 8:06:00", utc.toLocalTime().toString());
        });
    }

    @Test
    void toUniversalTime_usesRoundedHistoricalOffset_withShanghaiAsLocalZone() {
        withDefaultTimeZone("Asia/Shanghai", () -> {
            CSharpDateTime local = new CSharpDateTime(2, 1, 1, 8, 6, 0, 0, DateTimeKind.Local);
            assertEquals("1/1/2 0:00:00", local.toUniversalTime().toString());
        });
    }

    @Test
    void toLocalTime_and_getUtcOffset_agree_forHistoricalDates() {
        withDefaultTimeZone("Asia/Shanghai", () -> {
            CSharpDateTime utc = new CSharpDateTime(2, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc);
            CSharpDateTime local = utc.toLocalTime();
            CSharpDateTime expected = new CSharpDateTime(2, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc)
                .add(CSharpTimeZoneInfo.getLocal().getUtcOffset(utc));
            assertEquals(expected.toString(), local.toString());
        });
    }

    /**
     * Runs {@code body} with the JVM default time zone set to {@code zoneId}.
     * {@link ZoneId#systemDefault()} resolves the current default on every call,
     * so the mutation is visible to the code under test.
     */
    private static void withDefaultTimeZone(String zoneId, Runnable body) {
        TimeZone original = TimeZone.getDefault();
        TimeZone.setDefault(TimeZone.getTimeZone(zoneId));
        try {
            assertEquals(zoneId, ZoneId.systemDefault().getId());
            body.run();
        } finally {
            TimeZone.setDefault(original);
        }
    }
}


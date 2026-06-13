package io.github.ningpp.compat;

import java.time.Duration;
import java.time.ZoneOffset;

/** Helper methods for DateTime/DateTimeOffset comparisons. */
public final class DateTimeHelper {
    private DateTimeHelper() {
    }

    /** Mirrors C# DateTimeOffset.Offset != TimeSpan.Zero comparison. */
    public static boolean offsetNotZero(ZoneOffset offset) {
        return !offset.equals(ZoneOffset.UTC);
    }

    /** Mirrors C# DateTimeOffset.Offset == TimeSpan.Zero comparison. */
    public static boolean offsetIsZero(ZoneOffset offset) {
        return offset.equals(ZoneOffset.UTC);
    }

    /** Converts a Duration to a comparable value for ZoneOffset comparison. */
    public static ZoneOffset toZoneOffset(Duration d) {
        return ZoneOffset.ofTotalSeconds((int) d.toSeconds());
    }
}

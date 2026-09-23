package io.github.ningpp.compat;

/**
 * C# System.DayOfWeek compatibility enum.
 *
 * <p>Mirrors the pattern the converter generates for C# enums: an {@code _UNMAPPED}
 * constant carries any value the named constants do not cover (C# enums may hold
 * arbitrary underlying values, e.g. {@code (DayOfWeek)(-1)}), instead of indexing
 * {@code values()} with the raw value — which crashed for negative inputs
 * ({@code ArrayIndexOutOfBoundsException: Index -1 out of bounds for length 7}) and
 * silently aliased everything else through {@code % 7}.</p>
 */
public enum DayOfWeek {
    Sunday(0),
    Monday(1),
    Tuesday(2),
    Wednesday(3),
    Thursday(4),
    Friday(5),
    Saturday(6),
    _UNMAPPED(0);

    private final int value;
    private int unmappedValue;

    DayOfWeek(int value) { this.value = value; }

    public int getValue() {
        if (this == _UNMAPPED) return unmappedValue;
        return value;
    }

    /**
     * Mirrors C# {@code Enum.ToString()}: named constants render as their name,
     * while an undefined value (carried by the {@code _UNMAPPED} sentinel) renders
     * as its underlying number — {@code ((DayOfWeek)(-1)).ToString() == "-1"}.
     * The generated {@code EnumConverter.write} depends on this: it stringifies the
     * value, writes it as a JSON string only when it looks like an identifier, and
     * otherwise writes the numeric value, so a sentinel stringifying as its constant
     * name would leak {@code "_UNMAPPED"} into JSON instead of the number.
     */
    @Override
    public String toString() {
        if (this == _UNMAPPED) return Integer.toString(unmappedValue);
        return super.toString();
    }

    public static DayOfWeek fromValue(int v) {
        for (DayOfWeek e : values()) {
            if (e != _UNMAPPED && e.value == v) return e;
        }
        _UNMAPPED.unmappedValue = v;
        return _UNMAPPED;
    }

    /**
     * Maps the JDK enum (ISO-8601 numbering, Monday=1 .. Sunday=7) onto C#
     * numbering (Sunday=0 .. Saturday=6); {@code % 7} is the whole conversion.
     */
    public static DayOfWeek fromJava(java.time.DayOfWeek dow) {
        return dow == null ? null : fromValue(dow.getValue() % 7);
    }
}

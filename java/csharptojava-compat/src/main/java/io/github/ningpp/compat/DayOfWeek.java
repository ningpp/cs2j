package io.github.ningpp.compat;

/** C# System.DayOfWeek compatibility enum. */
public enum DayOfWeek {
    Sunday(0),
    Monday(1),
    Tuesday(2),
    Wednesday(3),
    Thursday(4),
    Friday(5),
    Saturday(6);

    private final int value;

    DayOfWeek(int value) { this.value = value; }

    public int getValue() { return value; }

    public static DayOfWeek fromJava(java.time.DayOfWeek dow) {
        return values()[dow.getValue() % 7];
    }
}

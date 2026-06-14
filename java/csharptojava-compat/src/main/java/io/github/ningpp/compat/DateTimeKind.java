package io.github.ningpp.compat;

/** C# System.DateTimeKind compatibility enum. */
public enum DateTimeKind {
    Unspecified(0),
    Utc(1),
    Local(2);

    private final int value;

    DateTimeKind(int value) { this.value = value; }

    public int getValue() { return value; }
}

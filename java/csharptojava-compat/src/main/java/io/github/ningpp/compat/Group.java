package io.github.ningpp.compat;

public final class Group {
    public static final Group Empty = new Group("");

    public final String Value;
    public final int Length;

    public Group(String value) {
        this.Value = value == null ? "" : value;
        this.Length = this.Value.length();
    }

    @Override
    public String toString() {
        return Value;
    }
}

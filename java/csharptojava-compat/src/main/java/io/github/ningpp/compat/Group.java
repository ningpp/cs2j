package io.github.ningpp.compat;

import java.util.regex.Matcher;

public final class Group {
    public static final Group Empty = new Group("");

    public final String Value;
    public final int Length;
    private final int index;

    public Group(String value) {
        this.Value = value == null ? "" : value;
        this.Length = this.Value.length();
        this.index = -1;
    }

    public Group(Matcher matcher, boolean success) {
        this.Value = success ? matcher.group() : "";
        this.Length = this.Value.length();
        this.index = success ? matcher.start() : -1;
    }

    public Group(Matcher matcher, boolean success, int index, String value) {
        this.Value = value == null ? "" : value;
        this.Length = this.Value.length();
        this.index = index;
    }

    public String getValue() {
        return Value;
    }

    public int getLength() {
        return Length;
    }

    public int getIndex() {
        return index;
    }

    @Override
    public String toString() {
        return Value;
    }
}

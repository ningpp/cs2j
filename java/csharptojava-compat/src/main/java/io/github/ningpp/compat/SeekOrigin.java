package io.github.ningpp.compat;

/**
 * Compat for System.IO.SeekOrigin.
 */
public enum SeekOrigin {
    Begin(0),
    Current(1),
    End(2);

    private final int value;

    SeekOrigin(int value) {
        this.value = value;
    }

    public int getValue() {
        return value;
    }

    public static SeekOrigin fromValue(int value) {
        for (SeekOrigin origin : values()) {
            if (origin.value == value) {
                return origin;
            }
        }
        return Begin;
    }
}

package io.github.ningpp.compat;

/**
 * Compat for Newtonsoft.Json.ReferenceLoopHandling.
 */
public enum ReferenceLoopHandling {
    Error(0),
    Ignore(1),
    Serialize(2);

    private final int value;

    ReferenceLoopHandling(int value) {
        this.value = value;
    }

    public int getValue() {
        return value;
    }

    public static ReferenceLoopHandling fromValue(int value) {
        for (ReferenceLoopHandling handling : values()) {
            if (handling.value == value) {
                return handling;
            }
        }
        return Error;
    }
}

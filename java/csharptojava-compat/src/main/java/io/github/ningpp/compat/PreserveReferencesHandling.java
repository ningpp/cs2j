package io.github.ningpp.compat;

/**
 * Compat for Newtonsoft.Json.PreserveReferencesHandling.
 */
public enum PreserveReferencesHandling {
    None(0),
    Objects(1),
    Arrays(2),
    All(3);

    private final int value;

    PreserveReferencesHandling(int value) {
        this.value = value;
    }

    public int getValue() {
        return value;
    }

    public static PreserveReferencesHandling fromValue(int value) {
        for (PreserveReferencesHandling handling : values()) {
            if (handling.value == value) {
                return handling;
            }
        }
        return None;
    }
}

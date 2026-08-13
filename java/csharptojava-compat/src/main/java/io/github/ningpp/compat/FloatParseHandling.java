package io.github.ningpp.compat;

/**
 * Compat for Newtonsoft.Json.FloatParseHandling.
 */
public enum FloatParseHandling {
    Double(0),
    Decimal(1);

    private final int value;

    FloatParseHandling(int value) {
        this.value = value;
    }

    public int getValue() {
        return value;
    }

    public static FloatParseHandling fromValue(int value) {
        for (FloatParseHandling handling : values()) {
            if (handling.value == value) {
                return handling;
            }
        }
        return Double;
    }
}

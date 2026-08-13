package io.github.ningpp.compat;

/**
 * Compat for Newtonsoft.Json.StringEscapeHandling.
 */
public enum StringEscapeHandling {
    Default(0),
    EscapeNonAscii(1),
    EscapeHtml(2);

    private final int value;

    StringEscapeHandling(int value) {
        this.value = value;
    }

    public int getValue() {
        return value;
    }

    public static StringEscapeHandling fromValue(int value) {
        for (StringEscapeHandling handling : values()) {
            if (handling.value == value) {
                return handling;
            }
        }
        return Default;
    }
}

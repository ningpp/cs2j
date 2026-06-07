package io.github.ningpp.compat;

/**
 * Replacement for System.Text.EncoderReplacementFallback.
 * In Java, this corresponds to {@link java.nio.charset.CodingErrorAction#REPLACE}.
 */
public class EncoderReplacementFallback {
    private final String defaultString;

    public EncoderReplacementFallback() {
        this.defaultString = "?";
    }

    public EncoderReplacementFallback(String defaultString) {
        this.defaultString = defaultString;
    }

    public String getDefaultString() {
        return defaultString;
    }
}

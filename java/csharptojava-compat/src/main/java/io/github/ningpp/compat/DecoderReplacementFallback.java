package io.github.ningpp.compat;

/**
 * Replacement for System.Text.DecoderReplacementFallback.
 * In Java, this corresponds to {@link java.nio.charset.CodingErrorAction#REPLACE}.
 */
public class DecoderReplacementFallback {
    private final String defaultString;

    public DecoderReplacementFallback() {
        this.defaultString = "\uFFFD";
    }

    public DecoderReplacementFallback(String defaultString) {
        this.defaultString = defaultString;
    }

    public String getDefaultString() {
        return defaultString;
    }
}

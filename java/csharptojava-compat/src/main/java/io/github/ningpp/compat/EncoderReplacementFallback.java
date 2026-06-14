package io.github.ningpp.compat;

/**
 * Replacement for System.Text.EncoderReplacementFallback.
 * In Java, this corresponds to {@link java.nio.charset.CodingErrorAction#REPLACE}.
 */
public class EncoderReplacementFallback extends EncoderFallback {
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

    @Override
    public EncoderFallbackBuffer createFallbackBuffer() {
        return new EncoderFallbackBuffer() {
            @Override
            public boolean fallback(char charUnknown, int index) { return false; }
            @Override
            public boolean fallback(char charUnknownHigh, char charUnknownLow, int index) { return false; }
            @Override
            public char getNextChar() { return '\0'; }
            @Override
            public boolean movePrevious() { return false; }
            @Override
            public int getRemaining() { return 0; }
        };
    }

    @Override
    public int getMaxCharCount() {
        return defaultString.length();
    }
}

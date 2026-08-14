package io.github.ningpp.compat;

/**
 * Compat for System.Text.Encodings.Web.TextEncoderSettings.
 */
public class TextEncoderSettings {
    public TextEncoderSettings() {
    }

    public TextEncoderSettings(TextEncoderSettings other) {
    }

    public void allowCharacters(int... ranges) {
    }

    public void allowRange(int firstCodePoint, int lastCodePoint) {
    }

    public void allowRanges(int... codePoints) {
    }

    public void allowCharacter(int character) {
    }

    /** C# TextEncoderSettings.ForbidCharacters(char...). */
    public void forbidCharacters(char... characters) {
    }

    public void clear() {
    }
}

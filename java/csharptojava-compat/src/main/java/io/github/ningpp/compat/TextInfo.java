package io.github.ningpp.compat;

import java.util.Locale;

/**
 * Stub for System.Globalization.TextInfo.
 * Wraps a java.util.Locale and delegates to Java's built-in case conversion.
 */
public class TextInfo {
    private final Locale locale;

    public TextInfo(Locale locale) {
        this.locale = locale != null ? locale : Locale.ROOT;
    }

    public String getCultureName() {
        return locale.toLanguageTag();
    }

    public int getLCID() {
        return 0; // not directly available in Java
    }

    public boolean getIsReadOnly() {
        return true;
    }

    public boolean getIsRightToLeft() {
        return locale.getLanguage().equals("ar")
            || locale.getLanguage().equals("he")
            || locale.getLanguage().equals("fa")
            || locale.getLanguage().equals("ur");
    }

    public String getListSeparator() {
        return ",";
    }

    public void setListSeparator(String value) {
        // no-op for stub
    }

    public int getANSICodePage() { return 0; }
    public int getEBCDICCodePage() { return 0; }
    public int getMacCodePage() { return 0; }
    public int getOEMCodePage() { return 0; }

    // --- Case conversion ---

    public char toLower(char c) {
        return Character.toLowerCase(c);
    }

    public String toLower(String str) {
        if (str == null) return null;
        return str.toLowerCase(locale);
    }

    public String toLowerCase(String str) {
        return toLower(str);
    }

    public char toUpper(char c) {
        return Character.toUpperCase(c);
    }

    public String toUpper(String str) {
        if (str == null) return null;
        return str.toUpperCase(locale);
    }

    public String toUpperCase(String str) {
        return toUpper(str);
    }

    public String toTitleCase(String str) {
        if (str == null || str.isEmpty()) return str;
        StringBuilder sb = new StringBuilder(str.length());
        boolean capitalizeNext = true;
        for (int i = 0; i < str.length(); i++) {
            char c = str.charAt(i);
            if (Character.isLetter(c)) {
                if (capitalizeNext) {
                    sb.append(Character.toTitleCase(c));
                    capitalizeNext = false;
                } else {
                    sb.append(Character.toLowerCase(c));
                }
            } else {
                sb.append(c);
                capitalizeNext = Character.isWhitespace(c) || c == '-' || c == '\'';
            }
        }
        return sb.toString();
    }

    // --- Static factory ---

    public static TextInfo ReadOnly(TextInfo textInfo) {
        return textInfo; // already immutable in this stub
    }

    @Override
    public String toString() {
        return "TextInfo [" + locale.toLanguageTag() + "]";
    }
}

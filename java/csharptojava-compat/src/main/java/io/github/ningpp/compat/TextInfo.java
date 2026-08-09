package io.github.ningpp.compat;

import java.util.Locale;

/**
 * Stub for System.Globalization.TextInfo.
 * Wraps a java.util.Locale and delegates to Java's built-in case conversion.
 */
public class TextInfo {
    private final Locale locale;
    private final boolean invariant;

    public TextInfo(Locale locale) {
        this(locale, false);
    }

    public TextInfo(Locale locale, boolean invariant) {
        this.locale = locale != null ? locale : Locale.ROOT;
        this.invariant = invariant;
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
        // .NET ICU: only the INVARIANT culture leaves U+0130 (İ) unchanged (its full
        // lowercase is "i" + combining dot); regular cultures use the simple mapping
        // (İ → i). Turkish/Azeri additionally map 'I' to 'ı'.
        if (c == '\u0130') {
            if (invariant) {
                return '\u0130';
            }
            if (isTurkishLike()) {
                return 'i';
            }
            return Character.toLowerCase(c);
        }
        if (isTurkishLike() && c == 'I') {
            return '\u0131';
        }
        return Character.toLowerCase(c);
    }

    public String toLower(String str) {
        if (str == null) return null;
        // Invariant culture: Java's String.toLowerCase(ROOT/"en") applies the full Unicode
        // lowercase mapping (U+0130 → "i" + combining dot), while .NET invariant leaves
        // U+0130 unchanged. Apply the char-level rules so the pattern/text stay aligned.
        if (invariant) {
            StringBuilder sb = new StringBuilder(str.length());
            for (int i = 0; i < str.length(); i++) {
                sb.append(toLower(str.charAt(i)));
            }
            return sb.toString();
        }
        return str.toLowerCase(locale);
    }

    public char toLowerCase(char c) {
        return toLower(c);
    }

    public String toLowerCase(String str) {
        return toLower(str);
    }

    public char toUpper(char c) {
        // .NET ICU: only the INVARIANT culture leaves U+0131 (ı) unchanged; regular
        // cultures map it to 'I'. Turkish additionally maps 'i' to U+0130 (İ).
        if (c == '\u0131') {
            return invariant ? c : 'I';
        }
        if (isTurkishLike() && c == 'i') {
            return '\u0130';
        }
        return Character.toUpperCase(c);
    }

    public String toUpper(String str) {
        if (str == null) return null;
        // Invariant: .NET invariant leaves U+0131 (ı) unchanged; Java's full uppercase
        // mapping would produce 'I'. Apply the char-level rules.
        if (invariant) {
            StringBuilder sb = new StringBuilder(str.length());
            for (int i = 0; i < str.length(); i++) {
                sb.append(toUpper(str.charAt(i)));
            }
            return sb.toString();
        }
        return str.toUpperCase(locale);
    }

    public char toUpperCase(char c) {
        return toUpper(c);
    }

    public String toUpperCase(String str) {
        return toUpper(str);
    }

    private boolean isTurkishLike() {
        String lang = locale.getLanguage();
        return lang.equals("tr") || lang.equals("az");
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

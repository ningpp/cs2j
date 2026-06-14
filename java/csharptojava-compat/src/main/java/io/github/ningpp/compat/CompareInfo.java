package io.github.ningpp.compat;

import java.util.Locale;

/**
 * Stub for System.Globalization.CompareInfo.
 * Wraps java.text.Collator for locale-aware string comparison.
 */
public class CompareInfo {
    private final Locale locale;

    public CompareInfo(Locale locale) {
        this.locale = locale != null ? locale : Locale.ROOT;
    }

    public String getName() {
        return locale.toLanguageTag();
    }

    public int getLCID() {
        return 0;
    }

    // --- Comparison ---

    public int compare(String string1, String string2) {
        if (string1 == string2) return 0;
        if (string1 == null) return -1;
        if (string2 == null) return 1;
        return java.text.Collator.getInstance(locale).compare(string1, string2);
    }

    public int compare(String string1, String string2, int options) {
        // Ignore options for stub; use default comparison
        return compare(string1, string2);
    }

    public int compare(String string1, int offset1, int length1,
                       String string2, int offset2, int length2) {
        String s1 = string1.substring(offset1, offset1 + length1);
        String s2 = string2.substring(offset2, offset2 + length2);
        return compare(s1, s2);
    }

    // --- IndexOf ---

    public int indexOf(String source, String value) {
        if (source == null || value == null) return -1;
        return source.indexOf(value);
    }

    public int indexOf(String source, char value) {
        if (source == null) return -1;
        return source.indexOf(value);
    }

    public int indexOf(String source, String value, int startIndex) {
        if (source == null || value == null) return -1;
        return source.indexOf(value, startIndex);
    }

    public int indexOf(String source, String value, int startIndex, int count) {
        if (source == null || value == null) return -1;
        int idx = source.indexOf(value, startIndex);
        if (idx >= 0 && idx + value.length() <= startIndex + count) return idx;
        return -1;
    }

    // --- LastIndexOf ---

    public int lastIndexOf(String source, String value) {
        if (source == null || value == null) return -1;
        return source.lastIndexOf(value);
    }

    public int lastIndexOf(String source, char value) {
        if (source == null) return -1;
        return source.lastIndexOf(value);
    }

    public int lastIndexOf(String source, String value, int startIndex) {
        if (source == null || value == null) return -1;
        return source.lastIndexOf(value, startIndex);
    }

    // --- IsPrefix / IsSuffix ---

    public boolean isPrefix(String source, String prefix) {
        if (source == null || prefix == null) return false;
        return source.startsWith(prefix);
    }

    public boolean isSuffix(String source, String suffix) {
        if (source == null || suffix == null) return false;
        return source.endsWith(suffix);
    }

    // --- Static factory ---

    public static CompareInfo getCompareInfo(int culture) {
        return new CompareInfo(Locale.ROOT);
    }

    public static CompareInfo getCompareInfo(String name) {
        return new CompareInfo(Locale.forLanguageTag(name.replace('_', '-')));
    }

    @Override
    public String toString() {
        return "CompareInfo [" + locale.toLanguageTag() + "]";
    }
}

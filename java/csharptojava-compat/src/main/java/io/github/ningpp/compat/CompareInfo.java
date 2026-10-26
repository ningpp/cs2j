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
        if (string1 == string2) return 0;
        if (string1 == null) return -1;
        if (string2 == null) return 1;
        // C# CompareInfo.Compare(string, string, CompareOptions) HONOURS the options.
        // This method used to discard them ("Ignore options for stub"), so even
        // CompareOptions.IgnoreCase produced a case-SENSITIVE answer: "hello" vs "HELLO"
        // returned -1 where .NET returns 0. Callers that pass None still take exactly the
        // same path as before, so the fix cannot change an option-less comparison.
        if ((options & CompareOptions.OrdinalIgnoreCase) != 0) {
            return Integer.signum(string1.compareToIgnoreCase(string2));
        }
        if ((options & CompareOptions.Ordinal) != 0) {
            return Integer.signum(string1.compareTo(string2));
        }
        java.text.Collator collator = java.text.Collator.getInstance(locale);
        // CompareOptions are collator STRENGTHS: IgnoreNonSpace compares base letters only
        // (PRIMARY), IgnoreCase folds case but keeps accents (SECONDARY), and no option keeps
        // the collator's default TERTIARY culture-aware order. IgnoreSymbols/IgnoreKanaType/
        // IgnoreWidth have no Collator equivalent and are approximated by the same strength.
        if ((options & CompareOptions.IgnoreNonSpace) != 0) {
            collator.setStrength(java.text.Collator.PRIMARY);
        } else if ((options & CompareOptions.IgnoreCase) != 0) {
            collator.setStrength(java.text.Collator.SECONDARY);
        }
        // .NET's contract is a negative/zero/positive result, and the ported assertions
        // compare against -1/0/1 exactly, so normalise the magnitude.
        return Integer.signum(collator.compare(string1, string2));
    }

    public int compare(String string1, int offset1, int length1,
                       String string2, int offset2, int length2) {
        String s1 = string1.substring(offset1, offset1 + length1);
        String s2 = string2.substring(offset2, offset2 + length2);
        return compare(s1, s2);
    }

    /**
     * C# CompareInfo.GetHashCode(string value, CompareOptions options) — a hash code
     * for the string that is consistent with {@link #compare(String, String, int)} for
     * the same options. Declared static so that both the C# static-looking call site
     * and an instance call site (Java allows a static method to be invoked through a
     * reference) keep working.
     *
     * @param value The string for which a hash code is returned.
     * @param options The compare options that control the hash; only IgnoreCase
     *        changes the result, and the Ordinal/OrdinalIgnoreCase flags are rejected
     *        exactly as in C#.
     * @return A hash code for the string.
     */
    public static int getHashCode(String value, int options) {
        if (value == null) {
            throw new ArgumentNullException("value");
        }
        if ((options & (CompareOptions.Ordinal | CompareOptions.OrdinalIgnoreCase)) != 0) {
            throw new ArgumentException("CompareOptions.Ordinal and CompareOptions.OrdinalIgnoreCase are not valid options for GetHashCode.");
        }
        if ((options & CompareOptions.IgnoreCase) != 0) {
            // Case-insensitive comparison treats strings that differ only by case as
            // equal, so the hash folds the case away too. The fold is the invariant
            // one because this helper is static and therefore has no culture.
            return value.toLowerCase(Locale.ROOT).hashCode();
        }
        return value.hashCode();
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

    // --- IndexOf, options-aware ---

    // CompareOptions is a bag of int constants, so an options-aware IndexOf CANNOT
    // overload against indexOf(String, String, int startIndex) — the compiler binds
    // CompareOptions.Ordinal (0x40000000) as a startIndex and the search always
    // misses. The converter maps the (…, CompareOptions) shapes here instead.

    public int indexOfOptions(String source, String value) {
        return indexOf(source, value);
    }

    public int indexOfOptions(String source, char value) {
        return indexOf(source, value);
    }

    public int indexOfOptions(String source, String value, int options) {
        if (source == null || value == null) return -1;
        if ((options & CompareOptions.OrdinalIgnoreCase) != 0) {
            int limit = source.length() - value.length();
            for (int i = 0; i <= limit; i++) {
                if (source.regionMatches(true, i, value, 0, value.length())) return i;
            }
            return -1;
        }
        // Ordinal and the culture-aware stub both resolve to the plain index search:
        // java.text.Collator has no search primitive, and the ported XPath string
        // functions (contains / substring-before / substring-after) only depend on
        // the ordinal result.
        return source.indexOf(value);
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

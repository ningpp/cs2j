package io.github.ningpp.compat;

import java.util.Locale;

/**
 * Minimal compat stub for System.StringComparer.
 * Provides commonly used string equality/comparison semantics.
 */
public final class StringComparer implements CSharpGenericEqualityComparer<String>, CSharpGenericComparer<String> {

    private static final StringComparer ORDINAL = new StringComparer(false, false, null);
    private static final StringComparer ORDINAL_IGNORE_CASE = new StringComparer(true, false, null);
    private static final StringComparer CURRENT_CULTURE_IGNORE_CASE = new StringComparer(true, true, Locale.getDefault());
    private static final StringComparer INVARIANT_CULTURE_IGNORE_CASE = new StringComparer(true, false, Locale.ROOT);

    private final boolean ignoreCase;
    private final boolean cultureAware;
    private final Locale locale;

    private StringComparer(boolean ignoreCase, boolean cultureAware, Locale locale) {
        this.ignoreCase = ignoreCase;
        this.cultureAware = cultureAware;
        this.locale = locale;
    }

    public static StringComparer getOrdinal() {
        return ORDINAL;
    }

    public static StringComparer getOrdinalIgnoreCase() {
        return ORDINAL_IGNORE_CASE;
    }

    public static StringComparer getCurrentCultureIgnoreCase() {
        return CURRENT_CULTURE_IGNORE_CASE;
    }

    public static StringComparer getInvariantCultureIgnoreCase() {
        return INVARIANT_CULTURE_IGNORE_CASE;
    }

    @Override
    public boolean equals(String x, String y) {
        if (x == null) return y == null;
        if (y == null) return false;
        if (ignoreCase) {
            Locale l = locale != null ? locale : Locale.ROOT;
            return x.equalsIgnoreCase(y) || x.toLowerCase(l).equals(y.toLowerCase(l));
        }
        return x.equals(y);
    }

    @Override
    public int hashCode(String obj) {
        if (obj == null) return 0;
        Locale l = locale != null ? locale : Locale.ROOT;
        return ignoreCase ? obj.toLowerCase(l).hashCode() : obj.hashCode();
    }

    @Override
    public int compare(String x, String y) {
        if (x == null && y == null) return 0;
        if (x == null) return -1;
        if (y == null) return 1;
        if (ignoreCase) {
            Locale l = locale != null ? locale : Locale.ROOT;
            return x.toLowerCase(l).compareTo(y.toLowerCase(l));
        }
        return x.compareTo(y);
    }
}

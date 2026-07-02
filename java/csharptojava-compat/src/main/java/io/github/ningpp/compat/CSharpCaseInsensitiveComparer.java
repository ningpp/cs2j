package io.github.ningpp.compat;

public class CSharpCaseInsensitiveComparer implements CSharpComparer {
    private static final CSharpCaseInsensitiveComparer INSTANCE = new CSharpCaseInsensitiveComparer();

    public static CSharpCaseInsensitiveComparer getDefault() { return INSTANCE; }

    @Override
    @SuppressWarnings("unchecked")
    public int compare(Object a, Object b) {
        if (a instanceof String sa && b instanceof String sb) {
            return String.CASE_INSENSITIVE_ORDER.compare(sa, sb);
        }
        if (a instanceof Comparable) return ((Comparable<Object>) a).compareTo(b);
        throw new IllegalArgumentException("Objects must be comparable");
    }
}

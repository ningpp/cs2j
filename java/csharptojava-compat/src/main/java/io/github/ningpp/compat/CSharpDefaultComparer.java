package io.github.ningpp.compat;

public class CSharpDefaultComparer implements CSharpComparer {
    private static final CSharpDefaultComparer INSTANCE = new CSharpDefaultComparer();

    public static CSharpDefaultComparer getDefault() { return INSTANCE; }

    @Override
    @SuppressWarnings("unchecked")
    public int compare(Object a, Object b) {
        if (a == null && b == null) return 0;
        if (a == null) return -1;
        if (b == null) return 1;
        if (a instanceof Comparable) return ((Comparable<Object>) a).compareTo(b);
        throw new IllegalArgumentException("Object must implement Comparable");
    }
}

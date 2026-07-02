package io.github.ningpp.compat;

public class CSharpDefaultComparerGeneric<T> implements CSharpGenericComparer<T> {

    @SuppressWarnings("rawtypes")
    private static final CSharpDefaultComparerGeneric INSTANCE = new CSharpDefaultComparerGeneric<>();

    @SuppressWarnings("unchecked")
    public static <T> CSharpDefaultComparerGeneric<T> getDefault() {
        return INSTANCE;
    }

    private CSharpDefaultComparerGeneric() {
    }

    @Override
    @SuppressWarnings("unchecked")
    public int compare(T a, T b) {
        if (a == null && b == null) return 0;
        if (a == null) return -1;
        if (b == null) return 1;
        if (a instanceof Comparable) {
            return ((Comparable<T>) a).compareTo(b);
        }
        throw new IllegalArgumentException("Object must implement Comparable");
    }
}

package io.github.ningpp.compat;

import java.util.Comparator;

public class CSharpDefaultComparerGeneric<T> implements CSharpGenericComparer<T> {

    @SuppressWarnings("rawtypes")
    private static final CSharpDefaultComparerGeneric INSTANCE = new CSharpDefaultComparerGeneric<>();

    private final Comparator<T> comparator;

    @SuppressWarnings("unchecked")
    public static <T> CSharpDefaultComparerGeneric<T> getDefault() {
        return INSTANCE;
    }

    public static <T> CSharpDefaultComparerGeneric<T> create(Comparator<T> comparator) {
        return new CSharpDefaultComparerGeneric<>(comparator);
    }

    private CSharpDefaultComparerGeneric() {
        this.comparator = null;
    }

    private CSharpDefaultComparerGeneric(Comparator<T> comparator) {
        this.comparator = comparator;
    }

    @Override
    @SuppressWarnings("unchecked")
    public int compare(T a, T b) {
        if (comparator != null) {
            return comparator.compare(a, b);
        }
        if (a == null && b == null) return 0;
        if (a == null) return -1;
        if (b == null) return 1;
        if (a instanceof Comparable) {
            return ((Comparable<T>) a).compareTo(b);
        }
        throw new IllegalArgumentException("Object must implement Comparable");
    }
}

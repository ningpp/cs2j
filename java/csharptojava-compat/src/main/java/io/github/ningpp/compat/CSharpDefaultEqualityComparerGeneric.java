package io.github.ningpp.compat;

import java.util.Objects;

public class CSharpDefaultEqualityComparerGeneric<T> implements CSharpGenericEqualityComparer<T> {

    @SuppressWarnings("rawtypes")
    private static final CSharpDefaultEqualityComparerGeneric INSTANCE = new CSharpDefaultEqualityComparerGeneric<>();

    @SuppressWarnings("unchecked")
    public static <T> CSharpDefaultEqualityComparerGeneric<T> getDefault() {
        return INSTANCE;
    }

    private CSharpDefaultEqualityComparerGeneric() {
    }

    @Override
    public boolean equals(T x, T y) {
        return Objects.equals(x, y);
    }

    @Override
    public int hashCode(T obj) {
        return Objects.hashCode(obj);
    }
}

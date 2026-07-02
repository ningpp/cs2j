package io.github.ningpp.compat;

public interface CSharpGenericEqualityComparer<T> {
    boolean equals(T x, T y);
    int hashCode(T obj);
}

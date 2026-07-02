package io.github.ningpp.compat;

@FunctionalInterface
public interface CSharpGenericComparer<T> {
    int compare(T a, T b);
}

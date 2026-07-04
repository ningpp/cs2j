package io.github.ningpp.compat;

import java.util.Comparator;

@FunctionalInterface
public interface CSharpGenericComparer<T> extends Comparator<T> {
    int compare(T a, T b);
}

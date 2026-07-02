package io.github.ningpp.compat;

public interface CSharpReadOnlyList<T> extends CSharpReadOnlyCollection<T> {
    T get(int index);
}

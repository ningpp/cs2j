package io.github.ningpp.compat;

public interface CSharpIterable<T> extends Iterable<T> {
    @Override
    CSharpGenericEnumerator<T> iterator();
}

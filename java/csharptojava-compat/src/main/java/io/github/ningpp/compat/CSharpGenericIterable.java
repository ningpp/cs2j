package io.github.ningpp.compat;

public interface CSharpGenericIterable<T> extends Iterable<T> {
    @Override
    CSharpGenericEnumerator<T> iterator();
}

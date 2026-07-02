package io.github.ningpp.compat;

public interface CSharpGenericIList<T> extends CSharpICollection<T> {
    int indexOf(Object o);
    void insert(int index, T item);
    void removeAt(int index);
    T get(int index);
    T set(int index, T value);
}

package io.github.ningpp.compat;

public interface CSharpICollection<T> extends CSharpGenericIterable<T> {
    boolean add(T item);
    void clear();
    boolean contains(Object o);
    void copyTo(T[] array, int arrayIndex);
    boolean remove(Object o);
    int getCount();
    boolean getIsReadOnly();

    default int size() {
        return getCount();
    }
}

package io.github.ningpp.compat;

public interface CSharpIList extends CSharpCollection {
    int add(Object value);
    void clear();
    boolean contains(Object value);
    int indexOf(Object value);
    void insert(int index, Object value);
    boolean getIsFixedSize();
    boolean getIsReadOnly();
    void remove(Object value);
    void removeAt(int index);
    Object get(int index);
    Object set(int index, Object value);
}

package io.github.ningpp.compat;

public interface CSharpCollection extends CSharpIterable {
    default int size() { return getCount(); }
    default int getCount() { return size(); }

    void copyTo(Object[] array, int index);

    boolean getIsSynchronized();
    Object getSyncRoot();
}

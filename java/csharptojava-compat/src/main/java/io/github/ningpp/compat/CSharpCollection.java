package io.github.ningpp.compat;

public interface CSharpCollection extends Iterable {
    default int size() {
        return getCount();
    }

    default int getCount() {
        return size();
    }

    void copyTo(CSharpArray array, int index);

    boolean getIsSynchronized();

    Object getSyncRoot();
}

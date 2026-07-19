package io.github.ningpp.compat;

public interface CSharpCollection extends CSharpIterable<Object> {
    default int size() { return getCount(); }
    default int getCount() { return size(); }

    void copyTo(CSharpArray array, int index);

    default void copyTo(Object[] array, int index) {
        copyTo(CSharpArray.of(array), index);
    }

    boolean getIsSynchronized();
    Object getSyncRoot();
}

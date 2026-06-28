package io.github.ningpp.compat;

public interface IDictionaryEnumerator extends CSharpEnumerator<Object> {
    DictionaryEntry getEntry();

    Object getKey();

    Object getValue();

    @Override
    default Object getCurrent() {
        return getEntry();
    }

    @Override
    default boolean hasNext() {
        return moveNext();
    }

    @Override
    default Object next() {
        return getCurrent();
    }
}

package io.github.ningpp.compat;

import java.util.Iterator;
import java.util.NoSuchElementException;

public interface CSharpEnumerator extends Iterator<Object> {
    static CSharpEnumerator from(Iterator<?> iterator) {
        return new IteratorBackedCSharpEnumerator(iterator);
    }

    boolean moveNext();
    Object getCurrent();

    default void reset() {
        throw new UnsupportedOperationException("Reset is not supported");
    }

    final class IteratorBackedCSharpEnumerator implements CSharpEnumerator {
        private final Iterator<?> iterator;
        private Object current;
        private boolean hasCurrent;

        IteratorBackedCSharpEnumerator(Iterator<?> iterator) {
            this.iterator = iterator;
        }

        @Override public boolean moveNext() {
            if (!iterator.hasNext()) { current = null; hasCurrent = false; return false; }
            current = iterator.next();
            hasCurrent = true;
            return true;
        }

        @Override public Object getCurrent() {
            if (!hasCurrent) throw new NoSuchElementException();
            return current;
        }

        @Override public boolean hasNext() { return iterator.hasNext(); }
        @Override public Object next() { return iterator.next(); }
    }
}

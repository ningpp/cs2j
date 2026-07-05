package io.github.ningpp.compat;

import java.util.Iterator;
import java.util.NoSuchElementException;

public interface CSharpGenericEnumerator<T> extends Iterator<T> {
    static <T> CSharpGenericEnumerator<T> from(Iterator<T> iterator) {
        return new IteratorBackedCSharpEnumerator<>(iterator);
    }

    boolean moveNext();
    T getCurrent();

    default void reset() {
        throw new UnsupportedOperationException("Reset is not supported");
    }

    final class IteratorBackedCSharpEnumerator<T> implements CSharpGenericEnumerator<T> {
        private final Iterator<T> iterator;
        private T current;
        private boolean hasCurrent;

        IteratorBackedCSharpEnumerator(Iterator<T> iterator) {
            this.iterator = iterator;
        }

        @Override
        public boolean moveNext() {
            if (!iterator.hasNext()) {
                current = null;
                hasCurrent = false;
                return false;
            }
            current = iterator.next();
            hasCurrent = true;
            return true;
        }

        @Override
        public T getCurrent() {
            if (!hasCurrent) throw new NoSuchElementException();
            return current;
        }

        @Override
        public boolean hasNext() {
            return iterator.hasNext();
        }

        @Override
        public T next() {
            return iterator.next();
        }

        @Override
        public void remove() {
            iterator.remove();
        }
    }
}

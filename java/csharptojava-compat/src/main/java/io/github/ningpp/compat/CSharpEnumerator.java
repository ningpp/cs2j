package io.github.ningpp.compat;

import java.util.Iterator;
import java.util.NoSuchElementException;

/**
 * C# IEnumerator-shaped contract for converted code.
 *
 * C# MoveNext() advances and Current is a stable read. Java Iterator.hasNext()
 * is only a lookahead check and next() advances. The converter maps
 * IEnumerator<T> to this interface, and wraps ordinary Java iterators with
 * {@link #from(Iterator)} at explicit GetEnumerator() call sites.
 */
public interface CSharpEnumerator<T> extends Iterator<T> {
    static <T> CSharpEnumerator<T> from(Iterator<T> iterator) {
        return new IteratorBackedCSharpEnumerator<>(iterator);
    }

    boolean moveNext();

    T getCurrent();

    default void reset() {
        throw new UnsupportedOperationException("Reset is not supported for Java iterators");
    }

    final class IteratorBackedCSharpEnumerator<T> implements CSharpEnumerator<T> {
        private final Iterator<T> iterator;
        private T current;
        private boolean hasCurrent;

        private IteratorBackedCSharpEnumerator(Iterator<T> iterator) {
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
            if (!hasCurrent) {
                throw new NoSuchElementException();
            }
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
    }
}

package io.github.ningpp.compat;

import java.util.Iterator;
import java.util.NoSuchElementException;

public interface CSharpGenericEnumerator<T> extends CSharpEnumerator, Iterator<T> {
    @SuppressWarnings("unchecked")
    static <T> CSharpGenericEnumerator<T> from(Iterator<? extends T> iterator) {
        return new IteratorBackedCSharpEnumerator<T>((Iterator<T>) iterator);
    }

    boolean moveNext();
    @Override
    T getCurrent();

    /** Iterator bridge — hasNext() delegates to moveNext(). */
    @Override
    default boolean hasNext() {
        return moveNext();
    }

    /** Iterator bridge — next() returns the current element. */
    @Override
    default T next() {
        return getCurrent();
    }

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
            // The wrapped iterator may itself be a .NET-style enumerator; delegate to
            // its moveNext so index semantics and reset() survive the wrapping.
            if (iterator instanceof CSharpGenericEnumerator<?>) {
                @SuppressWarnings("unchecked")
                CSharpGenericEnumerator<T> cs2j = (CSharpGenericEnumerator<T>) iterator;
                return cs2j.moveNext();
            }
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
            if (iterator instanceof CSharpGenericEnumerator<?>) {
                @SuppressWarnings("unchecked")
                CSharpGenericEnumerator<T> cs2j = (CSharpGenericEnumerator<T>) iterator;
                return cs2j.getCurrent();
            }
            // .NET IEnumerator.Current before first MoveNext / after the end throws
            // InvalidOperationException (mapped to IllegalStateException).
            if (!hasCurrent) throw new IllegalStateException("Enumeration has not started or has already finished.");
            return current;
        }

        @Override
        public void reset() {
            if (iterator instanceof CSharpGenericEnumerator<?>) {
                @SuppressWarnings("unchecked")
                CSharpGenericEnumerator<T> cs2j = (CSharpGenericEnumerator<T>) iterator;
                cs2j.reset();
                return;
            }
            throw new UnsupportedOperationException("Reset is not supported");
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

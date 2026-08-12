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

    /**
     * Adapts a non-generic CSharpEnumerator into a CSharpGenericEnumerator&lt;T&gt;.
     * Used when a GetEnumerator() implementation (returning System.Collections.IEnumerator
     * in C#) is returned from an iterator() method whose Java return type was upgraded
     * to CSharpGenericEnumerator&lt;Object&gt;. A CSharpEnumerator is NOT a java.util.Iterator,
     * so the from(Iterator) overload cannot accept it.
     */
    @SuppressWarnings("unchecked")
    static <T> CSharpGenericEnumerator<T> fromEnumerator(CSharpEnumerator enumerator) {
        if (enumerator instanceof CSharpGenericEnumerator<?>) {
            return (CSharpGenericEnumerator<T>) enumerator;
        }
        return new EnumeratorBackedCSharpEnumerator<T>(enumerator);
    }

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

    /**
     * Wraps a non-generic CSharpEnumerator, delegating the .NET-style enumeration
     * protocol (moveNext/getCurrent/reset) so the value satisfies the generic
     * CSharpGenericEnumerator contract.
     */
    final class EnumeratorBackedCSharpEnumerator<T> implements CSharpGenericEnumerator<T> {
        private final CSharpEnumerator enumerator;

        EnumeratorBackedCSharpEnumerator(CSharpEnumerator enumerator) {
            this.enumerator = enumerator;
        }

        @Override
        public boolean moveNext() {
            return enumerator.moveNext();
        }

        @Override
        @SuppressWarnings("unchecked")
        public T getCurrent() {
            return (T) enumerator.getCurrent();
        }

        @Override
        public void reset() {
            enumerator.reset();
        }

        @Override
        public boolean hasNext() {
            return moveNext();
        }

        @Override
        public T next() {
            return getCurrent();
        }
    }
}

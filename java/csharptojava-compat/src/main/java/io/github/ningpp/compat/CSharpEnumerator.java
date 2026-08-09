package io.github.ningpp.compat;

import java.util.Iterator;
import java.util.NoSuchElementException;

public interface CSharpEnumerator extends CSharpGenericEnumerator<Object> {
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
            // The wrapped iterator may itself be a .NET-style enumerator
            // (CSharpGenericEnumerator) converted by the generator; delegate to its
            // moveNext so index-based semantics and reset() survive the wrapping.
            if (iterator instanceof CSharpGenericEnumerator<?> cs2j) {
                return cs2j.moveNext();
            }
            if (!iterator.hasNext()) { current = null; hasCurrent = false; return false; }
            current = iterator.next();
            hasCurrent = true;
            return true;
        }

        @Override public Object getCurrent() {
            if (iterator instanceof CSharpGenericEnumerator<?> cs2j) {
                return cs2j.getCurrent();
            }
            // .NET IEnumerator.Current before first MoveNext / after the end throws
            // InvalidOperationException (mapped to IllegalStateException).
            if (!hasCurrent) throw new IllegalStateException("Enumeration has not started or has already finished.");
            return current;
        }

        @Override public void reset() {
            if (iterator instanceof CSharpGenericEnumerator<?> cs2j) {
                cs2j.reset();
                return;
            }
            throw new UnsupportedOperationException("Reset is not supported");
        }

        @Override public boolean hasNext() { return iterator.hasNext(); }
        @Override public Object next() { return iterator.next(); }
    }
}

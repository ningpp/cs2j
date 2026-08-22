package io.github.ningpp.compat;

import java.util.Arrays;
import java.util.Spliterator;

/**
 * Null-preserving, array-backed CSharpGenericIterable view.
 * Mirrors C# arrays exposed as IEnumerable&lt;T&gt;: iteration and size read
 * through to the source array, and toArray() returns the SAME array instance so
 * reference-identity semantics survive an array -> IEnumerable -> (T[]) cast
 * round trip (e.g. SegWithIndex.Equals comparing the backing array by identity).
 * Structural modifications (add/remove/clear) throw UnsupportedOperationException.
 */
public class CSharpArrayIterable<T> implements CSharpGenericIterable<T> {
    private final T[] items;

    public CSharpArrayIterable(T[] items) {
        this.items = items;
    }

    @Override
    public CSharpGenericEnumerator<T> iterator() {
        return new CSharpGenericEnumerator<T>() {
            private int index = -1;

            @Override
            public boolean moveNext() {
                int size = items.length;
                if (index >= size) {
                    return false;
                }
                index++;
                return index < size;
            }

            @Override
            public T getCurrent() {
                if (index < 0 || index >= items.length) {
                    throw new IllegalStateException("Enumeration has not started or has already finished.");
                }
                return items[index];
            }

            @Override
            public boolean hasNext() {
                return index + 1 < items.length;
            }

            @Override
            public T next() {
                if (!hasNext()) {
                    throw new java.util.NoSuchElementException();
                }
                index++;
                return items[index];
            }

            @Override
            public void reset() {
                index = -1;
            }

            @Override
            public void remove() {
                throw new UnsupportedOperationException();
            }
        };
    }

    @Override
    public Spliterator<T> spliterator() {
        return Arrays.spliterator(items);
    }

    @Override
    public int size() {
        return items.length;
    }

    @Override
    public boolean isEmpty() {
        return items.length == 0;
    }

    @Override
    public boolean contains(Object o) {
        for (T item : items) {
            if (java.util.Objects.equals(item, o)) {
                return true;
            }
        }
        return false;
    }

    @Override
    public Object[] toArray() {
        return items;
    }

    @Override
    @SuppressWarnings("unchecked")
    public <E> E[] toArray(E[] a) {
        // Identity-preserving: an empty destination array of a compatible
        // component type returns the backing array itself. Generated cast-back
        // sites always pass new T[0], so the original array instance survives.
        if (a.length == 0
                && a.getClass().getComponentType().isAssignableFrom(items.getClass().getComponentType())) {
            return (E[]) items;
        }
        return Arrays.asList(items).toArray(a);
    }
}

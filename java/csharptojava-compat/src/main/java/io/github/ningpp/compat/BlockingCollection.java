package io.github.ningpp.compat;

import java.util.ArrayList;
import java.util.Collection;
import java.util.Iterator;
import java.util.List;

/**
 * Compat for System.Collections.Concurrent.BlockingCollection&lt;T&gt; — a minimal
 * blocking queue wrapper. Blocking semantics (Take/Add timeouts) are not modeled;
 * the collection behaves as a plain queue for serialization purposes.
 */
public class BlockingCollection<T> implements CSharpGenericIterable<T> {
    private final List<T> items = new ArrayList<>();

    public BlockingCollection() {
    }

    public BlockingCollection(Collection<? extends T> c) {
        items.addAll(c);
    }

    public BlockingCollection(int boundedCapacity) {
    }

    public boolean add(T item) {
        items.add(item);
        return true;
    }

    public boolean remove(Object item) {
        return items.remove(item);
    }

    public T take() {
        if (items.isEmpty()) {
            throw new java.util.NoSuchElementException("BlockingCollection is empty");
        }
        return items.remove(0);
    }

    public T tryTake() {
        if (items.isEmpty()) {
            return null;
        }
        return items.remove(0);
    }

    public T peek() {
        if (items.isEmpty()) {
            return null;
        }
        return items.get(0);
    }

    public T tryPeek() {
        if (items.isEmpty()) {
            return null;
        }
        return items.get(0);
    }

    public boolean tryTake(ObjectHolder<T> outResult) {
        if (items.isEmpty()) {
            return false;
        }
        outResult.value = items.remove(0);
        return true;
    }

    public int getCount() {
        return items.size();
    }

    public int size() {
        return items.size();
    }

    public boolean isEmpty() {
        return items.isEmpty();
    }

    public boolean contains(Object o) {
        return items.contains(o);
    }

    public void clear() {
        items.clear();
    }

    public Object[] toArray() {
        return items.toArray();
    }

    @Override
    public CSharpGenericEnumerator<T> iterator() {
        return CSharpGenericEnumerator.from(items.iterator());
    }

    @Override
    public String toString() {
        return items.toString();
    }
}

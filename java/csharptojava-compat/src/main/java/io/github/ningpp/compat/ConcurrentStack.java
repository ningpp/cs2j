package io.github.ningpp.compat;

import java.util.Iterator;
import java.util.concurrent.ConcurrentLinkedDeque;

/**
 * Compat for System.Collections.Concurrent.ConcurrentStack&lt;T&gt; — a LIFO
 * collection. Internally backed by ConcurrentLinkedDeque (LIFO push/pop semantics).
 */
public class ConcurrentStack<T> implements CSharpGenericIterable<T> {
    private final ConcurrentLinkedDeque<T> items = new ConcurrentLinkedDeque<>();

    public ConcurrentStack() {
    }

    public ConcurrentStack(CSharpGenericIterable<? extends T> c) {
        for (T item : c) {
            items.push(item);
        }
    }

    public void push(T item) {
        items.push(item);
    }

    public T pop() {
        return items.pop();
    }

    public T peek() {
        return items.peekFirst();
    }

    public boolean tryPop(ObjectHolder<T> outResult) {
        T v = items.pollFirst();
        if (v == null) {
            return false;
        }
        outResult.value = v;
        return true;
    }

    public boolean tryPeek(ObjectHolder<T> outResult) {
        T v = items.peekFirst();
        if (v == null) {
            return false;
        }
        outResult.value = v;
        return true;
    }

    public boolean add(T item) {
        items.push(item);
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

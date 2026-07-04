package io.github.ningpp.compat;

import java.util.AbstractSet;
import java.util.Iterator;
import java.util.Set;

public class CSharpGenericIterableSet<T> extends AbstractSet<T> implements CSharpGenericIterable<T> {
    private final Set<T> delegate;

    public CSharpGenericIterableSet(Set<T> delegate) {
        this.delegate = delegate;
    }

    @Override
    public CSharpGenericEnumerator<T> iterator() {
        return CSharpGenericEnumerator.from(delegate.iterator());
    }

    @Override
    public int size() {
        return delegate.size();
    }

    @Override
    public boolean contains(Object o) {
        return delegate.contains(o);
    }

    @Override
    public boolean remove(Object o) {
        return delegate.remove(o);
    }

    @Override
    public void clear() {
        delegate.clear();
    }

    @Override
    public boolean add(T t) {
        return delegate.add(t);
    }

    @Override
    public boolean isEmpty() {
        return delegate.isEmpty();
    }

    @Override
    public Object[] toArray() {
        return delegate.toArray();
    }

    @Override
    public <T1> T1[] toArray(T1[] a) {
        return delegate.toArray(a);
    }
}

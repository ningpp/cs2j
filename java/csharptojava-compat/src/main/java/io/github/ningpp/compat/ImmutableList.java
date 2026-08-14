package io.github.ningpp.compat;

import java.util.ArrayList;
import java.util.Collection;
import java.util.List;

/**
 * Compat for System.Collections.Immutable.ImmutableList&lt;T&gt; — minimal
 * immutable list wrapper used by the converted tests.
 */
public class ImmutableList<T> implements IImmutableList<T> {
    private final List<T> items;

    public ImmutableList() {
        this.items = new ArrayList<>();
    }

    private ImmutableList(List<T> items) {
        this.items = items;
    }

    public static <T> ImmutableList<T> createRange(CSharpGenericIterable<T> source) {
        List<T> list = new ArrayList<>();
        for (T item : source) {
            list.add(item);
        }
        return new ImmutableList<>(list);
    }

    public static <T> ImmutableList<T> createRange(Iterable<T> source) {
        List<T> list = new ArrayList<>();
        for (T item : source) {
            list.add(item);
        }
        return new ImmutableList<>(list);
    }

    public static <T> ImmutableList<T> createRange(Collection<T> source) {
        return new ImmutableList<>(new ArrayList<>(source));
    }

    /** C# ImmutableList.Create&lt;T&gt;() — an empty immutable list. */
    public static <T> ImmutableList<T> create() {
        return new ImmutableList<>();
    }

    public int size() {
        return items.size();
    }

    public int getCount() {
        return items.size();
    }

    public boolean isEmpty() {
        return items.isEmpty();
    }

    public T get(int index) {
        return items.get(index);
    }

    public List<T> toList() {
        return new ArrayList<>(items);
    }

    @Override
    public java.util.Iterator<T> iterator() {
        return items.iterator();
    }

    @Override
    public ImmutableList<T> add(T value) {
        List<T> copy = new ArrayList<>(items);
        copy.add(value);
        return new ImmutableList<>(copy);
    }

    @Override
    public ImmutableList<T> addRange(Iterable<? extends T> values) {
        List<T> copy = new ArrayList<>(items);
        for (T value : values) {
            copy.add(value);
        }
        return new ImmutableList<>(copy);
    }

    @Override
    public ImmutableList<T> clear() {
        return new ImmutableList<>();
    }

    @Override
    public ImmutableList<T> insert(int index, T element) {
        List<T> copy = new ArrayList<>(items);
        copy.add(index, element);
        return new ImmutableList<>(copy);
    }

    @Override
    public ImmutableList<T> insertRange(int index, Iterable<? extends T> values) {
        List<T> copy = new ArrayList<>(items);
        int i = index;
        for (T value : values) {
            copy.add(i++, value);
        }
        return new ImmutableList<>(copy);
    }

    @Override
    public ImmutableList<T> removeCSharp(T value, CSharpGenericEqualityComparer<T> equalityComparer) {
        int index = indexOf(value, 0, items.size(), equalityComparer);
        if (index < 0) {
            return this;
        }
        List<T> copy = new ArrayList<>(items);
        copy.remove(index);
        return new ImmutableList<>(copy);
    }

    @Override
    public ImmutableList<T> removeAll(java.util.function.Predicate<T> match) {
        List<T> copy = new ArrayList<>(items.size());
        for (T item : items) {
            if (!match.test(item)) {
                copy.add(item);
            }
        }
        return new ImmutableList<>(copy);
    }

    @Override
    public ImmutableList<T> removeAt(int index) {
        List<T> copy = new ArrayList<>(items);
        copy.remove(index);
        return new ImmutableList<>(copy);
    }

    @Override
    public ImmutableList<T> removeRange(Iterable<? extends T> values, CSharpGenericEqualityComparer<T> equalityComparer) {
        List<T> copy = new ArrayList<>(items);
        for (T value : values) {
            int index = indexOf(value, 0, copy.size(), equalityComparer);
            if (index >= 0) {
                copy.remove(index);
            }
        }
        return new ImmutableList<>(copy);
    }

    @Override
    public ImmutableList<T> removeRange(int index, int count) {
        List<T> copy = new ArrayList<>(items);
        copy.subList(index, index + count).clear();
        return new ImmutableList<>(copy);
    }

    @Override
    public ImmutableList<T> replace(T oldValue, T newValue, CSharpGenericEqualityComparer<T> equalityComparer) {
        int index = indexOf(oldValue, 0, items.size(), equalityComparer);
        if (index < 0) {
            return this;
        }
        List<T> copy = new ArrayList<>(items);
        copy.set(index, newValue);
        return new ImmutableList<>(copy);
    }

    @Override
    public ImmutableList<T> setItem(int index, T value) {
        List<T> copy = new ArrayList<>(items);
        copy.set(index, value);
        return new ImmutableList<>(copy);
    }

    /** C# ImmutableList&lt;T&gt;.IndexOf(T, int, int, IEqualityComparer&lt;T&gt;) — returns -1 if not found. */
    public int indexOf(T item, int index, int count, CSharpGenericEqualityComparer<T> equalityComparer) {
        int end = Math.min(index + count, items.size());
        for (int i = index; i < end; i++) {
            T candidate = items.get(i);
            if (equalityComparer != null) {
                if (equalityComparer.equals(item, candidate)) {
                    return i;
                }
            } else if (item == null ? candidate == null : item.equals(candidate)) {
                return i;
            }
        }
        return -1;
    }

    /** C# ImmutableList&lt;T&gt;.LastIndexOf(T, int, int, IEqualityComparer&lt;T&gt;) — returns -1 if not found. */
    public int lastIndexOf(T item, int index, int count, CSharpGenericEqualityComparer<T> equalityComparer) {
        int start = Math.max(index - count + 1, 0);
        for (int i = index; i >= start; i--) {
            T candidate = items.get(i);
            if (equalityComparer != null) {
                if (equalityComparer.equals(item, candidate)) {
                    return i;
                }
            } else if (item == null ? candidate == null : item.equals(candidate)) {
                return i;
            }
        }
        return -1;
    }

    @Override
    public String toString() {
        return items.toString();
    }
}

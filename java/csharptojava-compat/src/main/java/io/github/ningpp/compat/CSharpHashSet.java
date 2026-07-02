package io.github.ningpp.compat;

import java.util.Collection;
import java.util.HashSet;
import java.util.Iterator;
import java.util.LinkedHashSet;
import java.util.Objects;
import java.util.Set;

public class CSharpHashSet<T> implements CSharpISet<T>, Cloneable {
    private final LinkedHashSet<T> set;
    private final CSharpGenericEqualityComparer<T> comparer;

    public CSharpHashSet() {
        this.set = new LinkedHashSet<>();
        this.comparer = null;
    }

    public CSharpHashSet(int capacity) {
        this.set = new LinkedHashSet<>(capacity);
        this.comparer = null;
    }

    public CSharpHashSet(Collection<? extends T> c) {
        this.set = new LinkedHashSet<>(c);
        this.comparer = null;
    }

    // CSharpICollection<T> methods

    @Override
    public boolean add(T item) {
        return set.add(item);
    }

    @Override
    public void clear() {
        set.clear();
    }

    @Override
    public boolean contains(Object o) {
        return set.contains(o);
    }

    @Override
    public void copyTo(T[] array, int arrayIndex) {
        int i = arrayIndex;
        for (T item : set) {
            array[i++] = item;
        }
    }

    @Override
    public boolean remove(Object o) {
        return set.remove(o);
    }

    @Override
    public int getCount() {
        return set.size();
    }

    @Override
    public boolean getIsReadOnly() {
        return false;
    }

    @Override
    public CSharpGenericEnumerator<T> iterator() {
        return CSharpGenericEnumerator.from(set.iterator());
    }

    // CSharpISet<T> methods

    @Override
    public void exceptWith(Collection<? extends T> other) {
        set.removeAll(other);
    }

    @Override
    public void intersectWith(Collection<? extends T> other) {
        set.retainAll(other);
    }

    @Override
    public boolean isProperSubsetOf(Collection<? extends T> other) {
        Set<T> otherSet = toSet(other);
        if (set.size() >= otherSet.size()) return false;
        return otherSet.containsAll(set);
    }

    @Override
    public boolean isProperSupersetOf(Collection<? extends T> other) {
        Set<T> otherSet = toSet(other);
        if (otherSet.size() >= set.size()) return false;
        return set.containsAll(otherSet);
    }

    @Override
    public boolean isSubsetOf(Collection<? extends T> other) {
        Set<T> otherSet = toSet(other);
        return otherSet.containsAll(set);
    }

    @Override
    public boolean isSupersetOf(Collection<? extends T> other) {
        return set.containsAll(other);
    }

    @Override
    public boolean overlaps(Collection<? extends T> other) {
        for (T item : other) {
            if (set.contains(item)) return true;
        }
        return false;
    }

    @Override
    public boolean setEquals(Collection<? extends T> other) {
        Set<T> otherSet = toSet(other);
        return set.size() == otherSet.size() && set.containsAll(otherSet);
    }

    @Override
    public void symmetricExceptWith(Collection<? extends T> other) {
        for (T item : other) {
            if (!set.add(item)) {
                set.remove(item);
            }
        }
    }

    @Override
    public void unionWith(Collection<? extends T> other) {
        set.addAll(other);
    }

    // HashSet-specific methods

    public int ensureCapacity(int minCapacity) {
        // LinkedHashSet doesn't expose ensureCapacity
        return set.size();
    }

    public void trimExcess() {
        // LinkedHashSet doesn't expose trimToSize
        // No-op for now; the internal table will be trimmed on next modification
    }

    public CSharpGenericEqualityComparer<T> getComparer() {
        return comparer;
    }

    @Override
    public CSharpHashSet<T> clone() {
        return new CSharpHashSet<>(set);
    }

    // Helper

    private Set<T> toSet(Collection<? extends T> other) {
        if (other instanceof Set) {
            @SuppressWarnings("unchecked")
            Set<T> s = (Set<T>) other;
            return s;
        }
        return new HashSet<>(other);
    }
}

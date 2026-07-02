package io.github.ningpp.compat;

import java.util.Collection;
import java.util.Comparator;
import java.util.HashSet;
import java.util.Iterator;
import java.util.NavigableSet;
import java.util.Objects;
import java.util.Set;
import java.util.TreeSet;
import java.util.function.Predicate;

public class CSharpSortedSet<T> implements CSharpISet<T>, Cloneable {
    private final TreeSet<T> set;
    private final CSharpGenericEqualityComparer<T> comparer;

    public CSharpSortedSet() {
        this.set = new TreeSet<>();
        this.comparer = null;
    }

    public CSharpSortedSet(Comparator<? super T> comparator) {
        this.set = new TreeSet<>(comparator);
        this.comparer = null;
    }

    public CSharpSortedSet(Collection<? extends T> c) {
        this.set = new TreeSet<>(c);
        this.comparer = null;
    }

    public CSharpSortedSet(Collection<? extends T> c, Comparator<? super T> comparator) {
        this.set = new TreeSet<>(comparator);
        this.set.addAll(c);
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

    // SortedSet-specific methods

    public T getMin() {
        return set.isEmpty() ? null : set.first();
    }

    public T getMax() {
        return set.isEmpty() ? null : set.last();
    }

    public CSharpSortedSet<T> getViewBetween(T lower, T upper) {
        NavigableSet<T> subSet = set.subSet(lower, true, upper, true);
        return new CSharpSortedSet<>(subSet, set.comparator());
    }

    public CSharpSortedSet<T> getReverse() {
        Comparator<? super T> currentComp = set.comparator();
        @SuppressWarnings("unchecked")
        Comparator<? super T> reverseComp = currentComp != null
            ? currentComp.reversed()
            : (a, b) -> ((Comparable<Object>) b).compareTo(a);
        return new CSharpSortedSet<>(set, reverseComp);
    }

    @SuppressWarnings("unchecked")
    public Comparator<? super T> getComparer() {
        Comparator<? super T> comp = set.comparator();
        if (comp != null) return comp;
        return (a, b) -> ((Comparable<Object>) a).compareTo(b);
    }

    public int removeWhere(Predicate<? super T> match) {
        int removed = 0;
        Iterator<T> it = set.iterator();
        while (it.hasNext()) {
            if (match.test(it.next())) {
                it.remove();
                removed++;
            }
        }
        return removed;
    }

    public void trimExcess() {
        // TreeSet doesn't expose trimToSize; no-op
    }

    @Override
    public CSharpSortedSet<T> clone() {
        return new CSharpSortedSet<>(set, set.comparator());
    }

    // Private constructor for getViewBetween and getReverse
    private CSharpSortedSet(NavigableSet<T> subSet, Comparator<? super T> comparator) {
        this.set = new TreeSet<>(comparator);
        this.set.addAll(subSet);
        this.comparer = null;
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

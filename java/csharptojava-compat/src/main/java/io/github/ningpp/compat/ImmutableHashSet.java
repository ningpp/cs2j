package io.github.ningpp.compat;

import java.util.AbstractSet;
import java.util.ArrayList;
import java.util.Iterator;
import java.util.LinkedHashSet;
import java.util.List;
import java.util.Set;

/**
 * Compat for System.Collections.Immutable.ImmutableHashSet&lt;T&gt;.
 */
public class ImmutableHashSet<T> implements IImmutableSet<T> {
    /** C# ImmutableHashSet.Empty — the empty singleton. */
    @SuppressWarnings({"rawtypes", "unchecked"})
    public static final ImmutableHashSet Empty = new ImmutableHashSet();

    private final Set<T> items;
    private final IEqualityComparer<T> comparer;

    public ImmutableHashSet() {
        this.comparer = EqualityComparer.getDefault();
        this.items = new LinkedHashSet<>();
    }

    private ImmutableHashSet(Set<T> items, IEqualityComparer<T> comparer) {
        this.items = items;
        this.comparer = comparer;
    }

    private static <T> IEqualityComparer<T> effectiveComparer(IEqualityComparer<T> equalityComparer) {
        return equalityComparer == null ? EqualityComparer.getDefault() : equalityComparer;
    }

    private static <T> Set<T> newSet(IEqualityComparer<T> comparer) {
        if (comparer == EqualityComparer.getDefault()) {
            return new LinkedHashSet<>();
        }
        return new ComparerSet<>(comparer);
    }

    /** C# ImmutableHashSet.Create&lt;T&gt;() — an empty set. */
    public static <T> ImmutableHashSet<T> create() {
        return new ImmutableHashSet<>();
    }

    /** C# ImmutableHashSet.Create&lt;T&gt;(item) / Create&lt;T&gt;(items). */
    @SafeVarargs
    public static <T> ImmutableHashSet<T> create(T... items) {
        ImmutableHashSet<T> set = create();
        for (T item : items) {
            set.items.add(item);
        }
        return set;
    }

    /** C# ImmutableHashSet.Create&lt;T&gt;(IEqualityComparer&lt;T&gt;). */
    public static <T> ImmutableHashSet<T> create(IEqualityComparer<T> equalityComparer) {
        return new ImmutableHashSet<>(newSet(effectiveComparer(equalityComparer)), effectiveComparer(equalityComparer));
    }

    /** C# ImmutableHashSet.Create&lt;T&gt;(IEqualityComparer&lt;T&gt;, item) / Create&lt;T&gt;(IEqualityComparer&lt;T&gt;, items). */
    @SafeVarargs
    public static <T> ImmutableHashSet<T> create(IEqualityComparer<T> equalityComparer, T... items) {
        ImmutableHashSet<T> set = create(equalityComparer);
        for (T item : items) {
            set.items.add(item);
        }
        return set;
    }

    public static <T> ImmutableHashSet<T> createRange(Iterable<T> source) {
        Set<T> set = new LinkedHashSet<>();
        for (T item : source) {
            set.add(item);
        }
        return new ImmutableHashSet<>(set, EqualityComparer.getDefault());
    }

    public static <T> ImmutableHashSet<T> createRange(CSharpGenericIterable<T> source) {
        Set<T> set = new LinkedHashSet<>();
        for (T item : source) {
            set.add(item);
        }
        return new ImmutableHashSet<>(set, EqualityComparer.getDefault());
    }

    /**
     * C#-named CreateRange used by the JSON immutable-collection factory, which
     * looks up a static generic method literally named "CreateRange" to build the
     * deserialized instance.
     */
    public static <T> ImmutableHashSet<T> CreateRange(CSharpGenericIterable<T> source) {
        return createRange(source);
    }

    /** C# ImmutableHashSet.CreateRange&lt;T&gt;(IEqualityComparer&lt;T&gt;, IEnumerable&lt;T&gt;). */
    public static <T> ImmutableHashSet<T> createRange(IEqualityComparer<T> equalityComparer, Iterable<T> source) {
        IEqualityComparer<T> comparer = effectiveComparer(equalityComparer);
        Set<T> set = newSet(comparer);
        for (T item : source) {
            set.add(item);
        }
        return new ImmutableHashSet<>(set, comparer);
    }

    /** C# ImmutableHashSet.CreateBuilder&lt;T&gt;(). */
    public static <T> Builder<T> createBuilder() {
        return new Builder<>();
    }

    /** C# ImmutableHashSet.CreateBuilder&lt;T&gt;(IEqualityComparer&lt;T&gt;). */
    public static <T> Builder<T> createBuilder(IEqualityComparer<T> equalityComparer) {
        return new Builder<>(equalityComparer);
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

    public boolean contains(T item) {
        return items.contains(item);
    }

    /** C# ImmutableHashSet&lt;T&gt;.KeyComparer — the equality comparer used by this set. */
    public IEqualityComparer<T> getKeyComparer() {
        return comparer;
    }

    /** C# ImmutableHashSet&lt;T&gt;.Add(item) — returns a new set with the item added. */
    public ImmutableHashSet<T> add(T item) {
        Set<T> copy = newSet(comparer);
        copy.addAll(items);
        copy.add(item);
        return new ImmutableHashSet<>(copy, comparer);
    }

    /** C# ImmutableHashSet&lt;T&gt;.Clear() — returns an empty set. */
    public ImmutableHashSet<T> clear() {
        return new ImmutableHashSet<>(newSet(comparer), comparer);
    }

    /** C# ImmutableHashSet&lt;T&gt;.Except(other) — returns a set without the items of other. */
    public ImmutableHashSet<T> except(Iterable<T> other) {
        Set<T> copy = newSet(comparer);
        copy.addAll(items);
        for (T item : other) {
            copy.remove(item);
        }
        return new ImmutableHashSet<>(copy, comparer);
    }

    public ImmutableHashSet<T> except(CSharpGenericIterable<T> other) {
        return except((Iterable<T>) other);
    }

    /** C# ImmutableHashSet&lt;T&gt;.Intersect(other) — returns the set of common items. */
    public ImmutableHashSet<T> intersect(Iterable<T> other) {
        Set<T> copy = newSet(comparer);
        for (T item : other) {
            if (contains(item)) {
                copy.add(item);
            }
        }
        return new ImmutableHashSet<>(copy, comparer);
    }

    public ImmutableHashSet<T> intersect(CSharpGenericIterable<T> other) {
        return intersect((Iterable<T>) other);
    }

    /** C# ImmutableHashSet&lt;T&gt;.Remove(item) — returns a new set without the item. */
    public ImmutableHashSet<T> removeCSharp(T item) {
        Set<T> copy = newSet(comparer);
        copy.addAll(items);
        copy.remove(item);
        return new ImmutableHashSet<>(copy, comparer);
    }

    /** C# ImmutableHashSet&lt;T&gt;.SymmetricExcept(other) — returns the symmetric difference. */
    public ImmutableHashSet<T> symmetricExcept(Iterable<T> other) {
        Set<T> copy = newSet(comparer);
        for (T item : items) {
            if (!containsInOther(other, item)) {
                copy.add(item);
            }
        }
        for (T item : other) {
            if (!contains(item)) {
                copy.add(item);
            }
        }
        return new ImmutableHashSet<>(copy, comparer);
    }

    public ImmutableHashSet<T> symmetricExcept(CSharpGenericIterable<T> other) {
        return symmetricExcept((Iterable<T>) other);
    }

    /** C# ImmutableHashSet&lt;T&gt;.Union(other) — returns a set with all items. */
    public ImmutableHashSet<T> union(Iterable<T> other) {
        Set<T> copy = newSet(comparer);
        copy.addAll(items);
        for (T item : other) {
            copy.add(item);
        }
        return new ImmutableHashSet<>(copy, comparer);
    }

    public ImmutableHashSet<T> union(CSharpGenericIterable<T> other) {
        return union((Iterable<T>) other);
    }

    /** C# ImmutableHashSet&lt;T&gt;.WithComparer(equalityComparer) — a set with the same items but a new comparer. */
    public ImmutableHashSet<T> withComparer(IEqualityComparer<T> equalityComparer) {
        IEqualityComparer<T> newComparer = effectiveComparer(equalityComparer);
        if (newComparer == comparer) {
            return this;
        }
        Set<T> copy = newSet(newComparer);
        copy.addAll(items);
        return new ImmutableHashSet<>(copy, newComparer);
    }

    /** C# ImmutableHashSet&lt;T&gt;.ToBuilder() — a mutable builder seeded with the current items. */
    public Builder<T> toBuilder() {
        return new Builder<>(comparer, items);
    }

    public boolean isProperSubsetOf(CSharpGenericIterable<T> other) {
        return isSubsetOf(other) && size() < other.size();
    }

    public boolean isProperSupersetOf(CSharpGenericIterable<T> other) {
        return isSupersetOf(other) && size() > other.size();
    }

    public boolean isSubsetOf(CSharpGenericIterable<T> other) {
        for (T item : this) {
            if (!other.contains(item)) {
                return false;
            }
        }
        return true;
    }

    public boolean isSupersetOf(CSharpGenericIterable<T> other) {
        for (T item : other) {
            if (!contains(item)) {
                return false;
            }
        }
        return true;
    }

    public boolean overlaps(CSharpGenericIterable<T> other) {
        for (T item : this) {
            if (other.contains(item)) {
                return true;
            }
        }
        return false;
    }

    public boolean setEquals(CSharpGenericIterable<T> other) {
        return size() == other.size() && isSubsetOf(other);
    }

    /** C# ImmutableHashSet&lt;T&gt;.TryGetValue(equalValue, out actualValue). */
    public boolean tryGetValue(T equalValue, ObjectHolder<T> actualValue) {
        for (T item : items) {
            if (comparer.equals(item, equalValue)) {
                actualValue.value = item;
                return true;
            }
        }
        return false;
    }

    public Set<T> toSet() {
        return new LinkedHashSet<>(items);
    }

    public java.util.stream.Stream<T> stream() {
        return items.stream();
    }

    public java.util.Spliterator<T> spliterator() {
        return items.spliterator();
    }

    @Override
    public java.util.Iterator<T> iterator() {
        return items.iterator();
    }

    @Override
    public String toString() {
        return items.toString();
    }

    private static <T> boolean containsInOther(Iterable<T> other, T item) {
        if (other instanceof java.util.Collection) {
            return ((java.util.Collection<?>) other).contains(item);
        }
        for (T element : other) {
            if (java.util.Objects.equals(element, item)) {
                return true;
            }
        }
        return false;
    }

    /** Set implementation backed by an IEqualityComparer&lt;T&gt; instead of equals/hashCode. */
    private static class ComparerSet<T> extends AbstractSet<T> {
        private final IEqualityComparer<T> comparer;
        private final List<T> list = new ArrayList<>();

        ComparerSet(IEqualityComparer<T> comparer) {
            this.comparer = comparer;
        }

        private int indexOf(Object o) {
            @SuppressWarnings("unchecked")
            T value = (T) o;
            for (int i = 0; i < list.size(); i++) {
                if (comparer.equals(list.get(i), value)) {
                    return i;
                }
            }
            return -1;
        }

        @Override
        public int size() {
            return list.size();
        }

        @Override
        public Iterator<T> iterator() {
            return list.iterator();
        }

        @Override
        public boolean contains(Object o) {
            return indexOf(o) >= 0;
        }

        @Override
        public boolean add(T item) {
            if (contains(item)) {
                return false;
            }
            list.add(item);
            return true;
        }

        @Override
        public boolean remove(Object o) {
            int index = indexOf(o);
            if (index < 0) {
                return false;
            }
            list.remove(index);
            return true;
        }
    }

    /**
     * Compat for System.Collections.Immutable.ImmutableHashSet&lt;T&gt;.Builder — the
     * mutable companion of ImmutableHashSet&lt;T&gt;.
     */
    public static class Builder<T> implements Iterable<T> {
        private Set<T> items;
        private IEqualityComparer<T> comparer;

        private Builder() {
            this.comparer = EqualityComparer.getDefault();
            this.items = new LinkedHashSet<>();
        }

        private Builder(IEqualityComparer<T> equalityComparer) {
            this.comparer = effectiveComparer(equalityComparer);
            this.items = newSet(this.comparer);
        }

        private Builder(IEqualityComparer<T> comparer, Set<T> items) {
            this.comparer = comparer;
            this.items = newSet(comparer);
            this.items.addAll(items);
        }

        public boolean add(T item) {
            return items.add(item);
        }

        public void clear() {
            items.clear();
        }

        public boolean contains(T item) {
            return items.contains(item);
        }

        public int getCount() {
            return items.size();
        }

        public IEqualityComparer<T> getKeyComparer() {
            return comparer;
        }

        public void setKeyComparer(IEqualityComparer<T> value) {
            IEqualityComparer<T> newComparer = effectiveComparer(value);
            if (newComparer == comparer) {
                return;
            }
            Set<T> rebuilt = newSet(newComparer);
            rebuilt.addAll(items);
            this.comparer = newComparer;
            this.items = rebuilt;
        }

        /** C# Builder.ToImmutable() — a snapshot of the builder contents. */
        public ImmutableHashSet<T> toImmutable() {
            Set<T> copy = newSet(comparer);
            copy.addAll(items);
            return new ImmutableHashSet<>(copy, comparer);
        }

        public boolean remove(T item) {
            return items.remove(item);
        }

        public void exceptWith(CSharpGenericIterable<T> other) {
            for (T item : other) {
                items.remove(item);
            }
        }

        public void intersectWith(CSharpGenericIterable<T> other) {
            items.retainAll(other);
        }

        public void symmetricExceptWith(CSharpGenericIterable<T> other) {
            Set<T> toAdd = newSet(comparer);
            for (T item : other) {
                if (items.contains(item)) {
                    items.remove(item);
                } else {
                    toAdd.add(item);
                }
            }
            items.addAll(toAdd);
        }

        public void unionWith(CSharpGenericIterable<T> other) {
            for (T item : other) {
                items.add(item);
            }
        }

        public boolean isProperSubsetOf(CSharpGenericIterable<T> other) {
            return isSubsetOf(other) && getCount() < other.size();
        }

        public boolean isProperSupersetOf(CSharpGenericIterable<T> other) {
            return isSupersetOf(other) && getCount() > other.size();
        }

        public boolean isSubsetOf(CSharpGenericIterable<T> other) {
            for (T item : items) {
                if (!other.contains(item)) {
                    return false;
                }
            }
            return true;
        }

        public boolean isSupersetOf(CSharpGenericIterable<T> other) {
            for (T item : other) {
                if (!contains(item)) {
                    return false;
                }
            }
            return true;
        }

        public boolean overlaps(CSharpGenericIterable<T> other) {
            for (T item : items) {
                if (other.contains(item)) {
                    return true;
                }
            }
            return false;
        }

        public boolean setEquals(CSharpGenericIterable<T> other) {
            return getCount() == other.size() && isSubsetOf(other);
        }

        public boolean tryGetValue(T equalValue, ObjectHolder<T> actualValue) {
            for (T item : items) {
                if (comparer.equals(item, equalValue)) {
                    actualValue.value = item;
                    return true;
                }
            }
            return false;
        }

        @Override
        public Iterator<T> iterator() {
            return items.iterator();
        }

        @Override
        public String toString() {
            return items.toString();
        }
    }
}
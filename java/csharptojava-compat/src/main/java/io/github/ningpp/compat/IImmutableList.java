package io.github.ningpp.compat;

import java.util.ArrayList;
import java.util.List;

/**
 * Compat for System.Collections.Immutable.IImmutableList&lt;T&gt;.
 */
public interface IImmutableList<T> extends Iterable<T> {
    int size();

    boolean isEmpty();

    T get(int index);

    /**
     * C# IImmutableList&lt;T&gt;.ToArray is generated as toList(); wrappers produced
     * from classes that only implement IImmutableList&lt;T&gt; rely on the default.
     */
    default List<T> toList() {
        List<T> result = new ArrayList<>();
        for (T item : this) {
            result.add(item);
        }
        return result;
    }

    /** C# IImmutableList&lt;T&gt;.Add(T) — returns the new list. */
    default IImmutableList<T> add(T value) {
        return this;
    }

    /** C# IImmutableList&lt;T&gt;.AddRange(IEnumerable&lt;T&gt;). */
    default IImmutableList<T> addRange(Iterable<? extends T> items) {
        return this;
    }

    /** C# IImmutableList&lt;T&gt;.Clear(). */
    default IImmutableList<T> clear() {
        return this;
    }

    /** C# IImmutableList&lt;T&gt;.Insert(int, T). */
    default IImmutableList<T> insert(int index, T element) {
        return this;
    }

    /** C# IImmutableList&lt;T&gt;.InsertRange(int, IEnumerable&lt;T&gt;). */
    default IImmutableList<T> insertRange(int index, Iterable<? extends T> items) {
        return this;
    }

    /** C# IImmutableList&lt;T&gt;.Remove(T, IEqualityComparer&lt;T&gt;) — generated as removeCSharp. */
    default IImmutableList<T> removeCSharp(T value, CSharpGenericEqualityComparer<T> equalityComparer) {
        return this;
    }

    /** C# IImmutableList&lt;T&gt;.RemoveAll(Predicate&lt;T&gt;). */
    default IImmutableList<T> removeAll(java.util.function.Predicate<T> match) {
        return this;
    }

    /** C# IImmutableList&lt;T&gt;.RemoveAt(int). */
    default IImmutableList<T> removeAt(int index) {
        return this;
    }

    /** C# IImmutableList&lt;T&gt;.RemoveRange(IEnumerable&lt;T&gt;, IEqualityComparer&lt;T&gt;). */
    default IImmutableList<T> removeRange(Iterable<? extends T> items, CSharpGenericEqualityComparer<T> equalityComparer) {
        return this;
    }

    /** C# IImmutableList&lt;T&gt;.RemoveRange(int, int). */
    default IImmutableList<T> removeRange(int index, int count) {
        return this;
    }

    /** C# IImmutableList&lt;T&gt;.Replace(T, T, IEqualityComparer&lt;T&gt;). */
    default IImmutableList<T> replace(T oldValue, T newValue, CSharpGenericEqualityComparer<T> equalityComparer) {
        return this;
    }

    /** C# IImmutableList&lt;T&gt;.SetItem(int, T). */
    default IImmutableList<T> setItem(int index, T value) {
        return this;
    }
}

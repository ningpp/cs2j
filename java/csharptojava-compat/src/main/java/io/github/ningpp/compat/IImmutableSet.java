package io.github.ningpp.compat;

import java.util.Objects;

/**
 * Compat for System.Collections.Immutable.IImmutableSet&lt;T&gt;.
 */
public interface IImmutableSet<T> extends Iterable<T> {
    int size();

    boolean isEmpty();

    boolean contains(T item);

    default IImmutableSet<T> add(T value) {
        return this;
    }

    default IImmutableSet<T> clear() {
        return this;
    }

    default IImmutableSet<T> except(CSharpGenericIterable<T> other) {
        return this;
    }

    default IImmutableSet<T> intersect(CSharpGenericIterable<T> other) {
        return this;
    }

    default IImmutableSet<T> removeCSharp(T value) {
        return this;
    }

    default IImmutableSet<T> symmetricExcept(CSharpGenericIterable<T> other) {
        return this;
    }

    default IImmutableSet<T> union(CSharpGenericIterable<T> other) {
        return this;
    }

    default boolean isProperSubsetOf(CSharpGenericIterable<T> other) {
        return isSubsetOf(other) && size() < other.size();
    }

    default boolean isProperSupersetOf(CSharpGenericIterable<T> other) {
        return isSupersetOf(other) && size() > other.size();
    }

    default boolean isSubsetOf(CSharpGenericIterable<T> other) {
        for (T item : this) {
            if (!other.contains(item)) {
                return false;
            }
        }
        return true;
    }

    default boolean isSupersetOf(CSharpGenericIterable<T> other) {
        for (T item : other) {
            if (!contains(item)) {
                return false;
            }
        }
        return true;
    }

    default boolean overlaps(CSharpGenericIterable<T> other) {
        for (T item : this) {
            if (other.contains(item)) {
                return true;
            }
        }
        return false;
    }

    default boolean setEquals(CSharpGenericIterable<T> other) {
        return size() == other.size() && isSubsetOf(other);
    }

    default boolean tryGetValue(T equalValue, ObjectHolder<T> actualValue) {
        for (T item : this) {
            if (Objects.equals(item, equalValue)) {
                actualValue.value = item;
                return true;
            }
        }
        return false;
    }
}
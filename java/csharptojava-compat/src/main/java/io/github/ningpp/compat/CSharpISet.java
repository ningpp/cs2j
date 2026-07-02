package io.github.ningpp.compat;

import java.util.Collection;

public interface CSharpISet<T> extends CSharpICollection<T> {
    boolean add(T item);
    void exceptWith(Collection<? extends T> other);
    void intersectWith(Collection<? extends T> other);
    boolean isProperSubsetOf(Collection<? extends T> other);
    boolean isProperSupersetOf(Collection<? extends T> other);
    boolean isSubsetOf(Collection<? extends T> other);
    boolean isSupersetOf(Collection<? extends T> other);
    boolean overlaps(Collection<? extends T> other);
    boolean setEquals(Collection<? extends T> other);
    void symmetricExceptWith(Collection<? extends T> other);
    void unionWith(Collection<? extends T> other);
}

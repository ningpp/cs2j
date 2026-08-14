package io.github.ningpp.compat;

public interface CSharpISet<T> extends CSharpICollection<T> {
    boolean add(T item);
    void exceptWith(CSharpGenericIterable<T> other);
    void intersectWith(CSharpGenericIterable<T> other);
    boolean isProperSubsetOf(CSharpGenericIterable<T> other);
    boolean isProperSupersetOf(CSharpGenericIterable<T> other);
    boolean isSubsetOf(CSharpGenericIterable<T> other);
    boolean isSupersetOf(CSharpGenericIterable<T> other);
    boolean overlaps(CSharpGenericIterable<T> other);
    boolean setEquals(CSharpGenericIterable<T> other);
    void symmetricExceptWith(CSharpGenericIterable<T> other);
    void unionWith(CSharpGenericIterable<T> other);
}

package io.github.ningpp.compat;

/**
 * C# IReadOnlyCollection&lt;T&gt; compat view. The iterator return is raw because
 * generated read-only collections bridge the generic and non-generic enumerators
 * through a single erased <c>iterator()</c> (Object elements). Concrete compat
 * collections returning the typed enumerator still satisfy the raw override.
 */
public interface CSharpReadOnlyCollection<T> extends CSharpGenericIterable<T> {
    int getCount();

    @Override
    CSharpGenericEnumerator iterator();
}

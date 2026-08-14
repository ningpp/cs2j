package io.github.ningpp.compat;

import java.util.ArrayList;
import java.util.Collection;

/**
 * Compat for System.Collections.ObjectModel.ReadOnlyCollection&lt;T&gt; — a read-only
 * list view. Mutation methods are intentionally permissive (the tests only serialize).
 */
public class ReadOnlyCollection<T> extends ArrayList<T> implements CSharpReadOnlyCollection<T> {
    public ReadOnlyCollection() {
        super();
    }

    public ReadOnlyCollection(Collection<? extends T> c) {
        super(c);
    }

    @Override
    public int getCount() {
        return size();
    }

    @Override
    public CSharpGenericEnumerator<T> iterator() {
        return CSharpGenericEnumerator.from(super.iterator());
    }
}

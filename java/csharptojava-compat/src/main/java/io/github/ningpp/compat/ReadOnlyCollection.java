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

    /** C# new ReadOnlyCollection&lt;T&gt;(IList&lt;T&gt;) over the compat IList view. */
    @SuppressWarnings("unchecked")
    public ReadOnlyCollection(CSharpIList<?> source) {
        super();
        if (source != null) {
            for (Object item : source) {
                add((T) item);
            }
        }
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

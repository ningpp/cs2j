package io.github.ningpp.compat;

import java.util.function.BiPredicate;
import java.util.function.Predicate;

/** Adapts a C# indexed predicate Func&lt;T,int,bool&gt; to Predicate for filter. */
public final class IndexedPredicate<T> implements Predicate<T> {
    private final BiPredicate<? super T, ? super Integer> predicate;
    private int index;

    public IndexedPredicate(BiPredicate<? super T, ? super Integer> predicate) {
        this.predicate = predicate;
    }

    @Override
    public boolean test(T value) {
        return predicate.test(value, index++);
    }
}

package io.github.ningpp.compat;

import java.util.function.BiFunction;
import java.util.function.Function;

/**
 * Adapts a C# indexed selector <c>Func&lt;T,int,R&gt;</c> to a Java
 * {@link Function} for Stream.map. The index is per adapter instance (streams
 * are sequential here), matching C# Select's source-order index.
 */
public final class IndexedFunction<T, R> implements Function<T, R> {
    private final BiFunction<? super T, ? super Integer, ? extends R> selector;
    private int index;

    public IndexedFunction(BiFunction<? super T, ? super Integer, ? extends R> selector) {
        this.selector = selector;
    }

    @Override
    public R apply(T value) {
        return selector.apply(value, index++);
    }
}

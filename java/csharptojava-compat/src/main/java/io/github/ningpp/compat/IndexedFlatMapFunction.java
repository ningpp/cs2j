package io.github.ningpp.compat;

import java.util.function.BiFunction;
import java.util.function.Function;
import java.util.stream.Stream;
import java.util.stream.StreamSupport;

/**
 * C# {@code SelectMany((item, index) => ...)}: Java's {@code Stream.flatMap}
 * takes a unary selector, so this adapter feeds a running index and bridges the
 * selector result (an Iterable carrier or an array) into a Stream.
 */
public final class IndexedFlatMapFunction<T, R> implements Function<T, Stream<R>> {
    private final BiFunction<T, Integer, ?> selector;
    private int index;

    public IndexedFlatMapFunction(BiFunction<T, Integer, ?> selector) {
        this.selector = selector;
    }

    @Override
    @SuppressWarnings("unchecked")
    public Stream<R> apply(T item) {
        Iterable<R> iterable = (Iterable<R>) CSharpGenericIterable.fromAny(selector.apply(item, index++));
        return StreamSupport.stream(iterable.spliterator(), false);
    }
}

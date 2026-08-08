package io.github.ningpp.compat;

/**
 * Bridges C# System.Converter&lt;TInput, TOutput&gt; used by List&lt;T&gt;.ConvertAll.
 */
@FunctionalInterface
public interface Converter<TInput, TOutput> {
    TOutput convert(TInput input);
}

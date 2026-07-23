package io.github.ningpp.compat;

import java.util.function.BiConsumer;
import java.util.function.Consumer;

/**
 * A raw functional interface that can be assigned to either {@link Consumer}
 * or {@link BiConsumer} via unchecked conversion. Used by
 * {@link LambdaExpression#compile()} so the same method can return both
 * one-parameter and two-parameter field-assignment lambdas.
 */
@SuppressWarnings("rawtypes")
public interface UniversalConsumer extends Consumer, BiConsumer {
}

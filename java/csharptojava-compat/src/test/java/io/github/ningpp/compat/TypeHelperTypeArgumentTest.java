package io.github.ningpp.compat;

import org.junit.jupiter.api.Test;

import static org.junit.jupiter.api.Assertions.assertSame;

/**
 * C# {@code typeof(T)} for a class type parameter is erased in Java. The runtime
 * token is recovered from the instance's generic superclass chain, unless the
 * converter pinned the exact C# token (numeric types that share a Java wrapper)
 * in {@code __cs2j_typeArg_<DeclaringSimpleName>_<index>}.
 */
class TypeHelperTypeArgumentTest {

    @Test
    void resolvesDirectGenericSuperclassArgument() {
        assertSame(Boolean.class,
                TypeHelper.getTypeArgument(new BooleanConverter(), Generic.class, 0, Object.class));
    }

    @Test
    void resolvesThroughIntermediateGenericClass() {
        assertSame(String.class,
                TypeHelper.getTypeArgument(new Deep(), Generic.class, 0, Object.class));
    }

    @Test
    void pinnedTokenWinsOverErasedJavaWrapper() {
        assertSame(CSharpByte.class,
                TypeHelper.getTypeArgument(new PinnedConverter(), Generic.class, 0, Object.class));
    }

    @Test
    void fallsBackWhenNotResolvable() {
        assertSame(Object.class,
                TypeHelper.getTypeArgument(new NoBase(), Generic.class, 0, Object.class));
        assertSame(Object.class,
                TypeHelper.getTypeArgument(null, Generic.class, 0, Object.class));
    }

    static class Generic<T> {
    }

    static class BooleanConverter extends Generic<Boolean> {
    }

    static class Mid<U> extends Generic<U> {
    }

    static class Deep extends Mid<String> {
    }

    static class PinnedConverter extends Generic<Integer> {
        private static final Class<?> __cs2j_typeArg_Generic_0 = CSharpByte.class;
    }

    static class NoBase {
    }
}

package io.github.ningpp.compat;

/**
 * Carries the runtime value type for the C# generic helper shape
 * <code>AddToMap&lt;TKey,TValue,TCollection&gt;(..., new TCollection())</code>.
 * Java erases TCollection, so the call site (which knows the closed dictionary
 * value type) supplies its Class literal to {@link #withValueType}; the helper
 * body then creates the correct collection via {@link #createValue()}.
 */
public final class CollectionFactory {
    private static final ThreadLocal<Class<?>> CURRENT_VALUE_TYPE = new ThreadLocal<>();

    private CollectionFactory() {
    }

    public static void withValueType(Class<?> valueType, Runnable action) {
        Class<?> previous = CURRENT_VALUE_TYPE.get();
        CURRENT_VALUE_TYPE.set(valueType);
        try {
            action.run();
        } finally {
            if (previous == null) {
                CURRENT_VALUE_TYPE.remove();
            } else {
                CURRENT_VALUE_TYPE.set(previous);
            }
        }
    }

    /** Two-argument variant avoids capturing mutable loop variables in a lambda. */
    public static <A, B> void withValueType(
            Class<?> valueType, A first, B second,
            java.util.function.BiConsumer<A, B> action) {
        Class<?> previous = CURRENT_VALUE_TYPE.get();
        CURRENT_VALUE_TYPE.set(valueType);
        try {
            action.accept(first, second);
        } finally {
            if (previous == null) {
                CURRENT_VALUE_TYPE.remove();
            } else {
                CURRENT_VALUE_TYPE.set(previous);
            }
        }
    }

    /**
     * Three-argument variant (dictionary, key, value) avoids capturing mutable
     * loop variables in a lambda: the call site's variables are passed by value.
     */
    public static <A, B, C> void withValueType(
            Class<?> valueType, A first, B second, C third,
            TriConsumer<A, B, C> action) {
        Class<?> previous = CURRENT_VALUE_TYPE.get();
        CURRENT_VALUE_TYPE.set(valueType);
        try {
            action.accept(first, second, third);
        } finally {
            if (previous == null) {
                CURRENT_VALUE_TYPE.remove();
            } else {
                CURRENT_VALUE_TYPE.set(previous);
            }
        }
    }

    @FunctionalInterface
    public interface TriConsumer<A, B, C> {
        void accept(A a, B b, C c);
    }

    @SuppressWarnings("unchecked")
    public static <T> T createValue() {
        Class<?> valueType = CURRENT_VALUE_TYPE.get();
        if (valueType == null) {
            // addToMap's erased body creates the bucket without a class token;
            // MSAGL's bucket type is the Set<T> container.
            try {
                Class<?> fallback = Class.forName(
                    "Microsoft.Msagl.Core.DataStructures.Set");
                java.lang.reflect.Constructor<?> ctor = fallback.getDeclaredConstructor();
                ctor.setAccessible(true);
                return (T) ctor.newInstance();
            } catch (ReflectiveOperationException ignored) {
                throw new IllegalStateException(
                    "CollectionFactory.createValue() called outside withValueType(...)");
            }
        }
        return (T) Activator.createInstance(valueType);
    }
}

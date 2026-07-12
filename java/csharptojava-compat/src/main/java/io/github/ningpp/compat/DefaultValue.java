package io.github.ningpp.compat;

/**
 * Produces default values for generic type parameters, bridging the gap between
 * C# {@code default(T)} semantics (zero-initialized struct / null for reference types)
 * and Java type erasure.
 *
 * <p>Use {@link #of()} when the concrete type is not known at generation time.
 * Use {@link #of(Class)} when a runtime {@code Class<?>} token is available,
 * enabling true zero-initialized instances for value-type (struct) bindings.</p>
 */
public final class DefaultValue {

    private DefaultValue() {}

    /**
     * Returns {@code null} — the safe "not yet frozen" default.
     * When the caller has a runtime {@code Class<T>} token, prefer {@link #of(Class)}.
     */
    @SuppressWarnings("unchecked")
    public static <T> T of() {
        return null;
    }

    /**
     * Returns the default value for the type represented by the given class token:
     * a zero-initialized instance for types with a no-arg constructor,
     * the primitive zero value for wrapper/{@code int.class} etc.,
     * or {@code null} when instantiation fails.
     */
    @SuppressWarnings("unchecked")
    public static <T> T of(Class<T> type) {
        if (type == null) return null;
        return (T) getDefaultByClass(type);
    }

    /**
     * Accepts a wildcard {@code Class<?>} and returns the default value as {@code Object}.
     * Used when the converter cannot preserve the {@code Class<T>} token.
     */
    public static Object ofClass(Class<?> type) {
        if (type == null) return null;
        return getDefaultByClass(type);
    }

    private static Object getDefaultByClass(Class<?> type) {
        if (type == int.class)        return 0;
        if (type == long.class)       return 0L;
        if (type == short.class)      return (short)0;
        if (type == byte.class)       return (byte)0;
        if (type == float.class)      return 0.0f;
        if (type == double.class)     return 0.0;
        if (type == boolean.class)    return false;
        if (type == char.class)       return '\0';
        try {
            return type.getDeclaredConstructor().newInstance();
        } catch (Exception e) {
            return null;
        }
    }
}

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
    /** Accepts a TypeReference (from deserialize(TypeReference) helpers) and returns its default value. */
    public static <T> T of(com.fasterxml.jackson.core.type.TypeReference<?> typeRef) {
        Class<?> raw = TypeHelper.toRawClass(typeRef.getType());
        return (T) ofClass(raw);
    }

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
        if (type == int.class || type == Integer.class)        return 0;
        if (type == long.class || type == Long.class)         return 0L;
        if (type == short.class || type == Short.class)      return (short)0;
        if (type == byte.class || type == Byte.class)         return (byte)0;
        if (type == float.class || type == Float.class)      return 0.0f;
        if (type == double.class || type == Double.class)     return 0.0;
        if (type == boolean.class || type == Boolean.class)  return false;
        if (type == char.class || type == Character.class)   return '\0';
        // String and Object are reference types in C# — default(string) / default(object) is null
        if (type == String.class || type == Object.class)    return null;
        try {
            return type.getDeclaredConstructor().newInstance();
        } catch (Exception e) {
            return null;
        }
    }
}

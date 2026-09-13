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

    /**
     * The C# {@code default(T)} for the type a converter is declared over, as an OBJECT so the
     * caller can tell "default is null" from "default is the zero value".
     *
     * <p>The wrapper cases answer NULL, not their zero value. A Java wrapper class in the converted
     * model stands for a C# REFERENCE-OR-NULLABLE type, so {@code default(bool?)} and the boxed
     * form are null — that is the whole point of {@code JsonPropertyInfoOfT.GetMemberAndWriteJson},
     * whose guard is
     * <code>default(T) == null ? value == null : EqualityComparer.Default.Equals(default, value)</code>.
     * Answering {@code false} for {@code Boolean.class} made the guard take the value-type branch,
     * so {@code false.equals(null)} was false and a null member was written even with
     * IgnoreNullValues set. Measured: {@code JsonPropertyInfo1} printed
     * {@code ignoreDefOnWrite=true defaultT=false value=null} and still emitted {@code "B":null}.</p>
     *
     * <p>The primitive {@code Class} constants keep their zero values: {@code boolean.class} is C#'s
     * {@code bool}, a genuine value type whose default is {@code false}.</p>
     */
    private static Object getDefaultByClass(Class<?> type) {
        if (type == int.class)        return 0;
        if (type == long.class)       return 0L;
        if (type == short.class)      return (short)0;
        if (type == byte.class)       return (byte)0;
        if (type == float.class)      return 0.0f;
        if (type == double.class)     return 0.0;
        if (type == boolean.class)    return false;
        if (type == char.class)       return '\0';
        // Every reference type: a wrapper, String, Object, or a converted class.
        if (type == Integer.class || type == Long.class || type == Short.class
                || type == Byte.class || type == Float.class || type == Double.class
                || type == Boolean.class || type == Character.class) {
            return null;
        }
        if (type == String.class || type == Object.class)    return null;
        // A converted C# STRUCT is emitted as a Java value class and has a zero instance;
        // everything else the converter produced is a C# class, whose default(T) is NULL.
        // Creating an instance for a plain class made deserializing the JSON literal null at the
        // root return a fully constructed object instead of null — measured:
        // NullTests#rootObjectIsNull expected null and reported
        // "Value was not null / Actual: TestClassWithNull@…".
        if (!type.isValue()) {
            return null;
        }
        try {
            return type.getDeclaredConstructor().newInstance();
        } catch (Exception e) {
            return null;
        }
    }
}

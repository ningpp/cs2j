package io.github.ningpp.compat;

/**
 * Entry point for JsonSerializer.Deserialize&lt;ClosedGeneric&gt; calls where the
 * closed generic's element types must survive Java erasure.
 *
 * The converter emits
 * {@code JsonSerializerCompat.deserialize(new TypeReference<CSharpList<String>>() {}, json, options)}
 * for such calls. The anonymous TypeReference subclass keeps the full generic
 * signature in its bytecode, so {@link java.lang.reflect.ParameterizedType} can
 * recover the type arguments at runtime. They are registered with
 * {@link TypeHelper#registerParameterizedType} for the duration of the call so
 * converter factories resolve element/key/value types instead of erasing them
 * to Object (which produced JsonElement elements / unsupported dictionary keys).
 *
 * The actual read is delegated to the generated dotnet.text.json.JsonSerializer
 * via reflection — this class lives in the compat jar and cannot reference the
 * generated module at compile time.
 */
public final class JsonSerializerCompat {
    private JsonSerializerCompat() {
    }

    /** Mirrors JsonSerializer.Deserialize&lt;T&gt;(string json, JsonSerializerOptions?) for closed generics. */
    public static Object deserialize(com.fasterxml.jackson.core.type.TypeReference<?> typeRef,
                                     String json,
                                     Object options) {
        return deserializeInternal(typeRef, json, options);
    }

    /** Mirrors JsonSerializer.Deserialize&lt;T&gt;(string json) for closed generics. */
    public static Object deserialize(com.fasterxml.jackson.core.type.TypeReference<?> typeRef, String json) {
        return deserializeInternal(typeRef, json, null);
    }

    /** Mirrors JsonSerializer.Deserialize&lt;T&gt;(ReadOnlySpan&lt;byte&gt;, JsonSerializerOptions?) for closed generics. */
    public static Object deserialize(com.fasterxml.jackson.core.type.TypeReference<?> typeRef,
                                     ReadOnlySpan<Integer> utf8Json,
                                     Object options) {
        return deserializeInternal(typeRef, utf8Json, options);
    }

    /** Mirrors JsonSerializer.Deserialize&lt;T&gt;(ReadOnlySpan&lt;byte&gt;) for closed generics. */
    public static Object deserialize(com.fasterxml.jackson.core.type.TypeReference<?> typeRef,
                                     ReadOnlySpan<Integer> utf8Json) {
        return deserializeInternal(typeRef, utf8Json, null);
    }

    /** Mirrors JsonSerializer.Serialize&lt;T&gt;(T value, JsonSerializerOptions?) for closed generics. */
    public static String serialize(com.fasterxml.jackson.core.type.TypeReference<?> typeRef,
                                   Object value,
                                   Object options) {
        java.lang.reflect.Type type = typeRef.getType();
        Class<?> raw = TypeHelper.toRawClass(type);
        TypeHelper.beginTypeRegistration();
        TypeHelper.registerParameterizedType(type);
        try {
            Class<?> serializer = Class.forName("dotnet.text.json.JsonSerializer");
            if (options != null) {
                java.lang.reflect.Method m = serializer.getMethod("serialize", Class.class, Object.class, Class.class);
                return (String) m.invoke(null, raw, value, options);
            }
            java.lang.reflect.Method m = serializer.getMethod("serialize", Class.class, Object.class);
            return (String) m.invoke(null, raw, value);
        } catch (ReflectiveOperationException ex) {
            throw new RuntimeException("JsonSerializerCompat: failed to delegate to JsonSerializer.serialize", ex);
        } finally {
            TypeHelper.endTypeRegistration();
        }
    }

    /** Mirrors JsonSerializer.Serialize&lt;T&gt;(T value) for closed generics. */
    public static String serialize(com.fasterxml.jackson.core.type.TypeReference<?> typeRef, Object value) {
        return serialize(typeRef, value, null);
    }

    private static Object deserializeInternal(com.fasterxml.jackson.core.type.TypeReference<?> typeRef,
                                              Object jsonOrSpan,
                                              Object options) {
        java.lang.reflect.Type type = typeRef.getType();
        Class<?> raw = TypeHelper.toRawClass(type);
        // Register every nested layer (outer first) so element/key/value types
        // resolve during converter creation.
        TypeHelper.beginTypeRegistration();
        TypeHelper.registerParameterizedType(type);
        // The concrete type's own signature names the arguments an interface Type
        // cannot report (class StrList implements IList<string>).
        TypeHelper.registerConcreteType(raw);
        try {
            if (options == null) {
                // Use a FRESH options instance instead of the shared default: the
                // default's converter cache may hold a converter created without
                // this TypeReference's type arguments (element types erased to
                // Object → JsonElement elements) from an earlier test.
                try {
                    Class<?> optionsType = Class.forName("dotnet.text.json.JsonSerializerOptions");
                    options = optionsType.getConstructor().newInstance();
                } catch (ReflectiveOperationException ex) {
                    // Fall back to the shared default if construction fails.
                }
            }
            return invokeGeneratedDeserialize(raw, jsonOrSpan, options);
        } finally {
            TypeHelper.endTypeRegistration();
        }
    }

    private static Object invokeGeneratedDeserialize(Class<?> raw, Object jsonOrSpan, Object options) {
        // The generated JsonSerializer.deserialize methods use TRAILING Class<?>
        // params (e.g. deserialize(String json, JsonSerializerOptions options,
        // Class<?> tValueClass)) because AddRuntimeClassParametersForTypeParameterArrays
        // appends them after the declared C# parameters. Try the trailing order first,
        // then fall back to leading order (forward compatibility if the converter
        // changes the convention later).
        try {
            Class<?> serializer = Class.forName("dotnet.text.json.JsonSerializer");
            Class<?> optionsType = options != null ? options.getClass() : null;

            // Build candidate (paramTypes, args) pairs: trailing first, then leading.
            java.util.List<java.lang.reflect.Method> candidates = new java.util.ArrayList<>();
            java.util.List<Object[]> candidateArgs = new java.util.ArrayList<>();

            if (jsonOrSpan instanceof String s) {
                if (options != null) {
                    addCandidate(candidates, candidateArgs, serializer,
                        new Class<?>[]{String.class, optionsType, Class.class},
                        new Object[]{s, options, raw});
                } else {
                    addCandidate(candidates, candidateArgs, serializer,
                        new Class<?>[]{String.class, Class.class},
                        new Object[]{s, raw});
                }
            } else {
                if (options != null) {
                    addCandidate(candidates, candidateArgs, serializer,
                        new Class<?>[]{ReadOnlySpan.class, optionsType, Class.class},
                        new Object[]{jsonOrSpan, options, raw});
                } else {
                    addCandidate(candidates, candidateArgs, serializer,
                        new Class<?>[]{ReadOnlySpan.class, Class.class},
                        new Object[]{jsonOrSpan, raw});
                }
            }

            // Leading order (Class first) as fallback.
            if (jsonOrSpan instanceof String s) {
                if (options != null) {
                    addCandidate(candidates, candidateArgs, serializer,
                        new Class<?>[]{Class.class, String.class, optionsType},
                        new Object[]{raw, s, options});
                } else {
                    addCandidate(candidates, candidateArgs, serializer,
                        new Class<?>[]{Class.class, String.class},
                        new Object[]{raw, s});
                }
            } else {
                if (options != null) {
                    addCandidate(candidates, candidateArgs, serializer,
                        new Class<?>[]{Class.class, ReadOnlySpan.class, optionsType},
                        new Object[]{raw, jsonOrSpan, options});
                } else {
                    addCandidate(candidates, candidateArgs, serializer,
                        new Class<?>[]{Class.class, ReadOnlySpan.class},
                        new Object[]{raw, jsonOrSpan});
                }
            }

            for (int i = 0; i < candidates.size(); i++) {
                java.lang.reflect.Method m = candidates.get(i);
                if (m == null) continue;
                return m.invoke(null, candidateArgs.get(i));
            }
            throw new RuntimeException("JsonSerializerCompat: failed to find a matching deserialize overload");
        } catch (java.lang.reflect.InvocationTargetException ex) {
            Throwable cause = ex.getCause();
            if (cause instanceof RuntimeException re) {
                throw re;
            }
            throw new RuntimeException(cause);
        } catch (ReflectiveOperationException ex) {
            throw new RuntimeException("JsonSerializerCompat: failed to delegate to JsonSerializer", ex);
        }
    }

    private static void addCandidate(
        java.util.List<java.lang.reflect.Method> candidates,
        java.util.List<Object[]> candidateArgs,
        Class<?> declaringClass,
        Class<?>[] paramTypes,
        Object[] args)
    {
        try {
            candidates.add(declaringClass.getMethod("deserialize", paramTypes));
            candidateArgs.add(args);
        } catch (NoSuchMethodException e) {
            candidates.add(null);
            candidateArgs.add(null);
        }
    }
}

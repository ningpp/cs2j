package io.github.ningpp.compat;

import org.junit.jupiter.api.AfterEach;
import org.junit.jupiter.api.Test;

import java.lang.reflect.Type;
import java.util.concurrent.ConcurrentHashMap;

import static org.junit.jupiter.api.Assertions.*;

/**
 * {@code JsonSerializerOptions._classes} (the {@code JsonClassInfo} cache) is a
 * {@code ConcurrentHashMap<Class, JsonClassInfo>} written through
 * {@link ConcurrentHashMapHelper#getOrAdd}. Keyed by the erased class alone, a
 * {@code CSharpList<String>} member and a {@code CSharpList<Integer>} member would share one
 * {@code JsonClassInfo} — hence one element type — so the member converted second used the
 * first member's element converter
 * ({@code ClassCastException: Integer cannot be cast to String}).
 */
class ConcurrentHashMapHelperClosedGenericTest {

    @AfterEach
    void forgetRegistrations() {
        TypeHelper.clearThreadRegistrations();
    }


    private static Type closedGeneric(Class<?> raw, Class<?>... args) {
        return new java.lang.reflect.ParameterizedType() {
            @Override
            public Type[] getActualTypeArguments() {
                return args.clone();
            }

            @Override
            public Type getRawType() {
                return raw;
            }

            @Override
            public Type getOwnerType() {
                return null;
            }
        };
    }

    @Test
    void getOrAdd_keepsEntriesForDifferentTypeArgumentsApart() {
        ConcurrentHashMap<Class<?>, String> cache = new ConcurrentHashMap<>();

        TypeHelper.registerMemberType(closedGeneric(CSharpList.class, String.class));
        String asString = ConcurrentHashMapHelper.getOrAdd(cache, CSharpList.class, "string-element");

        TypeHelper.registerMemberType(closedGeneric(CSharpList.class, Integer.class));
        String asInteger = ConcurrentHashMapHelper.getOrAdd(cache, CSharpList.class, "integer-element");

        assertEquals("string-element", asString);
        assertEquals("integer-element", asInteger, "a list of int must not reuse the list-of-string cache entry");
        assertEquals(2, cache.size());
    }

    @Test
    void getOrAdd_isStillIdempotentForTheSameTypeArguments() {
        ConcurrentHashMap<Class<?>, String> cache = new ConcurrentHashMap<>();

        TypeHelper.registerMemberType(closedGeneric(CSharpList.class, Integer.class));
        assertEquals("first", ConcurrentHashMapHelper.getOrAdd(cache, CSharpList.class, "first"));
        TypeHelper.registerMemberType(closedGeneric(CSharpList.class, Integer.class));
        assertEquals("first", ConcurrentHashMapHelper.getOrAdd(cache, CSharpList.class, "second"));
        assertEquals(1, cache.size());
    }

    @Test
    void getOrAdd_letsTheFactorySeeTheCallersKey() {
        ConcurrentHashMap<Class<?>, String> cache = new ConcurrentHashMap<>();
        TypeHelper.registerMemberType(closedGeneric(CSharpList.class, Integer.class));

        java.util.function.Function<Class<?>, String> factory =
            key -> "created-for-" + key.getSimpleName();
        String created = ConcurrentHashMapHelper.getOrAdd(cache, CSharpList.class, factory);

        assertEquals("created-for-CSharpList", created);
    }
}

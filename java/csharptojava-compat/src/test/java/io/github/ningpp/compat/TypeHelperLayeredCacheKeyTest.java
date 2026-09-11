package io.github.ningpp.compat;

import org.junit.jupiter.api.AfterEach;
import org.junit.jupiter.api.Test;

import java.lang.reflect.ParameterizedType;
import java.lang.reflect.Type;

import static org.junit.jupiter.api.Assertions.*;

/**
 * A closed generic with the same raw class at several layers ({@code List<List<int>>}) must
 * produce a different converter per layer: the outer list's converter is built while the
 * outer layer is in effect, the inner list's converter while the next layer is. If the cache
 * key ignores the session registrations, both lookups collide and the inner list is handed
 * the outer converter — which then writes integers with a list element converter
 * ({@code ClassCastException: Integer cannot be cast to CSharpList}).
 */
class TypeHelperLayeredCacheKeyTest {

    private static Type closed(Class<?> raw, Type... args) {
        return new ParameterizedType() {
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

    @AfterEach
    void forgetRegistrations() {
        TypeHelper.clearThreadRegistrations();
    }

    @Test
    void converterCacheKey_followsTheLayersOfANestedRegistration() {
        TypeHelper.registerParameterizedType(
            closed(CSharpList.class, closed(CSharpList.class, Integer.class)));

        // Converter factories resolve their element/key types first (this consumes the outer
        // layer), and only then is the converter cache key computed.
        assertArrayEquals(new Class<?>[] { CSharpList.class }, TypeHelper.getGenericArguments(CSharpList.class));
        Object outerKey = TypeHelper.converterCacheKey(CSharpList.class);

        // Creating the outer converter consumes the next layer for its element's converter.
        assertArrayEquals(new Class<?>[] { Integer.class }, TypeHelper.getGenericArguments(CSharpList.class));
        Object innerKey = TypeHelper.converterCacheKey(CSharpList.class);

        assertNotEquals(outerKey, innerKey,
            "the inner list must not reuse the outer list's converter cache entry");
        assertTrue(outerKey.toString().contains("CSharpList"), "outer key: " + outerKey);
        assertTrue(innerKey.toString().contains("Integer"), "inner key: " + innerKey);
    }

    @Test
    void converterCacheKey_staysStableWhileTheSameLayerIsInEffect() {
        TypeHelper.registerParameterizedType(closed(CSharpList.class, Integer.class));
        Object first = TypeHelper.converterCacheKey(CSharpList.class);
        Object second = TypeHelper.converterCacheKey(CSharpList.class);
        assertEquals(first, second);
    }

    @Test
    void converterCacheKey_leavesNonGenericTypesAlone() {
        TypeHelper.registerParameterizedType(closed(CSharpList.class, Integer.class));
        assertSame(String.class, TypeHelper.converterCacheKey(String.class));
    }
}

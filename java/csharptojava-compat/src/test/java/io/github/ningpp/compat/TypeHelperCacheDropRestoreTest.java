package io.github.ningpp.compat;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertNull;
import static org.junit.jupiter.api.Assertions.assertSame;
import static org.junit.jupiter.api.Assertions.assertTrue;

import java.lang.reflect.Field;
import java.util.HashMap;
import java.util.Map;

import org.junit.jupiter.api.Test;

/**
 * The generated JsonSerializerOptions caches converters under the raw class; a TypeReference
 * session must temporarily hide those entries (the generated cache lookup runs before the
 * converter factories) without losing them: later raw-class serializations legitimately reuse
 * them. dropSessionTypesFromCache hides them for the session, restoreSessionTypesFromCache
 * puts the originals back once the outermost registration session has ended.
 */
class TypeHelperCacheDropRestoreTest {

    /** Options-shaped stub: reflection looks for a Map field named _converters. */
    @SuppressWarnings("unused")
    private static final class FakeOptions {
        private final Map<Object, Object> _converters = new HashMap<>();
        private final Map<Object, Object> _classes = new HashMap<>();
    }

    @Test
    void dropHidesSessionTypesAndRestorePutsOriginalsBack() throws Exception {
        FakeOptions options = new FakeOptions();
        Map<Object, Object> converters = convertersOf(options);
        Object staleConverter = new Object();
        Class<?> listRaw = CSharpList.class;
        converters.put(listRaw, staleConverter);

        TypeHelper.beginTypeRegistration();
        try {
            TypeHelper.registerParameterizedType(
                TypeHelper.parameterized(listRaw, String.class));
            TypeHelper.dropSessionTypesFromCache(options);

            // Hidden while the session is live: the generated raw-class lookup must miss
            // so the factories re-resolve with the session's arguments.
            assertFalse(converters.containsKey(listRaw), "session type must be hidden during the session");
        } finally {
            TypeHelper.endTypeRegistration();
        }
        TypeHelper.restoreSessionTypesFromCache(options);

        assertSame(staleConverter, converters.get(listRaw), "the pre-session converter must survive the session");
    }

    @Test
    void nestedSessionRestoreDoesNotResurrectDuringOuterSession() throws Exception {
        FakeOptions options = new FakeOptions();
        Map<Object, Object> converters = convertersOf(options);
        Class<?> listRaw = CSharpList.class;
        converters.put(listRaw, new Object());

        TypeHelper.beginTypeRegistration();
        try {
            TypeHelper.registerParameterizedType(
                TypeHelper.parameterized(listRaw, String.class));
            TypeHelper.dropSessionTypesFromCache(options);

            // A nested session drops again (nothing new to save) and restores on exit; the
            // restore must be a no-op while the outer session is still open.
            TypeHelper.beginTypeRegistration();
            try {
                TypeHelper.dropSessionTypesFromCache(options);
            } finally {
                TypeHelper.endTypeRegistration();
            }
            TypeHelper.restoreSessionTypesFromCache(options);
            assertFalse(converters.containsKey(listRaw), "nested restore must not resurrect inside the outer session");
        } finally {
            TypeHelper.endTypeRegistration();
        }
        TypeHelper.restoreSessionTypesFromCache(options);
        assertTrue(converters.containsKey(listRaw), "outermost restore puts the entry back");
    }

    @Test
    void dropWithoutSessionTypesIsANoOp() throws Exception {
        FakeOptions options = new FakeOptions();
        TypeHelper.beginTypeRegistration();
        try {
            TypeHelper.dropSessionTypesFromCache(options);
        } finally {
            TypeHelper.endTypeRegistration();
        }
        TypeHelper.restoreSessionTypesFromCache(options);
        assertEquals(0, convertersOf(options).size());
        assertNull(convertersOf(options).get(CSharpList.class));
    }

    private static Map<Object, Object> convertersOf(FakeOptions options) throws Exception {
        Field field = FakeOptions.class.getDeclaredField("_converters");
        field.setAccessible(true);
        @SuppressWarnings("unchecked")
        Map<Object, Object> map = (Map<Object, Object>) field.get(options);
        return map;
    }
}

package io.github.ningpp.compat;

import org.junit.jupiter.api.Test;

import java.lang.reflect.Type;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertNotEquals;

/**
 * A top-level registration session (TypeHelper.beginTypeRegistration at depth
 * zero) models one JsonSerializer (de)serialize call. Member/call-site type
 * arguments published by an EARLIER call must not leak into it: the converter
 * cache key prefers the member layer, so a stale CSharpList&lt;Integer&gt;
 * member layer silently reused the Integer converter for a later root
 * CSharpList&lt;Employee&gt; read (ReferenceHandlerTests.arrayWithDuplicates /
 * dictionaryOfArrays / unicodeDictionaryKeys failed with the wrong element or
 * key converter whenever the caller passed shared JsonSerializerOptions).
 */
public class TypeHelperSessionIsolationTest {

    @Test
    public void freshSessionDoesNotReuseStaleMemberLayerForCacheKey() {
        TypeHelper.clearRegistrations();
        try {
            // A previous operation read a member declared CSharpList<Integer>.
            Type staleLayer = TypeHelper.parameterized(CSharpList.class, Integer.class);
            TypeHelper.registerMemberType(staleLayer);
            Object staleKey = TypeHelper.converterCacheKey(CSharpList.class);
            assertEquals(staleKey, TypeHelper.converterCacheKey(CSharpList.class));

            // A new top-level session publishes CSharpList<String> for the root type.
            TypeHelper.beginTypeRegistration();
            try {
                TypeHelper.registerParameterizedType(
                        TypeHelper.parameterized(CSharpList.class, String.class));
                Object freshKey = TypeHelper.converterCacheKey(CSharpList.class);
                // The stale Integer member layer must not decide the cache key anymore.
                assertNotEquals(staleKey, freshKey);
            } finally {
                TypeHelper.endTypeRegistration();
            }
        } finally {
            TypeHelper.clearRegistrations();
        }
    }

    @Test
    public void freshSessionDoesNotReuseStaleCallSiteLayerForCacheKey() {
        TypeHelper.clearRegistrations();
        try {
            Type staleLayer = TypeHelper.parameterized(CSharpList.class, Integer.class);
            TypeHelper.closedCallSiteGenericType(staleLayer);
            Object staleKey = TypeHelper.converterCacheKey(CSharpList.class);

            TypeHelper.beginTypeRegistration();
            try {
                TypeHelper.registerParameterizedType(
                        TypeHelper.parameterized(CSharpList.class, String.class));
                assertNotEquals(staleKey, TypeHelper.converterCacheKey(CSharpList.class));
            } finally {
                TypeHelper.endTypeRegistration();
            }
        } finally {
            TypeHelper.clearRegistrations();
        }
    }
}

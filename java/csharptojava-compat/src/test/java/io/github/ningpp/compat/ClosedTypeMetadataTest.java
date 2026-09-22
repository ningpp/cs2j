package io.github.ningpp.compat;

import org.junit.jupiter.api.Test;

import java.lang.reflect.Field;
import java.lang.reflect.Method;
import java.lang.reflect.ParameterizedType;
import java.lang.reflect.Type;
import java.lang.reflect.TypeVariable;
import java.util.List;
import java.util.Map;
import java.util.concurrent.ConcurrentHashMap;

import static org.junit.jupiter.api.Assertions.*;

/**
 * The closed {@link Type} metadata chain (specs/2026-09-22-closed-type-metadata-chain-design.md):
 * member/element types must resolve from explicit closed types with NO ThreadLocal registry
 * involved. These tests pin the pure resolution helpers the generated patches call.
 */
class ClosedTypeMetadataTest {

    private static Type closed(Class<?> raw, Type... args) {
        return TypeHelper.parameterized(raw, args);
    }

    // ------------------------------------------------------------------ collectClosureLayers

    @Test
    void collectClosureLayers_flattensEveryLayerOuterFirst() {
        Type inner = closed(CSharpList.class, Integer.class);
        Type outer = closed(CSharpList.class, inner);

        Map<Class<?>, Type[]> layers = TypeHelper.collectClosureLayers(closed(CSharpGenericIList.class, outer));

        assertEquals(2, layers.size(), "same raw class at two layers collapses to the outer layer");
        assertArrayEquals(new Type[] { inner }, layers.get(CSharpList.class));
    }

    @Test
    void collectClosureLayers_separatesDistinctRawClasses() {
        Type valueLayer = closed(CSharpList.class, Integer.class);
        Type layers = closed(CSharpDictionary.class, String.class, valueLayer);

        Map<Class<?>, Type[]> collected = TypeHelper.collectClosureLayers(layers);

        assertEquals(2, collected.size());
        assertArrayEquals(new Type[] { String.class, valueLayer },
            collected.get(CSharpDictionary.class));
        assertArrayEquals(new Type[] { Integer.class }, collected.get(CSharpList.class));
    }

    @Test
    void collectClosureLayers_answersPlainClassesWithAnEmptyMap() {
        assertTrue(TypeHelper.collectClosureLayers(String.class).isEmpty());
        assertTrue(TypeHelper.collectClosureLayers(null).isEmpty());
    }

    // ------------------------------------------------------------------ genericArgumentsInLayers

    @Test
    void genericArgumentsInLayers_resolvesFromTheClosureWithoutAnyRegistry() {
        Type inner = closed(CSharpList.class, Integer.class);
        Map<Class<?>, Type[]> layers = TypeHelper.collectClosureLayers(closed(CSharpList.class, inner));

        TypeHelper.clearThreadRegistrations();
        Class<?>[] args = TypeHelper.genericArgumentsInLayers(layers, CSharpList.class);

        assertArrayEquals(new Class<?>[] { CSharpList.class }, args,
            "the 2-D list's element must resolve to the raw inner list, not to bounds/Object");
    }

    @Test
    void genericArgumentsInLayers_resolvesTheInnerLayerFromItsOwnClosure() {
        Map<Class<?>, Type[]> layers = TypeHelper.collectClosureLayers(closed(CSharpList.class, Integer.class));

        TypeHelper.clearThreadRegistrations();
        assertArrayEquals(new Class<?>[] { Integer.class },
            TypeHelper.genericArgumentsInLayers(layers, CSharpList.class));
    }

    @Test
    void genericArgumentsInLayers_nullOrEmptyLayersDelegatesToLegacyLookup() {
        TypeHelper.clearThreadRegistrations();
        assertEquals(0, TypeHelper.genericArgumentsInLayers(null, String.class).length);
        assertEquals(0, TypeHelper.genericArgumentsInLayers(
            TypeHelper.collectClosureLayers(String.class), String.class).length);
    }

    @Test
    void genericArgumentsInLayers_unknownRawFallsBackWithoutRegistry() {
        TypeHelper.clearThreadRegistrations();
        // CSharpGenericIList does not appear in the closure; the answer must come from the
        // type-parameter bound, not from any ThreadLocal layer.
        Map<Class<?>, Type[]> layers = TypeHelper.collectClosureLayers(closed(CSharpList.class, Integer.class));
        Class<?>[] args = TypeHelper.genericArgumentsInLayers(layers, CSharpGenericIList.class);
        assertArrayEquals(new Class<?>[] { Object.class }, args, "single type parameter resolved from its bound");
    }

    // ------------------------------------------------------------------ declaredGenericType

    static class Sample {
        private CSharpList<CSharpList<Integer>> nested;
        private String plain;
    }

    @Test
    void declaredGenericType_reflectsTheFieldSignatureWithoutSideEffects() throws Exception {
        Field nested = Sample.class.getDeclaredField("nested");
        Type declared = TypeHelper.declaredGenericType(new FieldInfo(nested));

        assertTrue(declared instanceof ParameterizedType, "nested field: " + declared);
        assertEquals(CSharpList.class, TypeHelper.toRawClass(declared));
        Type[] args = ((ParameterizedType) declared).getActualTypeArguments();
        assertEquals(1, args.length);
        assertTrue(args[0] instanceof ParameterizedType, "inner layer must survive");

        Field plain = Sample.class.getDeclaredField("plain");
        // Non-generic members report their plain Class; the generated chain only branches on
        // ParameterizedType, so the erased path stays authoritative for them.
        assertSame(String.class, TypeHelper.declaredGenericType(new FieldInfo(plain)));
    }

    static class SampleBean {
        private CSharpList<String> value;

        public CSharpList<String> getValue() {
            return value;
        }

        public void setValue(CSharpList<String> value) {
            this.value = value;
        }
    }

    @Test
    void propertyInfoGenericPropertyType_carriesTheClosureAndPublishesNothing() throws Exception {
        Method getter = SampleBean.class.getMethod("getValue");
        PropertyInfo propertyInfo = new PropertyInfo(getter, SampleBean.class.getMethod("setValue", CSharpList.class));

        Type generic = propertyInfo.getGenericPropertyType();
        assertTrue(generic instanceof ParameterizedType, "getter generic return type: " + generic);
        assertArrayEquals(new Type[] { String.class },
            ((ParameterizedType) generic).getActualTypeArguments());

        // The legacy getPropertyType() publishes member layers as a side effect; the closed
        // accessor must not.
        TypeHelper.clearThreadRegistrations();
        propertyInfo.getGenericPropertyType();
        assertTrue(memberArgumentsMap().isEmpty(),
            "getGenericPropertyType must not publish member layers");
    }

    @SuppressWarnings("unchecked")
    private static Map<Class<?>, Type[]> memberArgumentsMap() throws Exception {
        java.lang.reflect.Field f = TypeHelper.class.getDeclaredField("MEMBER_ARGUMENTS");
        f.setAccessible(true);
        return ((ThreadLocal<Map<Class<?>, Type[]>>) f.get(null)).get();
    }

    // ------------------------------------------------------------------ closedCacheKey

    @Test
    void closedCacheKey_distinguishesDifferentClosuresOfOneRawClass() {
        Object stringKey = TypeHelper.closedCacheKey(closed(CSharpList.class, String.class));
        Object intKey = TypeHelper.closedCacheKey(closed(CSharpList.class, Integer.class));
        Object sameStringKey = TypeHelper.closedCacheKey(closed(CSharpList.class, String.class));

        assertNotEquals(stringKey, intKey);
        assertEquals(stringKey, sameStringKey);
        assertSame(String.class, TypeHelper.closedCacheKey(String.class),
            "non-parameterized types keep the Class key");
    }

    // ------------------------------------------------------------------ putIfAbsentUntyped

    @Test
    void putIfAbsentUntyped_keepsTheFirstValueUnderAClosedKey() {
        ConcurrentHashMap<Object, Object> map = new ConcurrentHashMap<>();
        Object key = TypeHelper.closedCacheKey(closed(CSharpList.class, Integer.class));

        assertNull(ConcurrentHashMapHelper.putIfAbsentUntyped(map, key, "first"));
        assertEquals("first", ConcurrentHashMapHelper.putIfAbsentUntyped(map, key, "second"));
        assertSame("first", map.get(key));
    }

    // ------------------------------------------------------------------ substituteTypeVariables

    private static Type typeVariable(String name) {
        return new TypeVariable<Method>() {
            @Override
            public String getName() {
                return name;
            }

            @Override
            public java.lang.reflect.AnnotatedType[] getAnnotatedBounds() {
                return new java.lang.reflect.AnnotatedType[0];
            }

            @Override
            public Type[] getBounds() {
                return new Type[] { Object.class };
            }

            @Override
            public Method getGenericDeclaration() {
                return null;
            }

            @Override
            public java.lang.annotation.Annotation[] getAnnotations() {
                return new java.lang.annotation.Annotation[0];
            }

            @Override
            public <T extends java.lang.annotation.Annotation> T getAnnotation(Class<T> annotationClass) {
                return null;
            }

            @Override
            public java.lang.annotation.Annotation[] getDeclaredAnnotations() {
                return new java.lang.annotation.Annotation[0];
            }
        };
    }

    @Test
    void substituteTypeVariables_bindsABareTypeParameterToTheClosureArgument() {
        Map<String, Type> binding = Map.of("T", Integer.class);

        assertSame(Integer.class, TypeHelper.substituteTypeVariables(typeVariable("T"), binding));
    }

    @Test
    void substituteTypeVariables_returnsNullForAnUnresolvedTypeParameter() {
        assertNull(TypeHelper.substituteTypeVariables(typeVariable("U"), Map.of("T", Integer.class)));
        assertNull(TypeHelper.substituteTypeVariables(null, Map.of("T", Integer.class)),
            "no declared generic type means nothing to substitute");
    }

    @Test
    void substituteTypeVariables_leavesConcreteTypesAlone() {
        Type concrete = closed(CSharpList.class, String.class);

        assertSame(concrete, TypeHelper.substituteTypeVariables(concrete, Map.of("T", Integer.class)));
        assertSame(String.class, TypeHelper.substituteTypeVariables(String.class, Map.of("T", Integer.class)));
    }

    @Test
    void substituteTypeVariables_rebuildsParameterizedTypesThatMentionTypeParameters() {
        Map<String, Type> binding = Map.of("T", Integer.class);
        Type declared = closed(CSharpList.class, typeVariable("T"));

        Type substituted = TypeHelper.substituteTypeVariables(declared, binding);

        assertEquals(TypeHelper.closedCacheKey(closed(CSharpList.class, Integer.class)),
            TypeHelper.closedCacheKey(substituted),
            "CSharpList<T> with T=Integer must rebuild to CSharpList<Integer>");
    }

    @Test
    void substituteTypeVariables_resolvesNestedLayersOfGenerics() {
        Map<String, Type> binding = Map.of("T", closed(CSharpList.class, String.class));
        Type declared = closed(CSharpList.class, typeVariable("T"));

        Type substituted = TypeHelper.substituteTypeVariables(declared, binding);

        assertEquals(TypeHelper.closedCacheKey(closed(CSharpList.class, closed(CSharpList.class, String.class))),
            TypeHelper.closedCacheKey(substituted),
            "substituting T=List<String> into List<T> must rebuild the closure at every layer");
    }

    // ------------------------------------------------------------------ consumeMemberTypeArguments

    @Test
    void consumeMemberTypeArguments_replacesThePendingChainWithTheLegacyConsumedState() {
        Type inner = closed(CSharpList.class, Integer.class);
        Type nested = closed(CSharpList.class, inner);
        TypeHelper.clearThreadRegistrations();
        TypeHelper.registerMemberType(nested);

        assertArrayEquals(new Type[] { inner },
            TypeHelper.currentGenericArgumentsLayer(CSharpList.class),
            "sanity: the member chain is pending before the closed chain serves it");

        TypeHelper.consumeMemberTypeArguments(nested);

        assertNull(TypeHelper.currentGenericArgumentsLayer(CSharpList.class),
            "the member's bookkeeping is dropped entirely: no pending queue, no consumed replay");
    }

    @Test
    void consumeMemberTypeArguments_ignoresPlainClassesAndUnregisteredRaws() {
        TypeHelper.clearThreadRegistrations();

        assertDoesNotThrow(() -> TypeHelper.consumeMemberTypeArguments(String.class));
        assertDoesNotThrow(() -> TypeHelper.consumeMemberTypeArguments(closed(CSharpList.class, String.class)));
        assertNull(TypeHelper.currentGenericArgumentsLayer(CSharpList.class),
            "nothing was pending, nothing may be recorded");
    }

    @Test
    void consumeMemberTypeArguments_leavesALaterRegistrationFreeToReuseTheRaw() {
        Type first = closed(CSharpDictionary.class, String.class, Integer.class);
        TypeHelper.clearThreadRegistrations();
        TypeHelper.registerMemberType(first);
        TypeHelper.consumeMemberTypeArguments(first);

        TypeHelper.registerTypeArguments(CSharpDictionary.class, new Type[] { String.class, String.class });

        // The real consumer polls the pending queue BEFORE the consumed replay, so the fresh
        // registration must win — exactly as it would have before the member was served.
        assertArrayEquals(new Class<?>[] { String.class, String.class },
            TypeHelper.getGenericArguments(CSharpDictionary.class),
            "a session registering the same raw afterwards must not be affected");
    }
}

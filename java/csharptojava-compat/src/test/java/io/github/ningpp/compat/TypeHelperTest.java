package io.github.ningpp.compat;

import org.junit.jupiter.api.Test;

import static org.junit.jupiter.api.Assertions.*;

class TypeHelperTest {
    @Test
    void asMemberInfo_wrapsClassWithMemberInfo() {
        MemberInfo member = TypeHelper.asMemberInfo(String.class);

        assertNotNull(member);
        assertEquals(String.class.getName(), member.getName());
        assertNull(member.getDeclaringType());
    }

    @Test
    void asMemberInfo_returnsNullForNullClass() {
        assertNull(TypeHelper.asMemberInfo(null));
    }

    @Test
    void asMemberInfo_providesClassAnnotations() {
        MemberInfo member = TypeHelper.asMemberInfo(AnnotatedSample.class);

        Object[] attrs = member.getCustomAttributes(Deprecated.class, false);
        assertEquals(1, attrs.length);
        assertTrue(attrs[0] instanceof Deprecated);
    }

    @Deprecated
    private static class AnnotatedSample {
    }

    @Test
    void getElementType_returnsWrapperClassForPrimitiveArrays() {
        assertEquals(Integer.class, TypeHelper.getElementType(int[].class));
        assertEquals(Long.class, TypeHelper.getElementType(long[].class));
        assertEquals(Boolean.class, TypeHelper.getElementType(boolean[].class));
    }

    @Test
    void getElementType_leavesReferenceTypesUnchanged() {
        assertEquals(String.class, TypeHelper.getElementType(String[].class));
        assertEquals(Integer.class, TypeHelper.getElementType(Integer[].class));
    }

    @Test
    void newArrayInstance_createsPrimitiveArrayFromWrapperClass() {
        Object arr = TypeHelper.newArrayInstance(Integer.class, 3, true);
        assertTrue(arr instanceof int[]);
        assertEquals(3, ((int[]) arr).length);
    }

    @Test
    void newArrayInstance_createsPrimitiveArrayFromPrimitiveClass() {
        Object arr = TypeHelper.newArrayInstance(int.class, 2);
        assertTrue(arr instanceof int[]);
        assertEquals(2, ((int[]) arr).length);
    }

    @Test
    void newArrayInstance_createsReferenceArrayWhenNoPrimitiveMapping() {
        Object arr = TypeHelper.newArrayInstance(String.class, 1);
        assertTrue(arr instanceof String[]);
        assertEquals(1, ((String[]) arr).length);
    }

    @Test
    void getConstructor_defaultFlagsFindsPublicInstanceConstructor() {
        // C# Type.GetConstructor(Type[]) only considers public instance
        // constructors; the flag-less compat overload must mirror that.
        ConstructorInfo ctor = TypeHelper.getConstructor(String.class);
        assertNotNull(ctor);

        ConstructorInfo pairCtor = TypeHelper.getConstructor(
            CSharpKeyValuePair.class, Object.class, Object.class);
        assertNotNull(pairCtor);
    }

    @Test
    void getConstructor_defaultFlagsDoesNotFindNonPublicConstructor() {
        ConstructorInfo ctor = TypeHelper.getConstructor(
            NonPublicCtorSample.class, int.class);
        assertNull(ctor);
    }

    @Test
    void getMember_defaultFlagsFindsPublicStaticAndInstanceMembers() {
        // C# Type.GetMember(name) defaults to Public|Instance|Static.
        assertNotNull(TypeHelper.getMember(PublicMemberSample.class, "PublicStatic"));
        assertNotNull(TypeHelper.getMember(PublicMemberSample.class, "PublicInstance"));
        assertNull(TypeHelper.getMember(PublicMemberSample.class, "Hidden"));
    }

    @SuppressWarnings("unused")
    public static class NonPublicCtorSample {
        NonPublicCtorSample(int x) {
        }
    }

    @SuppressWarnings("unused")
    public static class PublicMemberSample {
        public static int PublicStatic;
        public int PublicInstance;

        private int Hidden;
    }
}

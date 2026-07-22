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
}

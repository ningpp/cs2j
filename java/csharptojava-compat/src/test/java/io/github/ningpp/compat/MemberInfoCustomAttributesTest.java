package io.github.ningpp.compat;

import org.junit.jupiter.api.Test;

import static org.junit.jupiter.api.Assertions.assertArrayEquals;
import static org.junit.jupiter.api.Assertions.assertDoesNotThrow;

class MemberInfoCustomAttributesTest {

    /** Mirrors converter-generated attributes: plain classes, not Java annotations. */
    public static class PlainAttribute {
    }

    static class Sample {
        @SuppressWarnings("unused")
        public int field = 1;

        public Sample() {
        }
    }

    @Test
    void fieldInfo_nonAnnotationAttributeType_returnsEmptyWithoutThrowing() throws Exception {
        FieldInfo fieldInfo = new FieldInfo(Sample.class.getDeclaredField("field"));
        assertArrayEquals(new Object[0],
            assertDoesNotThrow(() -> fieldInfo.getCustomAttributes(PlainAttribute.class, true)));
    }

    @Test
    void typeMemberInfo_nonAnnotationAttributeType_returnsEmptyWithoutThrowing() {
        MemberInfo memberInfo = TypeHelper.asMemberInfo(Sample.class);
        assertArrayEquals(new Object[0],
            assertDoesNotThrow(() -> memberInfo.getCustomAttributes(PlainAttribute.class, true)));
    }

    @Test
    void customAttributeProvider_nonAnnotationAttributeType_returnsEmptyWithoutThrowing() {
        ICustomAttributeProvider provider = TypeHelper.asCustomAttributeProvider(Sample.class);
        assertArrayEquals(new Object[0],
            assertDoesNotThrow(() -> provider.getCustomAttributes(PlainAttribute.class, true)));
    }
}

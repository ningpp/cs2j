package io.github.ningpp.compat;

import org.junit.jupiter.api.Test;

import java.lang.reflect.Method;
import java.util.List;

import static org.junit.jupiter.api.Assertions.*;

class ReflectionComponentModelCompatTest {
    static class Sample {
        public String getName() {
            return "name";
        }

        public void setName(String value) {
        }

        public static int parse(String value) {
            return Integer.parseInt(value);
        }
    }

    public static class SampleConverter extends ComponentModelTypeConverter {
        @Override
        public boolean canConvertFrom(Class<?> sourceType) {
            return sourceType == String.class;
        }
    }

    @Test
    void typeHelperReturnsDotNetTypeCodesForCommonTypes() {
        assertEquals(TypeCode.Boolean, TypeHelper.getTypeCode(boolean.class));
        assertEquals(TypeCode.Int32, TypeHelper.getTypeCode(Integer.class));
        assertEquals(TypeCode.String, TypeHelper.getTypeCode(String.class));
        assertEquals(TypeCode.Object, TypeHelper.getTypeCode(Sample.class));
    }

    @Test
    void typeInfoWrapsJavaClassReflectionShape() {
        TypeInfo info = IntrospectionExtensions.getTypeInfo(Sample.class);

        assertFalse(info.isInterface());
        assertFalse(info.isEnum());
        assertTrue(info.isSubclassOf(Object.class));
        assertTrue(IntrospectionExtensions.getTypeInfo(Object.class).isAssignableFrom(info));
    }

    @Test
    void propertyInfoExposesReadAndWriteAccessorsAsMethodInfo() {
        PropertyInfo property = PropertyInfo.getProperties(Sample.class)[0];

        assertEquals("Name", property.getName());
        assertTrue(property.getCanRead());
        assertNotNull(property.getGetMethod());
        assertEquals(Sample.class, property.getDeclaringType());
    }

    @Test
    void runtimeReflectionExtensionsExposeMethodInfoWrappers() {
        MethodInfo parse = null;
        for (MethodInfo method : RuntimeReflectionExtensions.getRuntimeMethods(Sample.class)) {
            if (method.getName().equals("parse")) {
                parse = method;
                break;
            }
        }

        assertNotNull(parse);
        assertTrue(parse.getIsStatic());
        assertTrue(parse.getIsPublic());
        assertEquals(int.class, parse.getReturnParameter().getType());
        assertEquals(1, parse.getParameters().length);
    }

    @Test
    void typeDescriptorStoresAttributesAndCreatesConverters() {
        TypeDescriptor.addAttributes(Sample.class, new TypeConverterAttribute(SampleConverter.class));

        List<Object> attributes = TypeDescriptor.getAttributes(Sample.class);
        ComponentModelTypeConverter converter = TypeDescriptor.getConverter(Sample.class);

        assertEquals(1, attributes.stream().filter(TypeConverterAttribute.class::isInstance).count());
        assertInstanceOf(SampleConverter.class, converter);
        assertTrue(converter.canConvertFrom(String.class));
    }
}

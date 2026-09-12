package io.github.ningpp.compat;

import java.lang.reflect.Field;
import java.lang.reflect.Modifier;

public final class FieldInfo implements MemberInfo {
    private final Field field;

    public FieldInfo(Field field) {
        this.field = field;
        this.field.setAccessible(true);
    }

    public Field getField() {
        return field;
    }

    @Override
    public String getName() {
        return field.getName();
    }

    @Override
    public Class<?> getDeclaringType() {
        return field.getDeclaringClass();
    }

    public Class<?> getFieldType() {
        // Declared member types carry the generic arguments written in the converted
        // source (e.g. CSharpDictionary<String, String>); the erased class alone makes
        // converter factories fall back to type-parameter bounds (Object key/element
        // types). Publish them before handing back the raw class.
        TypeHelper.registerMemberType(field.getGenericType());
        // C# sbyte[] and byte[] are both byte[] in Java but serialize differently (JSON array
        // of numbers vs. Base64 string), so a marked field answers int[].class.
        if (field.isAnnotationPresent(CompatSByteArray.class)) {
            return int[].class;
        }
        return TypeHelper.toWrapperType(field.getType());
    }

    public boolean getIsStatic() {
        return Modifier.isStatic(field.getModifiers());
    }

    public boolean getIsPublic() {
        return Modifier.isPublic(field.getModifiers());
    }

    public boolean getIsInitOnly() {
        return Modifier.isFinal(field.getModifiers());
    }

    public boolean getIsSpecialName() {
        return false;
    }

    public Object getValue(Object obj) {
        try {
            return field.get(obj);
        } catch (IllegalAccessException e) {
            throw new RuntimeException(e);
        }
    }

    public void setValue(Object obj, Object value) {
        try {
            field.set(obj, value);
        } catch (IllegalAccessException e) {
            throw new RuntimeException(e);
        }
    }

    @Override
    public Object[] getCustomAttributes(boolean inherit) {
        Object[] annotations = field.getAnnotations();
        // A field-level [JsonNumberHandling] overrides the type's; the generated attribute is
        // a class, so it is materialized from the compat marker instead of being an annotation.
        Object numberHandlingAttribute = TypeHelper.materializeMemberNumberHandlingAttribute(
            field.getAnnotation(CompatJsonNumberHandling.class));
        if (numberHandlingAttribute == null) {
            return annotations;
        }
        Object[] combined = java.util.Arrays.copyOf(annotations, annotations.length + 1);
        combined[annotations.length] = numberHandlingAttribute;
        return combined;
    }

    @Override
    public Object[] getCustomAttributes(Class<?> attributeType, boolean inherit) {
        if (attributeType == null) {
            return new Object[0];
        }
        if ("JsonNumberHandlingAttribute".equals(attributeType.getSimpleName())) {
            Object materialized = TypeHelper.materializeMemberNumberHandlingAttribute(
                field.getAnnotation(CompatJsonNumberHandling.class));
            return materialized == null ? new Object[0] : new Object[] { materialized };
        }
        if (!attributeType.isAnnotation()) {
            // Converted C# attributes (e.g. JsonConverterAttribute) are plain Java
            // classes, not annotations. Java's getAnnotationsByType rejects
            // non-annotation types with ClassCastException, so return empty instead.
            return new Object[0];
        }
        return field.getAnnotationsByType((Class) attributeType);
    }

    public static FieldInfo[] getFields(Class<?> clazz) {
        Field[] fields = clazz.getFields();
        FieldInfo[] result = new FieldInfo[fields.length];
        for (int i = 0; i < fields.length; i++) {
            result[i] = new FieldInfo(fields[i]);
        }
        return result;
    }
}

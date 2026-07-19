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
        return field.getType();
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
        return field.getAnnotations();
    }

    @Override
    public Object[] getCustomAttributes(Class<?> attributeType, boolean inherit) {
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

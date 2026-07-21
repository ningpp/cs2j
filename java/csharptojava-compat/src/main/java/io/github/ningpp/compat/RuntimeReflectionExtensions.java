package io.github.ningpp.compat;

import java.lang.reflect.Method;
import java.util.ArrayList;
import java.util.List;

public final class RuntimeReflectionExtensions {
    private RuntimeReflectionExtensions() {
    }

    public static FieldInfo getRuntimeField(Class<?> type, String name) {
        return TypeHelper.getField(type, name);
    }

    public static Iterable<PropertyInfo> getRuntimeProperties(Class<?> type) {
        return List.of(PropertyInfo.getProperties(type));
    }

    public static Iterable<FieldInfo> getRuntimeFields(Class<?> type) {
        List<FieldInfo> fields = new ArrayList<>();
        for (java.lang.reflect.Field field : type.getDeclaredFields()) {
            fields.add(new FieldInfo(field));
        }
        return fields;
    }

    public static Iterable<MethodInfo> getRuntimeMethods(Class<?> type) {
        List<MethodInfo> methods = new ArrayList<>();
        for (Method method : type.getDeclaredMethods()) {
            methods.add(new MethodInfo(method));
        }
        for (Method method : type.getMethods()) {
            if (method.getDeclaringClass() != type) {
                methods.add(new MethodInfo(method));
            }
        }
        return methods;
    }
}

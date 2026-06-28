package io.github.ningpp.compat;

import java.lang.reflect.Field;
import java.lang.reflect.Method;
import java.util.ArrayList;
import java.util.List;

public final class RuntimeReflectionExtensions {
    private RuntimeReflectionExtensions() {
    }

    public static Field getRuntimeField(Class<?> type, String name) {
        return TypeHelper.getField(type, name);
    }

    public static Iterable<PropertyInfo> getRuntimeProperties(Class<?> type) {
        return List.of(PropertyInfo.getProperties(type));
    }

    public static Iterable<Field> getRuntimeFields(Class<?> type) {
        return List.of(type.getDeclaredFields());
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

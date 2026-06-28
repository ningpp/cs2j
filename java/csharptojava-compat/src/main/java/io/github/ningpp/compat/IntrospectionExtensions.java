package io.github.ningpp.compat;

public final class IntrospectionExtensions {
    private IntrospectionExtensions() {
    }

    public static TypeInfo getTypeInfo(Class<?> type) {
        return TypeInfo.of(type);
    }
}

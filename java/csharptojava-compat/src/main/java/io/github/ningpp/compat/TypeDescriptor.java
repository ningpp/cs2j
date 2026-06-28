package io.github.ningpp.compat;

import java.util.ArrayList;
import java.util.List;
import java.util.Map;
import java.util.concurrent.ConcurrentHashMap;

public final class TypeDescriptor {
    private static final Map<Class<?>, List<Object>> ATTRIBUTES = new ConcurrentHashMap<>();
    private static final ComponentModelTypeConverter DEFAULT_CONVERTER = new ComponentModelTypeConverter();

    private TypeDescriptor() {
    }

    public static ComponentModelTypeConverter getConverter(Class<?> type) {
        for (Object attribute : getAttributes(type)) {
            if (attribute instanceof TypeConverterAttribute converterAttribute) {
                Class<?> converterType = converterAttribute.getConverterType();
                if (converterType != null && ComponentModelTypeConverter.class.isAssignableFrom(converterType)) {
                    try {
                        return (ComponentModelTypeConverter) converterType.getDeclaredConstructor().newInstance();
                    } catch (ReflectiveOperationException ex) {
                        throw new IllegalArgumentException("Cannot create type converter " + converterType.getName(), ex);
                    }
                }
            }
        }
        return DEFAULT_CONVERTER;
    }

    public static List<Object> getAttributes(Class<?> type) {
        return new ArrayList<>(ATTRIBUTES.getOrDefault(type, List.of()));
    }

    public static void addAttributes(Class<?> type, Object... attributes) {
        ATTRIBUTES.computeIfAbsent(type, ignored -> new ArrayList<>()).addAll(List.of(attributes));
    }
}

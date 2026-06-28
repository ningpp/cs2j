package io.github.ningpp.compat;

public class ComponentModelTypeConverter {
    public boolean canConvertTo(Class<?> destinationType) {
        return false;
    }

    public boolean canConvertFrom(Class<?> sourceType) {
        return false;
    }

    public Object convertTo(Object context, IFormatProvider culture, Object value, Class<?> destinationType) {
        return value;
    }

    public Object convertFrom(Object context, IFormatProvider culture, Object value) {
        return value;
    }
}

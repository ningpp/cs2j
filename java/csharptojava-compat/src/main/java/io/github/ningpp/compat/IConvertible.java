package io.github.ningpp.compat;

import java.lang.reflect.InvocationTargetException;
import java.lang.reflect.Method;

/**
 * Marker interface mirroring System.IConvertible.
 * Used by converted code that checks whether a value implements IConvertible.
 */
public interface IConvertible {
    /**
     * Converts a value to a 64-bit signed integer, mirroring System.IConvertible.ToInt64.
     * Handles Java primitive wrappers, booleans, characters and converted enums.
     */
    static long toInt64(Object value, IFormatProvider provider) {
        if (value == null) {
            throw new NullPointerException("value");
        }
        if (value instanceof Number) {
            return ((Number) value).longValue();
        }
        if (value instanceof Boolean) {
            return ((Boolean) value).booleanValue() ? 1L : 0L;
        }
        if (value instanceof Character) {
            return ((Character) value).charValue();
        }
        if (value instanceof Enum<?>) {
            Enum<?> enumValue = (Enum<?>) value;
            try {
                Method getValue = enumValue.getDeclaringClass().getMethod("getValue");
                Object result = getValue.invoke(enumValue);
                if (result instanceof Number) {
                    return ((Number) result).longValue();
                }
            } catch (NoSuchMethodException e) {
                // Fall back to ordinal below.
            } catch (IllegalAccessException | InvocationTargetException e) {
                throw new RuntimeException(e);
            }
            return enumValue.ordinal();
        }
        throw new IllegalArgumentException("Cannot convert " + value.getClass().getName() + " to Int64");
    }

    static long toInt64(Object value) {
        return toInt64(value, null);
    }
}

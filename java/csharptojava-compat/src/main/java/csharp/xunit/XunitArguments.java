package csharp.xunit;

import java.util.Arrays;

/**
 * JUnit 5 counterpart of xUnit's optional theory-parameter padding.
 *
 * <p>xUnit fills parameters a MemberData/ClassData row omits from the declared
 * default value ({@code int maxDepth = 64}); JUnit's {@code @ParameterizedTest}
 * supplies exactly the arguments of each row and fails the invocation with
 * {@code ParameterResolutionException} when the row is short. MemberData rows are
 * only known at run time, so the converted provider pads them through this
 * helper.</p>
 */
public final class XunitArguments {
    private XunitArguments() {
    }

    /**
     * Pads a theory row with the declared defaults of its trailing optional
     * parameters. A row that already supplies them is returned unchanged.
     *
     * @param row The row produced by the MemberData/ClassData provider.
     * @param firstOptionalIndex Index of the first optional parameter.
     * @param defaults Default expressions of the optional parameters, in order.
     * @return The row, padded to the method arity when it was short.
     */
    public static Object[] pad(Object[] row, int firstOptionalIndex, Object... defaults) {
        int arity = firstOptionalIndex + defaults.length;
        if (row.length >= arity) {
            return row;
        }
        Object[] padded = Arrays.copyOf(row, arity);
        for (int i = Math.max(row.length, firstOptionalIndex); i < arity; i++) {
            padded[i] = defaults[i - firstOptionalIndex];
        }
        return padded;
    }

    /**
     * xUnit converts theory-row values to the declared parameter type (long → int,
     * enum constant → long); JUnit's built-in ArgumentConverter only widens. The
     * converted provider passes the method's parameter types so rows are adapted
     * before JUnit sees them.
     *
     * @param row The row produced by the data provider.
     * @param parameterTypes The Java parameter types of the theory method.
     * @return The same row with each element adapted to its parameter type.
     */
    public static Object[] coerce(Object[] row, Class<?>... parameterTypes) {
        if (row == null || parameterTypes == null) {
            return row;
        }
        int length = Math.min(row.length, parameterTypes.length);
        for (int i = 0; i < length; i++) {
            row[i] = coerceValue(row[i], parameterTypes[i]);
        }
        return row;
    }

    private static Object coerceValue(Object value, Class<?> target) {
        if (value == null || target == null || target == Object.class) {
            return value;
        }
        if (target.isInstance(value)) {
            return value;
        }
        if (target == boolean.class || target == Boolean.class) {
            if (value instanceof Number number) {
                return number.intValue() != 0;
            }
            return value;
        }
        if (target == char.class || target == Character.class) {
            if (value instanceof Number number) {
                return (char) number.intValue();
            }
            return value;
        }
        if (isNumericTarget(target)) {
            if (value instanceof Number number) {
                return narrow(number, target);
            }
            if (value instanceof Character character) {
                return narrow((int) character, target);
            }
            // Converter-generated pseudo-enum constants expose getValue().
            try {
                return narrow(io.github.ningpp.compat.EnumHelper.toInt64(value), target);
            } catch (RuntimeException ignored) {
                return value;
            }
        }
        // Numeric row value aimed at an enum (Java enum or converter pseudo-enum).
        if (value instanceof Number number) {
            Object fromValue = tryFromValue(target, number.intValue());
            if (fromValue != null) {
                return fromValue;
            }
            if (target.isEnum()) {
                return target.getEnumConstants()[number.intValue()];
            }
        }
        if (value instanceof String text && target.isEnum()) {
            @SuppressWarnings({"unchecked", "rawtypes"})
            Object parsed = Enum.valueOf((Class<? extends Enum>) target.asSubclass(Enum.class), text);
            return parsed;
        }
        return value;
    }

    private static boolean isNumericTarget(Class<?> target) {
        return target == int.class || target == Integer.class
            || target == long.class || target == Long.class
            || target == short.class || target == Short.class
            || target == byte.class || target == Byte.class
            || target == float.class || target == Float.class
            || target == double.class || target == Double.class;
    }

    private static Object narrow(Number number, Class<?> target) {
        if (target == int.class || target == Integer.class) {
            return number.intValue();
        }
        if (target == long.class || target == Long.class) {
            return number.longValue();
        }
        if (target == short.class || target == Short.class) {
            return number.shortValue();
        }
        if (target == byte.class || target == Byte.class) {
            return number.byteValue();
        }
        if (target == float.class || target == Float.class) {
            return number.floatValue();
        }
        if (target == double.class || target == Double.class) {
            return number.doubleValue();
        }
        return number;
    }

    private static Object tryFromValue(Class<?> target, int value) {
        try {
            return target.getMethod("fromValue", int.class).invoke(null, value);
        } catch (ReflectiveOperationException | RuntimeException ignored) {
            return null;
        }
    }
}

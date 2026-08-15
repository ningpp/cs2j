package io.github.ningpp.compat;

public class EnumHelper {
    @SuppressWarnings("unchecked")
    public static <T> boolean tryParse(String name, boolean ignoreCase, ObjectHolder<T> result, Class<?> enumClass) {
        if (name == null || enumClass == null || !enumClass.isEnum()) {
            return false;
        }
        for (T constant : (T[]) enumClass.getEnumConstants()) {
            String constName = ((Enum<?>) constant).name();
            if (ignoreCase ? constName.equalsIgnoreCase(name) : constName.equals(name)) {
                result.value = constant;
                return true;
            }
        }
        return false;
    }

    @SuppressWarnings("unchecked")
    public static <T> boolean tryParse(String name, ObjectHolder<T> result, Class<?> enumClass) {
        return tryParse(name, false, result, enumClass);
    }

    public static Object parse(Class<?> enumType, String name) {
        try { return Enum.valueOf((Class<Enum>) enumType, name); }
        catch (Exception e) { throw new IllegalArgumentException("No enum constant: " + name, e); }
    }

    public static Object parse(Class<?> enumType, String name, boolean ignoreCase) {
        if (!ignoreCase) {
            return parse(enumType, name);
        }
        if (name == null || enumType == null || !enumType.isEnum()) {
            throw new IllegalArgumentException("No enum constant: " + name);
        }
        for (Object constant : enumType.getEnumConstants()) {
            if (((Enum<?>) constant).name().equalsIgnoreCase(name)) {
                return constant;
            }
        }
        throw new IllegalArgumentException("No enum constant: " + name);
    }

    public static CSharpArray getValues(Class<?> enumType) {
        // C# Enum.GetValues returns an enum-typed array. Converted foreach loops
        // cast the returned CSharpArray to Iterable<T>, so elements must be the
        // enum constants themselves (callers read the numeric value via getValue()).
        // The converter adds a synthetic _UNMAPPED constant for unnamed numeric
        // values; C# Enum.GetValues does not contain it, so it must be filtered out.
        return CSharpArray.of(getValuesArray(enumType));
    }

    /** Mirrors System.Enum.GetNames(Type) — constant names in declaration order. */
    public static String[] getNames(Class<?> enumType) {
        if (enumType == null || !enumType.isEnum()) {
            throw new IllegalArgumentException("Type must be an enum: " + enumType);
        }
        Object[] constants = getValuesArray(enumType);
        String[] names = new String[constants.length];
        for (int i = 0; i < constants.length; i++) {
            names[i] = ((Enum<?>) constants[i]).name();
        }
        return names;
    }

    /** Object[]-typed variant for call sites that consume values as an array. */
    public static Object[] getValuesArray(Class<?> enumType) {
        if (enumType == null || !enumType.isEnum()) {
            throw new IllegalArgumentException("Type must be an enum: " + enumType);
        }
        Object[] constants = enumType.getEnumConstants();
        int unmappedCount = 0;
        for (Object constant : constants) {
            if (isSyntheticUnmapped(constant)) {
                unmappedCount++;
            }
        }
        if (unmappedCount == 0) {
            return constants;
        }
        Object[] filtered = new Object[constants.length - unmappedCount];
        int index = 0;
        for (Object constant : constants) {
            if (!isSyntheticUnmapped(constant)) {
                filtered[index++] = constant;
            }
        }
        return filtered;
    }

    private static boolean isSyntheticUnmapped(Object constant) {
        return constant instanceof Enum<?> e && "_UNMAPPED".equals(e.name());
    }

    /**
     * Unboxes an Object to an int, handling both plain boxed Numbers and
     * converter-generated enum constants (which expose their underlying value
     * through getValue()).
     */
    public static int toInt32(Object value) {
        return (int) toInt64(value);
    }

    /**
     * Unboxes an Object to a long, handling both plain boxed Numbers and
     * converter-generated enum constants (which expose their underlying value
     * through getValue()).
     */
    public static long toInt64(Object value) {
        if (value instanceof Number number) {
            return number.longValue();
        }
        if (value instanceof Enum<?> enumValue) {
            try {
                Object raw = enumValue.getClass().getMethod("getValue").invoke(enumValue);
                if (raw instanceof Number number) {
                    return number.longValue();
                }
            } catch (ReflectiveOperationException e) {
                throw new ClassCastException("Cannot cast " + enumValue.getClass().getName() + " to a numeric value");
            }
        }
        throw new ClassCastException("Cannot cast " + (value == null ? "null" : value.getClass().getName()) + " to a numeric value");
    }

    @SuppressWarnings("unchecked")
    public static Object toObject(Class<?> enumType, long value) {
        if (enumType == null || !enumType.isEnum()) {
            throw new IllegalArgumentException("Type must be an enum: " + enumType);
        }
        Object[] constants = enumType.getEnumConstants();
        int ordinal = (int) value;
        if (ordinal < 0 || ordinal >= constants.length) {
            throw new IllegalArgumentException("Invalid enum ordinal: " + ordinal);
        }
        return constants[ordinal];
    }

    public static String format(Class<?> enumType, Object value, String format) {
        if (enumType == null || value == null) {
            throw new IllegalArgumentException("enumType and value must not be null");
        }
        if (!enumType.isEnum()) {
            throw new IllegalArgumentException("Type must be an enum: " + enumType);
        }
        Enum<?> enumValue;
        if (value instanceof Enum) {
            enumValue = (Enum<?>) value;
        } else if (value instanceof Number) {
            int ordinal = ((Number) value).intValue();
            Object[] constants = enumType.getEnumConstants();
            if (ordinal < 0 || ordinal >= constants.length) {
                throw new IllegalArgumentException("Invalid enum ordinal: " + ordinal);
            }
            enumValue = (Enum<?>) constants[ordinal];
        } else {
            enumValue = Enum.valueOf((Class<Enum>) enumType, value.toString());
        }

        String fmt = format == null ? "G" : format.toUpperCase();
        return switch (fmt) {
            case "D", "d" -> String.valueOf(enumValue.ordinal());
            case "X", "x" -> String.format("%08X", enumValue.ordinal());
            case "F", "f", "G", "g" -> enumValue.name();
            default -> enumValue.name();
        };
    }
}

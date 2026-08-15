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
        // Enum.ToObject in C# performs an unchecked conversion of the value to the
        // enum's underlying type (e.g. ToObject(typeof(ushortEnum), 65536) -> 0).
        // Infer the underlying width from the declared constants and truncate.
        long truncated = truncateToEnumWidth(enumType, value);
        try {
            java.lang.reflect.Method fromValue = enumType.getMethod("fromValue", int.class);
            return fromValue.invoke(null, (int) truncated);
        } catch (NoSuchMethodException e) {
            // Try the long overload for 64-bit backed enums.
        } catch (ReflectiveOperationException e) {
            throw new IllegalArgumentException("Invalid enum value: " + value, e);
        }
        try {
            java.lang.reflect.Method fromValue = enumType.getMethod("fromValue", long.class);
            return fromValue.invoke(null, truncated);
        } catch (NoSuchMethodException e) {
            // Fall back to ordinal matching for hand-written enums.
        } catch (ReflectiveOperationException e) {
            throw new IllegalArgumentException("Invalid enum value: " + value, e);
        }
        Object[] constants = enumType.getEnumConstants();
        int ordinal = (int) truncated;
        if (ordinal < 0 || ordinal >= constants.length) {
            throw new IllegalArgumentException("Invalid enum ordinal: " + ordinal);
        }
        return constants[ordinal];
    }

    private static final class EnumWidth {
        final boolean intDomain;
        final boolean longDomain;
        final boolean has255;
        final boolean has65535;
        final boolean hasByteMinMax;
        final boolean hasShortMinMax;
        final boolean hasIntMinMax;
        final boolean hasLongMinMax;
        final boolean hasNegOne;
        final boolean hasZero;
        final boolean hasGetValue;

        EnumWidth(boolean intDomain, boolean longDomain, boolean has255, boolean has65535,
                  boolean hasByteMinMax, boolean hasShortMinMax, boolean hasIntMinMax,
                  boolean hasLongMinMax, boolean hasNegOne, boolean hasZero, boolean hasGetValue) {
            this.intDomain = intDomain;
            this.longDomain = longDomain;
            this.has255 = has255;
            this.has65535 = has65535;
            this.hasByteMinMax = hasByteMinMax;
            this.hasShortMinMax = hasShortMinMax;
            this.hasIntMinMax = hasIntMinMax;
            this.hasLongMinMax = hasLongMinMax;
            this.hasNegOne = hasNegOne;
            this.hasZero = hasZero;
            this.hasGetValue = hasGetValue;
        }
    }

    private static EnumWidth scanEnumWidth(Class<?> enumType) {
        boolean intDomain = false, longDomain = false;
        boolean has255 = false, has65535 = false, hasNegOne = false, hasZero = false;
        boolean hasByteMinMax = false, hasShortMinMax = false, hasIntMinMax = false, hasLongMinMax = false;
        boolean hasGetValue = false;
        for (Object c : enumType.getEnumConstants()) {
            if (isSyntheticUnmapped(c)) {
                continue;
            }
            try {
                Object raw = c.getClass().getMethod("getValue").invoke(c);
                hasGetValue = true;
                if (raw instanceof Number number) {
                    long v = number.longValue();
                    if (raw instanceof Integer) {
                        intDomain = true;
                        if (v == 255L) has255 = true;
                        if (v == 65535L) has65535 = true;
                        if (v == -1L) hasNegOne = true;
                        if (v == 0L) hasZero = true;
                        if (v == (long) Byte.MIN_VALUE || v == (long) Byte.MAX_VALUE) hasByteMinMax = true;
                        if (v == (long) Short.MIN_VALUE || v == (long) Short.MAX_VALUE) hasShortMinMax = true;
                        if (v == (long) Integer.MIN_VALUE || v == (long) Integer.MAX_VALUE) hasIntMinMax = true;
                    } else {
                        longDomain = true;
                        if (v == -1L) hasNegOne = true;
                        if (v == 0L) hasZero = true;
                        if (v == Long.MIN_VALUE || v == Long.MAX_VALUE) hasLongMinMax = true;
                    }
                }
            } catch (ReflectiveOperationException e) {
                // Hand-written enum without getValue: no width information.
            }
        }
        return new EnumWidth(intDomain, longDomain, has255, has65535, hasByteMinMax,
                hasShortMinMax, hasIntMinMax, hasLongMinMax, hasNegOne, hasZero, hasGetValue);
    }

    private static long truncateToEnumWidth(Class<?> enumType, long value) {
        EnumWidth w = scanEnumWidth(enumType);
        if (!w.hasGetValue) {
            return value;
        }
        if (w.intDomain) {
            if (w.has255) {
                return value & 0xFFL;                                        // byte
            }
            if (w.has65535) {
                return value & 0xFFFFL;                                      // uint16
            }
            if (w.hasByteMinMax) {
                return (byte) value;                                         // sbyte
            }
            if (w.hasShortMinMax) {
                return (short) value;                                        // int16
            }
            if (w.hasIntMinMax) {
                return (int) value;                                          // int32
            }
            if (w.hasNegOne && w.hasZero) {
                return value & 0xFFFFFFFFL;                                  // uint32
            }
            return (int) value;                                              // int32 fallback
        }
        if (w.longDomain) {
            if (w.hasLongMinMax) {
                return value;                                                // int64
            }
            if (w.hasNegOne && w.hasZero) {
                return value;                                                // uint64
            }
            return value;                                                    // int64 fallback
        }
        return value;
    }

    /**
     * Returns the C# underlying type code of a converter-generated enum, inferred
     * from the range of its declared constants. This mirrors Enum.GetUnderlyingType
     * in C# (lost by the converter, which only emits Java enum constants).
     */
    public static TypeCode getUnderlyingTypeCode(Class<?> enumType) {
        if (enumType == null || !enumType.isEnum()) {
            return TypeCode.Int32;
        }
        EnumWidth w = scanEnumWidth(enumType);
        if (w.intDomain) {
            if (w.has255) {
                return TypeCode.Byte;
            }
            if (w.has65535) {
                return TypeCode.UInt16;
            }
            if (w.hasByteMinMax) {
                return TypeCode.SByte;
            }
            if (w.hasShortMinMax) {
                return TypeCode.Int16;
            }
            if (w.hasIntMinMax) {
                return TypeCode.Int32;
            }
            if (w.hasNegOne && w.hasZero) {
                return TypeCode.UInt32;
            }
            return TypeCode.Int32;
        }
        if (w.longDomain) {
            if (w.hasLongMinMax) {
                return TypeCode.Int64;
            }
            if (w.hasNegOne && w.hasZero) {
                return TypeCode.UInt64;
            }
            return TypeCode.Int64;
        }
        return TypeCode.Int32;
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

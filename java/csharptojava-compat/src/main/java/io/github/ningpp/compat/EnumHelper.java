package io.github.ningpp.compat;

public class EnumHelper {
    @SuppressWarnings("unchecked")
    public static <T> boolean tryParse(String name, boolean ignoreCase, ObjectHolder<T> result, Class<T> enumClass) {
        if (name == null || enumClass == null || !enumClass.isEnum()) {
            return false;
        }
        for (T constant : enumClass.getEnumConstants()) {
            String constName = ((Enum<?>) constant).name();
            if (ignoreCase ? constName.equalsIgnoreCase(name) : constName.equals(name)) {
                result.value = constant;
                return true;
            }
        }
        return false;
    }

    @SuppressWarnings("unchecked")
    public static <T> boolean tryParse(String name, ObjectHolder<T> result, Class<T> enumClass) {
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

    @SuppressWarnings("unchecked")
    public static Object[] getValues(Class<?> enumType) {
        return enumType.getEnumConstants();
    }
}

package io.github.ningpp.compat;

public final class CustomAttributeExtensions {
    private CustomAttributeExtensions() {
    }

    public static Iterable<Object> getCustomAttributes(MemberInfo member) {
        if (member == null) {
            return java.util.List.of();
        }
        Object[] attrs = member.getCustomAttributes(true);
        return attrs == null ? java.util.List.of() : java.util.Arrays.asList(attrs);
    }

    /** C# MemberInfo.GetCustomAttribute&lt;T&gt;() — returns the single attribute or null. */
    public static <T> T getCustomAttribute(Class<T> attributeType, MemberInfo member) {
        Iterable<Object> attrs = getCustomAttributes(member);
        for (Object attr : attrs) {
            return attributeType.cast(attr);
        }
        return null;
    }

    /** C# MemberInfo.GetCustomAttribute&lt;T&gt;() — returns the single attribute or null. */
    public static <T> T getCustomAttribute(Class<T> attributeType, PropertyInfo member) {
        Iterable<Object> attrs = getCustomAttributes(member);
        for (Object attr : attrs) {
            return attributeType.cast(attr);
        }
        return null;
    }

    /** C# MemberInfo.GetCustomAttribute&lt;T&gt;(MemberInfo, bool inherit). */
    public static <T> T getCustomAttribute(MemberInfo member, Class<T> attributeType, boolean inherit) {
        return getCustomAttribute(attributeType, member);
    }

    /** C# MemberInfo.GetCustomAttribute&lt;T&gt;(PropertyInfo, bool inherit). */
    public static <T> T getCustomAttribute(PropertyInfo member, Class<T> attributeType, boolean inherit) {
        return getCustomAttribute(attributeType, member);
    }

    /** C# MemberInfo.GetCustomAttribute(Type) — extension-method order (member, type). */
    public static Object getCustomAttribute(MemberInfo member, Class<?> attributeType) {
        return getCustomAttribute(attributeType, member);
    }
}

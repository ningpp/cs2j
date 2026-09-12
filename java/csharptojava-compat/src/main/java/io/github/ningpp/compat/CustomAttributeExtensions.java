package io.github.ningpp.compat;

public final class CustomAttributeExtensions {
    /** C# attribute the @CompatJsonExtensionData annotation stands for. */
    private static final String JsonExtensionDataAttributeName = "JsonExtensionDataAttribute";

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
            if (attributeType.isInstance(attr)) {
                return attributeType.cast(attr);
            }
        }
        return materializeFromCompatAnnotation(attributeType, member);
    }

    /**
     * Materializes a C# attribute class from the compat annotation the converter emits for it
     * ({@code @CompatPropertyName}-style). System.Text.Json attributes are generated as classes,
     * not annotations, so without this the lookup resolves to nothing and e.g.
     * {@code [JsonExtensionData]} never marks a property (the extension-data cluster).
     */
    @SuppressWarnings("unchecked")
    private static <T> T materializeFromCompatAnnotation(Class<T> attributeType, MemberInfo member) {
        if (attributeType == null || member == null) {
            return null;
        }
        // Only the attribute the annotation actually stands for may be materialized. Without
        // this gate a member carrying @CompatJsonExtensionData would report EVERY queried
        // attribute (JsonIgnore, JsonInclude, JsonConverter, ...) as present.
        if (!JsonExtensionDataAttributeName.equals(attributeType.getSimpleName())) {
            return null;
        }
        boolean present = false;
        if (member instanceof PropertyInfo property) {
            java.lang.reflect.Method getter = property.getGetter();
            MethodInfo setterInfo = property.getSetMethod();
            java.lang.reflect.Method setter = setterInfo == null ? null : setterInfo.getMethod();
            present = (getter != null && getter.isAnnotationPresent(CompatJsonExtensionData.class))
                || (setter != null && setter.isAnnotationPresent(CompatJsonExtensionData.class));
        }
        if (!present) {
            return null;
        }
        try {
            java.lang.reflect.Constructor<T> ctor = attributeType.getDeclaredConstructor();
            ctor.setAccessible(true);
            return ctor.newInstance();
        } catch (ReflectiveOperationException | RuntimeException e) {
            return null;
        }
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

package io.github.ningpp.compat;

public final class CustomAttributeExtensions {
    /** C# attribute the @CompatJsonExtensionData annotation stands for. */
    private static final String JsonExtensionDataAttributeName = "JsonExtensionDataAttribute";
    /** C# attribute the @CompatJsonIgnore annotation stands for. */
    private static final String JsonIgnoreAttributeName = "JsonIgnoreAttribute";
    /** C# attribute the @CompatJsonInclude annotation stands for. */
    private static final String JsonIncludeAttributeName = "JsonIncludeAttribute";
    /** C# attribute the @CompatJsonPropertyName annotation stands for. */
    private static final String JsonPropertyNameAttributeName = "JsonPropertyNameAttribute";

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
        String attributeName = attributeType.getSimpleName();
        // [JsonPropertyName] is the one marker with a VALUE, so it is built from the annotation's
        // name rather than from a no-argument constructor.
        if (JsonPropertyNameAttributeName.equals(attributeName)) {
            String name = compatJsonPropertyName(member);
            return name == null ? null : newAttribute(attributeType, String.class, name);
        }
        // [JsonIgnore] is the second marker with a value: the condition decides whether the member
        // is dropped, kept, or ignored only when null/default.
        if (JsonIgnoreAttributeName.equals(attributeName)) {
            return (T) materializeJsonIgnore(attributeType, member);
        }
        Class<? extends java.lang.annotation.Annotation> marker;
        if (JsonExtensionDataAttributeName.equals(attributeName)) {
            marker = CompatJsonExtensionData.class;
        } else if (JsonIncludeAttributeName.equals(attributeName)) {
            marker = CompatJsonInclude.class;
        } else {
            return null;
        }
        boolean present = false;
        if (member instanceof PropertyInfo property) {
            java.lang.reflect.Method getter = property.getGetter();
            MethodInfo setterInfo = property.getSetMethod();
            java.lang.reflect.Method setter = setterInfo == null ? null : setterInfo.getMethod();
            present = (getter != null && getter.isAnnotationPresent(marker))
                || (setter != null && setter.isAnnotationPresent(marker));
        } else if (member instanceof FieldInfo fieldInfo) {
            present = fieldInfo.getField().isAnnotationPresent(marker);
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

    /**
     * The JSON name the member carried in C# {@code [JsonPropertyName("...")]}, or null.
     *
     * <p>Read from the getter first, then the setter, then the backing field — the converter emits
     * the marker on the accessor (see {@code PropertyTransformer}), and a field-only declaration
     * (like {@code ClassWithPropertyFieldNamingConflictWhichThrows.ConflictingString}) carries it on
     * the field.</p>
     */
    private static String compatJsonPropertyName(MemberInfo member) {
        if (member instanceof PropertyInfo property) {
            java.lang.reflect.Method getter = property.getGetter();
            if (getter != null && getter.isAnnotationPresent(CompatJsonPropertyName.class)) {
                return getter.getAnnotation(CompatJsonPropertyName.class).value();
            }
            MethodInfo setterInfo = property.getSetMethod();
            java.lang.reflect.Method setter = setterInfo == null ? null : setterInfo.getMethod();
            if (setter != null && setter.isAnnotationPresent(CompatJsonPropertyName.class)) {
                return setter.getAnnotation(CompatJsonPropertyName.class).value();
            }
        } else if (member instanceof FieldInfo fieldInfo
                && fieldInfo.getField().isAnnotationPresent(CompatJsonPropertyName.class)) {
            return fieldInfo.getField().getAnnotation(CompatJsonPropertyName.class).value();
        }
        return null;
    }

    /** Builds {@code attributeType} through its single-argument constructor, or null. */
    private static <T> T newAttribute(Class<T> attributeType, Class<?> parameterType, Object argument) {
        try {
            java.lang.reflect.Constructor<T> ctor = attributeType.getDeclaredConstructor(parameterType);
            ctor.setAccessible(true);
            return ctor.newInstance(argument);
        } catch (ReflectiveOperationException | RuntimeException e) {
            return null;
        }
    }

    /**
     * Materializes {@code JsonIgnoreAttribute} together with its {@code Condition}.
     *
     * <p>The generated attribute class has only the parameterless constructor (C#
     * {@code [JsonIgnore(Condition = X)]} is an object initializer, which the converter drops), so
     * the condition is applied through the setter. The enum is located through the getter's return
     * type and turned back into a constant by value, never by ordinal — the generated enum carries
     * C#'s numeric values and an {@code _UNMAPPED} member.</p>
     */
    private static Object materializeJsonIgnore(Class<?> attributeType, MemberInfo member) {
        Integer condition = jsonIgnoreConditionValue(member);
        if (condition == null) {
            return null;
        }
        Object attribute = newAttribute(attributeType);
        if (attribute == null) {
            return null;
        }
        try {
            java.lang.reflect.Method getter = attributeType.getMethod("getCondition");
            java.lang.reflect.Method setter = attributeType.getMethod("setCondition", getter.getReturnType());
            java.lang.reflect.Method fromValue = getter.getReturnType().getMethod("fromValue", int.class);
            setter.invoke(attribute, fromValue.invoke(null, condition));
        } catch (ReflectiveOperationException | RuntimeException e) {
            // Keep the attribute; its default Condition (Always) is what C# applies to a bare
            // [JsonIgnore], which is the pre-existing behaviour.
        }
        return attribute;
    }

    /** The {@code Condition} the converter recorded on the member's {@code @CompatJsonIgnore}. */
    private static Integer jsonIgnoreConditionValue(MemberInfo member) {
        CompatJsonIgnore marker = null;
        if (member instanceof PropertyInfo property) {
            java.lang.reflect.Method getter = property.getGetter();
            MethodInfo setterInfo = property.getSetMethod();
            java.lang.reflect.Method setter = setterInfo == null ? null : setterInfo.getMethod();
            marker = getter == null ? null : getter.getAnnotation(CompatJsonIgnore.class);
            if (marker == null && setter != null) {
                marker = setter.getAnnotation(CompatJsonIgnore.class);
            }
        } else if (member instanceof FieldInfo fieldInfo) {
            marker = fieldInfo.getField().getAnnotation(CompatJsonIgnore.class);
        }
        return marker == null ? null : marker.value();
    }

    /** Builds {@code attributeType} through its parameterless constructor, or null. */
    private static Object newAttribute(Class<?> attributeType) {
        try {
            java.lang.reflect.Constructor<?> ctor = attributeType.getDeclaredConstructor();
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
            if (attributeType.isInstance(attr)) {
                return attributeType.cast(attr);
            }
        }
        // A converted System.Text.Json attribute is a class, not an annotation, so it is
        // materialized from the compat marker the converter emits. Without this the member
        // lookup answered null for every compat-marked attribute.
        return materializeFromCompatAnnotation(attributeType, member);
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

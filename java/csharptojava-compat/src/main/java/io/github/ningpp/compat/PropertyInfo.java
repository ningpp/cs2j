package io.github.ningpp.compat;

import java.lang.reflect.Method;
import java.lang.reflect.Modifier;
import java.lang.reflect.InvocationTargetException;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.HashSet;
import java.util.List;
import java.util.Set;

/**
 * Bridges C# System.Reflection.PropertyInfo to Java reflection.
 * C# properties have getter/setter methods; Java uses separate getter/setter methods.
 * This class wraps a Java getter Method to provide C# PropertyInfo semantics.
 */
public final class PropertyInfo implements MemberInfo {

    private final Method getter;
    private final Method setter;
    private final String name;

    // Methods that are C# methods (not properties) but follow Java getter naming convention.
    // These should be excluded when bridging C# Type.GetProperties() semantics.
    private static final Set<String> EXCLUDED_METHODS = Set.of(
        "getClass",
        "hashCode",
        "isWellFormedOriginalString",
        "getUserDrivenParsing",
        "getHasAuthority"
    );

    public PropertyInfo(Method getter) {
        this(getter, null);
    }

    public PropertyInfo(Method getter, Method setter) {
        this.getter = getter;
        this.setter = setter;
        if (this.getter != null) {
            this.getter.setAccessible(true);
        }
        if (this.setter != null) {
            this.setter.setAccessible(true);
        }
        // Derive property name from getter: "getFoo" → "Foo", "isBar" → "Bar"
        String methodName = getter != null ? getter.getName() : setter.getName();
        CompatPropertyName compatName = getter != null
            ? getter.getAnnotation(CompatPropertyName.class)
            : null;
        if (compatName != null) {
            this.name = compatName.value();
            return;
        }
        if (methodName.startsWith("get") && methodName.length() > 3) {
            this.name = methodName.substring(3);
        } else if (methodName.startsWith("is") && methodName.length() > 2) {
            this.name = methodName.substring(2);
        } else if (methodName.startsWith("set") && methodName.length() > 3) {
            this.name = methodName.substring(3);
        } else {
            this.name = methodName;
        }
    }

    @Override
    public String getName() {
        return name;
    }

    @Override
    public Class<?> getDeclaringType() {
        Method method = getter != null ? getter : setter;
        return method == null ? null : method.getDeclaringClass();
    }

    /**
     * C# {@code MemberInfo.GetCustomAttributes(Type, bool)} — the overload the generated
     * {@code JsonSerializerOptions.getAttributeThatCanHaveMultiple} actually calls.
     *
     * <p>Only the no-argument-typed overload used to be implemented, so every typed lookup fell
     * through to the {@link ICustomAttributeProvider} default and answered an EMPTY array — the
     * materialized attribute was built and then discarded. Measured: a member-level
     * {@code [JsonConverter]} materialized correctly yet the caller still saw null, so
     * {@code determineConverter} skipped it and the invalid-converter validation never ran.</p>
     *
     * <p>The requested type filters the result, matching .NET: a derived attribute class counts as
     * an instance of its base, and asking for one attribute type must not report the member's other
     * attributes (which would also trip the duplicate-attribute check).</p>
     */
    @Override
    public Object[] getCustomAttributes(Class<?> attributeType, boolean inherit) {
        Object[] all = getCustomAttributes(inherit);
        if (all.length == 0 || attributeType == null) {
            return new Object[0];
        }
        List<Object> matching = new ArrayList<>(all.length);
        for (Object attribute : all) {
            if (attributeType.isInstance(attribute)) {
                matching.add(attribute);
            }
        }
        return matching.toArray();
    }

    @Override
    public Object[] getCustomAttributes(boolean inherit) {
        if (getter == null) {
            return new Object[0];
        }
        // A member-level [JsonNumberHandling] overrides the type's, so it must be visible
        // through the member lookup JsonPropertyInfo.initialize performs.
        CompatJsonNumberHandling numberHandling = getter.getAnnotation(CompatJsonNumberHandling.class);
        if (numberHandling == null && setter != null) {
            numberHandling = setter.getAnnotation(CompatJsonNumberHandling.class);
        }
        Object numberHandlingAttribute = TypeHelper.materializeMemberNumberHandlingAttribute(numberHandling);
        DebuggerBrowsable browsable = getter.getAnnotation(DebuggerBrowsable.class);
        // A member-level [JsonConverter] must be visible through this lookup, because it is the
        // only route JsonSerializerOptions.determineConverter has to it. The source may have used a
        // DERIVED attribute class (class InvalidConverterAttribute : JsonConverterAttribute), so the
        // converter resolves the class to the converter type the base constructor names; .NET's
        // GetCustomAttribute<T>() matches derived attributes, and answering with the base-typed
        // attribute here is what makes that match work in Java.
        Object converterAttribute = materializeMemberJsonConverterAttribute();
        if (numberHandlingAttribute == null && browsable == null && converterAttribute == null) {
            return new Object[0];
        }
        List<Object> attributes = new ArrayList<>(3);
        if (numberHandlingAttribute != null) {
            attributes.add(numberHandlingAttribute);
        }
        if (converterAttribute != null) {
            attributes.add(converterAttribute);
        }
        if (browsable != null) {
            attributes.add(new DebuggerBrowsableAttribute(browsable.value()));
        }
        return attributes.toArray();
    }

    /**
     * Builds the generated {@code JsonConverterAttribute} for this member's
     * {@code @CompatJsonConverter}, or null when the member carries none.
     *
     * <p>The attribute class lives in the converted module, which compat cannot compile against, so
     * it is resolved by name and built through its {@code Class} constructor.</p>
     */
    private Object materializeMemberJsonConverterAttribute() {
        CompatJsonConverter marker = getter == null ? null : getter.getAnnotation(CompatJsonConverter.class);
        if (marker == null && setter != null) {
            marker = setter.getAnnotation(CompatJsonConverter.class);
        }
        if (marker == null) {
            return null;
        }
        Class<?> attributeType = CompatAttributeTypes.jsonConverterAttribute();
        if (attributeType == null) {
            return null;
        }
        try {
            java.lang.reflect.Constructor<?> ctor = attributeType.getConstructor(Class.class);
            Class<?> converterType = marker.value();
            // Void.class is the sentinel for C# [JsonConverter(null)]: the attribute is present but
            // names no converter type, so createConverter(typeToConvert) is used instead. Passing the
            // sentinel through would make the type look like a converter and fail the wrong check.
            return ctor.newInstance(converterType == Void.class ? null : converterType);
        } catch (ReflectiveOperationException | RuntimeException e) {
            return null;
        }
    }

    /**
     * getValue() wraps array results so their toString() matches C# array semantics;
     * consumers that need the raw array (casts, deep comparisons) unwrap via this helper.
     */
    public static Object unwrapArrayValue(Object value) {
        if (value instanceof ArrayPropertyWrapper wrapper) {
            return wrapper.array;
        }
        return value;
    }

    public Object getValue(Object obj, Object[] index) {
        try {
            Object value = getter.invoke(obj, index != null ? index : new Object[0]);
            return wrapArray(value);
        } catch (IllegalAccessException | InvocationTargetException e) {
            throw new RuntimeException(e);
        }
    }

    public Object getValue(Object obj) {
        try {
            Object value = getter.invoke(obj);
            return wrapArray(value);
        } catch (IllegalAccessException | InvocationTargetException e) {
            throw new RuntimeException(e);
        }
    }

    private static Object wrapArray(Object value) {
        if (value != null && value.getClass().isArray()) {
            return new ArrayPropertyWrapper(value);
        }
        return value;
    }

    public Method getGetter() {
        return getter;
    }

    /** Mirrors C# PropertyInfo.GetIndexParameters() — indexer parameters (not modeled). */
    public java.lang.reflect.Parameter[] getIndexParameters() {
        return new java.lang.reflect.Parameter[0];
    }

    /**
     * C# {@code PropertyInfo.GetGetMethod()} — the PUBLIC getter, or null when the getter is
     * non-public, which is what .NET's member cache tests before it keeps a property.
     *
     * <p>The converted Java accessor is a public method even when the C# getter was not, so
     * {@link CompatNonPublicAccessor} carries the fact. Without it a C# {@code internal string P
     * { get; private set; }} is treated as readable and gets serialized
     * (PropertyVisibilityTests.nonPublicMembersAreNotIncluded expects {@code {}}).</p>
     */
    public MethodInfo getGetMethod() {
        // Symmetric with getSetMethod(): a property the whole of which is non-public in C#
        // (CompatNonPublicProperty) has no PUBLIC getter either, so C# GetGetMethod() answers null.
        // Without this the generated "is this member public?" test saw a Java-public accessor and
        // kept the member, so the "non-public member with [JsonInclude] is invalid" validation in
        // JsonClassInfo never ran for a C# internal property.
        if (getter == null
            || getter.isAnnotationPresent(CompatNonPublicProperty.class)
            || getter.isAnnotationPresent(CompatNonPublicAccessor.class)) {
            return null;
        }
        return new MethodInfo(getter);
    }

    public MethodInfo getGetMethod(boolean nonPublic) {
        if (getter == null) {
            return null;
        }
        if (!nonPublic && !Modifier.isPublic(getter.getModifiers())) {
            return null;
        }
        return new MethodInfo(getter);
    }

    /**
     * C# {@code PropertyInfo.SetMethod} — the accessor, whatever its visibility, EXCEPT when the
     * property is marked {@link CompatNonPublicProperty}.
     *
     * <p>The generated serializability test is
     * {@code getGetMethod() != null && getGetMethod().getIsPublic()} OR the same on
     * {@code getSetMethod()}; for a C# {@code private string P { get; set; }} the converted Java
     * accessors exist, so the test would accept a property .NET never caches
     * (PropertyVisibilityTests.privateSetter). Answering null here makes the test agree with .NET.
     * It does not disturb the "non-public member with [JsonInclude] is invalid" validation, which
     * is decided before the accessor choice matters.</p>
     */
    public MethodInfo getSetMethod() {
        if (setter == null) {
            return null;
        }
        if (getter != null && getter.isAnnotationPresent(CompatNonPublicProperty.class)) {
            return null;
        }
        if (setter.isAnnotationPresent(CompatNonPublicAccessor.class)) {
            return null;
        }
        return new MethodInfo(setter);
    }

    public MethodInfo getSetMethod(boolean nonPublic) {
        if (setter == null) {
            return null;
        }
        if (!nonPublic && !Modifier.isPublic(setter.getModifiers())) {
            return null;
        }
        return new MethodInfo(setter);
    }

    public boolean getCanRead() {
        return getter != null;
    }

    public boolean getCanWrite() {
        return setter != null;
    }

    /**
     * The member's DECLARED closed generic type, without any side effect.
     *
     * <p>{@link #getPropertyType()} returns the erased class and publishes member layers to the
     * ThreadLocal registry; this accessor is the closed-type chain's source of truth — it returns
     * the full reflected signature (e.g. {@code CSharpList<CSharpList<Integer>>}) and registers
     * nothing. The generated JsonPropertyInfo stamps it at initialize time so converter and class
     * info resolution never depends on reflection order.</p>
     */
    public java.lang.reflect.Type getGenericPropertyType() {
        if (getter != null) {
            return getter.getGenericReturnType();
        }
        if (setter != null && setter.getParameterCount() > 0) {
            return setter.getGenericParameterTypes()[0];
        }
        return null;
    }

    public Class<?> getPropertyType() {
        if (getter != null) {
            // Declared member types carry the generic arguments written in the
            // converted source (e.g. CSharpDictionary<String, String>); returning the
            // erased class alone makes converter factories fall back to type-parameter
            // bounds (Object key/element types). Publish them first.
            TypeHelper.registerMemberType(getter.getGenericReturnType(), this);
            Class<?> sbyteArray = sbyteArrayType();
            return sbyteArray != null ? sbyteArray : TypeHelper.toWrapperType(getter.getReturnType());
        }
        if (setter != null && setter.getParameterCount() > 0) {
            TypeHelper.registerMemberType(setter.getGenericParameterTypes()[0], this);
            Class<?> sbyteArray = sbyteArrayType();
            return sbyteArray != null ? sbyteArray : TypeHelper.toWrapperType(setter.getParameterTypes()[0]);
        }
        return null;
    }

    /**
     * The type the member was DECLARED with, without boxing — {@link #getPropertyType()} boxes
     * because every converter is registered under the wrapper class.
     *
     * <p>Boxing destroys the difference between a C# non-nullable value type and a nullable one:
     * {@code int MyInt} and {@code int? MyInt} both arrive as {@code Integer}. The declaration keeps
     * it, because the converter emits a primitive Java type for the first and the wrapper for the
     * second. {@code C#}'s {@code default(T) == null} — {@code Converter.CanBeNull}, the flag
     * {@code JsonPropertyInfoOfT} needs — depends on exactly that difference.</p>
     */
    public Class<?> getRawPropertyType() {
        Class<?> sbyteArray = sbyteArrayType();
        if (sbyteArray != null) {
            return sbyteArray;
        }
        if (getter != null) {
            return getter.getReturnType();
        }
        if (setter != null && setter.getParameterCount() > 0) {
            return setter.getParameterTypes()[0];
        }
        return null;
    }

    /**
     * C# {@code sbyte[]} and {@code byte[]} are both {@code byte[]} in Java, but System.Text.Json
     * writes the first as a JSON array of numbers and the second as a Base64 string. The converter
     * marks the member, and the member lookup answers {@code int[].class} so the array converter
     * uses the integer element converter (a byte-array converter would demand a Base64 string).
     */
    private Class<?> sbyteArrayType() {
        boolean marked = getter != null && getter.isAnnotationPresent(CompatSByteArray.class)
            || setter != null && setter.isAnnotationPresent(CompatSByteArray.class);
        return marked ? int[].class : null;
    }

    public void setValue(Object obj, Object value) {
        if (setter == null) {
            throw new UnsupportedOperationException("Property has no setter");
        }
        try {
            setter.invoke(obj, adaptSByteArray(value, setter.getParameterTypes()[0]));
        } catch (IllegalAccessException | InvocationTargetException e) {
            throw new RuntimeException(e);
        }
    }

    /**
     * The member lookup reports {@code int[].class} for a marked {@code sbyte[]} member (see
     * {@link #sbyteArrayType()}), so the value handed back here is an {@code int[]} while the
     * generated field and setter are {@code byte[]}. Narrow it back on the way in.
     */
    private static Object adaptSByteArray(Object value, Class<?> parameterType) {
        if (value instanceof int[] ints && parameterType == byte[].class) {
            byte[] bytes = new byte[ints.length];
            for (int i = 0; i < ints.length; i++) {
                bytes[i] = (byte) ints[i];
            }
            return bytes;
        }
        return value;
    }

    public void setValue(Object obj, Object value, Object[] index) {
        if (index == null || index.length == 0) {
            setValue(obj, value);
            return;
        }
        if (setter == null) {
            throw new UnsupportedOperationException("Property has no setter");
        }
        try {
            Object[] args = new Object[index.length + 1];
            System.arraycopy(index, 0, args, 0, index.length);
            args[index.length] = adaptSByteArray(value, setter.getParameterTypes()[index.length]);
            setter.invoke(obj, args);
        } catch (IllegalAccessException | InvocationTargetException e) {
            throw new RuntimeException(e);
        }
    }

    /**
     * Returns all public properties (getter methods) of the given class.
     * A property is defined as a public method starting with "get" (non-void return)
     * or "is" (boolean return), with zero parameters.
     * Bridges C# Type.GetProperties() semantics.
     * Methods in the EXCLUDED_METHODS set are excluded since they correspond
     * to C# methods (not properties).
     */
    public static PropertyInfo[] getProperties(Class<?> clazz) {
        List<PropertyInfo> props = new ArrayList<>();
        for (Method m : clazz.getMethods()) {
            // C# GetProperties(Public | Instance) never yields static properties.
            if (Modifier.isStatic(m.getModifiers())) continue;
            // Skip void returns and methods with parameters
            if (m.getParameterCount() != 0) continue;
            if (m.getReturnType() == void.class) continue;
            // Skip non-public members. A C# internal/private member is a public Java member, so
            // the naming walk below would invent a property .NET's GetProperties() never returns:
            // JsonSerializerOptions.MemberAccessorStrategy and .EffectiveMaxDepth showed up as
            // properties and OptionsTests.verifyOptionsEqual compared them with Assert.Same.
            if (m.isAnnotationPresent(CompatNonPublicProperty.class)
                || m.isAnnotationPresent(CompatNonPublicAccessor.class)) continue;
            // Skip methods that are C# methods (not properties)
            if (EXCLUDED_METHODS.contains(m.getName())) continue;
            String methodName = m.getName();
            if (methodName.startsWith("get") && methodName.length() > 3) {
                props.add(new PropertyInfo(m, findSetter(clazz, methodName.substring(3), m)));
            } else if (methodName.startsWith("is") && methodName.length() > 2
                       && (m.getReturnType() == boolean.class || m.getReturnType() == Boolean.class)) {
                props.add(new PropertyInfo(m, findSetter(clazz, methodName.substring(2), m)));
            }
        }
        // A property whose accessors are all invisible in C# is not a public property, and
        // GetProperties(Public) must not report it (the Java methods still exist because Java has
        // no assembly-level visibility).
        props.removeIf(p -> p.getGetMethod() == null && p.getSetMethod() == null);
        // Class#getMethods() order is unspecified and varies between JVM runs; C# returns
        // metadata declaration order, which the class file preserves (see DeclarationOrder).
        props.sort(java.util.Comparator
            .comparingInt((PropertyInfo p) -> DeclarationOrder.hierarchyDepth(clazz, getterClass(p)))
            .thenComparingInt(p -> DeclarationOrder.methodIndex(getterClass(p), getterName(p)))
            .thenComparing(p -> {
                Class<?> type = getterClass(p);
                return type == null ? "" : type.getName();
            })
            .thenComparing(PropertyInfo::getName));
        return props.toArray(new PropertyInfo[0]);
    }

    private static Class<?> getterClass(PropertyInfo property) {
        return property.getter == null ? null : property.getter.getDeclaringClass();
    }

    private static String getterName(PropertyInfo property) {
        if (property.getter != null) {
            return property.getter.getName();
        }
        return property.setter == null ? null : property.setter.getName();
    }

    /**
     * Resolves the Java setter method ("set" + property name) that pairs with the
     * given getter. Without this pairing a C# property { get; set; } would surface
     * only its getter, and consumers keying on SetMethod (e.g. the JSON serializer's
     * ShouldDeserialize) would silently treat every writable property as read-only.
     */
    private static Method findSetter(Class<?> clazz, String propertyName, Method getter) {
        String setterName = "set" + propertyName;
        Class<?> getterType = getter.getReturnType();
        Method fallback = null;
        for (Method m : clazz.getMethods()) {
            if (Modifier.isStatic(m.getModifiers())) continue;
            if (!m.getName().equals(setterName) || m.getParameterCount() != 1) continue;
            if (isBoxCompatible(m.getParameterTypes()[0], getterType)) {
                return m;
            }
            if (fallback == null) {
                fallback = m;
            }
        }
        // C# [JsonInclude] makes a NON-public setter usable for deserialization, and the converter
        // emits such an accessor as a protected/private Java method (C# `init` accessors especially),
        // which Class.getMethods() cannot see. Without this the member was readable but never
        // written: measured getSetMethod() == null and the field stayed 0 for a
        // [JsonInclude] property with a private or protected init-only setter, while the `internal`
        // one worked because its Java setter is public.
        if (getter.isAnnotationPresent(CompatJsonInclude.class)) {
            for (Class<?> current = clazz; current != null && current != Object.class; current = current.getSuperclass()) {
                for (Method m : current.getDeclaredMethods()) {
                    if (Modifier.isStatic(m.getModifiers())) continue;
                    if (!m.getName().equals(setterName) || m.getParameterCount() != 1) continue;
                    if (isBoxCompatible(m.getParameterTypes()[0], getterType)) {
                        m.setAccessible(true);
                        return m;
                    }
                }
            }
        }
        return fallback;
    }

    private static boolean isBoxCompatible(Class<?> setterParam, Class<?> getterType) {
        Class<?> boxedParam = TypeHelper.toWrapperType(setterParam);
        Class<?> boxedGetter = TypeHelper.toWrapperType(getterType);
        return boxedParam != null && boxedGetter != null
            && (boxedParam == boxedGetter || boxedParam.isAssignableFrom(boxedGetter));
    }

    /**
     * C# {@code Type.GetProperties(BindingFlags)}. The generated serializer asks for
     * {@code Instance | Static | Public | NonPublic} so that it can SEE non-public accessors and
     * reject a member annotated with {@code [JsonInclude]} (an InvalidOperationException in .NET).
     * Enumerating only public methods made that loop empty, so the invalid member was skipped
     * silently instead of failing.
     */
    public static PropertyInfo[] getProperties(Class<?> clazz, int bindingFlags) {
        boolean includeNonPublic = (bindingFlags & BindingFlags.NonPublic) != 0;
        boolean includePublic = (bindingFlags & BindingFlags.Public) != 0;
        if (!includeNonPublic) {
            return getProperties(clazz);
        }
        List<PropertyInfo> props = new ArrayList<>();
        for (Class<?> current = clazz; current != null && current != Object.class; current = current.getSuperclass()) {
            for (Method m : current.getDeclaredMethods()) {
                if (Modifier.isStatic(m.getModifiers())) continue;
                if (m.getParameterCount() != 0) continue;
                if (m.getReturnType() == void.class) continue;
                if (m.isSynthetic() || m.isBridge()) continue;
                if (!includePublic && Modifier.isPublic(m.getModifiers())) continue;
                if (EXCLUDED_METHODS.contains(m.getName())) continue;
                String methodName = m.getName();
                if (methodName.startsWith("get") && methodName.length() > 3) {
                    props.add(new PropertyInfo(m, findSetter(clazz, methodName.substring(3), m)));
                } else if (methodName.startsWith("is") && methodName.length() > 2
                           && (m.getReturnType() == boolean.class || m.getReturnType() == Boolean.class)) {
                    props.add(new PropertyInfo(m, findSetter(clazz, methodName.substring(2), m)));
                }
            }
        }
        props.sort(java.util.Comparator
            .comparingInt((PropertyInfo p) -> DeclarationOrder.hierarchyDepth(clazz, getterClass(p)))
            .thenComparingInt(p -> DeclarationOrder.methodIndex(getterClass(p), getterName(p)))
            .thenComparing(p -> {
                Class<?> type = getterClass(p);
                return type == null ? "" : type.getName();
            })
            .thenComparing(PropertyInfo::getName));
        return props.toArray(new PropertyInfo[0]);
    }

    /**
     * Wraps an array so that toString() returns a C#-compatible type name string
     * (e.g., "System.String[]") instead of content-based or identity hash code strings.
     * This matches C# array ToString() behavior where arrays return their type name.
     */
    static final class ArrayPropertyWrapper {
        private final Object array;

        ArrayPropertyWrapper(Object array) {
            this.array = array;
        }

        @Override
        public String toString() {
            // C# arrays return their type name from ToString(), e.g., "System.String[]"
            Class<?> componentType = array.getClass().getComponentType();
            if (componentType != null) {
                StringBuilder sb = new StringBuilder();
                sb.append("System.");
                if (componentType == String.class) {
                    sb.append("String");
                } else if (componentType == int.class) {
                    sb.append("Int32");
                } else if (componentType == byte.class) {
                    sb.append("Byte");
                } else if (componentType == char.class) {
                    sb.append("Char");
                } else if (componentType == long.class) {
                    sb.append("Int64");
                } else if (componentType == double.class) {
                    sb.append("Double");
                } else if (componentType == float.class) {
                    sb.append("Single");
                } else if (componentType == short.class) {
                    sb.append("Int16");
                } else if (componentType == boolean.class) {
                    sb.append("Boolean");
                } else {
                    sb.append("Object");
                }
                // Count array dimensions
                Class<?> type = array.getClass();
                while (type.isArray()) {
                    sb.append("[]");
                    type = type.getComponentType();
                }
                return sb.toString();
            }
            return array.toString();
        }

        @Override
        public boolean equals(Object obj) {
            if (this == obj) return true;
            if (obj instanceof ArrayPropertyWrapper) {
                return Arrays.deepEquals(new Object[]{array}, new Object[]{((ArrayPropertyWrapper) obj).array});
            }
            return false;
        }

        @Override
        public int hashCode() {
            return Arrays.deepHashCode(new Object[]{array});
        }
    }
}

package io.github.ningpp.compat;

import java.lang.reflect.Array;
import java.lang.reflect.Constructor;
import java.lang.reflect.Field;
import java.lang.reflect.Method;
import java.lang.reflect.Modifier;
import java.util.ArrayList;
import java.util.List;

/**
 * Bridges C# System.Type reflection idioms to java.lang.Class.
 */
public final class TypeHelper {
    private TypeHelper() {
    }

    /**
     * Mirrors C# Type.GetTypeCode() for common primitive/object mappings.
     */
    public static TypeCode getTypeCode(Class<?> type) {
        if (type == null) {
            return TypeCode.Empty;
        }
        if (type.isEnum()) {
            // Converted C# enums are Java enums with an int/long "value" field;
            // Type.GetTypeCode(enumType) reports the underlying numeric type.
            try {
                java.lang.reflect.Field valueField = type.getDeclaredField("value");
                if (valueField.getType() == long.class) {
                    return TypeCode.Int64;
                }
            } catch (NoSuchFieldException ignored) {
                // Default to Int32 below.
            }
            return TypeCode.Int32;
        }
        if (type == boolean.class || type == Boolean.class) {
            return TypeCode.Boolean;
        }
        if (type == char.class || type == Character.class) {
            return TypeCode.Char;
        }
        if (type == byte.class || type == Byte.class) {
            return TypeCode.Byte;
        }
        if (type == short.class || type == Short.class) {
            return TypeCode.Int16;
        }
        if (type == int.class || type == Integer.class) {
            return TypeCode.Int32;
        }
        if (type == long.class || type == Long.class) {
            return TypeCode.Int64;
        }
        if (type == float.class || type == Float.class) {
            return TypeCode.Single;
        }
        if (type == double.class || type == Double.class) {
            return TypeCode.Double;
        }
        if (type == String.class) {
            return TypeCode.String;
        }
        return TypeCode.Object;
    }

    public static Class<?> getBaseType(Class<?> type) {
        return type == null ? null : type.getSuperclass();
    }

    /** C# Type.BaseType on a TypeInfo wrapper. */
    public static Class<?> getBaseType(TypeInfo type) {
        return type == null ? null : type.getBaseType();
    }

    /** C# Type.GenericTypeArguments. */
    public static Class<?>[] getGenericTypeArguments(Class<?> type) {
        return type == null ? new Class<?>[0] : IntrospectionExtensions.getTypeInfo(type).getGenericTypeArguments();
    }

    /** C# Type.MakeGenericType(args) — stub returning the raw type. */
    public static Class<?> makeGenericType(Class<?> type, Class<?>... typeArguments) {
        return type;
    }

    /**
     * Mirrors Type.GetType(string, bool throwOnError) — loads a type by name,
     * returning null instead of throwing when throwOnError is false.
     */
    public static Class<?> getType(String name, boolean throwOnError) {
        if (name == null) {
            throw new NullPointerException("name");
        }
        try {
            return Class.forName(name);
        } catch (ClassNotFoundException e) {
            if (throwOnError) {
                throw new RuntimeException(e);
            }
            return null;
        }
    }

    public static Class<?> getDeclaringType(Class<?> type) {        return type == null ? null : type.getDeclaringClass();
    }

    /**
     * Mirrors Nullable.GetUnderlyingType(Type) — in the converted model C# value
     * types map to their boxed wrapper classes (typeof(int) → Integer.class), so a
     * boxed primitive wrapper represents Nullable&lt;T&gt; and its underlying type is
     * the wrapper of the primitive (matching typeof(int) → Integer.class).
     * Returns null for non-wrapper inputs.
     */
    public static Class<?> getUnderlyingType(Class<?> type) {
        return type != null && toPrimitiveType(type) != null ? type : null;
    }

    public static boolean getIsGenericType(Class<?> type) {
        return type != null && type.getTypeParameters().length > 0;
    }

    public static boolean getContainsGenericParameters(Class<?> type) {
        // Java class objects are always concrete: generic type arguments are
        // erased, so a raw generic class literal (CSharpList.class) represents a
        // closed runtime type, not an open C# generic definition.
        return false;
    }

    public static Class<?>[] getGenericArguments(Class<?> type) {
        if (type == null) {
            return new Class<?>[0];
        }
        java.lang.reflect.TypeVariable<?>[] params = type.getTypeParameters();
        Class<?>[] result = new Class<?>[params.length];
        for (int i = 0; i < params.length; i++) {
            result[i] = resolveClassBound(params[i]);
        }
        return result;
    }

    private static Class<?> resolveClassBound(java.lang.reflect.TypeVariable<?> tv) {
        for (java.lang.reflect.Type bound : tv.getBounds()) {
            if (bound instanceof Class<?>) {
                return (Class<?>) bound;
            }
        }
        return Object.class;
    }

    public static Class<?> getGenericTypeDefinition(Class<?> type) {
        if (type == null) {
            return null;
        }
        if (type.isArray()) {
            return getGenericTypeDefinition(type.getComponentType());
        }
        return type;
    }

    public static boolean getIsAbstract(Class<?> type) {
        return type != null && Modifier.isAbstract(type.getModifiers()) && !type.isInterface();
    }

    public static boolean getIsValueType(Class<?> type) {
        return type != null && type.isPrimitive();
    }

    public static boolean getIsVisible(Class<?> type) {
        return type != null && Modifier.isPublic(type.getModifiers());
    }

    public static boolean getIsNestedPublic(Class<?> type) {
        return type != null && type.isMemberClass() && Modifier.isPublic(type.getModifiers());
    }

    public static boolean getIsClass(Class<?> type) {
        return type != null && !type.isInterface() && !type.isPrimitive() && !type.isArray();
    }

    /** Mirrors C# Type.IsPointer — pointers are not representable in Java. */
    public static boolean getIsPointer(Class<?> type) {
        return false;
    }

    /** Mirrors C# Type.IsByRefLike — ref structs have no Java equivalent. */
    public static boolean getIsByRefLike(Class<?> type) {
        return false;
    }

    public static boolean getIsArray(Class<?> type) {
        return type != null && type.isArray();
    }

    public static boolean getIsEnum(Class<?> type) {
        return type != null && type.isEnum();
    }

    public static boolean getIsPrimitive(Class<?> type) {
        return type != null && type.isPrimitive();
    }

    public static boolean getIsInterface(Class<?> type) {
        return type != null && type.isInterface();
    }

    /**
     * Mirrors C# Type.IsAssignableFrom(Type). Java arrays are not assignable to
     * the CSharpGenericIterable interface, but in C# every array implements
     * IEnumerable, so the compat mapping treats arrays as assignable to
     * CSharpGenericIterable.
     */
    public static boolean isAssignableFrom(Class<?> baseType, Class<?> type) {
        if (baseType == CSharpGenericIterable.class && type != null && type.isArray()) {
            return true;
        }
        // .NET's non-generic collection interfaces (IDictionary, IList,
        // ICollection, IEnumerable) all derive from IEnumerable.  In the
        // compat library these are modeled as separate interface trees
        // (CSharpIDictionary/CSharpCollection/CSharpIterable vs.
        // CSharpGenericIterable), so bridge them here to keep
        // typeof(IEnumerable).IsAssignableFrom(...) semantics intact for
        // types like CSharpIDictionary and CSharpGenericIDictionary.
        if (baseType == CSharpGenericIterable.class && type != null
                && (type == CSharpIDictionary.class
                    || type == CSharpCollection.class
                    || type == CSharpIterable.class
                    || type == CSharpGenericIDictionary.class)) {
            return true;
        }
        // .NET's concrete generic collections (Dictionary<K,V>, List<T>)
        // implement both their generic interface (IDictionary<K,V>,
        // IList<T>) and the non-generic interface (IDictionary, IList).
        // The compat library models the generic and non-generic interfaces
        // as unrelated types, so bridge the assignability here.
        if (baseType == CSharpIDictionary.class && type == CSharpDictionary.class) {
            return true;
        }
        if (baseType == CSharpIList.class && type == CSharpList.class) {
            return true;
        }
        if (baseType != null && baseType.isAssignableFrom(type)) {
            return true;
        }
        // typeof(bool) is represented as Boolean.class while reflection may surface
        // boolean.class; treat primitive/wrapper pairs as assignable both ways.
        return type != null && isSameType(baseType, type);
    }

    public static boolean getIsSealed(Class<?> type) {
        return type != null && java.lang.reflect.Modifier.isFinal(type.getModifiers());
    }

    public static boolean getIsGenericTypeDefinition(Class<?> type) {
        return type != null && type.getTypeParameters().length > 0;
    }

    public static int getArrayRank(Class<?> type) {
        if (type == null || !type.isArray()) {
            return 0;
        }
        // In .NET, ArrayRank is the number of comma dimensions in a single
        // array type (int[,] => rank 2). Java has no true multidimensional
        // arrays — int[][] is a jagged array (rank 1, element type int[]).
        // Therefore the .NET-compatible rank is always 1 for any Java array
        // (which is always single-dimensional; "multidimensional" arrays are
        // modeled as arrays-of-arrays and handled recursively by element type).
        return 1;
    }

    public static MethodInfo[] getMethods(Class<?> type, int bindingFlags) {
        if (type == null) {
            return new MethodInfo[0];
        }
        Method[] methods = type.getMethods();
        List<MethodInfo> result = new ArrayList<>();
        for (Method method : methods) {
            if (matchesBindingFlags(method.getModifiers(), bindingFlags)) {
                result.add(new MethodInfo(method));
            }
        }
        return result.toArray(new MethodInfo[0]);
    }

    public static MethodInfo[] getMethods(Class<?> type) {
        return getMethods(type, BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic);
    }

    public static FieldInfo[] getFields(Class<?> type, int bindingFlags) {
        if (type == null) {
            return new FieldInfo[0];
        }
        Field[] fields = type.getFields();
        List<FieldInfo> result = new ArrayList<>();
        for (Field field : fields) {
            if (matchesBindingFlags(field.getModifiers(), bindingFlags)) {
                result.add(new FieldInfo(field));
            }
        }
        return result.toArray(new FieldInfo[0]);
    }

    public static FieldInfo[] getFields(Class<?> type) {
        return getFields(type, BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic);
    }

    public static ConstructorInfo[] getConstructors(Class<?> type, int bindingFlags) {
        if (type == null) {
            return new ConstructorInfo[0];
        }
        Constructor<?>[] ctors = type.getConstructors();
        List<ConstructorInfo> result = new ArrayList<>();
        for (Constructor<?> ctor : ctors) {
            if (matchesBindingFlags(ctor.getModifiers(), bindingFlags)) {
                result.add(new ConstructorInfo(ctor));
            }
        }
        return result.toArray(new ConstructorInfo[0]);
    }

    public static ConstructorInfo[] getConstructors(Class<?> type) {
        return getConstructors(type, BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic);
    }

    public static ConstructorInfo getConstructorInfo(Class<?> type, int bindingFlags, Class<?>... parameterTypes) {
        if (type == null) {
            return null;
        }
        try {
            return new ConstructorInfo(type.getConstructor(parameterTypes));
        } catch (NoSuchMethodException e) {
            return null;
        }
    }

    public static ConstructorInfo getConstructor(Class<?> type, Class<?>... parameterTypes) {
        return getConstructorInfo(type, 0, parameterTypes);
    }

    public static ConstructorInfo getConstructor(Class<?> type, int bindingFlags, Class<?>... parameterTypes) {
        if (type == null) {
            return null;
        }
        for (Constructor<?> ctor : type.getDeclaredConstructors()) {
            if (matchesParameterTypes(ctor.getParameterTypes(), parameterTypes)
                    && matchesBindingFlags(ctor.getModifiers(), bindingFlags)) {
                return new ConstructorInfo(ctor);
            }
        }
        return null;
    }

    public static MemberInfo getMember(Class<?> type, String name) {
        return getMember(type, name, 0);
    }

    public static MemberInfo getMember(Class<?> type, String name, int bindingFlags) {
        if (type == null || name == null) {
            return null;
        }
        for (Method method : type.getMethods()) {
            if (method.getName().equals(name) && matchesBindingFlags(method.getModifiers(), bindingFlags)) {
                return new MethodInfo(method);
            }
        }
        for (Field field : type.getFields()) {
            if (field.getName().equals(name) && matchesBindingFlags(field.getModifiers(), bindingFlags)) {
                return new FieldInfo(field);
            }
        }
        return null;
    }

    public static MemberInfo[] getMembers(Class<?> type, int bindingFlags) {
        if (type == null) {
            return new MemberInfo[0];
        }
        List<MemberInfo> members = new ArrayList<>();
        for (Method method : type.getMethods()) {
            if (matchesBindingFlags(method.getModifiers(), bindingFlags)) {
                members.add(new MethodInfo(method));
            }
        }
        for (Field field : type.getFields()) {
            if (matchesBindingFlags(field.getModifiers(), bindingFlags)) {
                members.add(new FieldInfo(field));
            }
        }
        return members.toArray(new MemberInfo[0]);
    }

    public static MemberInfo[] getMembers(Class<?> type) {
        return getMembers(type, BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic);
    }

    public static MemberInfo[] getMembers(Class<?> type, String name) {
        return getMembers(type, name, 0);
    }

    public static MemberInfo[] getMembers(Class<?> type, String name, int bindingFlags) {
        if (type == null || name == null) {
            return new MemberInfo[0];
        }
        List<MemberInfo> members = new ArrayList<>();
        for (Method method : type.getMethods()) {
            if (method.getName().equals(name) && matchesBindingFlags(method.getModifiers(), bindingFlags)) {
                members.add(new MethodInfo(method));
            }
        }
        for (Field field : type.getFields()) {
            if (field.getName().equals(name) && matchesBindingFlags(field.getModifiers(), bindingFlags)) {
                members.add(new FieldInfo(field));
            }
        }
        return members.toArray(new MemberInfo[0]);
    }

    public static FieldInfo getField(Class<?> type, String name) {
        if (type == null || name == null) {
            return null;
        }
        try {
            return new FieldInfo(type.getField(name));
        } catch (NoSuchFieldException e) {
            return null;
        }
    }

    /**
     * Bridges C# Type.GetField(name, BindingFlags) semantics.
     * When BindingFlags include NonPublic, searches declared fields
     * (including private, protected, and package-private) across the
     * class hierarchy, matching C# behavior where NonPublic | Instance
     * finds non-public instance fields.
     */
    public static FieldInfo getField(Class<?> type, String name, int bindingFlags) {
        if (type == null || name == null) {
            return null;
        }
        boolean wantNonPublic = (bindingFlags & 0x20) != 0;
        if (wantNonPublic) {
            // Search declared fields across the hierarchy to find non-public members
            Class<?> current = type;
            while (current != null) {
                for (Field field : current.getDeclaredFields()) {
                    if (field.getName().equals(name) && matchesBindingFlags(field.getModifiers(), bindingFlags)) {
                        field.setAccessible(true);
                        return new FieldInfo(field);
                    }
                }
                current = current.getSuperclass();
            }
            return null;
        }
        // Public-only search — delegate to the simple overload
        return getField(type, name);
    }

    /**
     * Bridges C# Type.GetProperty(name) semantics.
     * Looks up a Java bean property by its C#/Java property name (e.g. "Value" or "value").
     */
    public static PropertyInfo getProperty(Class<?> type, String name) {
        if (type == null || name == null || name.isEmpty()) {
            return null;
        }
        String capitalized = Character.toUpperCase(name.charAt(0)) + name.substring(1);
        String getterName = "get" + capitalized;
        String isGetterName = "is" + capitalized;
        String setterName = "set" + capitalized;

        Method getter = null;
        Method setter = null;
        for (Method method : type.getMethods()) {
            if (method.getParameterCount() != 0 || method.getReturnType() == void.class) {
                continue;
            }
            String methodName = method.getName();
            if (getter == null && (methodName.equals(getterName) || methodName.equals(isGetterName))) {
                getter = method;
            }
            if (setter == null && methodName.equals(setterName) && method.getParameterCount() == 1) {
                setter = method;
            }
            if (getter != null && setter != null) {
                break;
            }
        }
        if (getter == null && setter == null) {
            return null;
        }
        return new PropertyInfo(getter, setter);
    }

    public static String getFullName(Class<?> type) {
        return type == null ? null : type.getName();
    }

    /**
     * Mirrors C# {@code Type.Assembly}, {@code MemberInfo.Assembly}, and
     * {@code Module.Assembly} — returns the declaring assembly as an
     * {@link AssemblyCompat} instead of {@link java.lang.Package}.
     */
    public static AssemblyCompat getAssembly(Class<?> type) {
        return type == null ? null : AssemblyCompat.fromClass(type);
    }

    /**
     * Mirrors C# {@code MemberInfo.Assembly} for the compat wrapper.
     */
    public static AssemblyCompat getAssembly(MemberInfo member) {
        return member == null ? null : getAssembly(member.getDeclaringType());
    }

    /**
     * Mirrors C# {@code Module.Assembly} for the Java {@link java.lang.Module}
     * returned by {@link Class#getModule()}.
     */
    public static AssemblyCompat getAssembly(java.lang.Module module) {
        return module == null ? null : AssemblyCompat.fromClassLoader(module.getClassLoader(), module.getName());
    }

    public static Class<?> getElementType(Class<?> type) {
        return type == null ? null : toWrapperType(type.getComponentType());
    }

    public static boolean getIsGenericParameter(Class<?> type) {
        return type != null && java.lang.reflect.TypeVariable.class.isInstance(type);
    }

    public static Class<?> makeArrayType(Class<?> elementType) {
        if (elementType == null) {
            return null;
        }
        Class<?> primitiveType = toPrimitiveType(elementType);
        return java.lang.reflect.Array.newInstance(primitiveType != null ? primitiveType : elementType, 0).getClass();
    }

    public static Object newArrayInstance(Class<?> elementType, int length) {
        return newArrayInstance(elementType, length, false);
    }

    public static Object newArrayInstance(Class<?> elementType, int length, boolean primitive) {
        if (elementType == null) {
            return null;
        }
        Class<?> componentType = primitive ? toPrimitiveType(elementType) : null;
        return java.lang.reflect.Array.newInstance(componentType != null ? componentType : elementType, length);
    }

    /**
     * Maps a Java primitive class to its boxed wrapper class.
     * Non-primitive inputs are returned unchanged so that reflection-based
     * type comparisons (e.g. C# typeof(int) represented as Integer.class)
     * line up with primitive array component types.
     */
    public static Class<?> toWrapperType(Class<?> type) {
        if (type == null || !type.isPrimitive()) {
            return type;
        }
        if (type == int.class) return Integer.class;
        if (type == long.class) return Long.class;
        if (type == short.class) return Short.class;
        if (type == byte.class) return Byte.class;
        if (type == boolean.class) return Boolean.class;
        if (type == char.class) return Character.class;
        if (type == float.class) return Float.class;
        if (type == double.class) return Double.class;
        return type;
    }

    /**
     * Mirrors C# Type equality: typeof(bool) is represented as Boolean.class while
     * reflection surfaces boolean.class, so primitive/wrapper pairs compare equal.
     */
    public static boolean isSameType(Class<?> a, Class<?> b) {
        if (a == b) {
            return true;
        }
        if (a == null || b == null) {
            return false;
        }
        return toWrapperType(a) == toWrapperType(b);
    }

    /**
     * Bridges C# array covariance to Java. In C#, any array (including
     * {@code int[]}) can be cast to {@code T[]} where T is a reference type,
     * because C# supports array covariance through {@code IEnumerable}.
     * Java forbids casting {@code int[]} to {@code Object[]}.
     * <p>
     * This method returns the array itself if it is already assignable to
     * {@code Object[]}, otherwise it boxes primitive elements into a new
     * {@code Object[]} (or the requested component type array). This mirrors
     * the semantics of C#'s {@code (T[])(IEnumerable)value} cast.
     */
    @SuppressWarnings("unchecked")
    public static <T> T[] castArray(Object value, Class<T> componentType) {
        if (value == null) {
            return null;
        }
        if (!value.getClass().isArray()) {
            throw new ClassCastException(value.getClass().getName()
                + " is not an array");
        }
        Class<?> srcComponent = value.getClass().getComponentType();
        // If the source is already assignable (e.g. String[] → Object[]),
        // return as-is.
        if (componentType.isAssignableFrom(srcComponent)) {
            return (T[]) value;
        }
        // Otherwise box each element (e.g. int[] → Integer[] → T[]).
        int len = java.lang.reflect.Array.getLength(value);
        Object[] result = (Object[]) java.lang.reflect.Array.newInstance(
            componentType.isPrimitive() ? toWrapperType(componentType) : componentType,
            len);
        for (int i = 0; i < len; i++) {
            result[i] = java.lang.reflect.Array.get(value, i);
        }
        return (T[]) result;
    }

    /**
     * Maps a boxed wrapper class back to its Java primitive class.
     * Returns null for non-wrapper inputs so callers can decide whether to
     * preserve the original class.
     */
    private static Class<?> toPrimitiveType(Class<?> type) {
        if (type == Integer.class) return int.class;
        if (type == Long.class) return long.class;
        if (type == Short.class) return short.class;
        if (type == Byte.class) return byte.class;
        if (type == Boolean.class) return boolean.class;
        if (type == Character.class) return char.class;
        if (type == Float.class) return float.class;
        if (type == Double.class) return double.class;
        return null;
    }

    public static MethodInfo getMethod(Class<?> type, String name, Class<?>... parameterTypes) {
        if (type == null || name == null) {
            return null;
        }
        try {
            return new MethodInfo(type.getMethod(name, parameterTypes));
        } catch (NoSuchMethodException e) {
            return null;
        }
    }

    /**
     * Bridges C# {@code Type.GetMethod(name, BindingFlags)} semantics.
     */
    public static MethodInfo getMethod(Class<?> type, String name, int bindingFlags) {
        if (type == null || name == null) {
            return null;
        }
        for (Method method : type.getMethods()) {
            if (method.getName().equals(name) && matchesBindingFlags(method.getModifiers(), bindingFlags)) {
                return new MethodInfo(method);
            }
        }
        for (Method method : type.getDeclaredMethods()) {
            if (method.getName().equals(name) && matchesBindingFlags(method.getModifiers(), bindingFlags)) {
                return new MethodInfo(method);
            }
        }
        return null;
    }

    public static Object[] getCustomAttributes(Class<?> type, boolean inherit) {
        if (type == null) {
            return new Object[0];
        }
        return type.getAnnotations();
    }

    public static Object[] getCustomAttributes(Class<?> type, Class<?> attributeType, boolean inherit) {
        if (type == null || attributeType == null || !attributeType.isAnnotation()) {
            return new Object[0];
        }
        return type.getAnnotationsByType((Class) attributeType);
    }

    /**
     * Mirrors C# Attribute.IsDefined(element, attributeType, inherit) for Class targets.
     */
    public static boolean isDefined(Class<?> type, Class<?> attributeType, boolean inherit) {
        if (type == null || attributeType == null || !attributeType.isAnnotation()) {
            return false;
        }
        return type.isAnnotationPresent((Class) attributeType);
    }

    /**
     * Adapts a Java {@link Class} to the compat {@link ICustomAttributeProvider} interface.
     * C# System.Type implements ICustomAttributeProvider, but java.lang.Class does not,
     * so generated code that passes a Type where ICustomAttributeProvider is expected
     * must be wrapped through this helper.
     */
    public static ICustomAttributeProvider asCustomAttributeProvider(Class<?> type) {
        if (type == null) {
            return null;
        }
        return new ICustomAttributeProvider() {
            @Override
            public Object[] getCustomAttributes(boolean inherit) {
                return type.getAnnotations();
            }

            @Override
            @SuppressWarnings("unchecked")
            public Object[] getCustomAttributes(Class<?> attributeType, boolean inherit) {
                if (attributeType == null || !attributeType.isAnnotation()) {
                    return new Object[0];
                }
                return type.getAnnotationsByType((Class) attributeType);
            }

            @Override
            @SuppressWarnings("unchecked")
            public boolean isDefined(Class<?> attributeType, boolean inherit) {
                return type.isAnnotationPresent((Class) attributeType);
            }
        };
    }

    /**
     * Adapts a Java {@link Class} to the compat {@link MemberInfo} interface.
     * C# System.Type derives from System.Reflection.MemberInfo, but the Java mapping
     * for System.Type is java.lang.Class, which does not implement MemberInfo.
     * Generated code that passes a Type where MemberInfo is expected must wrap the
     * Class through this helper.
     */
    public static MemberInfo asMemberInfo(Class<?> type) {
        if (type == null) {
            return null;
        }
        return new TypeMemberInfo(type);
    }

    public static MemberInfo[] getDefaultMembers(Class<?> type) {
        if (type == null) {
            return new MemberInfo[0];
        }
        List<MemberInfo> members = new ArrayList<>();
        DefaultMemberAttribute attr = type.getAnnotation(DefaultMemberAttribute.class);
        if (attr == null) {
            return members.toArray(new MemberInfo[0]);
        }
        String name = attr.value();
        for (Method method : type.getMethods()) {
            if (method.getName().equals(name)) {
                members.add(new MethodInfo(method));
            }
        }
        for (Field field : type.getFields()) {
            if (field.getName().equals(name)) {
                members.add(new FieldInfo(field));
            }
        }
        return members.toArray(new MemberInfo[0]);
    }

    @SuppressWarnings("unchecked")
    private static <T> T[] filterByBindingFlags(T[] members, int bindingFlags, Class<T> clazz) {
        if (bindingFlags == 0) {
            return members;
        }
        List<T> result = new ArrayList<>();
        for (T member : members) {
            int mods;
            if (member instanceof Method m) {
                mods = m.getModifiers();
            } else if (member instanceof Field f) {
                mods = f.getModifiers();
            } else if (member instanceof Constructor<?> c) {
                mods = c.getModifiers();
            } else {
                continue;
            }
            if (matchesBindingFlags(mods, bindingFlags)) {
                result.add(member);
            }
        }
        return result.toArray((T[]) Array.newInstance(clazz, 0));
    }

    private static boolean matchesBindingFlags(int modifiers, int bindingFlags) {
        // C# BindingFlags: Public=0x10, NonPublic=0x20, Static=0x8, Instance=0x4
        boolean wantPublic = (bindingFlags & 0x10) != 0;
        boolean wantNonPublic = (bindingFlags & 0x20) != 0;
        boolean wantStatic = (bindingFlags & 0x08) != 0;
        boolean wantInstance = (bindingFlags & 0x04) != 0;

        boolean isPublic = Modifier.isPublic(modifiers);
        boolean isStatic = Modifier.isStatic(modifiers);

        // C# semantics: Public and NonPublic are alternatives, not requirements.
        // A member matches when it is public AND Public is requested, or when it
        // is non-public AND NonPublic is requested. Treating them as AND made
        // every member fail the common Public | NonPublic combination.
        // cs2j additionally emits C# internal members as public Java members
        // (Java has no assembly-level visibility), so a NonPublic-only lookup
        // must fall back to public members to keep reflection working.
        boolean nonPublicBridgeFallback = wantNonPublic && !wantPublic && isPublic;
        if (!((wantPublic && isPublic) || (wantNonPublic && !isPublic) || nonPublicBridgeFallback)) {
            return false;
        }
        if (wantStatic && !isStatic) {
            return false;
        }
        if (wantInstance && isStatic) {
            return false;
        }
        return true;
    }

    private static boolean matchesParameterTypes(Class<?>[] actual, Class<?>... expected) {
        if (actual.length != expected.length) {
            return false;
        }
        for (int i = 0; i < actual.length; i++) {
            if (!actual[i].equals(expected[i])) {
                return false;
            }
        }
        return true;
    }
}

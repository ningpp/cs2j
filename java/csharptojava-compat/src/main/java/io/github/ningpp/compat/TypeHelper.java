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
            // The converter emits getUnderlyingTypeCode() on enums with explicit
            // values so unsigned backing types (UInt32/UInt64) survive — a Java
            // long value field alone cannot distinguish Int64 from UInt64.
            try {
                java.lang.reflect.Method marker = type.getMethod("getUnderlyingTypeCode");
                return (TypeCode) marker.invoke(null);
            } catch (ReflectiveOperationException ignored) {
                // Fall back to field-type inference below (hand-written enums).
            }
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

    // Runtime type-argument context: JsonSerializer.deserialize(TypeReference)
    // registers the closed generic's erased raw class -> actual type arguments
    // so converter factories can recover element/key/value types that a bare
    // .class literal erases. ThreadLocal because the registry must not leak
    // across concurrent deserializations; entries are removed when the call
    // finishes.
    //
    // Nested closed generics (List<List<Integer>>) register the same raw class
    // at every layer, so the registry is a per-class stack: the OUTER layer is
    // registered first and consumed first (FIFO). Converter creation proceeds
    // outer-to-inner, so each factory's getGenericArguments call consumes its
    // own layer's arguments.
    private static final ThreadLocal<java.util.Map<Class<?>, java.util.ArrayDeque<java.lang.reflect.Type[]>>>
        TYPE_ARGUMENTS = ThreadLocal.withInitial(java.util.HashMap::new);

    // Consumed layers, keyed by raw class: converter factories query the same
    // raw class several times for one layer (e.g. KeyValuePair<K,V> reads
    // arguments twice), so after a layer is consumed its arguments are kept
    // here for repeat queries within the same registration session.
    private static final ThreadLocal<java.util.Map<Class<?>, java.lang.reflect.Type[]>>
        CONSUMED_ARGUMENTS = ThreadLocal.withInitial(java.util.HashMap::new);

    // Declared type arguments of the member currently being processed, keyed by raw
    // class. Fed by registerMemberType and read by converterCacheKey, which keeps the
    // converter cache from reusing a converter built for a different member's
    // arguments (the generated cache is keyed by the erased raw class).
    private static final ThreadLocal<java.util.Map<Class<?>, java.lang.reflect.Type[]>>
        MEMBER_ARGUMENTS = ThreadLocal.withInitial(java.util.HashMap::new);

    // Nesting guard: a deserialize call inside a converter (nested
    // JsonSerializerCompat.deserialize) registers the same raw classes again;
    // the registry is only cleared when the outermost registration finishes.
    private static final ThreadLocal<int[]> REGISTRATION_DEPTH = ThreadLocal.withInitial(() -> new int[1]);

    /** Registers closed-generic type arguments for a raw class (outer layers first). */
    public static void registerTypeArguments(Class<?> raw, java.lang.reflect.Type[] args) {
        if (raw != null && args != null && args.length > 0) {
            TYPE_ARGUMENTS.get().computeIfAbsent(raw, k -> new java.util.ArrayDeque<>()).addLast(args);
        }
    }

    /** Marks the start of a top-level registerParameterizedType session. */
    public static void beginTypeRegistration() {
        int[] depth = REGISTRATION_DEPTH.get();
        if (depth[0] == 0) {
            // Fresh outermost session: drop stale registrations left by
            // standalone typeof(ClosedGeneric) evaluations (e.g. a converter
            // factory calling closedGeneric() outside a deserialize session).
            TYPE_ARGUMENTS.remove();
            CONSUMED_ARGUMENTS.remove();
        }
        depth[0]++;
    }

    /**
     * Runtime equivalent of typeof(ClosedGeneric&lt;...&gt;): registers the closed
     * generic's type arguments (like deserialize(TypeReference) does) and
     * returns the erased raw class. Used when converted code passes
     * typeof(KV&lt;int, string&gt;) to a converter factory — the class literal alone
     * would lose the element types.
     */
    public static Class<?> closedGeneric(Class<?> raw, Class<?>... typeArguments) {
        registerTypeArguments(raw, typeArguments);
        return raw;
    }

    /** Clears the registry when the outermost registration session finishes. */
    public static void endTypeRegistration() {
        int[] depth = REGISTRATION_DEPTH.get();
        if (depth[0] > 0) {
            depth[0]--;
            if (depth[0] == 0) {
                TYPE_ARGUMENTS.remove();
                CONSUMED_ARGUMENTS.remove();
            }
        }
    }

    /** Recursively registers every layer of a ParameterizedType (outer first). */
    public static void registerParameterizedType(java.lang.reflect.Type type) {
        if (type instanceof java.lang.reflect.ParameterizedType pt) {
            Class<?> raw = (Class<?>) pt.getRawType();
            registerTypeArguments(raw, pt.getActualTypeArguments());
            for (java.lang.reflect.Type arg : pt.getActualTypeArguments()) {
                registerParameterizedType(arg);
            }
        }
    }

    /**
     * Publishes the declared generic type of a member whose runtime type was just
     * read (PropertyInfo.getPropertyType / FieldInfo.getFieldType). Java erases the
     * type arguments of a member, so the converter factories — which only receive
     * the raw {@link Class} — would otherwise fall back to the type-parameter
     * bounds (CSharpDictionary&lt;K,V&gt; → key/element {@code Object}).
     *
     * <p>Each raw class keeps the layer chain of the most recently read member
     * (outer layer first, replacing any previous chain) so that repeated reads of
     * the same member stay idempotent and a member whose converter is already cached
     * cannot leak stale arguments into the next member. Session registrations from
     * {@link #registerParameterizedType} are consumed before member type lookup
     * happens, so they keep priority.</p>
     */
    public static void registerMemberType(java.lang.reflect.Type type) {
        if (!(type instanceof java.lang.reflect.ParameterizedType)) {
            return;
        }
        java.util.Map<Class<?>, java.util.ArrayDeque<java.lang.reflect.Type[]>> chains =
            new java.util.LinkedHashMap<>();
        collectMemberLayers(type, chains);
        for (java.util.Map.Entry<Class<?>, java.util.ArrayDeque<java.lang.reflect.Type[]>> entry
                : chains.entrySet()) {
            TYPE_ARGUMENTS.get().put(entry.getKey(), entry.getValue());
            CONSUMED_ARGUMENTS.get().remove(entry.getKey());
            // Snapshot for converterCacheKey: the converter created for this member must
            // be cached under the closed generic type, not the erased raw class.
            MEMBER_ARGUMENTS.get().put(entry.getKey(), entry.getValue().peekFirst());
        }
    }

    private static void collectMemberLayers(
        java.lang.reflect.Type type,
        java.util.Map<Class<?>, java.util.ArrayDeque<java.lang.reflect.Type[]>> chains) {
        if (!(type instanceof java.lang.reflect.ParameterizedType pt)) {
            return;
        }
        Class<?> raw = (Class<?>) pt.getRawType();
        java.lang.reflect.Type[] args = pt.getActualTypeArguments();
        if (args.length > 0) {
            chains.computeIfAbsent(raw, k -> new java.util.ArrayDeque<>()).addLast(args);
        }
        for (java.lang.reflect.Type arg : args) {
            collectMemberLayers(arg, chains);
        }
    }

    /**
     * Forgets the current thread's type-argument registrations. Callers that drive
     * conversion member-by-member (and tests) use this to avoid leaking a registration
     * into unrelated lookups on the same thread.
     */
    static void clearThreadRegistrations() {
        TYPE_ARGUMENTS.get().clear();
        CONSUMED_ARGUMENTS.get().clear();
        MEMBER_ARGUMENTS.get().clear();
    }

    /**
     * The type arguments that the next {@link #getGenericArguments} call would report for
     * {@code raw}, without consuming them: the pending (outermost) session layer, else the
     * layer already consumed (repeat queries report it again).
     */
    static java.lang.reflect.Type[] peekGenericArguments(Class<?> raw) {
        java.util.ArrayDeque<java.lang.reflect.Type[]> deque = TYPE_ARGUMENTS.get().get(raw);
        if (deque != null && !deque.isEmpty()) {
            return deque.peekFirst();
        }
        return CONSUMED_ARGUMENTS.get().get(raw);
    }

    /**
     * Key used by the generated converter cache ({@code JsonSerializerOptions._converters}).
     * Returns the key unchanged for anything that is not a raw class with published
     * member type arguments; otherwise it returns a closed-generic key so that two
     * members sharing a raw class but not its type arguments (CSharpList&lt;String&gt;
     * vs CSharpList&lt;Integer&gt;) do not share one converter instance. The generated
     * cache only looks up by the raw class, so such entries are effectively
     * "create fresh per member", which is what makes the published member arguments
     * take effect instead of being masked by an earlier member's converter.
     */
    public static Object converterCacheKey(Object key) {
        if (!(key instanceof Class<?> raw)) {
            return key;
        }
        java.lang.reflect.Type[] args = MEMBER_ARGUMENTS.get().get(raw);
        if (args == null) {
            // Session registrations (typeof(ClosedGeneric), deserialize(TypeReference)) publish
            // layered arguments per raw class; converter creation consumes one layer at a time,
            // so two lookups for the same raw class - outer list, then its element list - must
            // not share one cache entry. Peeking (not consuming) keeps the key stable for repeat
            // queries of the layer already in effect.
            args = peekGenericArguments(raw);
        }
        if (args == null) {
            if (raw.getTypeParameters().length == 0) {
                return key;
            }
            // Generic raw class without published arguments: key it apart from the raw
            // class as well. Such a converter was built from erased type-parameter bounds
            // (element/key types Object); letting the generated raw-class lookup find it
            // would hand it to a member that does publish real arguments and undo the
            // published arguments entirely.
            args = new java.lang.reflect.Type[0];
        }
        return new ClosedGenericKey(raw, args);
    }

    /** Closed-generic cache key: raw class plus the type arguments in effect. */
    private static final class ClosedGenericKey {
        private final Class<?> raw;
        private final java.lang.reflect.Type[] args;
        private final int hash;

        ClosedGenericKey(Class<?> raw, java.lang.reflect.Type[] args) {
            this.raw = raw;
            this.args = args.clone();
            this.hash = 31 * raw.hashCode() + java.util.Arrays.hashCode(this.args);
        }

        @Override
        public boolean equals(Object other) {
            return other instanceof ClosedGenericKey key
                && raw == key.raw
                && java.util.Arrays.equals(args, key.args);
        }

        @Override
        public int hashCode() {
            return hash;
        }

        @Override
        public String toString() {
            return raw.getName() + java.util.Arrays.toString(args);
        }
    }

    /** Extracts the raw class from a Type (ParameterizedType → raw, Class → itself). */
    public static Class<?> toRawClass(java.lang.reflect.Type type) {
        if (type instanceof Class<?> c) {
            return c;
        }
        if (type instanceof java.lang.reflect.ParameterizedType pt) {
            return (Class<?>) pt.getRawType();
        }
        return Object.class;
    }

    /** Recursively converts a closed generic Type to its erased Class (used by converters). */
    public static Class<?> toErasedClass(java.lang.reflect.Type type) {
        return toRawClass(type);
    }

    public static Class<?>[] getGenericArguments(Class<?> type) {
        if (type == null) {
            return new Class<?>[0];
        }
        // Prefer registered runtime type arguments (from deserialize(TypeReference)).
        // Nested generics register the same raw class per layer; converter
        // creation consumes layers outer-to-inner, so poll the oldest entry.
        java.util.ArrayDeque<java.lang.reflect.Type[]> deque = TYPE_ARGUMENTS.get().get(type);
        if (deque != null && !deque.isEmpty()) {
            java.lang.reflect.Type[] registered = deque.pollFirst();
            if (registered.length > 0) {
                CONSUMED_ARGUMENTS.get().put(type, registered);
                Class<?>[] result = new Class<?>[registered.length];
                for (int i = 0; i < registered.length; i++) {
                    result[i] = toRawClass(registered[i]);
                }
                return result;
            }
        }
        // Repeat query for the same layer (e.g. KeyValuePair<K,V> reads its two
        // type arguments from two separate getGenericArguments calls).
        java.lang.reflect.Type[] consumed = CONSUMED_ARGUMENTS.get().get(type);
        if (consumed != null && consumed.length > 0) {
            Class<?>[] result = new Class<?>[consumed.length];
            for (int i = 0; i < consumed.length; i++) {
                result[i] = toRawClass(consumed[i]);
            }
            return result;
        }
        java.lang.reflect.TypeVariable<?>[] params = type.getTypeParameters();
        if (params.length == 0) {
            // In the converted model, C# Nullable<T> maps to a boxed wrapper
            // class (e.g. int? → Integer.class).  When code calls
            // GetGenericArguments()[0] on such a type, it expects the
            // underlying type argument.  Since Java type erasure discards the
            // parameter, we synthesize it here.
            if (getUnderlyingType(type) != null) {
                return new Class<?>[] { type };
            }
            return new Class<?>[0];
        }
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
                    || CSharpCollection.class.isAssignableFrom(type)
                    || type == CSharpIterable.class
                    || type == CSharpGenericIDictionary.class
                    || CSharpIList.class.isAssignableFrom(type))) {
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
        // C# immutable collections implement IEnumerable<T>; the compat stand-ins
        // model them outside the CSharpGenericIterable tree, so bridge them (and
        // any converted class implementing the immutable interfaces) to keep
        // IEnumerableConverterFactory.CanConvert(typeof(IEnumerable<>)) working.
        if (baseType == CSharpGenericIterable.class && CompatTypeIdentity.implementsImmutableInterface(type)) {
            return true;
        }
        if (baseType == CSharpIList.class && type == CSharpList.class) {
            return true;
        }
        if (baseType != null && baseType.isAssignableFrom(type)) {
            return true;
        }
        // Bridge C# type-token classes (CSharpByte, CSharpSByte, etc.) and
        // their Java wrapper counterparts (Byte, Integer, etc.) so that
        // isAssignableFrom(CSharpByte.class, Byte.class) returns true.
        if (baseType != null && type != null) {
            Class<?> n1 = normalizeClassToken(baseType);
            Class<?> n2 = normalizeClassToken(type);
            if (n1 == n2) {
                return true;
            }
            Class<?> d1 = denormalizeToCSharpTypeToken(baseType);
            Class<?> d2 = denormalizeToCSharpTypeToken(type);
            if (d1 == d2) {
                return true;
            }
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
        // .NET ArrayRank is the number of comma dimensions in a single array type
        // (int[,] => rank 2). Java arrays are always rank 1; the converter maps
        // C# multidimensional types to jagged arrays and registers the original
        // rank here via getMultiDimArrayClass.
        //
        // The registration is consumed by this query: java.lang.Class is canonical,
        // so a C# int[,] and a C# int[][] are the SAME runtime class and a durable
        // marker would make every later jagged int[][] conversion look
        // multidimensional ("The type 'class [[I' is not supported").
        Integer rank = MULTI_DIM_ARRAY_RANKS.remove(type);
        return rank != null ? rank : 1;
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
        result.sort(methodComparator(type));
        return result.toArray(new MethodInfo[0]);
    }

    /**
     * Deterministic, C#-compatible member order: declared members before inherited ones,
     * then declaration order taken from the class file. Callers that take the first match
     * (reflection-based converter/member discovery) would otherwise depend on
     * {@code Class#getMethods()} order, which changes between JVM runs.
     */
    private static java.util.Comparator<MethodInfo> methodComparator(Class<?> type) {
        return java.util.Comparator
            .comparingInt((MethodInfo m) -> DeclarationOrder.hierarchyDepth(type, m.getDeclaringType()))
            .thenComparingInt(m -> DeclarationOrder.methodIndex(m.getDeclaringType(), m.getMethod()))
            .thenComparing(m -> m.getDeclaringType() == null ? "" : m.getDeclaringType().getName())
            .thenComparing(MethodInfo::getName)
            .thenComparing(m -> m.getMethod().toGenericString());
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
        // Class#getFields() order is unspecified (it varies between JVM runs); C# returns
        // metadata declaration order, which the class file preserves (see DeclarationOrder).
        result.sort(java.util.Comparator
            .comparingInt((FieldInfo f) -> DeclarationOrder.hierarchyDepth(type, f.getDeclaringType()))
            .thenComparingInt(f -> DeclarationOrder.fieldIndex(f.getDeclaringType(), f.getName()))
            .thenComparing(f -> f.getDeclaringType() == null ? "" : f.getDeclaringType().getName())
            .thenComparing(FieldInfo::getName));
        return result.toArray(new FieldInfo[0]);
    }

    public static FieldInfo[] getFields(Class<?> type) {
        return getFields(type, BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic);
    }

    public static ConstructorInfo[] getConstructors(Class<?> type, int bindingFlags) {
        if (type == null) {
            return new ConstructorInfo[0];
        }
        // Use getDeclaredConstructors() instead of getConstructors() so that
        // non-public constructors are also visible. In C#, a private nested
        // class receives a *public* default constructor, but in Java the
        // implicit default constructor inherits the class access level (private).
        // The matchesConstructorBindingFlags bridge below handles this case.
        Constructor<?>[] ctors = type.getDeclaredConstructors();
        List<ConstructorInfo> result = new ArrayList<>();
        for (Constructor<?> ctor : ctors) {
            if (matchesBindingFlags(ctor.getModifiers(), bindingFlags)
                    || matchesConstructorBindingFlags(ctor, type, bindingFlags)) {
                ctor.setAccessible(true);
                result.add(new ConstructorInfo(ctor));
            }
        }
        // Same reasoning as getMethods: callers pick the first constructor that matches
        // (object converter factories), so the order must not depend on JVM reflection order.
        result.sort(java.util.Comparator
            .comparingInt((ConstructorInfo c) -> DeclarationOrder.constructorIndex(
                c.getDeclaringType(), c.getConstructor().getParameterTypes()))
            .thenComparing(c -> c.getConstructor().toGenericString()));
        return result.toArray(new ConstructorInfo[0]);
    }

    public static ConstructorInfo[] getConstructors(Class<?> type) {
        return getConstructors(type, BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic);
    }

    public static ConstructorInfo getConstructorInfo(Class<?> type, int bindingFlags, Class<?>... parameterTypes) {
        if (type == null) {
            return null;
        }
        // First try the public-only lookup for compatibility.
        try {
            Constructor<?> ctor = type.getConstructor(parameterTypes);
            if (matchesBindingFlags(ctor.getModifiers(), bindingFlags)) {
                return new ConstructorInfo(ctor);
            }
        } catch (NoSuchMethodException ignored) {
        }
        // Fall back to declared constructors so that non-public constructors
        // are also found. This bridges the C#/Java difference where a private
        // nested class in C# gets a public default constructor, but in Java
        // the implicit default constructor is private.
        for (Constructor<?> ctor : type.getDeclaredConstructors()) {
            if (matchesParameterTypes(ctor.getParameterTypes(), parameterTypes)
                    && (matchesBindingFlags(ctor.getModifiers(), bindingFlags)
                            || matchesConstructorBindingFlags(ctor, type, bindingFlags))) {
                ctor.setAccessible(true);
                return new ConstructorInfo(ctor);
            }
        }
        return null;
    }

    public static ConstructorInfo getConstructor(Class<?> type, Class<?>... parameterTypes) {
        // C# Type.GetConstructor(Type[]) only considers public instance
        // constructors; matchesBindingFlags treats 0 as "no member matches",
        // so the default must be spelled out explicitly.
        return getConstructorInfo(type, BindingFlags.Public | BindingFlags.Instance, parameterTypes);
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
        // C# Type.GetMember(name) defaults to Public|Instance|Static.
        return getMember(type, name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static);
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
        // C# Type.GetMember(name) defaults to Public|Instance|Static.
        return getMembers(type, name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static);
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
        if (type == null) {
            // C# would NPE on ((Type)null).FullName; generated switch statements on
            // FullName must fall through to their default branch instead of NPE-ing
            // inside the switch dispatch itself, so return a non-null empty name.
            return "";
        }
        // Compat stand-ins for BCL types carry their C# identity: generated code
        // switches on FullName against C#-style names (e.g. the immutable
        // collection factories compare "System.Collections.Immutable.ImmutableHashSet`1").
        String identity = CompatTypeIdentity.getFullName(type);
        return identity != null ? identity : type.getName();
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
        // Mirrors .NET Type.GetElementType(): typeof(int[]).GetElementType() is
        // typeof(int) — keep the primitive class. Callers that need the boxed
        // form normalize through toWrapperType (e.g. JsonConverter1.canConvert).
        return type == null ? null : type.getComponentType();
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
     * Returns the Java class for a C# multidimensional array type (int[,] → int[][])
     * and records its C# rank so {@link #getArrayRank} can still detect it. The
     * converter emits this for typeof(T[,]) and generic arguments of multidim types
     * because Java class literals (int[][].class) carry no rank information.
     */
    public static Class<?> getMultiDimArrayClass(Class<?> elementType, int rank) {
        Class<?> arrayClass = java.lang.reflect.Array.newInstance(elementType, new int[rank]).getClass();
        MULTI_DIM_ARRAY_RANKS.put(arrayClass, rank);
        return arrayClass;
    }

    private static final java.util.Map<Class<?>, Integer> MULTI_DIM_ARRAY_RANKS =
        java.util.Collections.synchronizedMap(new java.util.WeakHashMap<>());

    /**
     * Mirrors List&lt;T&gt;.ToArray() where T is only known at runtime. For a
     * primitive element class (C# List&lt;int&gt; → CSharpList&lt;Integer&gt;) the
     * .NET result is int[], which cannot be expressed through Java generics, so the
     * array is built reflectively with unboxing. For reference types the result is a
     * runtime T[] (not Object[]) so downstream casts to T[] succeed.
     */
    public static Object listToArray(java.util.Collection<?> collection, Class<?> elementClass) {
        if (elementClass.isPrimitive()) {
            Object array = java.lang.reflect.Array.newInstance(elementClass, collection.size());
            int i = 0;
            for (Object element : collection) {
                java.lang.reflect.Array.set(array, i++, element);
            }
            return array;
        }
        return collection.toArray((Object[]) java.lang.reflect.Array.newInstance(elementClass, collection.size()));
    }

    /**
     * Stream counterpart of {@link #listToArray(java.util.Collection, Class)}.
     * LINQ chains lower their receivers to {@code StreamSupport.stream(...)}, so
     * {@code items.ToArray()} over an {@code IEnumerable<T>} reaches the type-parameter
     * element branch with a Stream receiver. Mirrors the Collection semantics: a
     * primitive element class produces a primitive array (C# List&lt;int&gt;.ToArray()
     * → int[]), and a reference element class produces a runtime T[] (not Object[])
     * so downstream casts to T[] succeed.
     */
    public static Object listToArray(java.util.stream.Stream<?> stream, Class<?> elementClass) {
        if (elementClass.isPrimitive()) {
            Object[] boxed = stream.toArray(Object[]::new);
            Object array = java.lang.reflect.Array.newInstance(elementClass, boxed.length);
            for (int i = 0; i < boxed.length; i++) {
                java.lang.reflect.Array.set(array, i, boxed[i]);
            }
            return array;
        }
        return stream.toArray(size -> (Object[]) java.lang.reflect.Array.newInstance(elementClass, size));
    }

    /**
     * Target-typed {@code List<T>.ToArray()} / {@code IEnumerable<T>.ToArray()}
     * result. {@link #listToArray(java.util.Collection, Class)} returns an
     * {@code Object} because the runtime array may be a primitive array
     * ({@code int[]} for C# {@code List<int>}), which a {@code (Object[])} cast at
     * the call site would reject with a {@link ClassCastException}. Typing the
     * result by the call site's target keeps Object contexts (assigning to
     * {@code state.Current.ReturnValue}) working while preserving the primitive
     * array that later member setters expect.
     */
    @SuppressWarnings("unchecked")
    public static <A> A toArrayAs(java.util.Collection<?> collection, Class<?> elementClass) {
        return (A) listToArray(collection, elementClass);
    }

    /** Stream counterpart of {@link #toArrayAs(java.util.Collection, Class)}. */
    @SuppressWarnings("unchecked")
    public static <A> A toArrayAs(java.util.stream.Stream<?> stream, Class<?> elementClass) {
        return (A) listToArray(stream, elementClass);
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
     * Normalizes C# type-token classes (CSharpSByte, CSharpByte, etc.)
     * to their corresponding Java wrapper classes so that dictionary
     * lookups by Class key succeed regardless of whether the key was
     * registered under the Java wrapper or the C# type-token.
     * <p>
     * In C#, {@code typeof(sbyte)} and {@code typeof(byte)} are distinct
     * types, but the converter maps both to Java {@code byte}/{@code Byte}.
     * The type-token classes (CSharpSByte, CSharpByte, ...) exist to
     * preserve the distinction in reflection, but when used as dictionary
     * keys they must be normalized to the Java wrapper class that was
     * actually used during converter registration.
     */
    public static Class<?> normalizeClassToken(Class<?> type) {
        if (type == null) {
            return null;
        }
        if (type == CSharpSByte.class) return Byte.class;
        if (type == CSharpByte.class) return Byte.class;
        if (type == CSharpUInt16.class) return Integer.class;
        if (type == CSharpUInt32.class) return Long.class;
        if (type == CSharpUInt64.class) return Long.class;
        return type;
    }

    /**
     * Reverse of {@link #normalizeClassToken}: maps Java wrapper classes
     * back to their C# type-token classes.  This is needed when a wrapper
     * class is used to look up a converter in {@code s_defaultSimpleConverters},
     * which registers keys as C# type-token classes (e.g.
     * {@code CSharpByte.class} for {@code ByteConverter}).
     *
     * @return the C# type-token class, or {@code type} if no mapping exists.
     */
    public static Class<?> denormalizeToCSharpTypeToken(Class<?> type) {
        if (type == null) {
            return null;
        }
        // The converter maps C# byte -> Java int/Integer (widened, unsigned
        // range preserved by masking) and C# sbyte -> Java byte/Byte.  A
        // java.lang.Byte therefore only ever represents a C# sbyte, e.g. when
        // reflection surfaces the property type of an sbyte member, so it must
        // resolve to CSharpSByte.class (SByteConverter, JsonConverter1<Byte>).
        // Mapping it to CSharpByte.class would select ByteConverter
        // (JsonConverter1<Integer>) and crash with ClassCastException on the
        // boxed Byte value.  C# byte reaches ByteConverter through the explicit
        // CSharpByte.class token (as emitted for typeof(byte)), which is a
        // direct dictionary hit and never passes through this fallback.
        // For all other wrapper types (Integer, Long, Short, etc.) the simple
        // converter is registered under the Java wrapper class itself, so no
        // remapping is needed.
        if (type == Byte.class) return CSharpSByte.class;
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
        // return as-is. A primitive source can never satisfy the T[]-typed
        // caller (checkcast Object[] fails on int[]), so always box it.
        if (!srcComponent.isPrimitive() && componentType.isAssignableFrom(srcComponent)) {
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
            // See the BindingFlags overload: the call site carries the C# member name.
            String mappedName = toJavaMemberName(name);
            if (!mappedName.equals(name)) {
                try {
                    return new MethodInfo(type.getMethod(mappedName, parameterTypes));
                } catch (NoSuchMethodException ignored) {
                    // Fall through to "not found".
                }
            }
            return null;
        }
    }

    /**
     * Bridges C# {@code Type.GetMethod(name, BindingFlags)} semantics.
     *
     * <p>Converted members are renamed to Java conventions (PascalCase → camelCase),
     * while reflection-by-name call sites keep the C# name they were written with
     * (typically {@code nameof(...)}). The exact-name pass therefore falls back to
     * the C#→Java member name before reporting "not found".</p>
     */
    public static MethodInfo getMethod(Class<?> type, String name, int bindingFlags) {
        if (type == null || name == null) {
            return null;
        }
        MethodInfo found = findMethodByName(type, name, bindingFlags);
        if (found != null) {
            return found;
        }
        String mappedName = toJavaMemberName(name);
        if (!mappedName.equals(name)) {
            return findMethodByName(type, mappedName, bindingFlags);
        }
        return null;
    }

    private static MethodInfo findMethodByName(Class<?> type, String name, int bindingFlags) {
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

    /**
     * C# member name → converted Java member name (PascalCase → camelCase). Names
     * that are already camelCase are returned unchanged.
     */
    private static String toJavaMemberName(String name) {
        if (name.isEmpty() || Character.isLowerCase(name.charAt(0))) {
            return name;
        }
        return Character.toLowerCase(name.charAt(0)) + name.substring(1);
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

        // C# semantics: Public/NonPublic (and Instance/Static) are alternative
        // inclusion filters, not requirements — a member matches when it
        // satisfies ANY requested visibility and ANY requested member kind.
        // cs2j additionally emits C# internal members as public Java members
        // (Java has no assembly-level visibility), so a NonPublic-only lookup
        // must fall back to public members to keep reflection working.
        boolean nonPublicBridgeFallback = wantNonPublic && !wantPublic && isPublic;
        if (!((wantPublic && isPublic) || (wantNonPublic && !isPublic) || nonPublicBridgeFallback)) {
            return false;
        }
        if (!((wantInstance && !isStatic) || (wantStatic && isStatic))) {
            return false;
        }
        return true;
    }

    /**
     * Bridge for the C#/Java difference in default constructor accessibility.
     * <p>
     * In C#, the implicit default constructor of a class is always {@code public}
     * regardless of the class's own access modifier. In Java, the implicit default
     * constructor inherits the access level of the enclosing class, so a
     * {@code private} nested class gets a {@code private} default constructor.
     * <p>
     * When a BindingFlags.Public lookup is performed, this method returns
     * {@code true} for a non-public constructor of a non-public class to
     * emulate the C# behaviour.
     */
    private static boolean matchesConstructorBindingFlags(Constructor<?> ctor, Class<?> type, int bindingFlags) {
        boolean wantPublic = (bindingFlags & 0x10) != 0;
        boolean wantInstance = (bindingFlags & 0x04) != 0;
        if (!wantPublic || !wantInstance) {
            return false;
        }
        int ctorMods = ctor.getModifiers();
        int typeMods = type.getModifiers();
        // If the constructor is non-public but the class itself is also
        // non-public, treat it as public (C# semantics).
        if (Modifier.isPublic(ctorMods)) {
            return false; // already matched by matchesBindingFlags
        }
        return !Modifier.isPublic(typeMods);
    }

private static boolean matchesParameterTypes(Class<?>[] actual, Class<?>... expected) {
if (actual.length != expected.length) {
return false;
}
for (int i = 0; i < actual.length; i++) {
// Use isAssignableFrom instead of equals to handle Java type erasure.
// A generic constructor parameter K erases to Object.class, so when
// the caller passes Integer.class (or any other wrapper), it should
// still match because Object.class.isAssignableFrom(Integer.class).
if (!actual[i].isAssignableFrom(expected[i])) {
return false;
}
}
return true;
}
}

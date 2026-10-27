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
     * C# Type.MakeGenericMethodParameter(position) — Reflection.Emit/generic-method
     * emulation stub. Java reflection has no method type-parameter tokens; the
     * enclosing MethodInfo.makeGenericMethod(...) records the actual arguments,
     * so this placeholder is compile-time only. RegexCompiler's reflection-emit
     * path is already stubbed on Java, so no runtime semantics are lost here.
     */
    public static Class<?> makeGenericMethodParameter(int position) {
        return Object.class;
    }


    /**
     * Mirrors Type.GetType(string) — the single-argument overload throws when
     * the type cannot be loaded (C# defaults throwOnError to true).
     */
    public static Class<?> getType(String name) {
        return getType(name, true);
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
            Class<?> mapped = mapAssemblyQualifiedName(name);
            if (mapped != null) {
                return mapped;
            }
            if (throwOnError) {
                throw new RuntimeException(e);
            }
            return null;
        }
    }

    // C# type names reaching Type.GetType carry an assembly qualification
    // ("System.Collections.Stack, System.Collections.NonGeneric"). Class.forName cannot load one, so
    // ports of helpers such as IEnumerableConverterFactoryHelpers.isNonGenericStackOrQueue always saw
    // null and their branch was dead: a Stack/Queue member fell through to the generic IEnumerable
    // converter, whose createCollection cannot populate the type and threw "The collection type
    // '...CSharpObjQueue' is abstract, an interface, or is read only". The framework collections that
    // reach here this way are exactly the ones compat models by hand.
    private static Class<?> mapAssemblyQualifiedName(String name) {
        int comma = name.indexOf(',');
        String typeName = (comma < 0 ? name : name.substring(0, comma)).trim();
        return switch (typeName) {
            case "System.Collections.Stack" -> CSharpObjStack.class;
            case "System.Collections.Queue" -> CSharpObjQueue.class;
            case "System.Collections.ArrayList" -> CSharpArrayList.class;
            default -> null;
        };
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

    // Layered arguments already claimed to resolve a class's OWN type variables (see
    // ownTypeArguments). Kept per raw class and per session so the layer that answered
    // "what is this wrapper parameterised with" is not answered again as the arguments of
    // the same raw class.
    private static final ThreadLocal<java.util.Map<Class<?>, java.util.Set<java.lang.reflect.Type[]>>>
        WRAPPER_LAYER_TAKEN = ThreadLocal.withInitial(java.util.HashMap::new);

    /** Marks the start of a top-level registerParameterizedType session. */
    /**
     * Drops every per-thread type-argument registration.
     *
     * <p>These ThreadLocals outlive a single (de)serialization, so a member read by an earlier
     * operation can still be answering lookups in a later one. That showed up as test-order
     * dependence: ~28 nested-collection WRITE tests fail in full-suite order but pass when their
     * class runs alone. The generated entry points call this before publishing their own
     * registrations so each operation starts from a clean slate.</p>
     */
    public static void clearRegistrations() {
        TYPE_ARGUMENTS.remove();
        CONSUMED_ARGUMENTS.remove();
        MEMBER_ARGUMENTS.remove();
        CALLSITE_ARGUMENTS.remove();
        WRAPPER_LAYER_TAKEN.remove();
        CONCRETE_SUBTYPES.remove();
    }

    public static void beginTypeRegistration() {
        int[] depth = REGISTRATION_DEPTH.get();
        if (depth[0] == 0) {
            // Fresh outermost session: drop stale registrations left by
            // standalone typeof(ClosedGeneric) evaluations (e.g. a converter
            // factory calling closedGeneric() outside a deserialize session).
            TYPE_ARGUMENTS.remove();
            CONSUMED_ARGUMENTS.remove();
            WRAPPER_LAYER_TAKEN.remove();
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

    /**
     * Builds a {@link java.lang.reflect.ParameterizedType} for a closed generic, so nested
     * layers survive: {@code parameterized(List.class, parameterized(List.class, Integer.class))}
     * represents {@code List<List<int>>}. Together with {@link #closedGenericType} this is what
     * the converter emits at a call site where C# had {@code typeof(List<List<int>>)} and a Java
     * class literal would erase every layer.
     */
    public static java.lang.reflect.Type parameterized(Class<?> raw, java.lang.reflect.Type... typeArguments) {
        var args = typeArguments.clone();
        return new java.lang.reflect.ParameterizedType() {
            @Override
            public java.lang.reflect.Type[] getActualTypeArguments() {
                return args.clone();
            }

            @Override
            public java.lang.reflect.Type getRawType() {
                return raw;
            }

            @Override
            public java.lang.reflect.Type getOwnerType() {
                return null;
            }

            @Override
            public String toString() {
                return raw.getSimpleName() + java.util.Arrays.toString(args);
            }
        };
    }

    /**
     * Runtime equivalent of {@code typeof(Closed<...>)} for an already-built closed generic
     * (see {@link #parameterized}): publishes every layer to the converter factories and
     * returns the erased raw class, so the emitted cast still compiles.
     */
    public static Class<?> closedGenericType(java.lang.reflect.Type closed) {
        registerParameterizedType(closed);
        return toRawClass(closed);
    }

    /**
     * Lowest-priority type arguments, published at a call site whose static type was a closed
     * generic. They are consulted only after the pending/consumed session layers and before the
     * type-parameter bounds, so a member that published its own arguments through
     * {@code PropertyInfo}/{@code FieldInfo} keeps them: registering unconditionally at every
     * call site used to override the member layers and cost more tests than it fixed.
     */
    private static final ThreadLocal<java.util.Map<Class<?>, java.util.ArrayDeque<java.lang.reflect.Type[]>>> CALLSITE_ARGUMENTS =
        ThreadLocal.withInitial(java.util.HashMap::new);

    /** Publishes a call site's closed generic (all layers) at the lowest priority. */
    public static void registerCallSiteType(java.lang.reflect.Type type) {
        // A fresh call site replaces whatever the previous one published, then every layer of
        // this type is appended outer-first (nested generics repeat the raw class, so the layers
        // must live in one per-raw chain exactly like the session registry).
        CALLSITE_ARGUMENTS.get().clear();
        appendCallSiteLayers(type);
    }

    private static void appendCallSiteLayers(java.lang.reflect.Type type) {
        if (type instanceof java.lang.reflect.ParameterizedType pt) {
            Class<?> raw = (Class<?>) pt.getRawType();
            java.lang.reflect.Type[] args = pt.getActualTypeArguments();
            if (args.length > 0) {
                CALLSITE_ARGUMENTS.get()
                    .computeIfAbsent(raw, k -> new java.util.ArrayDeque<>())
                    .addLast(args);
            }
            for (java.lang.reflect.Type arg : args) {
                appendCallSiteLayers(arg);
            }
        }
    }

    /** Emission form: publishes the call site's closed generic and returns the erased raw class. */
    public static Class<?> closedCallSiteGenericType(java.lang.reflect.Type closed) {
        registerCallSiteType(closed);
        return toRawClass(closed);
    }

    /** Clears the registry when the outermost registration session finishes. */
    public static void endTypeRegistration() {
        int[] depth = REGISTRATION_DEPTH.get();
        if (depth[0] > 0) {
            depth[0]--;
            if (depth[0] == 0) {
                TYPE_ARGUMENTS.remove();
                CONSUMED_ARGUMENTS.remove();
                WRAPPER_LAYER_TAKEN.remove();
                CONCRETE_SUBTYPES.remove();
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
            return;
        }
        // An array of a closed generic ("Dictionary<string,int>[]") is a GenericArrayType, not a
        // ParameterizedType. Its element layer still has to be registered or the converter for the
        // array resolves its elements to Object/JsonElement while the caller's generic expects the
        // concrete element type ("JsonElement cannot be cast to Integer").
        if (type instanceof java.lang.reflect.GenericArrayType gat) {
            registerParameterizedType(gat.getGenericComponentType());
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
        registerMemberType(type, null);
    }

    /** @see #registerMemberType(java.lang.reflect.Type) */
    public static void registerMemberType(java.lang.reflect.Type type, MemberInfo member) {
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
            if (member != null) {
                rememberMemberArguments(member, entry.getValue().peekFirst());
            }
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
     * Retires the pending member registration a closed-type converter creation just served.
     *
     * <p>Warm-up registers each member's layer chain through {@link #registerMemberType}; legacy
     * converter creation then <em>consumed</em> that chain via {@link #getGenericArguments}, so
     * nothing was left pending. The closed-type chain resolves the converter from the closure's
     * layers instead and never touches the queue — leaving the chain pending, where the next
     * unrelated Class-path lookup of the same raw class polls it as if it were its own arguments
     * (observed as DictionaryTests.hashtable / iDictionary_Keys failing only in full-suite order
     * after ArrayTests.readObjectArray warmed up SimpleTestClass's dictionary members).</p>
     *
     * <p>The member's per-raw bookkeeping is dropped entirely — pending queue, consumed replay
     * and the MEMBER_ARGUMENTS snapshot — rather than mirroring the legacy consumed state: the
     * closed chain never re-reads these layers (converter caches are keyed structurally), while
     * a stale consumed/member replay is exactly what mis-keys later unrelated lookups.</p>
     *
     * <p>This is ThreadLocal <em>hygiene</em> for the entry/warm-up registry, not a dependency:
     * the closed chain never reads these layers, it only cleans them up.</p>
     */
    public static void consumeMemberTypeArguments(java.lang.reflect.Type closed) {
        if (!(closed instanceof java.lang.reflect.ParameterizedType)) {
            return;
        }
        java.util.Map<Class<?>, java.lang.reflect.Type[]> layers = collectClosureLayers(closed);
        for (java.util.Map.Entry<Class<?>, java.lang.reflect.Type[]> entry : layers.entrySet()) {
            Class<?> raw = entry.getKey();
            TYPE_ARGUMENTS.get().remove(raw);
            CONSUMED_ARGUMENTS.get().remove(raw);
            MEMBER_ARGUMENTS.get().remove(raw);
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
        CALLSITE_ARGUMENTS.get().clear();
        WRAPPER_LAYER_TAKEN.get().clear();
        CONCRETE_SUBTYPES.get().clear();
    }

    /**
     * The type-argument layer currently in effect for {@code raw}, without consuming anything.
     *
     * <p>Converter factories resolve their element/key types through
     * {@link #getGenericArguments} <em>before</em> the converter cache key is computed, so the
     * layer that was just consumed identifies the converter being created; a pending layer only
     * applies while nothing has been consumed yet. Getting this order wrong makes consecutive
     * layers of {@code List<List<int>>} collide on one cache entry
     * ({@code Integer cannot be cast to CSharpList}).</p>
     */
    static java.lang.reflect.Type[] currentGenericArgumentsLayer(Class<?> raw) {
        java.lang.reflect.Type[] consumed = CONSUMED_ARGUMENTS.get().get(raw);
        if (consumed != null) {
            return consumed;
        }
        java.util.ArrayDeque<java.lang.reflect.Type[]> deque = TYPE_ARGUMENTS.get().get(raw);
        if (deque != null && !deque.isEmpty()) {
            return deque.peekFirst();
        }
        // Lowest priority, matching getGenericArguments.
        java.util.ArrayDeque<java.lang.reflect.Type[]> callSite = CALLSITE_ARGUMENTS.get().get(raw);
        return callSite == null || callSite.isEmpty() ? null : callSite.peekFirst();
    }

    /**
     * Builds the instance the generated dictionary converters populate for an interface-typed
     * target (C# <c>IDictionary</c> → CSharpIDictionary), or null when the target truly cannot be
     * populated.
     *
     * <p>The generated <c>IDictionaryConverter.createCollection</c> interface branch created a bare
     * <c>new CSharpDictionary&lt;String,Object&gt;()</c> while its own <c>TCollection</c> is
     * <c>CSharpIDictionary</c>, so the instance failed the boundary cast at the test's
     * <c>(CSharpIDictionary) JsonSerializer.deserialize(...)</c>. CSharpDictionary cannot implement
     * CSharpIDictionary directly (reflection-visible conflict on <c>remove</c>: boolean vs void), so
     * the bridge mirroring C#'s implicit Dictionary → IDictionary conversion is used instead.</p>
     */
    public static Object newNonGenericDictionary(Class<?> typeToConvert, boolean assignableFromRuntime) {
        if (typeToConvert == null || !assignableFromRuntime) {
            return null;
        }
        // CSharpDictionary implements the GENERIC dictionary interface only, so a Java
        // isAssignableFrom(CSharpDictionary.class) check is necessarily false and cannot guard this:
        // the assignability that matters is the C# one, which the caller already evaluated and passed
        // in as `assignableFromRuntime` (computed through compat's isAssignableFrom bridge). Requiring
        // the Java relation made this method always return null, and IDictionaryConverter.add then
        // failed with "Cannot invoke CSharpIDictionary.put(Object,Object) because the return value of
        // getReturnValue() is null" for every non-generic IDictionary member.
        if (!CSharpIDictionary.class.isAssignableFrom(typeToConvert)) {
            return null;
        }
        return CSharpIDictionary.from(new CSharpDictionary<String, Object>());
    }

    /**
     * The JsonPropertyInfo the WRITE path should install for a collection's element frame.
     *
     * <p>The generated write path takes it from {@code JsonClassInfo.getElementClassInfo()}, built
     * during warm-up from the erased element {@code Class}. When several members share a raw element
     * class with different arguments that class info can belong to another member, so the element
     * frame receives a property of the wrong type and reflection then invokes a getter with the
     * wrong receiver ("IllegalArgumentException: public ... getChild2IList() target=..."). The
     * converter knows its own element type, so prefer the class info of that type.</p>
     *
     * <p>Reflection-only: compat cannot reference the generated SDK types. Returns the fallback when
     * the converter does not report an element type, or when the lookup fails.</p>
     */
    public static Object writeElementPropertyInfo(Object converter, Object options, Object fallback) {
        if (converter == null || options == null) {
            return fallback;
        }
        try {
            java.lang.reflect.Method getElementType = converter.getClass().getMethod("getElementType");
            Object elementType = getElementType.invoke(converter);
            if (!(elementType instanceof Class<?> elementClass)) {
                return fallback;
            }
            java.lang.reflect.Method getOrAddClass = options.getClass().getMethod("getOrAddClass", Class.class);
            Object elementInfo = getOrAddClass.invoke(options, toWrapperType(elementClass));
            if (elementInfo == null) {
                return fallback;
            }
            java.lang.reflect.Method getPropertyInfo = elementInfo.getClass().getMethod("getPropertyInfoForClassInfo");
            Object propertyInfo = getPropertyInfo.invoke(elementInfo);
            return propertyInfo != null ? propertyInfo : fallback;
        } catch (ReflectiveOperationException ignored) {
            return fallback;
        }
    }
    /**
     * Narrows the "current member" argument context to a collection's ELEMENT for the duration of
     * an element class-info lookup.
     *
     * <p>{@code JsonClassInfo.getElementClassInfo()} resolves the element with
     * {@code getOptions().getOrAddClass(getElementType())}, and that call reaches
     * {@link #converterCacheKey}, which <em>prefers {@code MEMBER_ARGUMENTS}</em> — the arguments of
     * whichever member was read last on this thread. Building the element's class info therefore
     * keys off an unrelated member's arguments: for {@code CSharpGenericIList<Object>[]} the element
     * gets a {@code CSharpGenericIList<String>} converter and the write path then calls
     * {@code StringConverter.write} on {@code Integer} elements
     * ("class java.lang.Integer cannot be cast to class java.lang.String").</p>
     *
     * <p>While the element's own declared arguments are known they are installed as the member
     * arguments, so the element's converter is built and cached for the type it actually is. The
     * previous context is restored by {@link #endElementArgumentsScope(Object)}; a null token means
     * nothing was changed.</p>
     */
    public static Object beginElementArgumentsScope(Class<?> ownerType, Class<?> elementType, java.lang.reflect.Type[] declaredArgs) {
        if (elementType == null || declaredArgs == null || declaredArgs.length == 0) {
            return null;
        }
        // Only a SAME-RAW-CLASS nesting (List<List<short>>, Queue<Queue<int>>) needs the element's
        // own arguments here. For every other element the ambient context is what the element's
        // converter was built with, and overriding it perturbs unrelated members: with the scope
        // applied unconditionally, ImmutableCollectionsTests.serializeList/serializeStack started
        // invoking SimpleTestClass getters on a String receiver once the suite ran in its normal
        // order (they passed in isolation). Keeping the scope to same-raw-class nesting captures the
        // whole nested-collection cluster without touching the rest.
        if (ownerType == null || ownerType != elementType) {
            return null;
        }
        java.lang.reflect.Type[] savedMember = MEMBER_ARGUMENTS.get().put(elementType, declaredArgs);
        java.lang.reflect.Type[] savedConsumed = CONSUMED_ARGUMENTS.get().remove(elementType);
        trace("beginElementArgumentsScope(" + elementType.getSimpleName() + ") = " + java.util.Arrays.toString(declaredArgs));
        return new Object[] { elementType, savedMember, savedConsumed };
    }

    /**
     * The type arguments of the element a collection's owner arguments describe.
     *
     * <p>{@code List<List<short>>} is described by {@code [List<short>]}: the owner's first argument
     * IS the element, and its own arguments ({@code [short]}) are what an element lookup must key
     * off. For {@code ImmutableList<SimpleTestClass>} the first argument is a bare class, so the
     * element has no arguments and the scope must stay empty — applying the OWNER's arguments there
     * would claim {@code SimpleTestClass} is itself parameterized, and the element frame then gets a
     * property info belonging to some other type (observed as
     * "public short SimpleTestClass.getMyInt16() target=java.lang.String").</p>
     *
     * @return the element's arguments, or an empty array when it has none.
     */
    public static java.lang.reflect.Type[] elementArgumentsOfOwner(java.lang.reflect.Type[] ownerArgs) {
        if (ownerArgs == null || ownerArgs.length == 0 || ownerArgs[0] == null) {
            return new java.lang.reflect.Type[0];
        }
        java.lang.reflect.Type first = ownerArgs[0];
        if (first instanceof java.lang.reflect.ParameterizedType pt) {
            java.lang.reflect.Type[] args = pt.getActualTypeArguments();
            if (args.length > 0) {
                return args;
            }
        }
        if (first instanceof java.lang.reflect.GenericArrayType gat
            && gat.getGenericComponentType() instanceof java.lang.reflect.ParameterizedType cpt) {
            return cpt.getActualTypeArguments();
        }
        return new java.lang.reflect.Type[0];
    }

    /**
     * Reinstates the argument context of a class info's OWNER inside an element lookup scope.
     *
     * <p>{@code JsonClassInfo} stores the owner's declared arguments at construction (see
     * {@link #captureDeclaredTypeArguments}) and re-enters them here, so the nested lookups the
     * element lookup performs still see the collection's arguments rather than whatever member the
     * thread happened to touch last.</p>
     */
    public static Object pushArgumentContext(Class<?> ownerType, Class<?> elementType, java.lang.reflect.Type[] ownerArgs) {
        if (ownerType == null || ownerType != elementType || ownerArgs == null || ownerArgs.length == 0) {
            return null;
        }
        java.util.Map<Class<?>, java.lang.reflect.Type[]> savedMember = new java.util.HashMap<>(MEMBER_ARGUMENTS.get());
        java.util.Map<Class<?>, java.lang.reflect.Type[]> savedConsumed = new java.util.HashMap<>(CONSUMED_ARGUMENTS.get());
        MEMBER_ARGUMENTS.get().clear();
        CONSUMED_ARGUMENTS.get().clear();
        return new Object[] { savedMember, savedConsumed };
    }

    /** Undoes {@link #pushArgumentContext(java.lang.reflect.Type[])}. */
    public static void popArgumentContext(Object token) {
        if (!(token instanceof Object[] entry) || entry.length != 2) {
            return;
        }
        @SuppressWarnings("unchecked")
        java.util.Map<Class<?>, java.lang.reflect.Type[]> savedMember = (java.util.Map<Class<?>, java.lang.reflect.Type[]>) entry[0];
        @SuppressWarnings("unchecked")
        java.util.Map<Class<?>, java.lang.reflect.Type[]> savedConsumed = (java.util.Map<Class<?>, java.lang.reflect.Type[]>) entry[1];
        MEMBER_ARGUMENTS.get().clear();
        MEMBER_ARGUMENTS.get().putAll(savedMember);
        CONSUMED_ARGUMENTS.get().clear();
        CONSUMED_ARGUMENTS.get().putAll(savedConsumed);
    }

    /**
     * The declared type arguments resolution would use for {@code type} right now, without consuming
     * anything. {@code JsonClassInfo} stores this at construction so an element lookup performed much
     * later (after other members have overwritten the context) can still key off this class info's
     * own arguments.
     */
    public static java.lang.reflect.Type[] captureDeclaredTypeArguments(Class<?> type) {
        java.lang.reflect.Type[] args = currentGenericArgumentsLayer(type);
        return args == null ? new java.lang.reflect.Type[0] : args.clone();
    }


    /** Undoes {@link #beginElementArgumentsScope(Class, java.lang.reflect.Type[])}. */
    public static void endElementArgumentsScope(Object token) {
        if (!(token instanceof Object[] entry) || entry.length != 3) {
            return;
        }
        Class<?> elementType = (Class<?>) entry[0];
        if (entry[1] == null) {
            MEMBER_ARGUMENTS.get().remove(elementType);
        } else {
            MEMBER_ARGUMENTS.get().put(elementType, (java.lang.reflect.Type[]) entry[1]);
        }
        if (entry[2] != null) {
            CONSUMED_ARGUMENTS.get().put(elementType, (java.lang.reflect.Type[]) entry[2]);
        }
    }

    // Member -> the arguments its OWN declared type has, captured while the member was reflected.
    //
    // MEMBER_ARGUMENTS is one slot per RAW CLASS, so when two members share a raw class with
    // different arguments (List<string> and List<List<short>>) whichever member is reflected last
    // owns the slot and every other member's lookup then reads ITS arguments - the 2-D list property
    // received List<string>'s class info and a String element converter. Keying by the member gives
    // each member its own copy.
    private static final java.util.Map<MemberInfo, java.lang.reflect.Type[]> MEMBER_DECLARED_ARGUMENTS =
        java.util.Collections.synchronizedMap(new java.util.WeakHashMap<>());

    /** Remembers the arguments the member's own declared type has. */
    public static void rememberMemberArguments(MemberInfo member, java.lang.reflect.Type[] args) {
        if (member != null && args != null && args.length > 0) {
            MEMBER_DECLARED_ARGUMENTS.put(member, args.clone());
        }
    }

    /** The arguments remembered for {@code member}, or null. */
    public static java.lang.reflect.Type[] memberArguments(MemberInfo member) {
        return member == null ? null : MEMBER_DECLARED_ARGUMENTS.get(member);
    }

    /**
     * The class info already cached under the key the OWNING member's arguments imply, or null.
     *
     * <p><b>Known insufficient (round 41).</b> This is a pure HIT-PATH probe, and it cannot fix the
     * nested-collection cluster because the entry it looks for is never created:
     * {@code getOrAddClass} keys by the ERASED class, so
     * {@code CSharpList[CSharpList&lt;Integer&gt;]} does not exist to be hit. Measured with probes in
     * the generated tree: {@code getOrAddClass(CSharpList)} is entered once, under
     * {@code CSharpList[class java.lang.String]} — the {@code List&lt;string&gt;} member's key.</p>
     *
     * <p>Creating the entry instead was implemented and measured (round 41): it moves the failure
     * rather than removing it — the 2-D property then gets {@code ListOfTConverter} but its inner
     * element is still read with the wrong converter, and the change costs 39 other tests
     * (net 737 → 756 on the same regenerated tree). See {@code systematic-fix-plan.md} §8.2/§8.2.1
     * before attempting it again.</p>
     *
     * <p>{@code JsonPropertyInfo.getRuntimeClassInfo()} looks its class info up by the ERASED runtime
     * type, and the generated cache funnels that through {@link #converterCacheKey}, which reads
     * {@code MEMBER_ARGUMENTS} — a single slot per raw class. By read time a later member
     * ({@code List<string>}) has overwritten that slot, so a {@code List<List<short>>} property asks
     * for {@code CSharpList[String]} and receives the wrong class info; its element then gets a String
     * converter and reads {@code [10,11]} as a string.</p>
     *
     * <p>This is a pure HIT-PATH probe: it only READS the cache, under the key computed from the
     * member's own recorded arguments. A hit answers the lookup correctly and nothing else runs. A
     * miss returns null and the caller keeps the generated behaviour, so no other path changes shape
     * — in particular {@code getOrAddClass} and the element lookup inside it are never entered with
     * the wrong arguments, which is what made the round-8 explicit scope regress.</p>
     *
     * <p>Reflection-only: compat cannot reference the generated options/class-info types.</p>
     */
    public static Object cachedClassInfoForMember(MemberInfo member, Object options, Class<?> rawType) {
        if (member == null || options == null || rawType == null) {
            return null;
        }
        java.lang.reflect.Type[] args = memberArguments(member);
        // Only the single-type-argument shape is targeted: List<string> and List<List<short>> share the
        // raw CSharpList and pollute each other's one slot. Multi-argument members (Dictionary<K,V>,
        // ValueTuple, ...) keep the generated path, because their arguments can legitimately differ
        // from what warm-up cached - applying the key to the nullable-dictionary members of NullTests
        // regressed them (measured).
        if (args == null || args.length != 1) {
            return null;
        }
        try {
            java.lang.reflect.Field classes = options.getClass().getDeclaredField("_classes");
            classes.setAccessible(true);
            Object table = classes.get(options);
            if (!(table instanceof java.util.Map<?, ?> map)) {
                return null;
            }
            Object key = new ClosedGenericKey(rawType, args);
            Object found = map.get(key);
            if (found != null) {
                trace("cachedClassInfoForMember(" + member.getName() + ") HIT " + key);
            }
            return found;
        } catch (ReflectiveOperationException ignored) {
            return null;
        }
    }

    /**
     * Mirrors {@code JsonClassInfo.GetKey(ReadOnlySpan&lt;byte&gt;)}: the first 7 bytes plus the length.
     *
     * <p>The generated method lowers the byte reads to "reference + {@code Unsafe}", and in Java
     * {@code MemoryMarshal.getReference} yields the first element's VALUE while
     * {@code Unsafe.readUnaligned} is a pass-through returning that same value. Every name of the
     * same length that starts with the same byte therefore produced the SAME key — the byte offsets
     * were never read at all.</p>
     *
     * <p>{@code IsPropertyRefEqual} accepts a short name ({@code <= 7} bytes) on key equality ALONE,
     * so a 6-byte name starting with 'M' matched whichever 6-byte 'M…' property the scan reached
     * first. Measured: reading {@code "MyChar" : "a"} reached the ENUM converter and reported
     * "The JSON value could not be converted to SampleEnum. Path: $.MyEnum" in nine tests.</p>
     */
    public static long propertyNameKey(ReadOnlySpan<Integer> name) {
        int length = name.getLength();
        long key;
        if (length > 7) {
            long first8 = 0;
            for (int i = 0; i < 8; i++) {
                first8 |= ((long) byteAt(name, i)) << (8 * i);
            }
            key = first8 & 0x00FFFFFFFFFFFFFFL;
            key |= ((long) Math.min(length, 0xFF)) << 56;
            return key;
        }
        if (length > 5) {
            key = 0;
            for (int i = 0; i < 6; i++) {
                key |= ((long) byteAt(name, i)) << (8 * i);
            }
        } else if (length > 3) {
            key = 0;
            for (int i = 0; i < 4; i++) {
                key |= ((long) byteAt(name, i)) << (8 * i);
            }
        } else if (length > 1) {
            key = (long) byteAt(name, 0) | ((long) byteAt(name, 1) << 8);
        } else if (length == 1) {
            key = byteAt(name, 0);
        } else {
            key = 0;
        }
        key |= ((long) length) << 56;
        if ((length & 1) != 0) {
            // The last byte is not covered by the reads above.
            int offset = length - 1;
            key |= ((long) byteAt(name, offset)) << (offset * 8);
        }
        return key;
    }

    private static int byteAt(ReadOnlySpan<Integer> name, int index) {
        Integer value = name.get(index);
        return (value == null ? 0 : value) & 0xFF;
    }

    /** Opt-in diagnostics: {@code -Dcs2j.trace.typeargs=true} traces type-argument resolution. */
    private static final boolean TRACE_TYPE_ARGS = Boolean.getBoolean("cs2j.trace.typeargs");

    static void trace(String message) {
        if (TRACE_TYPE_ARGS) {
            System.err.println("[cs2j.typeargs] " + message);
        }
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
            // not share one cache entry. The consumed layer wins because factories resolve their
            // element/key types before the cache key is computed.
            args = currentGenericArgumentsLayer(raw);
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
        ClosedGenericKey closedKey = new ClosedGenericKey(raw, args);
        trace("converterCacheKey(" + raw.getSimpleName() + ") = " + closedKey
            + (MEMBER_ARGUMENTS.get().get(raw) != null ? " [member layer]" : " [session layer]"));
        return closedKey;
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
        // An array of a closed generic reaches the converters as a GenericArrayType. Returning
        // Object.class here made every such target convert to a bare Object; the array class of the
        // erased component is what the array converter needs.
        if (type instanceof java.lang.reflect.GenericArrayType gat) {
            Class<?> component = toRawClass(gat.getGenericComponentType());
            return java.lang.reflect.Array.newInstance(component, 0).getClass();
        }
        return Object.class;
    }

    /**
     * Creates the collection a non-generic enumerable converter should populate.
     *
     * <p>The generated {@code IEnumerableConverter.createCollection} always built
     * {@code new CSharpList<Object>()} for the interface-typed branch (the conversion of C#
     * {@code new List<TElement>()}). For a converted non-generic collection that is neither the
     * target type nor the type {@code getRuntimeType()} reports, the generated guard
     * {@code isAssignableFrom(getTypeToConvert(), getRuntimeType())} rejects the collection outright,
     * and once it is satisfied the populated list holds {@code CSharpList} elements, so converting to
     * the target's array type dies with an ArrayStoreException.</p>
     *
     * <p>Instantiating the TARGET type satisfies both: {@code GetRuntimeType()} is the target or a
     * supertype, and {@code ConvertCollection} then produces the right element type.</p>
     *
     * @param assignableFromRuntime the generated guard's verdict, computed by the caller.
     * @return the collection to populate, or null when the target really cannot be populated.
     */
    public static Object newNonGenericCollection(Class<?> typeToConvert, boolean assignableFromRuntime) {
        if (typeToConvert == null || typeToConvert.isInterface()
                || java.lang.reflect.Modifier.isAbstract(typeToConvert.getModifiers())) {
            return new CSharpList<Object>();
        }
        if (!assignableFromRuntime) {
            // Keep the generated guard's rejection for a target that cannot be populated.
            return null;
        }
        try {
            java.lang.reflect.Constructor<?> ctor = typeToConvert.getDeclaredConstructor();
            ctor.setAccessible(true);
            return ctor.newInstance();
        } catch (ReflectiveOperationException | RuntimeException e) {
            return new CSharpList<Object>();
        }
    }

    /**
     * The collection to ADD into for a converted list converter: the return value itself when it
     * already is a {@link CSharpGenericIList}, otherwise a live generic view of it.
     *
     * <p>A converted non-generic list (CSharpArrayList) is a legitimate {@code IList<T>} in C# terms
     * but cannot implement {@link CSharpGenericIList} directly, because C# {@code IList.Add} returns
     * {@code int} while {@code ICollection<T>.Add} returns {@code boolean} and Java allows one
     * signature. The generated {@code IListOfTConverter.add} casts the return value to the generic
     * interface and would throw ClassCastException; this hands it a view onto the same backing list
     * instead.</p>
     */
    public static CSharpGenericIList<Object> listToAddInto(Object returnValue) {
        if (returnValue instanceof CSharpGenericIList) {
            @SuppressWarnings("unchecked")
            CSharpGenericIList<Object> generic = (CSharpGenericIList<Object>) returnValue;
            return generic;
        }
        if (returnValue instanceof CSharpArrayList arrayList) {
            return arrayList.asGenericList();
        }
        throw new ClassCastException("Cannot add into " + (returnValue == null ? "null" : returnValue.getClass().getName()));
    }

    /** Recursively converts a closed generic Type to its erased Class (used by converters). */
    public static Class<?> toErasedClass(java.lang.reflect.Type type) {
        return toRawClass(type);
    }

    // =====================================================================
    // Closed-type metadata chain (specs/2026-09-22-closed-type-metadata-chain-design.md).
    //
    // Member/element types flow through the generated serializer metadata as explicit
    // java.lang.reflect.Type values carried on JsonPropertyInfo / JsonClassInfo. The
    // helpers below resolve types from those closures WITHOUT touching any ThreadLocal
    // registry (TYPE_ARGUMENTS/CONSUMED_ARGUMENTS/MEMBER_ARGUMENTS/CALLSITE_ARGUMENTS):
    // resolution is a pure function of the closure plus the class hierarchy.
    // =====================================================================

    /**
     * Flattens a closed generic Type into per-raw-class layers, outermost layer first.
     *
     * <p>{@code CSharpList<CSharpList<Integer>>} yields one entry (the raw class repeats per
     * layer, the outermost wins); {@code CSharpDictionary<String, CSharpList<Integer>>} yields
     * two. This mirrors the consumption order of the legacy session registry, but as a pure
     * function of the closure — no state, no thread affinity.</p>
     */
    public static java.util.LinkedHashMap<Class<?>, java.lang.reflect.Type[]> collectClosureLayers(java.lang.reflect.Type type) {
        java.util.LinkedHashMap<Class<?>, java.lang.reflect.Type[]> layers =
            new java.util.LinkedHashMap<>();
        collectClosureLayersInto(type, layers);
        return layers;
    }

    private static void collectClosureLayersInto(
        java.lang.reflect.Type type,
        java.util.LinkedHashMap<Class<?>, java.lang.reflect.Type[]> layers) {
        if (type instanceof java.lang.reflect.ParameterizedType pt) {
            Class<?> raw = (Class<?>) pt.getRawType();
            java.lang.reflect.Type[] args = pt.getActualTypeArguments();
            if (args.length > 0) {
                layers.putIfAbsent(raw, args);
            }
            for (java.lang.reflect.Type arg : args) {
                collectClosureLayersInto(arg, layers);
            }
            return;
        }
        // An array of a closed generic contributes its component layer.
        if (type instanceof java.lang.reflect.GenericArrayType gat) {
            collectClosureLayersInto(gat.getGenericComponentType(), layers);
        }
    }

    /**
     * Resolves the erased type arguments of {@code raw} from explicit closure layers.
     *
     * <p>{@code layers == null} or empty means the caller is on the legacy Class/TypeReference
     * path and the lookup delegates to {@link #getGenericArguments(Class)} unchanged. A non-empty
     * closure NEVER consults the registries: an unmatched raw class falls back to the
     * subtype-declared / hierarchy / type-parameter-bound walks only.</p>
     */
    public static Class<?>[] genericArgumentsInLayers(
        java.util.Map<Class<?>, java.lang.reflect.Type[]> layers, Class<?> raw) {
        if (raw == null) {
            return new Class<?>[0];
        }
        if (layers == null || layers.isEmpty()) {
            return getGenericArguments(raw);
        }
        java.lang.reflect.Type[] args = layers.get(raw);
        if (args != null && args.length > 0) {
            Class<?>[] result = new Class<?>[args.length];
            for (int i = 0; i < args.length; i++) {
                result[i] = toRawClass(args[i]);
            }
            trace("genericArgumentsInLayers(" + raw.getSimpleName() + ") = "
                + java.util.Arrays.toString(result) + " [closed layer]");
            return result;
        }
        return genericArgumentsWithoutRegistry(raw);
    }

    /** The non-registry fallbacks only: subtype declaration, hierarchy, then bounds. */
    private static Class<?>[] genericArgumentsWithoutRegistry(Class<?> raw) {
        java.lang.reflect.Type[] fromSubtype = findDeclaredArgumentsFor(raw);
        if (fromSubtype != null) {
            return eraseTypes(fromSubtype);
        }
        java.lang.reflect.TypeVariable<?>[] params = raw.getTypeParameters();
        if (params.length == 0) {
            java.lang.reflect.Type[] fromHierarchy = findTypeArgumentsInHierarchy(raw);
            if (fromHierarchy != null) {
                return eraseTypes(fromHierarchy);
            }
            return new Class<?>[0];
        }
        Class<?>[] result = new Class<?>[params.length];
        for (int i = 0; i < params.length; i++) {
            result[i] = resolveClassBound(params[i]);
        }
        return result;
    }

    private static Class<?>[] eraseTypes(java.lang.reflect.Type[] types) {
        Class<?>[] result = new Class<?>[types.length];
        for (int i = 0; i < types.length; i++) {
            result[i] = toRawClass(types[i]);
        }
        return result;
    }

    /**
     * The declared generic type of a reflected member, WITHOUT publishing anything.
     *
     * <p>This is the closed chain's source of truth: the emitted Java keeps generic member
     * signatures, so {@code getter.getGenericReturnType()} carries the full
     * {@code CSharpList<CSharpList<Integer>>} closure. The generated JsonPropertyInfo stamps it
     * at initialize time. Null when the member is not generic (the erased path stays
     * authoritative there) or the member kind is unknown.</p>
     */
    public static java.lang.reflect.Type declaredGenericType(MemberInfo member) {
        if (member == null) {
            return null;
        }
        if (member instanceof PropertyInfo propertyInfo) {
            return propertyInfo.getGenericPropertyType();
        }
        if (member instanceof FieldInfo fieldInfo) {
            return fieldInfo.getGenericType();
        }
        return null;
    }

    /**
     * Structural cache key for a closed type: equal closures collide, distinct closures never
     * share. Parameterized types key as raw + recursively-normalized arguments (reflection Type
     * implementations do not implement equals, so identity would split the cache for no benefit);
     * anything else keys as its raw Class, keeping the Class/TypeReference path unchanged.
     */
    public static Object closedCacheKey(java.lang.reflect.Type closed) {
        if (closed instanceof java.lang.reflect.ParameterizedType pt) {
            return new ClosedStructuralKey((Class<?>) pt.getRawType(), pt.getActualTypeArguments());
        }
        return toRawClass(closed);
    }

    /**
     * Replaces the raw class's type parameters (K, V, ...) with the closure's actual arguments,
     * so a member declared as {@code K} or {@code CSharpList<K>} on a generic definition resolves
     * to its closed form. Returns null when the declaration is null or still mentions a type
     * parameter the binding does not cover — callers must keep the erased resolution in that
     * case. Concrete types come back unchanged (same instance), so callers can compare
     * identities to skip redundant converter re-resolution. Pure function of its arguments; no
     * thread state.
     */
    public static java.lang.reflect.Type substituteTypeVariables(
            java.lang.reflect.Type declared, java.util.Map<String, java.lang.reflect.Type> binding) {
        if (declared instanceof java.lang.reflect.TypeVariable<?> typeVariable) {
            return binding.get(typeVariable.getName());
        }
        if (declared instanceof java.lang.reflect.ParameterizedType parameterized) {
            java.lang.reflect.Type[] actual = parameterized.getActualTypeArguments();
            java.lang.reflect.Type[] replaced = new java.lang.reflect.Type[actual.length];
            boolean changed = false;
            for (int i = 0; i < actual.length; i++) {
                replaced[i] = substituteTypeVariables(actual[i], binding);
                if (replaced[i] == null) {
                    return null;
                }
                changed = changed || replaced[i] != actual[i];
            }
            if (!changed) {
                return declared;
            }
            return parameterized(
                    toRawClass(parameterized.getRawType()), replaced);
        }
        return declared;
    }

    /** Raw class + structurally-normalized arguments; equal closures are equal keys. */
    private static final class ClosedStructuralKey {
        private final Class<?> raw;
        private final Object[] args;
        private final int hash;

        ClosedStructuralKey(Class<?> raw, java.lang.reflect.Type[] args) {
            this.raw = raw;
            this.args = new Object[args.length];
            for (int i = 0; i < args.length; i++) {
                this.args[i] = normalize(args[i]);
            }
            this.hash = 31 * raw.hashCode() + java.util.Arrays.hashCode(this.args);
        }

        private static Object normalize(java.lang.reflect.Type type) {
            if (type instanceof java.lang.reflect.ParameterizedType pt) {
                return new ClosedStructuralKey((Class<?>) pt.getRawType(), pt.getActualTypeArguments());
            }
            if (type instanceof java.lang.reflect.GenericArrayType gat) {
                return new Object[] { normalize(gat.getGenericComponentType()) + "[]" };
            }
            if (type instanceof Class<?> c) {
                return c;
            }
            return String.valueOf(type);
        }

        @Override
        public boolean equals(Object other) {
            return other instanceof ClosedStructuralKey key
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

    // -------------------------------------------------------------------
    // Closed-type registration surface (specs/2026-09-29-c30-closed-metadata-design.md,
    // decision D3): a process-wide registry of explicit closed types, published by the
    // generated bootstrap registration code and consulted by consumers that hold only a
    // raw class plus the type arguments they observed at the consumption site.
    //
    // Deliberately thread-free: the map is a plain ConcurrentHashMap keyed by
    // closedCacheKey structural keys, so registration and resolution are pure functions
    // of their inputs. None of the legacy session-chain ThreadLocal registries
    // (TYPE_ARGUMENTS/CONSUMED_ARGUMENTS/MEMBER_ARGUMENTS/REGISTRATION_DEPTH/
    // WRAPPER_LAYER_TAKEN/CALLSITE_ARGUMENTS/CONCRETE_SUBTYPES) is read or written.
    // -------------------------------------------------------------------

    private static final java.util.concurrent.ConcurrentHashMap<Object, java.lang.reflect.Type> _closedTypes =
        new java.util.concurrent.ConcurrentHashMap<>();

    /** Test hook: empties the closed-type registry (package-private, JUnit-only). */
    static void clearClosedTypes() {
        _closedTypes.clear();
    }

    /**
     * Registers a closed type under its structural cache key. Idempotent: the FIRST
     * registration of an equal closure wins and later equal closures are ignored
     * (putIfAbsent semantics via {@link ConcurrentHashMapHelper#putIfAbsentUntyped}).
     * A {@code null} input is a no-op; a bare (non-generic) Class registers under itself.
     */
    public static void registerClosedType(java.lang.reflect.Type closed) {
        if (closed == null) {
            return;
        }
        ConcurrentHashMapHelper.putIfAbsentUntyped(_closedTypes, closedCacheKey(closed), closed);
    }

    /**
     * Resolves the registered closed type for a raw class plus the type arguments
     * observed at the consumption site. The query builds
     * {@code parameterized(raw, observedArgs)} and looks its structural key up in the
     * registration table; with no observed arguments it degenerates to a bare-raw
     * lookup. Returns the registered closed Type, or null when nothing was registered
     * for that key.
     */
    public static java.lang.reflect.Type resolveClosedType(Class<?> raw, java.lang.reflect.Type... observedArgs) {
        if (raw == null) {
            return null;
        }
        Object key = observedArgs == null || observedArgs.length == 0
            ? closedCacheKey(raw)
            : closedCacheKey(parameterized(raw, observedArgs));
        return _closedTypes.get(key);
    }

    /**
     * Runs a converter factory for a CLOSED type: prefers the factory's closed-aware
     * {@code createConverter(Class, Map, JsonSerializerOptions)} entry (typed as Object here —
     * compat cannot reference the generated types), falling back to the legacy raw-class entry
     * for factories without nested layers to resolve (Enum, Nullable).
     */
    public static Object createConverterForClosedType(Object factory, java.lang.reflect.Type closed, Object options) {
        if (factory == null || closed == null || options == null) {
            return null;
        }
        Class<?> raw = toRawClass(closed);
        java.util.Map<Class<?>, java.lang.reflect.Type[]> layers = collectClosureLayers(closed);
        try {
            java.lang.reflect.Method closedAware = factory.getClass().getMethod(
                "createConverter", Class.class, java.util.Map.class, options.getClass());
            return closedAware.invoke(factory, raw, layers, options);
        } catch (NoSuchMethodException notClosedAware) {
            try {
                java.lang.reflect.Method legacy = factory.getClass().getMethod(
                    "createConverter", Class.class, options.getClass());
                return legacy.invoke(factory, raw, options);
            } catch (ReflectiveOperationException e) {
                trace("createConverterForClosedType: no entry on "
                    + factory.getClass().getSimpleName() + ": " + e);
                return null;
            }
        } catch (ReflectiveOperationException e) {
            trace("createConverterForClosedType failed on "
                + factory.getClass().getSimpleName() + ": " + e);
            return null;
        }
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
            java.util.Set<java.lang.reflect.Type[]> taken = WRAPPER_LAYER_TAKEN.get().get(type);
            java.lang.reflect.Type[] registered = null;
            if (taken == null) {
                registered = deque.pollFirst();
            } else {
                // A layer already claimed to resolve the class's own type variables belongs to
                // that class and must not also be answered as this query's arguments.
                for (java.util.Iterator<java.lang.reflect.Type[]> it = deque.iterator(); it.hasNext(); ) {
                    java.lang.reflect.Type[] candidate = it.next();
                    if (!taken.contains(candidate)) {
                        registered = candidate;
                        it.remove();
                        break;
                    }
                }
            }
            if (registered != null && registered.length > 0) {
                CONSUMED_ARGUMENTS.get().put(type, registered);
                Class<?>[] result = new Class<?>[registered.length];
                for (int i = 0; i < registered.length; i++) {
                    result[i] = toRawClass(registered[i]);
                }
                trace("getGenericArguments(" + type.getSimpleName() + ") = " + java.util.Arrays.toString(result)
                    + " [pending layer, " + deque.size() + " left behind]");
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
            trace("getGenericArguments(" + type.getSimpleName() + ") = " + java.util.Arrays.toString(result)
                + " [repeat of consumed layer]");
            return result;
        }
        // An interface has no arguments of its own, but a subclass that implements it
        // usually names them (class StrList implements IList<string>). C# reads them
        // off the interface Type; Java erased the class literal, so recover them from
        // the hierarchy before losing the element type to Object.
        java.lang.reflect.Type[] fromSubtype = findDeclaredArgumentsFor(type);
        if (fromSubtype != null) {
            rememberConsumedArguments(type, fromSubtype);
            Class<?>[] result = new Class<?>[fromSubtype.length];
            for (int i = 0; i < fromSubtype.length; i++) {
                result[i] = toRawClass(fromSubtype[i]);
            }
            trace("getGenericArguments(" + type.getSimpleName() + ") = " + java.util.Arrays.toString(result)
                + " [declared by a subtype]");
            return result;
        }
        // Lowest priority: the call site's own closed generic (a class literal erased it).
        java.util.ArrayDeque<java.lang.reflect.Type[]> callSiteDeque = CALLSITE_ARGUMENTS.get().get(type);
        if (callSiteDeque != null && !callSiteDeque.isEmpty()) {
            java.lang.reflect.Type[] callSite = callSiteDeque.pollFirst();
            Class<?>[] result = new Class<?>[callSite.length];
            for (int i = 0; i < callSite.length; i++) {
                result[i] = toRawClass(callSite[i]);
            }
            trace("getGenericArguments(" + type.getSimpleName() + ") = " + java.util.Arrays.toString(result)
                + " [call-site layer]");
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
            // A concrete collection class usually gets its element type from the
            // generic supertype it declares (class StrList implements IList<string>),
            // not from type parameters of its own. Java erases the interface's
            // arguments from the class literal, so recover them from the hierarchy:
            // without this an element converter is built for the erased Object and
            // every element comes back as a JsonElement.
            java.lang.reflect.Type[] fromHierarchy = findTypeArgumentsInHierarchy(type);
            if (fromHierarchy != null) {
                Class<?>[] result = new Class<?>[fromHierarchy.length];
                for (int i = 0; i < fromHierarchy.length; i++) {
                    result[i] = toRawClass(fromHierarchy[i]);
                }
                trace("getGenericArguments(" + type.getSimpleName() + ") = " + java.util.Arrays.toString(result)
                    + " [from declared supertype]");
                return result;
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

    /**
     * Recovers the type arguments a class leaves in its generic supertypes.
     * {@code class StringIListWrapper implements CSharpGenericIList<String>} has no
     * type parameters of its own, so {@code getTypeParameters()} is empty and the
     * element type would erase to {@code Object}. The declared supertypes still
     * carry it in their bytecode signature, which is what this walk reads.
     *
     * @return the arguments of the nearest generic supertype, or null when the
     *         class declares no generic supertype with arguments.
     */
    private static java.lang.reflect.Type[] findTypeArgumentsInHierarchy(Class<?> type) {
        // Superclass first: a C# derived collection's own interface list is
        // inherited from the base class, and the base is the more specific owner.
        Class<?> superClass = type.getSuperclass();
        if (superClass != null && superClass != Object.class) {
            java.lang.reflect.Type genericSuper = type.getGenericSuperclass();
            java.lang.reflect.Type[] found = typeArgumentsOf(genericSuper);
            if (found != null) {
                return found;
            }
        }
        for (java.lang.reflect.Type iface : type.getGenericInterfaces()) {
            java.lang.reflect.Type[] found = typeArgumentsOf(iface);
            if (found != null) {
                return found;
            }
        }
        if (superClass != null && superClass != Object.class) {
            return findTypeArgumentsInHierarchy(superClass);
        }
        return null;
    }

    /** Type arguments of a generic supertype, or null when it has none. */
    private static java.lang.reflect.Type[] typeArgumentsOf(java.lang.reflect.Type supertype) {
        if (supertype instanceof java.lang.reflect.ParameterizedType pt) {
            // Only collection supertypes carry the element/key/value types this query
            // exists for. A plain class can implement a generic interface for unrelated
            // reasons (String implements Comparable<String>), and reading those
            // arguments would answer a question nobody asked.
            if (!(pt.getRawType() instanceof Class<?> raw) || !isCollectionType(raw)) {
                return null;
            }
            java.lang.reflect.Type[] args = pt.getActualTypeArguments();
            if (args.length > 0) {
                return args;
            }
        }
        return null;
    }

    /** A supertype whose type arguments describe elements, keys or values. */
    private static boolean isCollectionType(Class<?> raw) {
        return raw != null
            && (java.util.Map.class.isAssignableFrom(raw) || java.lang.Iterable.class.isAssignableFrom(raw));
    }

    /**
     * Whether {@code default(T)} is null for the type a converter is declared over — C#'s
     * {@code Converter.CanBeNull}, the flag {@code JsonPropertyInfoOfT} passes as
     * {@code defaultValueIsNull}.
     *
     * <p>It cannot be derived from a Java default value alone. C#'s value types are boxed, so
     * {@code int} arrives as {@code Integer}: its default must still compare as ZERO, or
     * {@code IgnoreNullValues}/{{@code WhenWritingDefault} stop omitting defaulted value members and
     * two tests regress. {@code bool?} also arrives as {@code Boolean} but its default must be null.
     * The declared Java type distinguishes them: a primitive Class constant is a C# value type.</p>
     */
    public static boolean defaultValueIsNullForWrite(Class<?> declaredType) {
        return declaredType == null || !declaredType.isPrimitive();
    }

    /**
     * {@code default(T) == null} for a member, which only the DECLARATION can answer.
     *
     * <p>{@code declaredType} is boxed — C#'s {@code int} and {@code int?} both arrive as
     * {@code Integer} — so it cannot tell a non-nullable value type (whose default is zero/false)
     * from a nullable one (whose default is null). The member's raw declaration can: the converter
     * emits a primitive Java type for {@code int} and the wrapper for {@code int?}, and a C# struct
     * as a Java {@code value class}.</p>
     */
    public static boolean defaultValueIsNullForWrite(Class<?> declaredType, MemberInfo memberInfo) {
        Class<?> raw = rawMemberType(memberInfo);
        if (raw != null) {
            // A primitive declaration is a C# non-nullable value type: default(int) is 0, not null.
            if (raw.isPrimitive()) {
                return false;
            }
            // A Java value class that is not a boxed wrapper is a converted C# struct (or another
            // value type the converter mapped to one): default(T) is its zero instance, not null.
            // JDK 28 makes the wrapper classes value classes too, and those stand for `int?`,
            // `bool?` etc. here — their default IS null.
            if (raw.isValue() && !isBoxedWrapperClass(raw)) {
                return false;
            }
        }
        return defaultValueIsNullForWrite(declaredType);
    }

    /** The Java wrapper classes, which stand for C# nullable/reference uses of the same width. */
    private static boolean isBoxedWrapperClass(Class<?> type) {
        return type == Integer.class || type == Long.class || type == Short.class
            || type == Byte.class || type == Float.class || type == Double.class
            || type == Boolean.class || type == Character.class;
    }

    /** The type a member was declared with, without boxing, or null when it cannot be told. */
    private static Class<?> rawMemberType(MemberInfo memberInfo) {
        if (memberInfo instanceof PropertyInfo propertyInfo) {
            return propertyInfo.getRawPropertyType();
        }
        if (memberInfo instanceof FieldInfo fieldInfo) {
            return fieldInfo.getRawFieldType();
        }
        return null;
    }

    /**
     * The whole "skip this member on write" decision, mirroring
     * {@code if (IgnoreDefaultValuesOnWrite && (default(T) == null ? value == null :
     * EqualityComparer<T>.Default.Equals(default, value)))}.
     *
     * <p>Split out of the generated code because C# value types arrive boxed, so the two branches
     * need care: a value type must compare against ZERO (otherwise {@code WhenWritingDefault} stops
     * omitting {@code 0} and two tests regress), while a nullable must compare against null. The
     * comparison is done numerically so a boxed {@code Integer(0)} and a primitive {@code 0} agree.</p>
     */
    public static boolean shouldIgnoreForDefaultWrite(
            boolean ignoreDefaultValuesOnWrite, boolean defaultValueIsNull, Object value, Object defaultValue) {
        if (!ignoreDefaultValuesOnWrite) {
            return false;
        }
        // C#: default(T) == null ? value == null : EqualityComparer<T>.Default.Equals(default, value)
        //
        // The null branch is taken from the DECLARED type (a primitive Class constant is a C# value
        // type), never from the Java default value: a boxed value type also has a non-null Java
        // default, so inferring from the default made "default(T) == null" false for a nullable and
        // wrote the null out (measured: {"B":null} under IgnoreNullValues).
        if (defaultValueIsNull) {
            return value == null;
        }
        if (value == null) {
            // A value type whose boxed value is null is its default by definition.
            return true;
        }
        // Value-type comparison. Boxed primitives must be compared numerically, and the default may
        // be null when the converter could not supply one — then a boxed zero is still the default.
        if (defaultValue == null) {
            return isBoxedZero(value);
        }
        if (value instanceof Number v && defaultValue instanceof Number d) {
            return v.doubleValue() == d.doubleValue();
        }
        if (value instanceof Character v && defaultValue instanceof Character d) {
            return v.charValue() == d.charValue();
        }
        return java.util.Objects.equals(value, defaultValue);
    }

    /** Whether {@code value} is a boxed primitive equal to its zero value. */
    private static boolean isBoxedZero(Object value) {
        if (value instanceof Number n) {
            return n.doubleValue() == 0.0;
        }
        if (value instanceof Character c) {
            return c.charValue() == '\0';
        }
        return Boolean.FALSE.equals(value);
    }

    /**
     * Whether a member's type is one the SDK accepts for {@code [JsonExtensionData]}.
     *
     * <p>C# accepts an {@code IDictionary<string, T>} and — since .NET 5 — a
     * {@code IReadOnlyDictionary<string, T>}, which is what an immutable dictionary implements. The
     * generated check tested only {@code CSharpGenericIDictionary}, so a member declared as an
     * immutable dictionary was rejected even though the SDK documents and tests that form. Measured:
     * {@code ExtensionDataTests.ClassWithExtensionPropertyAsImmutable.MyOverflow} is an
     * {@code ImmutableDictionary<string, object>}, {@code isImmutableDictionaryType} answers true for
     * it, and {@code isAssignableFrom(CSharpGenericIDictionary, ...)} answers false — so the property
     * was refused with "does not match the required signature".</p>
     *
     * <p>{@code null} answers false: the caller tests the declared type and then the runtime type,
     * which may be null.</p>
     */
    public static boolean isExtensionDataDictionaryType(Class<?> type) {
        if (type == null) {
            return false;
        }
        return isAssignableFrom(CSharpGenericIDictionary.class, type)
            || isAssignableFrom(CSharpReadOnlyDict.class, type)
            || isAssignableFrom(IImmutableDictionary.class, type);
    }

    /**
     * Arguments a concrete subtype declares for {@code target}. Java erases them from
     * the class literal, but a class that implements {@code IList<String>} keeps the
     * argument in its bytecode signature; converter factories ask the interface for
     * its arguments, so this is where the element type is recoverable.
     */
    private static java.lang.reflect.Type[] findDeclaredArgumentsFor(Class<?> target) {
        if (target == null || !isCollectionType(target)) {
            return null;
        }
        java.util.List<Class<?>> candidates = CONCRETE_SUBTYPES.get();
        // Most recently published first: the entry points publish the type being
        // converted right before the lookup that needs it.
        for (int i = candidates.size() - 1; i >= 0; i--) {
            java.lang.reflect.Type[] found = declaredArgumentsFor(candidates.get(i), target);
            if (found != null) {
                return found;
            }
        }
        return null;
    }

    /**
     * Arguments {@code concrete} declares for {@code target} anywhere in its type
     * hierarchy, or null when it declares none. Deeper declarations win over the ones
     * inherited from the supertype, so {@code class Wrapper<String> extends Stack<T>}
     * answers with the argument the concrete class itself wrote down.
     */
    private static java.lang.reflect.Type[] declaredArgumentsFor(Class<?> concrete, Class<?> target) {
        if (concrete == null || concrete == Object.class) {
            return null;
        }
        java.lang.reflect.Type[] found = declaredArgumentsIn(concrete, concrete.getGenericSuperclass(), target);
        if (found != null) {
            return found;
        }
        for (java.lang.reflect.Type iface : concrete.getGenericInterfaces()) {
            found = declaredArgumentsIn(concrete, iface, target);
            if (found != null) {
                return found;
            }
        }
        for (java.lang.reflect.Type iface : concrete.getGenericInterfaces()) {
            if (iface instanceof java.lang.reflect.ParameterizedType pt
                    && pt.getRawType() instanceof Class<?> raw) {
                found = declaredArgumentsFor(raw, target);
                if (found != null) {
                    return found;
                }
            }
        }
        return declaredArgumentsFor(concrete.getSuperclass(), target);
    }

    /**
     * Arguments {@code supertype} declares for {@code target}, or null.
     *
     * <p>A wrapper class that is itself generic declares the parameter, not the argument
     * ({@code class Wrapper<T> implements IList<T>}), so a type variable is resolved through
     * the arguments the CONCRETE class was converted with; without published arguments it
     * keeps C# erasure semantics and falls back to the bound.</p>
     */
    private static java.lang.reflect.Type[] declaredArgumentsIn(
            Class<?> concrete, java.lang.reflect.Type supertype, Class<?> target) {
        if (!(supertype instanceof java.lang.reflect.ParameterizedType pt)
                || !(pt.getRawType() instanceof Class<?> raw)
                || !target.isAssignableFrom(raw)) {
            return null;
        }
        java.lang.reflect.Type[] args = pt.getActualTypeArguments();
        if (args.length == 0) {
            return null;
        }
        Class<?>[] concreteArguments = ownTypeArguments(concrete);
        if (concreteArguments == null) {
            // No published arguments: a type variable can only mean its bound (Object).
            for (java.lang.reflect.Type arg : args) {
                if (arg instanceof java.lang.reflect.TypeVariable<?>) {
                    return null;
                }
            }
            return args;
        }
        java.lang.reflect.TypeVariable<?>[] parameters = concrete.getTypeParameters();
        java.lang.reflect.Type[] resolved = new java.lang.reflect.Type[args.length];
        for (int i = 0; i < args.length; i++) {
            resolved[i] = args[i];
            if (args[i] instanceof java.lang.reflect.TypeVariable<?> tv) {
                for (int p = 0; p < parameters.length && p < concreteArguments.length; p++) {
                    if (parameters[p].getName().equals(tv.getName())) {
                        resolved[i] = concreteArguments[p];
                        break;
                    }
                }
            }
        }
        return resolved;
    }

    /**
     * The type arguments {@code raw} is currently being converted with, without consuming the
     * layer: a generic wrapper needs its own arguments to resolve the variables it declares.
     *
     * <p>The layer is CLAIMED here (recorded in {@link #WRAPPER_LAYER_TAKEN}) so the later
     * {@link #getGenericArguments} call for the same raw class does not hand the same layer to
     * a converter as its own arguments; for a nested closed generic that double use mixed the
     * outer layer with the inner one.</p>
     */
    private static Class<?>[] ownTypeArguments(Class<?> raw) {
        java.util.ArrayDeque<java.lang.reflect.Type[]> pending = TYPE_ARGUMENTS.get().get(raw);
        java.lang.reflect.Type[] layer = null;
        if (pending != null) {
            java.util.Set<java.lang.reflect.Type[]> taken = WRAPPER_LAYER_TAKEN.get().get(raw);
            for (java.lang.reflect.Type[] candidate : pending) {
                if (taken == null || !taken.contains(candidate)) {
                    layer = candidate;
                    break;
                }
            }
        }
        if (layer == null) {
            layer = CONSUMED_ARGUMENTS.get().get(raw);
            if (layer == null) {
                java.util.ArrayDeque<java.lang.reflect.Type[]> callSite = CALLSITE_ARGUMENTS.get().get(raw);
                layer = callSite == null || callSite.isEmpty() ? null : callSite.peekFirst();
            }
        }
        if (layer == null || layer.length == 0) {
            return null;
        }
        WRAPPER_LAYER_TAKEN.get()
            .computeIfAbsent(raw, k -> java.util.Collections.newSetFromMap(new java.util.IdentityHashMap<>()))
            .add(layer);
        Class<?>[] result = new Class<?>[layer.length];
        for (int i = 0; i < layer.length; i++) {
            result[i] = toRawClass(layer[i]);
        }
        return result;
    }

    // Concrete types whose declared generic supertypes the current query may consult.
    // A converter factory asks the ERASED interface for its arguments
    // (getCompatibleGenericInterface returns the raw type), so the element type is only
    // recoverable from the concrete class that was handed to getConverter. Recency
    // ordered and bounded, so a long test session cannot grow it without limit and the
    // type being converted is always the first candidate.
    private static final int CONCRETE_SUBTYPE_LIMIT = 64;
    private static final ThreadLocal<java.util.List<Class<?>>> CONCRETE_SUBTYPES =
        ThreadLocal.withInitial(java.util.ArrayList::new);

    /** Publishes a concrete type whose declared generic supertypes may be consulted. */
    public static void registerConcreteType(Class<?> type) {
        if (type == null || type.isPrimitive() || type == Object.class) {
            return;
        }
        java.util.List<Class<?>> candidates = CONCRETE_SUBTYPES.get();
        // Move to the end when already known so "most recently converted" is the order.
        for (int i = candidates.size() - 1; i >= 0; i--) {
            if (candidates.get(i) == type) {
                candidates.remove(i);
                break;
            }
        }
        candidates.add(type);
        while (candidates.size() > CONCRETE_SUBTYPE_LIMIT) {
            candidates.remove(0);
        }
    }

    /** Keeps a recovered layer available to the repeat queries one converter creation makes. */
    private static void rememberConsumedArguments(Class<?> raw, java.lang.reflect.Type[] args) {
        // Only an actual argument layer may be remembered. A resolved-but-still-variable
        // answer belongs to one concrete conversion and must not answer the next query for
        // the same raw class (wrapper<T> would otherwise hand the previous T to everyone).
        for (java.lang.reflect.Type arg : args) {
            if (arg instanceof java.lang.reflect.TypeVariable<?>) {
                return;
            }
        }
        // Without an active registration session the recovered layer would outlive its
        // concrete type and answer later, unrelated queries for the same raw class.
        if (REGISTRATION_DEPTH.get()[0] > 0) {
            CONSUMED_ARGUMENTS.get().put(raw, args);
        }
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

    /**
     * Mirrors {@code Type.GetInterfaces()} for the purpose of matching a generic interface.
     *
     * <p>.NET's {@code Type.GetInterfaces()} returns the <em>transitive closure</em> of the
     * interface hierarchy; Java's {@code Class.getInterfaces()} returns direct interfaces only. A
     * ported helper that walks {@code getInterfaces()} therefore never sees an interface inherited
     * through a superclass or a parent interface: a {@code StringHashSetWrapper} exposing no direct
     * interfaces produced {@code null} from
     * {@code IEnumerableConverterFactoryHelpers.getCompatibleGenericInterface}, so it fell to the
     * generic IEnumerable converter and its {@code createCollection} rejected the type
     * ("... is abstract, an interface, or is read only"). This walks interfaces and superclasses.</p>
     */
    public static java.util.List<Class<?>> getAllInterfaces(Class<?> type) {
        java.util.LinkedHashSet<Class<?>> found = new java.util.LinkedHashSet<>();
        collectInterfaces(type, found);
        return new java.util.ArrayList<>(found);
    }

    private static void collectInterfaces(Class<?> type, java.util.Set<Class<?>> found) {
        for (Class<?> current = type; current != null && current != Object.class; current = current.getSuperclass()) {
            for (Class<?> iface : current.getInterfaces()) {
                if (found.add(iface)) {
                    collectInterfaces(iface, found);
                }
            }
        }
    }

    public static boolean getIsAbstract(Class<?> type) {
        return type != null && Modifier.isAbstract(type.getModifiers()) && !type.isInterface();
    }

    /**
     * Mirrors C# {@code Type.IsValueType} for the type a CONVERTER is declared over.
     *
     * <p>A converted C# struct is emitted as a Java {@code value class} (JDK 28), so
     * {@code Type.IsValueType} answers true for it — but {@link #getIsValueType(Class)} cannot say
     * so: that predicate also decides whether a null JSON value may be ASSIGNED to a member, and a
     * Java {@code Class} cannot tell a C# struct from its nullable form ({@code ValueTypedMember?}
     * is emitted as the same class). Making the general predicate answer true therefore regressed
     * four nullable-struct members with {@code DeserializeUnableToAssignNull} (measured 548 → 547
     * with 5 fixed / 4 regressed).</p>
     *
     * <p>{@code CanBeNull} only needs the converter's side of it: C#'s
     * {@code JsonConverter<T>.CanBeNull} is false for a non-nullable struct, which makes
     * {@code HandleNullOnRead} true so the converter reads the null token itself. Keeping the
     * question on this narrower entry point fixes the converter without touching assignment.</p>
     */
    public static boolean getIsValueTypeForConverter(Class<?> type) {
        if (type == null) {
            return false;
        }
        if (type.isPrimitive()) {
            return true;
        }
        // The boxed wrapper classes are value classes in JDK 28 too, but here they stand for the
        // C# nullable forms (int?, bool?), so they must not count.
        return type.isValue() && !isBoxedWrapperClass(type);
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
        // The converter factories ask this question about the concrete type they are
        // about to convert (IEnumerableConverterFactory.canConvert, the immutable-type
        // checks), and the answer is the last moment the concrete class is visible
        // before the factory asks the ERASED interface for its element type. Record it
        // so that lookup can recover the argument the class declared.
        if (type != null && isCollectionType(baseType)) {
            registerConcreteType(type);
        }
        // Pure JVM answer first: it cannot be affected by the thread-local type-argument
        // registries below, and the extension-data check
        // (isAssignableFrom(CSharpGenericIDictionary.class, CSharpDictionary.class)) must not
        // depend on whatever member happened to publish arguments last.
        if (baseType != null && type != null && baseType.isAssignableFrom(type)) {
            return true;
        }
        if (baseType == CSharpGenericIterable.class && type != null && type.isArray()) {
            return true;
        }
        // A C# type that merely IMPLEMENTS IDictionary<K,V> (the SDK test suite declares
        // extension-data wrappers like GenericIDictionaryWrapperPrivateConstructor<string, object>)
        // can be ported as a class implementing only the non-generic compat dictionary interface.
        // The extension-data check asks isAssignableFrom(CSharpGenericIDictionary.class, wrapper)
        // and C# would accept it, so bridge the two dictionary trees here (same idea as the
        // CSharpIList -> CSharpList bridge below).
        if (baseType == CSharpGenericIDictionary.class && type != null
                && CSharpIDictionary.class.isAssignableFrom(type)) {
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
        // .NET's IEnumerable<T> derives from the non-generic IEnumerable, so anything that
        // implements the non-generic compat tree is also an IEnumerable<T>. Without this a
        // non-generic collection (CSharpObjQueue/CSharpObjStack, which implement
        // CSharpIterable<Object>) is not claimed by IEnumerableConverterFactory.CanConvert and
        // serializes as a POCO: {"Count":2,"SyncRoot":{},...} instead of [1,2].
        if (baseType == CSharpGenericIterable.class && type != null
                && CSharpIterable.class.isAssignableFrom(type)) {
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
        if (baseType != null && type != null && baseType.isAssignableFrom(type)) {
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

    /**
     * Mirrors C# {@code Type.GetFields(BindingFlags)}.
     *
     * <p>{@code Class.getFields()} returns PUBLIC fields only, so the {@code NonPublic} flag could
     * never be honoured and every non-public field was invisible. Measured: the "non-public member
     * annotated with JsonIncludeAttribute is invalid" validation in {@code JsonClassInfo} sits in the
     * field loop and never ran for a private/internal/protected field, so
     * {@code PropertyVisibilityTests#nonPublicProperty_WithJsonInclude_Invalid} got a JSON parse
     * error instead of the {@code InvalidOperationException} .NET raises during warm-up.</p>
     */
    public static FieldInfo[] getFields(Class<?> type, int bindingFlags) {
        if (type == null) {
            return new FieldInfo[0];
        }
        boolean wantPublic = (bindingFlags & BindingFlags.Public) != 0;
        boolean wantNonPublic = (bindingFlags & BindingFlags.NonPublic) != 0;
        boolean wantStatic = (bindingFlags & BindingFlags.Static) != 0;
        boolean wantInstance = (bindingFlags & BindingFlags.Instance) != 0;
        boolean declaredOnly = (bindingFlags & BindingFlags.DeclaredOnly) != 0;
        List<FieldInfo> result = new ArrayList<>();
        for (Class<?> current = type; current != null && current != Object.class; current = current.getSuperclass()) {
            for (Field field : current.getDeclaredFields()) {
                if (field.isSynthetic()) {
                    continue;
                }
                int modifiers = field.getModifiers();
                boolean isPublic = Modifier.isPublic(modifiers)
                    && !field.isAnnotationPresent(CompatNonPublicProperty.class);
                if (isPublic ? !wantPublic : !wantNonPublic) {
                    continue;
                }
                if (Modifier.isStatic(modifiers) ? !wantStatic : !wantInstance) {
                    continue;
                }
                // FieldInfo makes the field accessible; JDK internals are not open to that and
                // `setAccessible` throws InaccessibleObjectException. Skipping them keeps the C#
                // shape (which can read any field) from turning a framework type into a hard error.
                if (!field.trySetAccessible()) {
                    continue;
                }
                result.add(new FieldInfo(field));
            }
            if (declaredOnly) {
                break;
            }
        }
        // C# returns metadata declaration order, which the class file preserves (see DeclarationOrder).
        result.sort(java.util.Comparator
            .comparingInt((FieldInfo f) -> DeclarationOrder.hierarchyDepth(type, f.getDeclaringType()))
            .thenComparingInt(f -> DeclarationOrder.fieldIndex(f.getDeclaringType(), f.getName()))
            .thenComparing(f -> f.getDeclaringType() == null ? "" : f.getDeclaringType().getName())
            .thenComparing(FieldInfo::getName));
        return result.toArray(new FieldInfo[0]);
    }

    /** Mirrors C# {@code Type.GetFields()} — PUBLIC fields only. */
    public static FieldInfo[] getFields(Class<?> type) {
        return getFields(type, BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance);
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
            // A C# 'internal' constructor is emitted as Java 'public' so other packages stay
            // reachable, but for an accessibility question it is NOT public. This fast path runs
            // before the declared-constructor fallback below, so the marker has to be honoured
            // HERE too — without this the skip further down is never reached (measured: the
            // internal-constructor wrappers still answered "found").
            if (!ctor.isAnnotationPresent(CompatInternalCtor.class)
                    && matchesBindingFlags(ctor.getModifiers(), bindingFlags)) {
                return new ConstructorInfo(ctor);
            }
        } catch (NoSuchMethodException ignored) {
        }
        // Fall back to declared constructors so that non-public constructors
        // are also found. This bridges the C#/Java difference where a private
        // nested class in C# gets a public default constructor, but in Java
        // the implicit default constructor is private.
        for (Constructor<?> ctor : type.getDeclaredConstructors()) {
            // A C# 'internal' constructor is emitted as Java 'public' so other packages stay
            // reachable, but for THIS question it is not public: .NET's collection converter
            // rejects a type whose parameterless constructor is not public. The marker restores
            // the C# answer without changing the emitted modifier.
            if (ctor.isAnnotationPresent(CompatInternalCtor.class)) {
                continue;
            }
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

    /**
     * Bridges C# {@code Type.GetProperty(name, BindingFlags)} semantics.
     *
     * <p>Mirrors {@link #getField(Class, String, int)}: {@code Class#getMethods()} returns PUBLIC
     * methods only, so the {@code NonPublic} flag could never be honoured and a C# private property
     * was invisible. The SDK's own {@code DebuggerDisplay} property is exactly that shape —
     * {@code private string DebuggerDisplay => ...} becomes a private {@code getDebuggerDisplay()}
     * — and the runtime's debugger tests look it up with
     * {@code BindingFlags.Instance | BindingFlags.NonPublic}, so a public-only lookup answered
     * {@code null} and the following {@code .GetValue(value)} threw NullReferenceException.</p>
     */
    public static PropertyInfo getProperty(Class<?> type, String name, int bindingFlags) {
        if (type == null || name == null || name.isEmpty()) {
            return null;
        }
        boolean wantNonPublic = (bindingFlags & BindingFlags.NonPublic) != 0;
        if (!wantNonPublic) {
            return getProperty(type, name);
        }
        String capitalized = Character.toUpperCase(name.charAt(0)) + name.substring(1);
        String getterName = "get" + capitalized;
        String isGetterName = "is" + capitalized;
        String setterName = "set" + capitalized;
        boolean declaredOnly = (bindingFlags & BindingFlags.DeclaredOnly) != 0;

        Method getter = null;
        Method setter = null;
        for (Class<?> current = type; current != null && current != Object.class; current = current.getSuperclass()) {
            for (Method method : current.getDeclaredMethods()) {
                if (method.isSynthetic() || method.isBridge()) {
                    continue;
                }
                if (!matchesBindingFlags(method.getModifiers(), bindingFlags)) {
                    continue;
                }
                String methodName = method.getName();
                if (getter == null && method.getParameterCount() == 0 && method.getReturnType() != void.class) {
                    if (methodName.equals(getterName)
                        || (methodName.equals(isGetterName)
                            && (method.getReturnType() == boolean.class || method.getReturnType() == Boolean.class))) {
                        getter = method;
                    }
                }
                if (setter == null && methodName.equals(setterName) && method.getParameterCount() == 1) {
                    setter = method;
                }
            }
            if (declaredOnly || (getter != null && setter != null)) {
                break;
            }
        }
        if (getter == null && setter == null) {
            return getProperty(type, name);
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
     * The C#-style "Type.Member" location the SDK embeds in attribute-validation messages:
     * {@code dotnet.text.json.Serialization.Tests.CustomConverterTests+PocoWithInvalidConverter.MyInt}.
     *
     * <p>{@code Class.toString()} renders a Java {@code class} prefix and a {@code $} for a nested
     * type, so a message built from it reads
     * {@code class ...CustomConverterTests$PocoWithInvalidConverter.MyInt} — close enough to look
     * right but not the string the tests assert. {@link #getFullName} already carries the C# name,
     * including nested-type spelling, so the location is assembled from that instead.</p>
     */
    public static String getMemberLocation(Class<?> type, MemberInfo memberInfo) {
        String location = getFullName(type);
        if (location != null) {
            // C# spells a nested type with '+'; Java's Class.getName() uses '$'. The SDK asserts
            // the C# spelling, so both spellings of the separator have to be normalised.
            location = location.replace('$', '+');
        }
        if (memberInfo != null && memberInfo.getName() != null) {
            location += "." + memberInfo.getName();
        }
        return location;
    }

    /**
     * The C#-style name of a type, for messages the SDK asserts on: a generic renders as
     * {@code System.Collections.Generic.ICollection`1[Arg]}, a nested type uses {@code +}, and a
     * plain type is just its full name.
     *
     * <p>Java erases type arguments from a {@code Class}, so a generic target collapses to its raw
     * interface — {@code CSharpICollection} — which is not the string any assertion expects. The
     * arguments are recovered from the converters' registries: by the time a converter-compatibility
     * message is built, the factory has just consumed the layer describing this very type
     * ({@code CONSUMED_ARGUMENTS}), so that layer is what the name should show.</p>
     */
    public static String getCsTypeName(Class<?> type) {
        if (type == null) {
            return "";
        }
        java.lang.reflect.Type[] args = currentGenericArgumentsLayer(type);
        String base = csBaseName(type);
        if (args == null || args.length == 0) {
            return base;
        }
        // C# spells arity as `N immediately after the name, before the argument list.
        StringBuilder sb = new StringBuilder(base).append('`').append(args.length).append('[');
        for (int i = 0; i < args.length; i++) {
            if (i > 0) {
                sb.append(',');
            }
            sb.append(csArgumentName(args[i]));
        }
        return sb.append(']').toString();
    }

    /**
     * The C# namespace-qualified name of a compat stand-in for a BCL collection interface.
     *
     * <p>These deliberately do NOT go through {@link CompatTypeIdentity}: that registry also feeds
     * {@code isImmutableCollection}, so registering a BCL interface there would make the immutable
     * collection factory treat it as one.</p>
     */
    private static final java.util.Map<Class<?>, String> BCL_CS_NAMES = java.util.Map.ofEntries(
        // Collection interfaces and classes.
        java.util.Map.entry(CSharpICollection.class, "System.Collections.Generic.ICollection"),
        java.util.Map.entry(CSharpGenericIList.class, "System.Collections.Generic.IList"),
        java.util.Map.entry(CSharpGenericIDictionary.class, "System.Collections.Generic.IDictionary"),
        java.util.Map.entry(CSharpReadOnlyList.class, "System.Collections.Generic.IReadOnlyList"),
        java.util.Map.entry(CSharpReadOnlyDict.class, "System.Collections.Generic.IReadOnlyDictionary"),
        java.util.Map.entry(CSharpList.class, "System.Collections.Generic.List"),
        java.util.Map.entry(CSharpDictionary.class, "System.Collections.Generic.Dictionary"),
        java.util.Map.entry(CSharpISet.class, "System.Collections.Generic.ISet"),
        java.util.Map.entry(CSharpGenericIterable.class, "System.Collections.Generic.IEnumerable"),
        // Boxed primitives: the compat layer maps a C# primitive to its Java wrapper, so a message
        // that names the converted type has to spell the C# one (measured: the assert wants
        // 'System.Int32' and the message said 'java.lang.Integer').
        java.util.Map.entry(Integer.class, "System.Int32"),
        java.util.Map.entry(Long.class, "System.Int64"),
        java.util.Map.entry(Short.class, "System.Int16"),
        java.util.Map.entry(Byte.class, "System.Byte"),
        java.util.Map.entry(Boolean.class, "System.Boolean"),
        java.util.Map.entry(Double.class, "System.Double"),
        java.util.Map.entry(Float.class, "System.Single"),
        java.util.Map.entry(Character.class, "System.Char"),
        java.util.Map.entry(String.class, "System.String"),
        java.util.Map.entry(Object.class, "System.Object"));

    /** The C# full name of {@code type}, falling back to its Java name. */
    private static String csBaseName(Class<?> type) {
        String mapped = BCL_CS_NAMES.get(type);
        return (mapped != null ? mapped : getFullName(type)).replace('$', '+');
    }

    /** One type argument of {@link #getCsTypeName}, recursively. */
    private static String csArgumentName(java.lang.reflect.Type arg) {
        if (arg instanceof Class<?> cls) {
            return getCsTypeName(cls);
        }
        if (arg instanceof java.lang.reflect.ParameterizedType pt
                && pt.getRawType() instanceof Class<?> raw) {
            String base = csBaseName(raw);
            java.lang.reflect.Type[] args = pt.getActualTypeArguments();
            if (args.length == 0) {
                return base;
            }
            StringBuilder sb = new StringBuilder(base).append('`').append(args.length).append('[');
            for (int i = 0; i < args.length; i++) {
                if (i > 0) {
                    sb.append(',');
                }
                sb.append(csArgumentName(args[i]));
            }
            return sb.append(']').toString();
        }
        if (arg instanceof java.lang.reflect.GenericArrayType gat) {
            return csArgumentName(gat.getGenericComponentType()) + "[]";
        }
        return String.valueOf(arg);
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
     * Mirrors C# {@code TypeInfo.Assembly} — in C# TypeInfo inherits from
     * MemberInfo, but the Java compat TypeInfo does not implement MemberInfo,
     * so the conversion emits this overload.
     */
    public static AssemblyCompat getAssembly(TypeInfo typeInfo) {
        return typeInfo == null ? null : getAssembly(typeInfo.asClass());
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
    /**
     * Takes the pending multidimensional-array note for {@code type}, answering whether it claimed
     * rank &gt; 1.
     *
     * <p>{@code getArrayRank} consumes the same note, but it is only asked by
     * {@code IEnumerableConverterFactory.createConverter} — which does not run when the converter is
     * already cached, so the note outlived the operation that published it and a later JAGGED
     * {@code int[][]} lookup saw it ("The type 'class [[I' is not supported"). The generated
     * {@code JsonSerializerOptions.getConverter} now takes it at ENTRY, on every lookup.</p>
     */
    public static boolean takeMultiDimArrayRank(Class<?> type) {
        if (type == null || !type.isArray()) {
            return false;
        }
        Integer rank = MULTI_DIM_ARRAY_RANKS.remove(type);
        return rank != null && rank > 1;
    }

    public static Class<?> getMultiDimArrayClass(Class<?> elementType, int rank) {
        Class<?> arrayClass = java.lang.reflect.Array.newInstance(elementType, new int[rank]).getClass();
        MULTI_DIM_ARRAY_RANKS.put(arrayClass, rank);
        return arrayClass;
    }

    /**
     * The array class for a C# multidimensional type, WITHOUT claiming a rank.
     *
     * <p>{@link #getMultiDimArrayClass} publishes a rank note that the next
     * {@code getArrayRank} consumes. {@code Class} is canonical, so a C# {@code int[,]} and a C#
     * {@code int[][]} are the SAME runtime class: a note left behind by a call that only renders the
     * token (the generated tests build expected message text with
     * {@code TypeHelper.getMultiDimArrayClass(int.class, 2).toString()}) made every LATER jagged
     * {@code int[][]} conversion look multidimensional — measured three tests reporting
     * "The type 'class [[I' is not supported". The converter emits this variant when the token is
     * only rendered.</p>
     */
    public static Class<?> getMultiDimArrayClassName(Class<?> elementType, int rank) {
        return java.lang.reflect.Array.newInstance(elementType, new int[rank]).getClass();
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
     * Mirrors C# {@code Type.IsSubclassOf(Type)}: true only when {@code type}
     * derives from {@code base} through class inheritance. Unlike
     * {@link Class#isAssignableFrom}, the answer is false when the two are the
     * same type, and interface implementation / interface derivation never
     * counts (C# IsSubclassOf walks the class parent chain only).
     */
    public static boolean isSubclassOf(Class<?> type, Class<?> base) {
        if (type == null || base == null) {
            return false;
        }
        if (isSameType(type, base)) {
            return false;
        }
        if (base.isInterface()) {
            return false;
        }
        return base.isAssignableFrom(type);
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

    /**
     * Bridges the C# {@code Type.GetMethod(name, bindingFlags, types)} shape
     * (real chain: TypeScope's enumerator lookup passes
     * {@code Array.Empty<Type>()} alongside BindingFlags). Selects by name +
     * binding flags and verifies the parameter list when one is supplied, so
     * overloads of the same name resolve the way the C# overload set does.
     */
    /**
     * C# {@code Type.GetMethod(string, BindingFlags, Binder, Type[], ParameterModifier[])}
     * shape: the binder and modifiers slots are reflection internals with no Java
     * counterpart, so only the parameter list takes part in selection.
     */
    public static MethodInfo getMethod(Class<?> type, String name, int bindingFlags,
            Object binder, Class<?>[] parameterTypes, Object[] modifiers) {
        return getMethod(type, name, bindingFlags, (Object[]) parameterTypes);
    }

    public static MethodInfo getMethod(Class<?> type, String name, int bindingFlags, Object... parameterTypes) {
        MethodInfo candidate = getMethod(type, name, bindingFlags);
        if (candidate == null) {
            return null;
        }
        Class<?>[] wanted = normalizeParameterList(parameterTypes);
        if (wanted == null) {
            return candidate;
        }
        Class<?>[] actual = candidate.getMethod().getParameterTypes();
        if (actual.length != wanted.length) {
            return null;
        }
        for (int i = 0; i < wanted.length; i++) {
            if (!wanted[i].equals(actual[i])) {
                return null;
            }
        }
        return candidate;
    }

    /**
     * The converted shape of C# {@code Type[]}-shaped arguments varies: a plain
     * {@code new Type[]{...}} renders as a Java {@code Class[]} (spread or passed
     * through varargs elementwise) while {@code Array.Empty<Type>()} renders as a
     * compat CSharpArray, which varargs then boxes as a single element. Normalize
     * both to the declared parameter list; empty means "no arity constraint".
     */
    private static Class<?>[] normalizeParameterList(Object... parameterTypes) {
        if (parameterTypes == null || parameterTypes.length == 0) {
            return null;
        }
        if (parameterTypes.length == 1 && parameterTypes[0] instanceof CSharpArray csharpArray) {
            int length = csharpArray.getLength();
            Class<?>[] result = new Class<?>[length];
            for (int i = 0; i < length; i++) {
                result[i] = (Class<?>) csharpArray.getValue(i);
            }
            return result;
        }
        Class<?>[] result = new Class<?>[parameterTypes.length];
        for (int i = 0; i < parameterTypes.length; i++) {
            result[i] = (Class<?>) parameterTypes[i];
        }
        return result;
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
        if (type == null || attributeType == null) {
            return new Object[0];
        }
        // A converted System.Text.Json attribute is a CLASS, not an annotation, so the
        // annotation lookup only applies when the queried type really is one; a class-typed
        // attribute is materialized from the compat marker the converter emits.
        if (attributeType.isAnnotation()) {
            Object[] seen = type.getAnnotationsByType((Class) attributeType);
            if (seen.length > 0) {
                return seen;
            }
        }
        Object materialized = materializeTypeAttributeFromCompatAnnotation(type, attributeType);
        return materialized == null ? new Object[0] : new Object[] { materialized };
    }

    /**
     * Materializes a C# attribute class from the compat annotation the converter emits on a
     * TYPE. The generated System.Text.Json attributes are classes, not annotations, so a
     * type-level lookup ({@code JsonClassInfo.GetNumberHandlingForType}) finds nothing without
     * this: the attribute exists in the C# source but has no Java representation to read.
     */
    private static Object materializeTypeAttributeFromCompatAnnotation(Class<?> type, Class<?> attributeType) {
        if (type == null || attributeType == null) {
            return null;
        }
        String attributeName = attributeType.getSimpleName();
        if ("JsonConverterAttribute".equals(attributeName)) {
            // A TYPE-level [JsonConverter]. JsonSerializerOptions looks this up through
            // getAttributeThatCanHaveMultiple(typeToConvert, ...), so without it a converter that
            // has no public parameterless constructor is accepted instead of throwing
            // (CustomConverterTests.converterWithoutDefaultCtor asserts the throw).
            CompatJsonConverter marker = type.getAnnotation(CompatJsonConverter.class);
            if (marker == null) {
                return null;
            }
            Class<?> generated = CompatAttributeTypes.jsonConverterAttribute();
            if (generated == null || !generated.equals(attributeType)) {
                return null;
            }
            try {
                java.lang.reflect.Constructor<?> ctor = generated.getConstructor(Class.class);
                // Void.class is the C# [JsonConverter(null)] sentinel; see PropertyInfo.
                Class<?> converterType = marker.value();
                return ctor.newInstance(converterType == Void.class ? null : converterType);
            } catch (ReflectiveOperationException | RuntimeException e) {
                return null;
            }
        }
        if (!"JsonNumberHandlingAttribute".equals(attributeName)) {
            return null;
        }
        CompatJsonNumberHandling marker = type.getAnnotation(CompatJsonNumberHandling.class);
        return materializeMemberNumberHandlingAttribute(marker);
    }

    /**
     * Builds the generated {@code JsonNumberHandlingAttribute} for a member annotation, or null
     * when the member does not carry one. Member attributes override the type's, so this is the
     * only place a member-level {@code [JsonNumberHandling]} can be observed.
     *
     * <p>The attribute class lives in the converted module, which compat cannot compile
     * against, so it is resolved by name and built through its int constructor.</p>
     */
    public static Object materializeMemberNumberHandlingAttribute(CompatJsonNumberHandling marker) {
        if (marker == null) {
            return null;
        }
        try {
            Class<?> attributeType = Class.forName("dotnet.text.json.Serialization.JsonNumberHandlingAttribute");
            java.lang.reflect.Constructor<?> ctor = attributeType.getDeclaredConstructor(int.class);
            ctor.setAccessible(true);
            return ctor.newInstance(marker.value());
        } catch (ReflectiveOperationException | RuntimeException e) {
            return null;
        }
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

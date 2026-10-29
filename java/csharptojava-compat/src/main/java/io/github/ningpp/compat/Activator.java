package io.github.ningpp.compat;

import java.lang.reflect.Constructor;
import java.lang.reflect.InvocationTargetException;

/**
 * Minimal compat stub for System.Activator.
 * Provides object creation helpers used by converted C# code.
 */
public final class Activator {

    private Activator() {
    }

    /**
     * C# Activator.CreateInstance&lt;T&gt;() after erasure. The converter's
     * CollectionUtilities call site supplies T via CollectionFactory; this
     * no-arg overload is the only Java shape that preserves the C# generic
     * arity.
     */
    public static <T> T createInstance() {
        return CollectionFactory.createValue();
    }

    public static <T> T createInstance(Class<T> type) {
        if (type == null) {
            throw new NullPointerException("type");
        }
        // C# Activator.CreateInstance(typeof(T)) answers default(T) for a VALUE type — 0 for int,
        // false for bool, '\0' for char, the zeroed instance for a struct. The converter maps C#
        // int/long/short/byte/bool/char/float/double onto the Java wrappers, and neither those nor
        // the primitive Class constants have a parameterless constructor, so without this branch
        // `new T()` over a value-type token threw
        // "No parameterless constructor defined for java.lang.Integer" instead of answering 0
        // (measured: ImmutableStackTest.emptyTest / pushAndCountTest, T = int).
        Class<?> wrapper = type.isPrimitive() ? TypeHelper.toWrapperType(type) : type;
        if (isConverterValueType(wrapper)) {
            @SuppressWarnings("unchecked")
            T zero = (T) zeroOf(wrapper);
            return zero;
        }
        try {
            Constructor<T> ctor = type.getDeclaredConstructor();
            ctor.setAccessible(true);
            return ctor.newInstance();
        } catch (NoSuchMethodException e) {
            throw new RuntimeException("No parameterless constructor defined for " + type.getName(), e);
        } catch (InstantiationException | IllegalAccessException | InvocationTargetException e) {
            throw new RuntimeException(e);
        }
    }

    /**
     * The value types the converter represents as Java wrapper classes. {@code Character} is
     * included, and its zero is {@code '\0'}, which is why {@link #zeroOf} cannot use null as the
     * "not a value type" answer for the caller.
     */
    private static boolean isConverterValueType(Class<?> type) {
        return type == Integer.class || type == Long.class || type == Short.class || type == Byte.class
            || type == Boolean.class || type == Character.class || type == Float.class || type == Double.class;
    }

    private static Object zeroOf(Class<?> type) {
        if (type == Integer.class) return 0;
        if (type == Long.class) return 0L;
        if (type == Short.class) return (short) 0;
        if (type == Byte.class) return (byte) 0;
        if (type == Boolean.class) return false;
        if (type == Character.class) return '\0';
        if (type == Float.class) return 0.0f;
        if (type == Double.class) return 0.0;
        return null;
    }

    public static Object createInstance(Class<?> type, Object... args) {
        return createInstanceWithArgs(type, args);
    }

    /** C# Activator.CreateInstance(type, bindingAttr, binder, args, culture) — flags ignored. */
    public static Object createInstance(Class<?> type, Object bindingAttr, Object binder, Object[] args, Object culture) {
        return createInstanceWithArgs(type, args);
    }

    /**
     * C# Activator.CreateInstance(constructedGenericType, bindingAttr, binder,
     * args: null, culture) — the open generic type has an implicit parameterless
     * constructor, but the Java conversion injects Class runtime parameters into
     * converter constructors (e.g. ObjectDefaultConverter(Class)). Match the
     * injected constructor by forwarding the MakeGenericType type arguments: use
     * the longest constructor whose parameters are all Class and take the
     * corresponding prefix of the supplied type arguments.
     */
    public static Object createInstanceForGenericType(Class<?> type, Object bindingAttr, Object binder, Class<?>... genericTypeArguments) {
        if (type == null) {
            throw new NullPointerException("type");
        }
        try {
            Constructor<?>[] ctors = type.getDeclaredConstructors();
            // Prefer a real parameterless constructor when the generated class has one.
            for (Constructor<?> ctor : ctors) {
                if (ctor.getParameterTypes().length == 0) {
                    ctor.setAccessible(true);
                    return ctor.newInstance();
                }
            }
            Constructor<?> chosen = null;
            Constructor<?> onlyAllClassCtor = null;
            int allClassCtorCount = 0;
            for (Constructor<?> ctor : ctors) {
                Class<?>[] params = ctor.getParameterTypes();
                if (params.length == 0) {
                    continue;
                }
                boolean allClassParams = true;
                for (Class<?> param : params) {
                    if (param != Class.class) {
                        allClassParams = false;
                        break;
                    }
                }
                if (!allClassParams) {
                    continue;
                }
                allClassCtorCount++;
                onlyAllClassCtor = ctor;
                // Prefer the longest constructor whose Class parameters are all covered by
                // the forwarded type arguments.
                if (params.length <= genericTypeArguments.length
                    && (chosen == null || params.length > chosen.getParameterTypes().length)) {
                    chosen = ctor;
                }
            }
            if (chosen == null && allClassCtorCount == 1) {
                // The converter injects one Class<?> parameter per C# generic parameter, but
                // a call site only forwards the type arguments it knows: ObjectConverterFactory
                // passes MakeGenericType(typeToConvert) while
                // SmallObjectWithParameterizedConstructorConverter<,,,,> declares five Class
                // parameters whose extra tokens are placeholders the generated code never
                // reads. When the class has exactly one Class-parameter constructor there is
                // no ambiguity, so pad the remaining parameters with Object.class instead of
                // failing to match. Classes with several such constructors keep the old
                // "no matching constructor" behaviour.
                chosen = onlyAllClassCtor;
            }
            if (chosen == null) {
                throw new RuntimeException("No matching constructor found for " + type.getName(), new NoSuchMethodException());
            }
            Object[] args = new Object[chosen.getParameterTypes().length];
            for (int i = 0; i < args.length; i++) {
                args[i] = i < genericTypeArguments.length ? genericTypeArguments[i] : Object.class;
            }
            chosen.setAccessible(true);
            return chosen.newInstance(args);
        } catch (InstantiationException | IllegalAccessException e) {
            throw new RuntimeException(e);
        } catch (InvocationTargetException e) {
            // .NET wraps constructor exceptions in TargetInvocationException whose
            // InnerException is the original exception.
            throw new TargetInvocationException(null, e.getCause() == null ? e : e.getCause());
        }
    }

    /**
     * Variant of {@link #createInstanceForGenericType} for C# code where the
     * constructed generic type comes from a local variable assigned in mutually
     * exclusive branches (e.g. a converter factory picking a 1/2/3-parameter
     * converter at runtime). Each branch's MakeGenericType arguments are passed
     * as one {@code Class<?>[]} group; the group whose arity matches the runtime
     * generic type's parameter count is forwarded, preserving each branch's own
     * argument order (mixing branches would swap e.g. dictionary key/value).
     */
    public static Object createInstanceForGenericTypeFromGroups(Class<?> type, Object bindingAttr, Object binder, Class<?>[]... candidateGroups) {
        if (candidateGroups == null || candidateGroups.length == 0) {
            return createInstanceForGenericType(type, bindingAttr, binder);
        }
        int parameterCount = type.getTypeParameters().length;
        Class<?>[] chosen = null;
        for (Class<?>[] group : candidateGroups) {
            if (group.length == parameterCount) {
                chosen = group;
                break;
            }
        }
        if (chosen == null) {
            // Fall back to the longest group (the full MakeGenericType call is
            // the 3-argument branch in the SDK converter factories).
            for (Class<?>[] group : candidateGroups) {
                if (chosen == null || group.length > chosen.length) {
                    chosen = group;
                }
            }
        }
        return createInstanceForGenericType(type, bindingAttr, binder, chosen);
    }

    private static Object createInstanceWithArgs(Class<?> type, Object... args) {
        if (type == null) {
            throw new NullPointerException("type");
        }
        if (args == null) {
            args = new Object[0];
        }
        try {
            // .NET's Activator.CreateInstance(Type, object[]) matches constructors whose
            // parameter types are assignment-compatible with the runtime argument types
            // (interface parameters accept implementing instances, null matches any
            // reference parameter). Java's getDeclaredConstructor requires exact
            // parameter types, so scan all constructors and pick the best match.
            Constructor<?>[] ctors = type.getDeclaredConstructors();
            Constructor<?> exact = null;
            Constructor<?> assignable = null;
            for (Constructor<?> ctor : ctors) {
                Class<?>[] params = ctor.getParameterTypes();
                if (params.length != args.length) {
                    continue;
                }
                boolean matches = true;
                for (int i = 0; i < params.length; i++) {
                    Object arg = args[i];
                    if (arg == null) {
                        if (params[i].isPrimitive()) {
                            matches = false;
                            break;
                        }
                        continue;
                    }
                    if (!isCompatible(params[i], arg)) {
                        matches = false;
                        break;
                    }
                }
                if (!matches) {
                    continue;
                }
                boolean isExact = true;
                for (int i = 0; i < params.length; i++) {
                    if (args[i] != null && !isExactParamType(params[i], args[i])) {
                        isExact = false;
                        break;
                    }
                }
                if (isExact) {
                    exact = ctor;
                    break;
                }
                if (assignable == null) {
                    assignable = ctor;
                }
            }
            Constructor<?> chosen = exact != null ? exact : assignable;
            if (chosen != null) {
                // The converter restores the original C# constructor signature alongside
                // the injected-trailing-Class-token version (GD-CTORARR companions, e.g.
                // CollectionDebuggerProxy(CSharpIList) delegating to
                // CollectionDebuggerProxy(CSharpIList, Class)). On the JVM the restored
                // companion cannot re-derive the runtime type token its C# form gets from
                // typeof(T), so when the matched constructor has a token-augmented sibling
                // (identical parameter prefix + trailing all-Class parameters), prefer the
                // sibling: the inferred Class values reproduce the C# typeof(T) answer.
                Constructor<?> sibling = findTokenAugmentedSibling(ctors, chosen);
                if (sibling != null) {
                    chosen = sibling;
                    Object[] expanded = new Object[sibling.getParameterTypes().length];
                    System.arraycopy(args, 0, expanded, 0, args.length);
                    Class<?> inferred = inferElementType(args);
                    for (int i = args.length; i < expanded.length; i++) {
                        expanded[i] = inferred;
                    }
                    args = expanded;
                }
            }
            if (chosen == null) {
                // DebuggerTypeProxy scenario: the argument is a C# collection whose list
                // interfaces were dropped by Java erasure-conflict resolution (e.g.
                // GroupCollection implements only CSharpReadOnlyDict<K,V>). The proxy's
                // constructor wants the collection contract (ICollection<T>), whose
                // elements are the dictionary's VALUES. The constructor may also have
                // injected trailing Class<?> parameters.
                Object[] adaptedArgs = tryAdaptReadOnlyDictValuesWithClassParams(ctors, args);
                if (adaptedArgs != null) {
                    // Re-scan for the matching constructor with the adapted args.
                    for (Constructor<?> ctor : ctors) {
                        Class<?>[] params = ctor.getParameterTypes();
                        if (params.length != adaptedArgs.length) {
                            continue;
                        }
                        boolean matches = true;
                        for (int i = 0; i < params.length; i++) {
                            Object arg = adaptedArgs[i];
                            if (arg == null) {
                                if (params[i].isPrimitive()) {
                                    matches = false;
                                    break;
                                }
                                continue;
                            }
                            if (!isCompatible(params[i], arg)) {
                                matches = false;
                                break;
                            }
                        }
                        if (matches) {
                            chosen = ctor;
                            args = adaptedArgs;
                            break;
                        }
                    }
                }
            }
            if (chosen == null) {
                // The converter injects trailing Class<?> parameters into constructors
                // of generic classes that need runtime type tokens (e.g.
                // CollectionDebuggerProxy(CSharpIList<T> collection, Class<?> token) for
                // new T[] inside the class body). Reflection callers that pass the
                // original C# argument count cannot supply the extra Class<?> token.
                // Try to match a constructor whose prefix matches the supplied args and
                // whose suffix is all Class<?> params.
                Object[] expandedArgs = tryMatchInjectedClassParams(ctors, args);
                if (expandedArgs != null) {
                    // Re-scan for the matching constructor with the expanded args.
                    for (Constructor<?> ctor : ctors) {
                        Class<?>[] params = ctor.getParameterTypes();
                        if (params.length != expandedArgs.length) {
                            continue;
                        }
                        boolean matches = true;
                        for (int i = 0; i < params.length; i++) {
                            Object arg = expandedArgs[i];
                            if (arg == null) {
                                if (params[i].isPrimitive()) {
                                    matches = false;
                                    break;
                                }
                                continue;
                            }
                            if (!isCompatible(params[i], arg)) {
                                matches = false;
                                break;
                            }
                        }
                        if (matches) {
                            chosen = ctor;
                            args = expandedArgs;
                            break;
                        }
                    }
                }
            }
            if (chosen == null) {
                throw new RuntimeException("No matching constructor found for " + type.getName(), new NoSuchMethodException());
            }
            chosen.setAccessible(true);
            return chosen.newInstance(args);
        } catch (InstantiationException | IllegalAccessException e) {
            throw new RuntimeException(e);
        } catch (InvocationTargetException e) {
            // .NET wraps constructor exceptions in TargetInvocationException whose
            // InnerException is the original exception.
            throw new TargetInvocationException(null, e.getCause() == null ? e : e.getCause());
        }
    }

    /**
     * Finds a constructor whose parameter list is the given exact-match constructor's
     * parameter list followed by one or more trailing Class parameters — the converter's
     * injected runtime-type-token shape. Returns null when no such sibling exists.
     */
    private static Constructor<?> findTokenAugmentedSibling(Constructor<?>[] ctors, Constructor<?> exact) {
        Class<?>[] exactParams = exact.getParameterTypes();
        Constructor<?> best = null;
        for (Constructor<?> ctor : ctors) {
            Class<?>[] params = ctor.getParameterTypes();
            if (params.length <= exactParams.length) {
                continue;
            }
            boolean samePrefix = true;
            for (int i = 0; i < exactParams.length; i++) {
                if (params[i] != exactParams[i]) {
                    samePrefix = false;
                    break;
                }
            }
            if (!samePrefix) {
                continue;
            }
            boolean allClassSuffix = true;
            for (int i = exactParams.length; i < params.length; i++) {
                if (params[i] != Class.class) {
                    allClassSuffix = false;
                    break;
                }
            }
            if (!allClassSuffix) {
                continue;
            }
            if (best == null || params.length > best.getParameterTypes().length) {
                best = ctor;
            }
        }
        return best;
    }

    /**
     * When the sole argument is a CSharpReadOnlyDict-backed collection and a constructor
     * accepts a collection contract (CSharpICollection / java.util.Collection), adapt the
     * dictionary's VALUES view (the elements of the C# list contract) into the parameter.
     */
    private static Constructor<?> tryMatchReadOnlyDictValuesCtor(Constructor<?>[] ctors, Object[] args) {
        if (args.length != 1 || !(args[0] instanceof CSharpReadOnlyDict<?, ?> dict)) {
            return null;
        }
        Object values = dict.values();
        if (values == null) {
            return null;
        }
        Object adapted = CSharpICollection.from(values);
        if (adapted == null) {
            return null;
        }
        for (Constructor<?> ctor : ctors) {
            Class<?>[] params = ctor.getParameterTypes();
            if (params.length == 1 && params[0].isAssignableFrom(adapted.getClass())) {
                args[0] = adapted;
                return ctor;
            }
        }
        return null;
    }

    /**
     * Combined adaptation for CSharpReadOnlyDict argument: adapts the dictionary's
     * values to CSharpICollection AND handles injected trailing Class<?> parameters.
     * This is needed when the constructor signature is e.g.
     * (CSharpICollection<T>, Class<?>) but the caller passes a single
     * CSharpReadOnlyDict argument.
     *
     * @return expanded args array with adapted collection + null Class<?> slots, or null
     */
    private static Object[] tryAdaptReadOnlyDictValuesWithClassParams(Constructor<?>[] ctors, Object[] args) {
        if (args.length != 1 || !(args[0] instanceof CSharpReadOnlyDict<?, ?> dict)) {
            return null;
        }
        Object values = tryDictValuesQuietly(dict);
        if (values == null) {
            return null;
        }
        Object adapted = CSharpICollection.from(values);
        if (adapted == null) {
            return null;
        }
        // Find a constructor whose first param accepts the adapted collection
        // and remaining params are all Class<?>
        for (Constructor<?> ctor : ctors) {
            Class<?>[] params = ctor.getParameterTypes();
            if (params.length < 1 || !params[0].isAssignableFrom(adapted.getClass())) {
                continue;
            }
            // Check that remaining params are all Class<?>
            boolean allClassParams = true;
            for (int i = 1; i < params.length; i++) {
                if (params[i] != Class.class) {
                    allClassParams = false;
                    break;
                }
            }
            if (!allClassParams) {
                continue;
            }
            // Found a match: build expanded args array
            Object[] expanded = new Object[params.length];
            expanded[0] = adapted;
            // Fill Class<?> slots with inferred element type
            Class<?> inferred = inferElementType(new Object[]{dict});
            for (int i = 1; i < params.length; i++) {
                expanded[i] = inferred;
            }
            return expanded;
        }
        return null;
    }

    /**
     * Reflection probing must not turn a values() failure (a half-populated or
     * exotic dict view) into a crash of the Activator path, so swallow it here.
     * Since CSharpReadOnlyDict.values() routes through pairIterator() it also
     * works for dual-view generated collections such as GroupCollection.
     */
    private static Object tryDictValuesQuietly(CSharpReadOnlyDict<?, ?> dict) {
        try {
            return dict.values();
        } catch (RuntimeException e) {
            return null;
        }
    }

    /**
     * When no constructor matches the supplied argument count, try to find a
     * constructor whose parameter list is: [matched-prefix] + [trailing Class<?> params].
     * The converter injects trailing Class<?> parameters for runtime type tokens
     * (e.g. for new T[] inside the class body). Fill the extra Class<?> slots
     * with null (the constructor body typically throws before using the token
     * when the collection argument is null, matching C# semantics).
     *
     * @return expanded args array with Class<?> slots appended, or null if no match
     */
    private static Object[] tryMatchInjectedClassParams(Constructor<?>[] ctors, Object[] args) {
        for (Constructor<?> ctor : ctors) {
            Class<?>[] params = ctor.getParameterTypes();
            if (params.length <= args.length) {
                continue;
            }
            // Check that the prefix matches the supplied args
            boolean prefixMatches = true;
            for (int i = 0; i < args.length; i++) {
                Object arg = args[i];
                if (arg == null) {
                    if (params[i].isPrimitive()) {
                        prefixMatches = false;
                        break;
                    }
                    continue;
                }
                if (!isCompatible(params[i], arg)) {
                    prefixMatches = false;
                    break;
                }
            }
            if (!prefixMatches) {
                continue;
            }
            // Check that the remaining params are all Class<?>
            boolean allClassParams = true;
            for (int i = args.length; i < params.length; i++) {
                if (params[i] != Class.class) {
                    allClassParams = false;
                    break;
                }
            }
            if (!allClassParams) {
                continue;
            }
            // Found a match: expand the args array with inferred Class<?> values
            Object[] expanded = new Object[params.length];
            System.arraycopy(args, 0, expanded, 0, args.length);
            // Try to infer Class<?> from the first non-null collection argument
            Class<?> inferred = inferElementType(args);
            for (int i = args.length; i < params.length; i++) {
                expanded[i] = inferred;
            }
            return expanded;
        }
        // Reverse case: caller supplied more args than the constructor expects
        // because MakeGenericType injected Class<?> runtime tokens that the
        // constructor doesn't declare (e.g. DictionaryKeyValueConverterInner
        // only takes options, but Activator.createInstance passes {options, keyType, valueType}).
        // Try to match a constructor whose prefix matches and whose trailing
        // supplied args are all Class instances.
        for (Constructor<?> ctor : ctors) {
            Class<?>[] params = ctor.getParameterTypes();
            if (params.length >= args.length) {
                continue;
            }
            // Check that the prefix matches
            boolean prefixMatches = true;
            for (int i = 0; i < params.length; i++) {
                Object arg = args[i];
                if (arg == null) {
                    if (params[i].isPrimitive()) {
                        prefixMatches = false;
                        break;
                    }
                    continue;
                }
                if (!isCompatible(params[i], arg)) {
                    prefixMatches = false;
                    break;
                }
            }
            if (!prefixMatches) {
                continue;
            }
            // Check that the remaining supplied args are all Class instances
            boolean allClassArgs = true;
            for (int i = params.length; i < args.length; i++) {
                if (!(args[i] instanceof Class)) {
                    allClassArgs = false;
                    break;
                }
            }
            if (!allClassArgs) {
                continue;
            }
            // Found a match: truncate the args array to the constructor's param count
            Object[] truncated = new Object[params.length];
            System.arraycopy(args, 0, truncated, 0, params.length);
            return truncated;
        }
        return null;
    }

    /**
     * Try to infer the element Class<?> from the first non-null collection
     * argument. This is used to fill injected Class<?> parameters when the
     * caller cannot supply them (reflection-based instantiation).
     *
     * The C# answer is the DECLARED element type (T of the collection contract),
     * so the static generic signatures kept in the class's bytecode are consulted
     * first: a MatchCollection declares CSharpIList&lt;Match&gt; and a GroupCollection
     * declares CSharpReadOnlyDict&lt;String, Group&gt;, whose value argument is the
     * element type even when the runtime items are subtypes (entry 0 of a
     * GroupCollection is the whole Match) or the collection is empty — cases the
     * runtime element scan below cannot answer faithfully.
     *
     * If the collection contains elements of different runtime types (e.g.
     * Match and Group where Match extends Group), fall back to Object.class
     * to avoid ArrayStoreException. The caller (tryAsArray) can later
     * re-type the Object[] to the correct component type.
     *
     * @return the inferred element Class, or null if inference fails
     */
    private static Class<?> inferElementType(Object[] args) {
        for (Object arg : args) {
            if (arg == null) {
                continue;
            }
            // Declared-type inference from the retained generic signatures.
            Class<?> declared = inferDeclaredElementType(arg);
            if (declared != null) {
                return declared;
            }
            java.util.Collection<?> collection = null;
            // Try CSharpReadOnlyDict — get values
            if (arg instanceof CSharpReadOnlyDict<?, ?> dict) {
                Object values = tryDictValuesQuietly(dict);
                collection = values instanceof java.util.Collection<?> coll ? coll : null;
            } else if (arg instanceof java.util.Collection<?> coll) {
                collection = coll;
            }
            if (collection == null) {
                continue;
            }
            Class<?> inferred = null;
            boolean uniform = true;
            for (Object elem : collection) {
                if (elem == null) {
                    continue;
                }
                Class<?> elemClass = elem.getClass();
                if (inferred == null) {
                    inferred = elemClass;
                } else if (inferred != elemClass) {
                    // Different runtime types — find common superclass
                    inferred = findCommonSuperclass(inferred, elemClass);
                    if (inferred == Object.class) {
                        // Can't get more general than Object — stop
                        uniform = false;
                        break;
                    }
                }
            }
            if (inferred != null) {
                return inferred;
            }
        }
        return null;
    }

    /**
     * Reads the declared element type off the argument's generic interface/superclass
     * signatures: the value argument of a CSharpReadOnlyDict&lt;K, V&gt;, or the single
     * argument of a one-type-argument CSharp* collection contract (CSharpIList&lt;T&gt;,
     * CSharpList&lt;T&gt;, ...). Returns null when no such signature exists — the caller
     * falls back to the runtime element scan.
     */
    private static Class<?> inferDeclaredElementType(Object arg) {
        if (arg instanceof CSharpReadOnlyDict<?, ?>) {
            Class<?> valueArg = findGenericTypeArgument(arg.getClass(), "CSharpReadOnlyDict", -1);
            if (valueArg != null) {
                return valueArg;
            }
        }
        // One-type-argument CSharp collection contract: try the interfaces first,
        // then the generic superclass chain (wrapper classes extend CSharpList etc.).
        Class<?> declared = findGenericTypeArgument(arg.getClass(), null, 0);
        if (declared != null) {
            return declared;
        }
        for (Class<?> supervisor = arg.getClass().getSuperclass();
             supervisor != null && supervisor != Object.class;
             supervisor = supervisor.getSuperclass()) {
            declared = findGenericTypeArgument(supervisor, null, 0);
            if (declared != null) {
                return declared;
            }
        }
        return null;
    }

    /**
     * Scans the type's generic interface signatures (and, when {@code rawName} is null,
     * the type itself) for a parameterized CSharp* contract and returns one type argument:
     * index &gt;= 0 selects that argument, index &lt; 0 selects the LAST argument (the value
     * type of a dictionary).
     */
    private static Class<?> findGenericTypeArgument(Class<?> type, String rawName, int argumentIndex) {
        if (type == null) {
            return null;
        }
        java.lang.reflect.Type[] candidates;
        if (rawName == null) {
            // The type itself when directly parameterized (e.g. a subclass of CSharpList<T>).
            java.lang.reflect.Type genericSuperclass = type.getGenericSuperclass();
            if (genericSuperclass instanceof java.lang.reflect.ParameterizedType parameterized
                && isCSharpContract(parameterized.getRawType())) {
                return typeArgumentOf(parameterized, argumentIndex);
            }
            candidates = type.getGenericInterfaces();
        } else {
            candidates = type.getGenericInterfaces();
        }
        for (java.lang.reflect.Type candidate : candidates) {
            if (!(candidate instanceof java.lang.reflect.ParameterizedType parameterized)) {
                continue;
            }
            if (rawName != null) {
                if (!String.valueOf(((Class<?>) parameterized.getRawType()).getSimpleName()).equals(rawName)) {
                    continue;
                }
            } else if (!isCSharpContract(parameterized.getRawType())) {
                continue;
            }
            Class<?> argument = typeArgumentOf(parameterized, argumentIndex);
            if (argument != null) {
                return argument;
            }
        }
        return null;
    }

    private static boolean isCSharpContract(Object rawType) {
        return rawType instanceof Class<?> raw
            && raw.getSimpleName().startsWith("CSharp");
    }

    private static Class<?> typeArgumentOf(java.lang.reflect.ParameterizedType parameterized, int argumentIndex) {
        java.lang.reflect.Type[] arguments = parameterized.getActualTypeArguments();
        if (arguments.length == 0) {
            return null;
        }
        int index = argumentIndex < 0 ? arguments.length - 1 : argumentIndex;
        if (index >= arguments.length || !(arguments[index] instanceof Class<?> argument)) {
            return null;
        }
        return argument;
    }

    /**
     * Find the most specific common superclass of two classes.
     */
    private static Class<?> findCommonSuperclass(Class<?> a, Class<?> b) {
        if (a == b) return a;
        if (a == null) return b;
        if (b == null) return a;
        // Walk up a's hierarchy to find a superclass that b is assignable to
        Class<?> current = a;
        while (current != null) {
            if (current.isAssignableFrom(b)) {
                return current;
            }
            current = current.getSuperclass();
        }
        return Object.class;
    }

    private static boolean isCompatible(Class<?> paramType, Object arg) {
        if (paramType.isPrimitive()) {
            return boxedType(paramType).isInstance(arg);
        }
        return paramType.isAssignableFrom(arg.getClass());
    }

    private static boolean isExactParamType(Class<?> paramType, Object arg) {
        if (paramType.isPrimitive()) {
            return boxedType(paramType) == arg.getClass();
        }
        return paramType == arg.getClass();
    }

    private static Class<?> boxedType(Class<?> primitive) {
        if (primitive == int.class) return Integer.class;
        if (primitive == long.class) return Long.class;
        if (primitive == short.class) return Short.class;
        if (primitive == byte.class) return Byte.class;
        if (primitive == boolean.class) return Boolean.class;
        if (primitive == char.class) return Character.class;
        if (primitive == float.class) return Float.class;
        if (primitive == double.class) return Double.class;
        return primitive;
    }
}

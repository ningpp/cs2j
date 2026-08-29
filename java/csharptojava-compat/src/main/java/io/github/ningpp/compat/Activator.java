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

    public static <T> T createInstance(Class<T> type) {
        if (type == null) {
            throw new NullPointerException("type");
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
            for (Constructor<?> ctor : ctors) {
                Class<?>[] params = ctor.getParameterTypes();
                if (params.length == 0 || params.length > genericTypeArguments.length) {
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
                if (chosen == null || params.length > chosen.getParameterTypes().length) {
                    chosen = ctor;
                }
            }
            if (chosen == null) {
                throw new RuntimeException("No matching constructor found for " + type.getName(), new NoSuchMethodException());
            }
            Object[] args = new Object[chosen.getParameterTypes().length];
            System.arraycopy(genericTypeArguments, 0, args, 0, args.length);
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
                // CollectionDebuggerProxy(CSharpICollection<T>, Class<?>)). Reflection
                // callers that pass the original C# argument count cannot supply the
                // extra Class<?> token. Try to match a constructor whose prefix
                // matches the supplied args and whose suffix is all Class<?> params.
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
        Object values = dict.values();
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
            java.util.Collection<?> collection = null;
            // Try CSharpReadOnlyDict — get values
            if (arg instanceof CSharpReadOnlyDict<?, ?> dict) {
                collection = (java.util.Collection<?>) dict.values();
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

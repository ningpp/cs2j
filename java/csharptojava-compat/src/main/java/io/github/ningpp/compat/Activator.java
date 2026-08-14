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

    private static Object createInstanceWithArgs(Class<?> type, Object... args) {
        if (type == null) {
            throw new NullPointerException("type");
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
                    if (!params[i].isAssignableFrom(arg.getClass())) {
                        matches = false;
                        break;
                    }
                }
                if (!matches) {
                    continue;
                }
                boolean isExact = true;
                for (int i = 0; i < params.length; i++) {
                    if (args[i] != null && params[i] != args[i].getClass()) {
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
                // elements are the dictionary's VALUES.
                chosen = tryMatchReadOnlyDictValuesCtor(ctors, args);
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
}

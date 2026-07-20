package io.github.ningpp.compat;

import java.lang.reflect.Array;
import java.lang.reflect.Constructor;
import java.lang.reflect.Field;
import java.lang.reflect.InvocationHandler;
import java.lang.reflect.Method;
import java.lang.reflect.Modifier;
import java.lang.reflect.Parameter;
import java.lang.reflect.Proxy;
import java.util.ArrayList;
import java.util.Arrays;

/**
 * Reflection helpers that bridge C# to Java semantic differences.
 */
public final class ReflectionHelper {

    private ReflectionHelper() {}

    /**
     * Returns the public field with the given name, or null if not found.
     * Bridges the semantic gap between C# Type.GetField (returns null)
     * and Java Class.getField (throws NoSuchFieldException).
     */
    public static Field getField(Class<?> clazz, String name) {
        try {
            return clazz.getField(name);
        } catch (NoSuchFieldException e) {
            return null;
        }
    }

    /**
     * Returns the first declared method matching {@code name} regardless of
     * parameter types, or null if not found.  Searches all methods including
     * non-public ones, and makes the result accessible so it can be invoked
     * across package boundaries.
     * <p>
     * Bridges C# {@code Type.GetMethod(name, BindingFlags)} semantics — C#
     * finds by name only and allows invoking non-public members via
     * {@code MethodInfo.Invoke}.
     */
    public static Method getDeclaredMethodByName(Class<?> clazz, String name) {
        for (Method m : clazz.getDeclaredMethods()) {
            if (m.getName().equals(name)) {
                m.setAccessible(true);
                return m;
            }
        }
        return null;
    }

    /**
     * Returns the first public method matching {@code name} regardless of
     * parameter types, or null if not found.
     * <p>
     * Bridges C# {@code Type.GetMethod(name)} semantics when no parameter
     * types are specified.
     */
    public static Method getMethodByName(Class<?> clazz, String name) {
        for (Method m : clazz.getMethods()) {
            if (m.getName().equals(name)) {
                return m;
            }
        }
        return null;
    }

    @SuppressWarnings("unchecked")
    public static <T> T createDelegate(Method method, Class<T> delegateType) {
        InvocationHandler handler = (proxy, m, args) -> method.invoke(null, args);
        return (T) Proxy.newProxyInstance(
            delegateType.getClassLoader(),
            new Class<?>[] { delegateType },
            handler);
    }

    @SuppressWarnings("unchecked")
    public static <T> T createDelegate(MethodInfo method, Class<T> delegateType) {
        return createDelegate(method.getMethod(), delegateType);
    }

    @SuppressWarnings("unchecked")
    public static <T> T createDelegate(Method method, Object target, Class<T> delegateType) {
        InvocationHandler handler = (proxy, m, args) -> method.invoke(target, args);
        return (T) Proxy.newProxyInstance(
            delegateType.getClassLoader(),
            new Class<?>[] { delegateType },
            handler);
    }

    @SuppressWarnings("unchecked")
    public static <T> T createDelegate(MethodInfo method, Object target, Class<T> delegateType) {
        return createDelegate(method.getMethod(), target, delegateType);
    }

    /**
     * Returns the base definition of a method, i.e. the declaration in the
     * least-derived class/interface in the declaring type's hierarchy that
     * declares a method with the same name and parameter types.
     * <p>
     * Bridges C# {@code MethodInfo.GetBaseDefinition()}, which returns the
     * original virtual method declaration.
     */
    public static Method getBaseDefinition(Method method) {
        if (method == null) {
            return null;
        }
        Class<?> declaringClass = method.getDeclaringClass();
        Class<?>[] parameterTypes = method.getParameterTypes();
        String name = method.getName();

        Class<?> current = declaringClass;
        Method best = method;
        while (current != null) {
            try {
                Method candidate = current.getDeclaredMethod(name, parameterTypes);
                best = candidate;
            } catch (NoSuchMethodException e) {
                // ignore
            }
            current = current.getSuperclass();
        }

        for (Class<?> iface : declaringClass.getInterfaces()) {
            try {
                Method candidate = iface.getMethod(name, parameterTypes);
                best = candidate;
            } catch (NoSuchMethodException e) {
                // ignore
            }
        }

        return best;
    }

    /**
     * Returns whether the given method is public.
     * <p>
     * Bridges C# {@code MethodBase.IsPublic} for Java reflection.
     */
    public static boolean isPublic(Method method) {
        return method != null && Modifier.isPublic(method.getModifiers());
    }

    /**
     * Returns whether the given MethodInfo wrapper represents a public method.
     */
    public static boolean isPublic(MethodInfo method) {
        return method != null && isPublic(method.getMethod());
    }

    /**
     * Returns whether the given constructor is public.
     */
    public static boolean isPublic(Constructor<?> ctor) {
        return ctor != null && Modifier.isPublic(ctor.getModifiers());
    }

    /**
     * Returns whether the given parameter is optional.
     * <p>
     * Java reflection has no notion of optional/default parameters, so this
     * returns {@code false} for regular parameters. Varargs are considered
     * optional to mirror C# params arrays semantics.
     */
    public static boolean isOptional(Parameter parameter) {
        return parameter != null && parameter.isVarArgs();
    }

    @SuppressWarnings("unchecked")
    public static <T> Iterable<T> asIterable(Object value) {
        if (value == null) return null;
        if (value instanceof Iterable<?>) return (Iterable<T>) value;
        if (value instanceof Object[] items) return (Iterable<T>) Arrays.asList(items);
        Class<?> type = value.getClass();
        if (!type.isArray()) {
            throw new ClassCastException(type.getName() + " cannot be converted to Iterable");
        }

        int length = Array.getLength(value);
        ArrayList<T> result = new ArrayList<>(length);
        for (int i = 0; i < length; i++) {
            result.add((T) Array.get(value, i));
        }
        return result;
    }
}

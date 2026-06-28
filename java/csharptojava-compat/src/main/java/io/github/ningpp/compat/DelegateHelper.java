package io.github.ningpp.compat;

import java.lang.reflect.InvocationHandler;
import java.lang.reflect.InvocationTargetException;
import java.lang.reflect.Method;
import java.lang.reflect.Proxy;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.LinkedHashSet;
import java.util.List;
import java.util.Objects;
import java.util.Set;

/** Helper for C# delegate combine/remove operations (+ / - operators). */
public final class DelegateHelper {
    private DelegateHelper() {}

    /** Combine two delegates (C# delegate + operator). */
    @SuppressWarnings("unchecked")
    public static <T> T combine(T a, T b) {
        if (a == null) return b;
        if (b == null) return a;
        List<Object> delegates = new ArrayList<>();
        appendInvocationList(delegates, a);
        appendInvocationList(delegates, b);
        return (T) createProxy(a, delegates);
    }

    /** Remove a delegate (C# delegate - operator). */
    @SuppressWarnings("unchecked")
    public static <T> T remove(T source, T value) {
        if (source == null) return null;
        if (value == null) return source;
        List<Object> delegates = new ArrayList<>();
        appendInvocationList(delegates, source);

        List<Object> valueList = new ArrayList<>();
        appendInvocationList(valueList, value);

        int match = lastIndexOf(delegates, valueList);
        if (match < 0) return source;
        delegates.subList(match, match + valueList.size()).clear();
        if (delegates.isEmpty()) return null;
        if (delegates.size() == 1) return (T) delegates.get(0);
        return (T) createProxy(source, delegates);
    }

    private static void appendInvocationList(List<Object> target, Object delegate) {
        if (Proxy.isProxyClass(delegate.getClass())
            && Proxy.getInvocationHandler(delegate) instanceof MulticastInvocationHandler handler) {
            target.addAll(handler.delegates);
        } else {
            target.add(delegate);
        }
    }

    private static int lastIndexOf(List<Object> source, List<Object> value) {
        if (value.isEmpty() || value.size() > source.size()) return -1;
        for (int i = source.size() - value.size(); i >= 0; i--) {
            boolean same = true;
            for (int j = 0; j < value.size(); j++) {
                if (!delegateEquals(source.get(i + j), value.get(j))) {
                    same = false;
                    break;
                }
            }
            if (same) return i;
        }
        return -1;
    }

    private static boolean delegateEquals(Object left, Object right) {
        return left == right || Objects.equals(left, right);
    }

    private static Object createProxy(Object sample, List<Object> delegates) {
        ClassLoader loader = sample.getClass().getClassLoader();
        Class<?>[] interfaces = delegateInterfaces(sample);
        return Proxy.newProxyInstance(loader, interfaces, new MulticastInvocationHandler(delegates));
    }

    private static Class<?>[] delegateInterfaces(Object sample) {
        Set<Class<?>> interfaces = new LinkedHashSet<>();
        Class<?> type = sample.getClass();
        while (type != null) {
            interfaces.addAll(Arrays.asList(type.getInterfaces()));
            type = type.getSuperclass();
        }
        if (interfaces.isEmpty()) {
            throw new IllegalArgumentException("Delegate type must implement an interface");
        }
        return interfaces.toArray(Class<?>[]::new);
    }

    private static final class MulticastInvocationHandler implements InvocationHandler {
        private final List<Object> delegates;

        private MulticastInvocationHandler(List<Object> delegates) {
            this.delegates = List.copyOf(delegates);
        }

        @Override
        public Object invoke(Object proxy, Method method, Object[] args) throws Throwable {
            if (method.getDeclaringClass() == Object.class) {
                return switch (method.getName()) {
                    case "toString" -> delegates.toString();
                    case "hashCode" -> System.identityHashCode(proxy);
                    case "equals" -> proxy == args[0];
                    default -> method.invoke(this, args);
                };
            }

            Object result = defaultValue(method.getReturnType());
            for (Object delegate : delegates) {
                try {
                    result = method.invoke(delegate, args);
                } catch (InvocationTargetException ex) {
                    throw ex.getCause();
                }
            }
            return result;
        }

        private static Object defaultValue(Class<?> returnType) {
            if (returnType == Void.TYPE) return null;
            if (!returnType.isPrimitive()) return null;
            if (returnType == Boolean.TYPE) return false;
            if (returnType == Character.TYPE) return '\0';
            if (returnType == Byte.TYPE) return (byte) 0;
            if (returnType == Short.TYPE) return (short) 0;
            if (returnType == Integer.TYPE) return 0;
            if (returnType == Long.TYPE) return 0L;
            if (returnType == Float.TYPE) return 0f;
            if (returnType == Double.TYPE) return 0d;
            return null;
        }
    }
}

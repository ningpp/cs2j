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
        if (type == null) {
            throw new NullPointerException("type");
        }
        try {
            Class<?>[] paramTypes = new Class<?>[args.length];
            for (int i = 0; i < args.length; i++) {
                paramTypes[i] = args[i] == null ? Object.class : args[i].getClass();
            }
            Constructor<?> ctor = type.getDeclaredConstructor(paramTypes);
            ctor.setAccessible(true);
            return ctor.newInstance(args);
        } catch (NoSuchMethodException e) {
            throw new RuntimeException("No matching constructor found for " + type.getName(), e);
        } catch (InstantiationException | IllegalAccessException | InvocationTargetException e) {
            throw new RuntimeException(e);
        }
    }
}

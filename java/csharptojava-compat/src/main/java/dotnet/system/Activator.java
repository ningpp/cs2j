package dotnet.system;

/**
 * Bridge for System.Activator — delegates to io.github.ningpp.compat.Activator.
 */
public final class Activator {
    private Activator() {
    }

    public static <T> T createInstance(Class<T> type) {
        return io.github.ningpp.compat.Activator.createInstance(type);
    }

    public static Object createInstance(Class<?> type, Object... args) {
        return io.github.ningpp.compat.Activator.createInstance(type, args);
    }

    /** C# Activator.CreateInstance(type, bindingAttr, binder, args, culture) — flags ignored. */
    public static Object createInstance(Class<?> type, Object bindingAttr, Object binder, Object[] args, Object culture) {
        return io.github.ningpp.compat.Activator.createInstance(type, args);
    }
}

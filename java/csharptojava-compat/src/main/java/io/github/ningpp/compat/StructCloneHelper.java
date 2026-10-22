package io.github.ningpp.compat;

import java.lang.reflect.Field;
import java.lang.reflect.Modifier;

/**
 * Java counterpart of C# struct self-assignment ({@code this = other;}).
 * The converter emits structs as ordinary classes, so assigning to <c>this</c>
 * cannot be expressed directly; copying every instance field preserves the
 * C# value-copy semantics (the ref-out-in/struct-value lowering always assigns
 * from a distinct clone or a fresh default instance).
 */
public final class StructCloneHelper {
    private StructCloneHelper() {
    }

    public static void copyInto(Object target, Object source) {
        if (target == null || source == null || target == source) {
            return;
        }
        for (Class<?> type = target.getClass(); type != null && type != Object.class; type = type.getSuperclass()) {
            for (Field field : type.getDeclaredFields()) {
                if (Modifier.isStatic(field.getModifiers())) {
                    continue;
                }
                try {
                    field.setAccessible(true);
                    field.set(target, field.get(source));
                } catch (ReflectiveOperationException ex) {
                    throw new RuntimeException(ex);
                }
            }
        }
    }
}

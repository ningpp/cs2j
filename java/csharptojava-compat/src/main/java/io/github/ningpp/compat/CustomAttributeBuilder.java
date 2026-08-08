package io.github.ningpp.compat;

import java.lang.reflect.Constructor;

/**
 * Stub for System.Reflection.Emit.CustomAttributeBuilder.
 */
public final class CustomAttributeBuilder {

    public CustomAttributeBuilder(Class<?> constructorOrType, Object[] constructorArgs) {
    }

    /**
     * C# CustomAttributeBuilder(ConstructorInfo, object[]) — the converter maps
     * typeof(X).GetConstructor(...) to java.lang.reflect.Constructor, so accept
     * it here too.
     */
    public CustomAttributeBuilder(Constructor<?> constructor, Object[] constructorArgs) {
    }
}

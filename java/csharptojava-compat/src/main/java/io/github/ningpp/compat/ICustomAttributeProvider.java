package io.github.ningpp.compat;

/**
 * Bridges C# System.Reflection.ICustomAttributeProvider to Java.
 * Implemented by reflection-related compat types (MemberInfo, PropertyInfo, MethodInfo, etc.)
 * so that generated Java code can call GetCustomAttributes(...) and IsDefined(...).
 */
public interface ICustomAttributeProvider {
    default Object[] getCustomAttributes(boolean inherit) {
        return new Object[0];
    }

    default Object[] getCustomAttributes(Class<?> attributeType, boolean inherit) {
        return new Object[0];
    }

    default boolean isDefined(Class<?> attributeType, boolean inherit) {
        return false;
    }
}

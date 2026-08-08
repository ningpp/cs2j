package io.github.ningpp.compat;

/**
 * Stub for System.Reflection.CustomAttributeTypedArgument.
 */
public class CustomAttributeTypedArgument {
    private final Class<?> argumentType;
    private final Object value;

    public CustomAttributeTypedArgument(Class<?> argumentType, Object value) {
        this.argumentType = argumentType;
        this.value = value;
    }

    public Class<?> getArgumentType() {
        return argumentType;
    }

    public Object getValue() {
        return value;
    }
}

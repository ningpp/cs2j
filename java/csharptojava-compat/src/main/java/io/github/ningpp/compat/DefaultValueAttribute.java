package io.github.ningpp.compat;

/**
 * Compat stub for System.ComponentModel.DefaultValueAttribute.
 */
public class DefaultValueAttribute {

    private final Object value;

    public DefaultValueAttribute(Object value) {
        this.value = value;
    }

    public Object getValue() {
        return value;
    }
}

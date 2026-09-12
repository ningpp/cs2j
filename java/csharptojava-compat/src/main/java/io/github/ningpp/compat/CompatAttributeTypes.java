package io.github.ningpp.compat;

/**
 * Resolves a generated System.Text.Json attribute class by name, for compat code that cannot name
 * it directly (compat compiles before the generated module).
 */
public final class CompatAttributeTypes {

    private CompatAttributeTypes() {
    }

    /** The generated {@code JsonConverterAttribute} class, or null when it cannot be loaded. */
    public static Class<?> jsonConverterAttribute() {
        try {
            return Class.forName("dotnet.text.json.Serialization.JsonConverterAttribute");
        } catch (ClassNotFoundException | LinkageError e) {
            return null;
        }
    }
}
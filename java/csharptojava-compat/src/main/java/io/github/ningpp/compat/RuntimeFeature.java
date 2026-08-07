package io.github.ningpp.compat;

/**
 * Replacement for System.Runtime.RuntimeFeature (converted from C#).
 * The converted runtime executes generated (always-available) Java code, so
 * dynamic-code support is reported as present.
 */
public final class RuntimeFeature {
    /** Mirrors C# RuntimeFeature.IsDynamicCodeCompiled. */
    public static boolean getIsDynamicCodeCompiled() {
        return true;
    }

    /** Mirrors C# RuntimeFeature.IsDynamicCodeSupported. */
    public static boolean getIsDynamicCodeSupported() {
        return true;
    }

    private RuntimeFeature() {
    }
}

package io.github.ningpp.compat;

/**
 * Replacement for System.Runtime.RuntimeFeature (converted from C#).
 * The converted runtime does not yet implement IL generation (System.Reflection.Emit
 * is stubbed out), so dynamic-code compilation is reported as unavailable. Code that
 * depends on it (e.g. the regex LWCG compiler) falls back to the interpreter, mirroring
 * .NET behavior on platforms where IsDynamicCodeCompiled is false.
 */
public final class RuntimeFeature {
    /** Mirrors C# RuntimeFeature.IsDynamicCodeCompiled. */
    public static boolean getIsDynamicCodeCompiled() {
        return false;
    }

    /** Mirrors C# RuntimeFeature.IsDynamicCodeSupported. */
    public static boolean getIsDynamicCodeSupported() {
        return true;
    }

    private RuntimeFeature() {
    }
}

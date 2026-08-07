package io.github.ningpp.compat;

/**
 * Replacement for System.Reflection.CallingConventions (flags enum).
 * Modeled as int constants (BindingFlags style) because the converter lowers
 * C# flag-enum composition to Java bitwise expressions.
 */
public final class CallingConventions {
    private CallingConventions() {}

    public static final int Standard = 1;
    public static final int VarArgs = 2;
    public static final int Any = 3;
    public static final int HasThis = 32;
    public static final int ExplicitThis = 64;
}

package io.github.ningpp.compat;

/**
 * Compat for System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes —
 * a [Flags] metadata enum carried as plain int constants, matching the converter's
 * int-constant metadata-enum convention (BindingFlags/MethodAttributes/...).
 */
public final class DynamicallyAccessedMemberTypes {
    private DynamicallyAccessedMemberTypes() {
    }

    public static final int None = 0;
    public static final int PublicParameterlessConstructor = 0x0001;
    public static final int PublicConstructors = 0x0002 | PublicParameterlessConstructor;
    public static final int NonPublicConstructors = 0x0004;
    public static final int PublicMethods = 0x0008;
    public static final int NonPublicMethods = 0x0010;
    public static final int PublicFields = 0x0020;
    public static final int NonPublicFields = 0x0040;
    public static final int PublicNestedTypes = 0x0080;
    public static final int NonPublicNestedTypes = 0x0100;
    public static final int PublicProperties = 0x0200;
    public static final int NonPublicProperties = 0x0400;
    public static final int PublicEvents = 0x0800;
    public static final int NonPublicEvents = 0x1000;
    public static final int Interfaces = 0x2000;
    public static final int All = ~None;
}

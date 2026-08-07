package io.github.ningpp.compat;

/**
 * Replacement for System.Reflection.MethodAttributes (flags enum).
 * Modeled as int constants (BindingFlags style) because the converter lowers
 * C# flag-enum composition (Public | Static) to Java bitwise expressions.
 */
public final class MethodAttributes {
    private MethodAttributes() {}

    public static final int PrivateScope = 0;
    public static final int Private = 1;
    public static final int FamANDAssem = 2;
    public static final int Assembly = 3;
    public static final int Family = 4;
    public static final int FamORAssem = 5;
    public static final int Public = 6;
    public static final int Static = 16;
    public static final int Final = 32;
    public static final int Virtual = 64;
    public static final int HideBySig = 128;
    public static final int NewSlot = 256;
    public static final int CheckAccessOnOverride = 512;
    public static final int Abstract = 1024;
    public static final int SpecialName = 2048;
    public static final int PinvokeImpl = 8192;
    public static final int UnmanagedExport = 8;
    public static final int RTSpecialName = 4096;
    public static final int HasSecurity = 16384;
    public static final int RequireSecObject = 32768;
}

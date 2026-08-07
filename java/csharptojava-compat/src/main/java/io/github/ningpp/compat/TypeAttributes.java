package io.github.ningpp.compat;

/**
 * Replacement for System.Reflection.TypeAttributes (flags enum).
 * Modeled as int constants (BindingFlags style) because the converter lowers
 * C# flag-enum composition (Class | BeforeFieldInit | Public) to Java bitwise
 * expressions. Duplicate values mirror the C# layout-mask constants.
 */
public final class TypeAttributes {
    private TypeAttributes() {}

    public static final int NotPublic = 0;
    public static final int Public = 1;
    public static final int NestedPublic = 2;
    public static final int NestedPrivate = 3;
    public static final int NestedFamily = 4;
    public static final int NestedAssembly = 5;
    public static final int NestedFamANDAssem = 6;
    public static final int NestedFamORAssem = 7;
    public static final int AutoLayout = 0;
    public static final int SequentialLayout = 8;
    public static final int ExplicitLayout = 16;
    public static final int Class = 0;
    public static final int Interface = 32;
    public static final int Abstract = 128;
    public static final int Sealed = 256;
    public static final int SpecialName = 1024;
    public static final int Import = 4096;
    public static final int Serializable = 8192;
    public static final int WindowsRuntime = 16384;
    public static final int UnicodeClass = 65536;
    public static final int AutoClass = 131072;
    public static final int CustomFormatClass = 196608;
    public static final int HasSecurity = 262144;
    public static final int BeforeFieldInit = 1048576;
    public static final int RTSpecialName = 2048;
    public static final int HasDefault = 983040;
}

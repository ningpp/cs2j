package io.github.ningpp.compat;

/** Replacement for System.Reflection.BindingFlags enumeration. */
public final class BindingFlags {
    private BindingFlags() {}

    public static final int Default = 0;
    public static final int IgnoreCase = 1;
    public static final int DeclaredOnly = 2;
    public static final int Instance = 4;
    public static final int Static = 8;
    public static final int Public = 16;
    public static final int NonPublic = 32;
    public static final int FlattenHierarchy = 64;
    public static final int InvokeMethod = 256;
    public static final int CreateInstance = 512;
    public static final int GetField = 1024;
    public static final int SetField = 2048;
    public static final int GetProperty = 4096;
    public static final int SetProperty = 8192;
    public static final int PutDispProperty = 16384;
    public static final int ExactBinding = 65536;
    public static final int SuppressChangeType = 131072;
    public static final int OptionalParamBinding = 262144;
    public static final int IgnoreReturn = 16777216;
}

package io.github.ningpp.compat;

/**
 * Replacement for System.Globalization.CompareOptions enum.
 * Uses int constants so bitwise operations and casts from C# continue to work in Java.
 */
public final class CompareOptions {
    public static final int None = 0;
    public static final int IgnoreCase = 1;
    public static final int IgnoreNonSpace = 2;
    public static final int IgnoreSymbols = 4;
    public static final int IgnoreKanaType = 8;
    public static final int IgnoreWidth = 16;
    public static final int StringSort = 0x20000000;
    public static final int Ordinal = 0x40000000;
    public static final int OrdinalIgnoreCase = 0x10000000;

    private CompareOptions() {}
}

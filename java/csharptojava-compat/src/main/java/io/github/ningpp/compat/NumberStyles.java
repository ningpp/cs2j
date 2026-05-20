package io.github.ningpp.compat;

/** Minimal System.Globalization.NumberStyles flag constants. */
public final class NumberStyles {
    private NumberStyles() {
    }

    public static final int None = 0;
    public static final int AllowLeadingWhite = 1;
    public static final int AllowTrailingWhite = 2;
    public static final int AllowLeadingSign = 4;
    public static final int AllowTrailingSign = 8;
    public static final int AllowParentheses = 16;
    public static final int AllowDecimalPoint = 32;
    public static final int AllowThousands = 64;
    public static final int AllowExponent = 128;
    public static final int AllowCurrencySymbol = 256;
    public static final int AllowHexSpecifier = 512;
    public static final int Integer = AllowLeadingWhite | AllowTrailingWhite | AllowLeadingSign;
    public static final int Float = Integer | AllowDecimalPoint | AllowExponent;
    public static final int Number = Float | AllowThousands;
    public static final int HexNumber = AllowLeadingWhite | AllowTrailingWhite | AllowHexSpecifier;
}

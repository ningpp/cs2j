package io.github.ningpp.compat;

/**
 * Bridges C# System.Globalization.CharUnicodeInfo to Java Character.
 * C# CharUnicodeInfo.GetUnicodeCategory(char) returns UnicodeCategory enum.
 * Java Character.getType(int) returns int; we wrap it to return our UnicodeCategory enum.
 */
public final class CharUnicodeInfo {

    private CharUnicodeInfo() {}

    public static UnicodeCategory getUnicodeCategory(char ch) {
        return UnicodeCategory.of(Character.getType(ch));
    }

    public static UnicodeCategory getUnicodeCategory(int codePoint) {
        return UnicodeCategory.of(Character.getType(codePoint));
    }

    public static double GetNumericValue(char ch) {
        return Character.getNumericValue(ch);
    }

    public static int GetDecimalDigitValue(char ch) {
        int v = Character.getNumericValue(ch);
        return (v >= 0 && v <= 9) ? v : -1;
    }

    public static int GetDigitValue(char ch) {
        int v = Character.getNumericValue(ch);
        return Character.isDigit(ch) ? v : -1;
    }
}

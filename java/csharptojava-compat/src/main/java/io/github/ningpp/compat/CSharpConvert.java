package io.github.ningpp.compat;

/**
 * Provides C#-compatible conversion methods.
 * C# Boolean.ToString() returns "True"/"False" (capitalized),
 * while Java String.valueOf(boolean) returns "true"/"false".
 */
public final class CSharpConvert {

    private CSharpConvert() { }

    /**
     * Converts a boolean to its C# string representation.
     * C# returns "True" or "False" (capitalized first letter).
     */
    public static String toString(boolean value) {
        return value ? "True" : "False";
    }

    /**
     * Converts a Boolean (boxed) to its C# string representation.
     * C# returns "True" or "False" (capitalized first letter).
     */
    public static String toString(Boolean value) {
        if (value == null) {
            return "";
        }
        return value ? "True" : "False";
    }
}

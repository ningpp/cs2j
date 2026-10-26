package io.github.ningpp.compat;

import java.util.Collections;
import java.util.List;

/**
 * Helpers for C# StringBuilder members that have no direct java.lang.StringBuilder
 * equivalent.
 */
public final class StringBuilderExtensions {
    private StringBuilderExtensions() {}

    /** C# StringBuilder.GetChunks() → one ReadOnlyMemory chunk over the whole content. */
    public static CSharpList<ReadOnlyMemory<Character>> getChunks(java.lang.StringBuilder sb) {
        CSharpList<ReadOnlyMemory<Character>> result = new CSharpList<>();
        result.add(ReadOnlyMemory.fromString(sb.toString()));
        return result;
    }

    /** C# StringBuilder.AppendLine() — returns the receiver (fluent). */
    public static java.lang.StringBuilder appendLine(java.lang.StringBuilder sb) {
        return sb.append(System.lineSeparator());
    }

    /** C# StringBuilder.AppendLine(string) — returns the receiver (fluent). */
    public static java.lang.StringBuilder appendLine(java.lang.StringBuilder sb, String value) {
        return sb.append(value).append(System.lineSeparator());
    }

    /** C# StringBuilder.AppendLine(char). */
    public static java.lang.StringBuilder appendLine(java.lang.StringBuilder sb, char value) {
        return sb.append(value).append(System.lineSeparator());
    }

    /** C# StringBuilder.Clear() — reset to empty (returns the receiver, fluent). */
    public static java.lang.StringBuilder clear(java.lang.StringBuilder sb) {
        sb.setLength(0);
        return sb;
    }
}

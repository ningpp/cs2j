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
    public static List<ReadOnlyMemory<Character>> getChunks(java.lang.StringBuilder sb) {
        return Collections.singletonList(ReadOnlyMemory.fromString(sb.toString()));
    }
}

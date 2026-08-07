package io.github.ningpp.compat;

import static org.junit.jupiter.api.Assertions.assertArrayEquals;

import org.junit.jupiter.api.Test;

/// Reproduces Regex.java: 'MemoryExtensions.asSpan(capslist).toArray()' where capslist
/// is a String[] (converted from C# 'capslist.AsSpan().ToArray()'). Before the fix there
/// was no asSpan(T[]) overload, so javac reported "no suitable method for asSpan(String[])".
class MemoryExtensionsAsSpanArrayTest {
    @Test
    void asSpanOfObjectArrayRoundTrips() {
        String[] capslist = { "a", "b", "c" };

        String[] roundTrip = MemoryExtensions.asSpan(capslist).toArray();

        assertArrayEquals(capslist, roundTrip);
    }
}

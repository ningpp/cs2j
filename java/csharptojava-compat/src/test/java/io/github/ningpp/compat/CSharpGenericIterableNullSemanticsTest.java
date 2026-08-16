package io.github.ningpp.compat;

import static org.junit.jupiter.api.Assertions.assertNotNull;
import static org.junit.jupiter.api.Assertions.assertNull;

import java.util.List;
import org.junit.jupiter.api.Test;

class CSharpGenericIterableNullSemanticsTest {
    @Test
    void fromNullIterableReturnsNull() {
        // Mirrors C# `IEnumerable<T> e = null;` — a null reference must remain null
        // so callers can perform `if (e == null)` and fail fast instead of iterating
        // a phantom object whose inner iterable is null.
        assertNull(CSharpGenericIterable.from((Iterable<String>) null));
    }

    @Test
    void fromNullStringReturnsNull() {
        // C# string is IEnumerable<char>; a null string is a null enumeration.
        assertNull(CSharpGenericIterable.from((String) null));
    }

    @Test
    void fromNonNullIterableStillWorks() {
        CSharpGenericIterable<String> iterable = CSharpGenericIterable.from(List.of("a", "b"));
        assertNotNull(iterable);
        org.junit.jupiter.api.Assertions.assertEquals(2, iterable.size());
    }

    @Test
    void fromCSharpListKeepsConcreteInstance() {
        CSharpList<String> list = new CSharpList<>(List.of("a"));
        // Identity-preserving fast path: wrapping a CSharpGenericIterable must not copy.
        org.junit.jupiter.api.Assertions.assertSame(list, CSharpGenericIterable.from(list));
    }
}

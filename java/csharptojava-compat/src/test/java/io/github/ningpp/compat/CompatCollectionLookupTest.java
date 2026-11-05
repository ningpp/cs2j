package io.github.ningpp.compat;

import org.junit.jupiter.api.Test;

import java.util.List;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertNull;
import static org.junit.jupiter.api.Assertions.assertThrows;

/** Element lookup on the compat IList / IEnumerable carriers (C# FirstOrDefault / ElementAt). */
class CompatCollectionLookupTest {

    @Test
    void csharpIListFindAnyReturnsFirstMatchOrNull() {
        CSharpIList<String> list =
            CSharpIList.from(new CSharpList<>(List.of("alpha", "beta", "gamma")));

        assertEquals("beta", list.findAny(s -> s.startsWith("b")));
        assertNull(list.findAny(s -> s.startsWith("z")));
    }

    @Test
    void csharpGenericIterableGetUsesZeroBasedIndex() {
        CSharpGenericIterable<String> iterable =
            CSharpGenericIterable.from(List.of("a", "b", "c"));

        assertEquals("a", iterable.get(0));
        assertEquals("c", iterable.get(2));
    }

    @Test
    void csharpGenericIterableGetRejectsOutOfRangeIndex() {
        CSharpGenericIterable<String> iterable = CSharpGenericIterable.from(List.of("a"));

        assertThrows(IndexOutOfBoundsException.class, () -> iterable.get(1));
        assertThrows(IndexOutOfBoundsException.class, () -> iterable.get(-1));
    }
}

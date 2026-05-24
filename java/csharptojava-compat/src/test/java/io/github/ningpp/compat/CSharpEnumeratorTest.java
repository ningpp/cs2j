package io.github.ningpp.compat;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertTrue;

import java.util.List;
import org.junit.jupiter.api.Test;

class CSharpEnumeratorTest {
    @Test
    void currentDoesNotAdvanceAfterMoveNext() {
        CSharpEnumerator<Integer> en = CSharpEnumerator.from(List.of(1, 2).iterator());

        assertTrue(en.moveNext());
        assertEquals(1, en.getCurrent());
        assertEquals(1, en.getCurrent());

        assertTrue(en.moveNext());
        assertEquals(2, en.getCurrent());
        assertFalse(en.moveNext());
    }

    @Test
    void nextKeepsJavaIteratorSemantics() {
        CSharpEnumerator<Integer> en = CSharpEnumerator.from(List.of(1, 2).iterator());

        assertTrue(en.hasNext());
        assertEquals(1, en.next());
        assertEquals(2, en.next());
        assertFalse(en.hasNext());
    }
}

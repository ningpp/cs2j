package io.github.ningpp.compat;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertTrue;

import java.util.ArrayList;
import java.util.List;
import org.junit.jupiter.api.Test;

class CSharpEnumeratorTest {
    @Test
    void currentDoesNotAdvanceAfterMoveNext() {
        List<Object> items = new ArrayList<>();
        items.add(1);
        items.add(2);
        CSharpEnumerator en = CSharpEnumerator.from(items.iterator());

        assertTrue(en.moveNext());
        assertEquals(1, en.getCurrent());
        assertEquals(1, en.getCurrent());

        assertTrue(en.moveNext());
        assertEquals(2, en.getCurrent());
        assertFalse(en.moveNext());
    }

    @Test
    void nextKeepsJavaIteratorSemantics() {
        List<Object> items = new ArrayList<>();
        items.add(1);
        items.add(2);
        CSharpEnumerator en = CSharpEnumerator.from(items.iterator());

        assertTrue(en.hasNext());
        assertEquals(1, en.next());
        assertEquals(2, en.next());
        assertFalse(en.hasNext());
    }
}

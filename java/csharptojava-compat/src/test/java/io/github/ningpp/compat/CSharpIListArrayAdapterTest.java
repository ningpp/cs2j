package io.github.ningpp.compat;

import org.junit.jupiter.api.Test;

import java.util.List;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertNull;

/** C# List&lt;T&gt;[] → IList&lt;T&gt;[] element-wise array adaptation. */
class CSharpIListArrayAdapterTest {

    @Test
    @SuppressWarnings({ "rawtypes", "unchecked" })
    void fromArrayAdaptsEachCSharpListElement() {
        CSharpList<String>[] lists = (CSharpList<String>[]) new CSharpList[] {
            new CSharpList<>(List.of("a", "b")),
            new CSharpList<>(List.of("c")),
        };

        CSharpIList<String>[] adapted = CSharpIList.fromArray(lists);

        assertEquals(2, adapted.length);
        assertEquals(2, adapted[0].getCount());
        assertEquals("a", adapted[0].get(0));
        assertEquals(1, adapted[1].getCount());
        assertEquals("c", adapted[1].get(0));
    }

    @Test
    @SuppressWarnings({ "rawtypes", "unchecked" })
    void fromArrayHandlesNullAndEmptyArrays() {
        assertNull(CSharpIList.fromArray(null));

        CSharpList<String>[] empty = (CSharpList<String>[]) new CSharpList[0];
        CSharpIList<String>[] adapted = CSharpIList.fromArray(empty);
        assertEquals(0, adapted.length);
    }
}

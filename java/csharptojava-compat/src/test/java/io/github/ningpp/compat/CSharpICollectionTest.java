package io.github.ningpp.compat;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertTrue;

import java.util.Arrays;
import java.util.List;
import org.junit.jupiter.api.Test;

class CSharpICollectionTest {
    @Test
    void from_javaUtilCollection_adaptsToCSharpICollection() {
        List<String> list = Arrays.asList("a", "b");
        CSharpICollection<Object> adapted = CSharpICollection.from(list);
        assertEquals(2, adapted.getCount());
        assertTrue(adapted.contains("a"));
        assertFalse(adapted.isEmpty());
    }
}

package io.github.ningpp.compat;

import org.junit.jupiter.api.Test;

import static org.junit.jupiter.api.Assertions.assertEquals;

class CSharpDefaultComparerGenericTest {

    @Test
    void create_AcceptsComparatorLambda() {
        CSharpDefaultComparerGeneric<Integer> comparer =
                CSharpDefaultComparerGeneric.create((a, b) -> Integer.compare(b, a));
        assertEquals(1, comparer.compare(1, 2));
        assertEquals(-1, comparer.compare(2, 1));
    }
}

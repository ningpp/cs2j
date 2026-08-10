package io.github.ningpp.compat;

import org.junit.jupiter.api.Test;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertNull;

/**
 * CSharpKeyValuePair must support the default (no-arg) construction the
 * converter emits for C# default(KeyValuePair&lt;K,V&gt;) in struct-bound
 * factory overrides (KeyValuePairConverter._cs2jDefault_T).
 */
public class CSharpKeyValuePairDefaultCtorTest {

    @Test
    public void defaultCtor_yieldsNullKeyValue() {
        CSharpKeyValuePair<String, Integer> pair = new CSharpKeyValuePair<>();
        assertNull(pair.getKey());
        assertNull(pair.getValue());
    }

    @Test
    public void valueCtor_stillWorks() {
        CSharpKeyValuePair<String, Integer> pair = new CSharpKeyValuePair<>("k", 1);
        assertEquals("k", pair.getKey());
        assertEquals(Integer.valueOf(1), pair.getValue());
    }
}

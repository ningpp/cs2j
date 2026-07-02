package io.github.ningpp.compat;

import org.junit.jupiter.api.Test;
import static org.junit.jupiter.api.Assertions.*;

class CSharpKeyValuePairTest {

    // ---- Construction ----

    @Test
    void constructor_setsKeyAndValue() {
        CSharpKeyValuePair<String, Integer> kvp = new CSharpKeyValuePair<>("test", 42);
        assertEquals("test", kvp.getKey());
        assertEquals(42, kvp.getValue());
    }

    // ---- getKey ----

    @Test
    void getKey_returnsKey() {
        CSharpKeyValuePair<String, Integer> kvp = new CSharpKeyValuePair<>("myKey", 100);
        assertEquals("myKey", kvp.getKey());
    }

    // ---- getValue ----

    @Test
    void getValue_returnsValue() {
        CSharpKeyValuePair<String, Integer> kvp = new CSharpKeyValuePair<>("key", 99);
        assertEquals(99, kvp.getValue());
    }

    // ---- setValue ----

    @Test
    void setValue_updatesValue() {
        CSharpKeyValuePair<String, Integer> kvp = new CSharpKeyValuePair<>("key", 1);
        Integer old = kvp.setValue(10);
        assertEquals(1, old);
        assertEquals(10, kvp.getValue());
    }

    // ---- toString ----

    @Test
    void toString_returnsBracketFormat() {
        CSharpKeyValuePair<String, Integer> kvp = new CSharpKeyValuePair<>("test", 42);
        assertEquals("[test, 42]", kvp.toString());
    }

    // ---- equals ----

    @Test
    void equals_sameKeyAndValue() {
        CSharpKeyValuePair<String, Integer> kvp1 = new CSharpKeyValuePair<>("a", 1);
        CSharpKeyValuePair<String, Integer> kvp2 = new CSharpKeyValuePair<>("a", 1);
        assertEquals(kvp1, kvp2);
    }

    @Test
    void equals_differentKeyOrValue() {
        CSharpKeyValuePair<String, Integer> kvp1 = new CSharpKeyValuePair<>("a", 1);
        CSharpKeyValuePair<String, Integer> kvp2 = new CSharpKeyValuePair<>("a", 2);
        CSharpKeyValuePair<String, Integer> kvp3 = new CSharpKeyValuePair<>("b", 1);
        assertNotEquals(kvp1, kvp2);
        assertNotEquals(kvp1, kvp3);
    }

    // ---- hashCode ----

    @Test
    void hashCode_consistentWithEquals() {
        CSharpKeyValuePair<String, Integer> kvp1 = new CSharpKeyValuePair<>("a", 1);
        CSharpKeyValuePair<String, Integer> kvp2 = new CSharpKeyValuePair<>("a", 1);
        assertEquals(kvp1.hashCode(), kvp2.hashCode());
    }

    // ---- null values ----

    @Test
    void nullKeyAndValue() {
        CSharpKeyValuePair<String, String> kvp = new CSharpKeyValuePair<>(null, null);
        assertNull(kvp.getKey());
        assertNull(kvp.getValue());
    }

    // ---- Oracle data validation (KeyValuePair<string,int>) ----

    @Test
    void oracleData_keyValuePairOperations() {
        CSharpKeyValuePair<String, Integer> kvp = new CSharpKeyValuePair<>("test", 42);
        assertEquals("test", kvp.getKey());
        assertEquals(42, kvp.getValue());
        assertEquals("[test, 42]", kvp.toString());
    }
}

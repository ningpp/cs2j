package io.github.ningpp.compat;

import org.junit.jupiter.api.Test;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertNotNull;
import static org.junit.jupiter.api.Assertions.assertTrue;

class DBNullTest {

    @Test
    void valueIsSingleton() {
        assertNotNull(DBNull.VALUE);
        assertTrue(DBNull.VALUE.equals(DBNull.VALUE));
    }

    @Test
    void toStringReturnsEmpty() {
        assertEquals("", DBNull.VALUE.toString());
    }
}

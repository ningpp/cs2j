package io.github.ningpp.compat;

import org.junit.jupiter.api.Test;
import static org.junit.jupiter.api.Assertions.*;

/**
 * Bridges C# System.Guid to Java.
 * java.util.UUID lacks the parameterless constructor and the
 * (int, short, short, byte...) constructor used by translated C# code.
 */
class UUIDCompatTest {

    @Test
    void defaultConstructor_createsEmptyGuid() {
        UUID guid = new UUID();
        assertEquals("00000000-0000-0000-0000-000000000000", guid.toString());
    }

    @Test
    void constructorWithComponents_createsExpectedGuid() {
        // C#: new Guid(0xa, 0xb, 0xc, 0, 1, 2, 3, 4, 5, 6, 7)
        UUID guid = new UUID(0xa, (short) 0xb, (short) 0xc,
                0, 1, 2, 3, 4, 5, 6, 7);
        assertEquals("0000000a-000b-000c-0001-020304050607", guid.toString());
    }

    @Test
    void constructorWithLongs_createsExpectedGuid() {
        UUID guid = new UUID(0x123456789abcdef0L, 0x0fedcba987654321L);
        assertEquals("12345678-9abc-def0-0fed-cba987654321", guid.toString());
    }

    @Test
    void fromString_parsesGuid() {
        UUID guid = UUID.fromString("0000000a-000b-000c-0001-020304050607");
        assertEquals("0000000a-000b-000c-0001-020304050607", guid.toString());
    }

    @Test
    void randomUUID_isNotEmpty() {
        UUID guid = UUID.randomUUID();
        assertNotEquals(new UUID().toString(), guid.toString());
    }

    @Test
    void equals_sameGuid_returnsTrue() {
        UUID a = new UUID(0xa, (short) 0xb, (short) 0xc,
                0, 1, 2, 3, 4, 5, 6, 7);
        UUID b = UUID.fromString("0000000a-000b-000c-0001-020304050607");
        assertEquals(a, b);
    }
}

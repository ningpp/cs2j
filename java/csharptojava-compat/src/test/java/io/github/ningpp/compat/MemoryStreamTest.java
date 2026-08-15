package io.github.ningpp.compat;

import org.junit.jupiter.api.Test;
import static org.junit.jupiter.api.Assertions.*;

class MemoryStreamTest {

    @Test
    void memoryStream_IsAlwaysReadableAndWritable() {
        MemoryStream stream = new MemoryStream();
        assertTrue(stream.getCanWrite());
        assertTrue(stream.getCanRead());
    }

    @Test
    void memoryStream_WriteThenReadBack() {
        MemoryStream stream = new MemoryStream();
        byte[] payload = new byte[] { 1, 2, 3, 4 };
        stream.write(payload);
        assertEquals(4, stream.getPosition());
        stream.setPosition(0);
        byte[] readBack = new byte[4];
        int n = stream.read(readBack, 0, readBack.length);
        assertEquals(4, n);
        assertArrayEquals(payload, readBack);
    }
}

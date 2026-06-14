package io.github.ningpp.compat;

import org.junit.jupiter.api.Test;

import java.io.StringReader;

import static org.junit.jupiter.api.Assertions.*;

class TextReaderTest {
    @Test
    void blockRead_copiesCharactersAndReturnsZeroAtEnd() {
        TextReader reader = new TextReader(new StringReader("abcd"));
        char[] buffer = new char[6];

        assertEquals(3, reader.read(buffer, 1, 3));
        assertArrayEquals(new char[] { '\0', 'a', 'b', 'c', '\0', '\0' }, buffer);

        assertEquals(1, reader.read(buffer, 4, 2));
        assertEquals('d', buffer[4]);

        assertEquals(0, reader.read(buffer, 0, buffer.length));
    }
}

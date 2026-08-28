package io.github.ningpp.compat;

import java.io.StringReader;

import org.junit.jupiter.api.Test;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertTrue;

/**
 * JsonTextReader must consume characters as it advances (Peek/Read semantics
 * over the wrapped TextReader). Regression: nextChar() only re-peeked the same
 * character, so skipWhitespace() and token scans looped forever on the first
 * token (e.g. Utf8JsonReaderTests.testTrySkipWithComments).
 */
public class JsonTextReaderTest {

    @Test
    public void readAdvancesPastWhitespaceAndTokens() {
        JsonTextReader reader = new JsonTextReader(new TextReader(new StringReader(" { } ")));
        assertTrue(reader.read());
        assertEquals(JsonToken.StartObject, reader.getTokenType());
        assertTrue(reader.read());
        assertEquals(JsonToken.EndObject, reader.getTokenType());
        assertFalse(reader.read());
        reader.close();
    }

    @Test
    public void readParsesStringAndRawTokens() {
        JsonTextReader reader = new JsonTextReader(new TextReader(new StringReader("{\"a\":true}")));
        assertTrue(reader.read());
        assertEquals(JsonToken.StartObject, reader.getTokenType());
        // Newtonsoft semantics: "a" followed by ':' is a property name (the ':'
        // is consumed), and true/false/null parse as Boolean/Null tokens.
        assertTrue(reader.read());
        assertEquals(JsonToken.PropertyName, reader.getTokenType());
        assertEquals("a", reader.getValue());
        assertTrue(reader.read());
        assertEquals(JsonToken.Boolean, reader.getTokenType());
        assertEquals(Boolean.TRUE, reader.getValue());
        assertTrue(reader.read());
        assertEquals(JsonToken.EndObject, reader.getTokenType());
        reader.close();
    }
}

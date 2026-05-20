package io.github.ningpp.compat;

import org.junit.jupiter.api.Test;

import static org.junit.jupiter.api.Assertions.*;

class StreamCompatibilityTest {
    @Test
    void memoryStream_roundTripsThroughStreamWriterAndStreamReader() {
        MemoryStream ms = new MemoryStream();

        StreamWriter writer = new StreamWriter(ms);
        writer.print("hello");
        writer.flush();

        assertSame(ms, writer.getBaseStream());
        assertEquals(5, ms.getPosition());

        ms.setPosition(0);
        StreamReader reader = new StreamReader(ms);

        assertEquals("hello", reader.readToEnd());
    }

    @Test
    void streamWrapper_ofMemoryStream_preservesThePositionableStream() {
        MemoryStream ms = new MemoryStream();

        assertSame(ms, StreamWrapper.of(ms));
    }

    @Test
    void xmlWriter_writesToMemoryStreamViaStreamWrapper() {
        MemoryStream ms = new MemoryStream();

        XmlWriter.create(ms, new XmlWriterSettings());
        XmlWriter.writeStartElement("root");
        XmlWriter.writeString("ok");
        XmlWriter.writeEndElement();
        XmlWriter.flush();

        ms.setPosition(0);
        String xml = new StreamReader(ms).readToEnd();

        assertTrue(xml.contains("<root>ok</root>"), xml);
    }
}

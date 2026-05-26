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

    @Test
    void streamWrapper_readByteArray_emptyStream_returnsZero() {
        StreamWrapper sw = StreamWrapper.of(new java.io.ByteArrayInputStream(new byte[0]));
        byte[] buf = new byte[10];
        assertEquals(0, sw.read(buf, 0, 10));
    }

    @Test
    void memoryStream_readByteArray_emptyStream_returnsZero() {
        MemoryStream ms = new MemoryStream();
        byte[] buf = new byte[10];
        assertEquals(0, ms.read(buf, 0, 10));
    }

    @Test
    void memoryStream_readByteArray_partialThenEOF_returnsZeroAtEnd() {
        byte[] data = {1, 2, 3};
        MemoryStream ms = new MemoryStream(data);
        byte[] buf = new byte[10];
        assertEquals(3, ms.read(buf, 0, 10));
        assertEquals(0, ms.read(buf, 0, 10));
    }

    @Test
    void memoryStream_readByteArray_blockReaderPattern_zeroOnEOF() {
        MemoryStream ms = new MemoryStream();
        byte[] buf = new byte[10];
        int count = ms.read(buf, 0, 10);
        assertEquals(0, count);
    }

    @Test
    void memoryStream_inputAdapter_returnsMinusOneOnEOF() throws Exception {
        MemoryStream ms = new MemoryStream();
        byte[] buf = new byte[10];
        assertEquals(-1, ms.inputStream().read(buf, 0, 10));
    }

    @Test
    void streamReader_emptyMemoryStream_readToEnd_returnsEmpty() {
        MemoryStream ms = new MemoryStream();
        StreamReader reader = new StreamReader(ms);
        assertEquals("", reader.readToEnd());
    }
}

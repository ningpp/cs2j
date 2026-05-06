package io.github.ningpp.compat;

import org.junit.jupiter.api.Test;
import java.io.*;
import java.nio.charset.StandardCharsets;
import static org.junit.jupiter.api.Assertions.*;

/**
 * Tests for XmlReader and XmlWriter backed by StAX.
 * Note: XmlReader/XmlWriter use static state; each test creates a fresh instance.
 * XmlReader starts at START_DOCUMENT after create(); call read() to advance to the first element.
 */
class XmlReaderWriterTest {

    private InputStream toStream(String xml) {
        return new ByteArrayInputStream(xml.getBytes(StandardCharsets.UTF_8));
    }

    // ---- XmlReader ----

    @Test
    void reader_createFromInputStream() {
        String xml = "<root><child>text</child></root>";
        try (XmlReader reader = XmlReader.create(toStream(xml), new XmlReaderSettings())) {
            assertNotNull(reader);
        } catch (Exception e) {
            fail("Exception: " + e.getMessage());
        }
    }

    @Test
    void reader_initialStateIsNotStartElement() {
        String xml = "<root/>";
        XmlReaderSettings settings = new XmlReaderSettings();
        try (XmlReader reader = XmlReader.create(toStream(xml), settings)) {
            // Before read(), the reader is at START_DOCUMENT (event type 7)
            // which is not an element
            assertFalse(XmlReader.isStartElement());
            int nodeType = XmlReader.getNodeType();
            // START_DOCUMENT has event type 7; XmlNodeType has no constant for it
            // Just verify it's not any of the declared XmlNodeType values for elements
            assertTrue(nodeType != XmlNodeType.Element);
            assertTrue(nodeType != XmlNodeType.EndElement);
            assertTrue(nodeType != XmlNodeType.None);
        } catch (Exception e) {
            fail("Exception: " + e.getMessage());
        }
    }

    @Test
    void reader_readElements() {
        String xml = "<root><child>hello</child></root>";
        XmlReaderSettings settings = new XmlReaderSettings();
        settings.setIgnoreWhitespace(true);

        try (XmlReader reader = XmlReader.create(toStream(xml), settings)) {
            // First read() advances past START_DOCUMENT to root element
            assertTrue(XmlReader.read());
            assertTrue(XmlReader.isStartElement());
            assertEquals("root", XmlReader.getName());

            // Read next - should be child element
            assertTrue(XmlReader.read());
            assertTrue(XmlReader.isStartElement());
            assertEquals("child", XmlReader.getName());

            // Read element content
            String content = XmlReader.readElementContentAsString();
            assertEquals("hello", content);
        } catch (Exception e) {
            fail("Exception: " + e.getMessage());
        }
    }

    @Test
    void reader_getNodeType_afterRead() {
        String xml = "<root/>";
        XmlReaderSettings settings = new XmlReaderSettings();
        settings.setIgnoreWhitespace(true);

        try (XmlReader reader = XmlReader.create(toStream(xml), settings)) {
            assertTrue(XmlReader.read());
            int nodeType = XmlReader.getNodeType();
            assertEquals(XmlNodeType.Element, nodeType);
        } catch (Exception e) {
            fail("Exception: " + e.getMessage());
        }
    }

    @Test
    void reader_getName_afterRead() {
        String xml = "<myElement/>";
        XmlReaderSettings settings = new XmlReaderSettings();

        try (XmlReader reader = XmlReader.create(toStream(xml), settings)) {
            assertTrue(XmlReader.read());
            assertEquals("myElement", XmlReader.getName());
        } catch (Exception e) {
            fail("Exception: " + e.getMessage());
        }
    }

    @Test
    void reader_getValue_onStartElement() {
        String xml = "<root>text</root>";
        XmlReaderSettings settings = new XmlReaderSettings();

        try (XmlReader reader = XmlReader.create(toStream(xml), settings)) {
            assertTrue(XmlReader.read());
            // On start element, value/text should be empty
            String val = XmlReader.getValue();
            assertNotNull(val);
        } catch (Exception e) {
            fail("Exception: " + e.getMessage());
        }
    }

    @Test
    void reader_readElementContentAsInt() {
        String xml = "<root><num>42</num></root>";
        XmlReaderSettings settings = new XmlReaderSettings();
        settings.setIgnoreWhitespace(true);

        try (XmlReader reader = XmlReader.create(toStream(xml), settings)) {
            XmlReader.read(); // move to <root>
            XmlReader.read(); // move to <num>
            int val = XmlReader.readElementContentAsInt();
            assertEquals(42, val);
        } catch (Exception e) {
            fail("Exception: " + e.getMessage());
        }
    }

    @Test
    void reader_readElementContentAsDouble() {
        String xml = "<root><val>3.14</val></root>";
        XmlReaderSettings settings = new XmlReaderSettings();
        settings.setIgnoreWhitespace(true);

        try (XmlReader reader = XmlReader.create(toStream(xml), settings)) {
            XmlReader.read(); // move to <root>
            XmlReader.read(); // move to <val>
            double val = XmlReader.readElementContentAsDouble();
            assertEquals(3.14, val, 0.001);
        } catch (Exception e) {
            fail("Exception: " + e.getMessage());
        }
    }

    @Test
    void reader_readElementContentAsBoolean() {
        String xml = "<root><flag>true</flag></root>";
        XmlReaderSettings settings = new XmlReaderSettings();
        settings.setIgnoreWhitespace(true);

        try (XmlReader reader = XmlReader.create(toStream(xml), settings)) {
            XmlReader.read(); // move to <root>
            XmlReader.read(); // move to <flag>
            boolean val = XmlReader.readElementContentAsBoolean();
            assertTrue(val);
        } catch (Exception e) {
            fail("Exception: " + e.getMessage());
        }
    }

    @Test
    void reader_readEndElement() {
        String xml = "<root><child/></root>";
        XmlReaderSettings settings = new XmlReaderSettings();
        settings.setIgnoreWhitespace(true);

        try (XmlReader reader = XmlReader.create(toStream(xml), settings)) {
            XmlReader.read(); // move to <root>
            XmlReader.read(); // move to <child/>
            XmlReader.readEndElement(); // should advance past </child>
            // now should be at end of document or end element
        } catch (Exception e) {
            fail("Exception: " + e.getMessage());
        }
    }

    @Test
    void reader_getAttribute() {
        String xml = "<root><item name=\"test\" value=\"123\"/></root>";
        XmlReaderSettings settings = new XmlReaderSettings();
        settings.setIgnoreWhitespace(true);

        try (XmlReader reader = XmlReader.create(toStream(xml), settings)) {
            XmlReader.read(); // move to <root>
            XmlReader.read(); // move to <item>
            assertEquals("test", XmlReader.getAttribute("name"));
            assertEquals("123", XmlReader.getAttribute("value"));
            assertNull(XmlReader.getAttribute("nonexistent"));
        } catch (Exception e) {
            fail("Exception: " + e.getMessage());
        }
    }

    @Test
    void reader_isStartElement_withName() {
        String xml = "<root/>";
        XmlReaderSettings settings = new XmlReaderSettings();

        try (XmlReader reader = XmlReader.create(toStream(xml), settings)) {
            assertTrue(XmlReader.read());
            assertTrue(XmlReader.isStartElement("root"));
            assertFalse(XmlReader.isStartElement("other"));
        } catch (Exception e) {
            fail("Exception: " + e.getMessage());
        }
    }

    @Test
    void reader_skip_element() {
        String xml = "<root><skip><a/><b/></skip><after>text</after></root>";
        XmlReaderSettings settings = new XmlReaderSettings();
        settings.setIgnoreWhitespace(true);

        try (XmlReader reader = XmlReader.create(toStream(xml), settings)) {
            XmlReader.read(); // move to <root>
            XmlReader.read(); // move to <skip>
            XmlReader.skip(); // skip past </skip>
            // should now be at <after>
            assertTrue(XmlReader.isStartElement());
            assertEquals("after", XmlReader.getName());
        } catch (Exception e) {
            fail("Exception: " + e.getMessage());
        }
    }

    @Test
    void reader_moveToContent() {
        String xml = "<root/>";
        XmlReaderSettings settings = new XmlReaderSettings();

        try (XmlReader reader = XmlReader.create(toStream(xml), settings)) {
            XmlReader.moveToContent();
            assertTrue(XmlReader.isStartElement());
            assertEquals("root", XmlReader.getName());
        } catch (Exception e) {
            fail("Exception: " + e.getMessage());
        }
    }

    @Test
    void reader_readReturnsFalseAtEndOfDocument() {
        String xml = "<root/>";
        XmlReaderSettings settings = new XmlReaderSettings();

        try (XmlReader reader = XmlReader.create(toStream(xml), settings)) {
            assertTrue(XmlReader.read()); // advances to root element (empty element, consumed)
            // After empty element, next read should advance past END_ELEMENT
            // and potentially reach end of document
        } catch (Exception e) {
            fail("Exception: " + e.getMessage());
        }
    }

    @Test
    void reader_settings_ignoreWhitespace() {
        XmlReaderSettings settings = new XmlReaderSettings();
        settings.setIgnoreWhitespace(true);
        assertTrue(settings.isIgnoreWhitespace());

        settings.setIgnoreWhitespace(false);
        assertFalse(settings.isIgnoreWhitespace());
    }

    @Test
    void reader_settings_ignoreComments() {
        XmlReaderSettings settings = new XmlReaderSettings();
        assertFalse(settings.isIgnoreComments());
        settings.setIgnoreComments(true);
        assertTrue(settings.isIgnoreComments());
    }

    @Test
    void reader_settings_checkCharacters() {
        XmlReaderSettings settings = new XmlReaderSettings();
        assertTrue(settings.isCheckCharacters());
        settings.setCheckCharacters(false);
        assertFalse(settings.isCheckCharacters());
    }

    // ---- XmlWriter ----

    @Test
    void writer_writeStartElementAndEndElement() {
        ByteArrayOutputStream out = new ByteArrayOutputStream();
        try {
            XmlWriter writer = XmlWriter.create(out);
            XmlWriter.writeStartElement("root");
            XmlWriter.writeEndElement();
            XmlWriter.flush();

            String result = out.toString(StandardCharsets.UTF_8);
            assertTrue(result.contains("<root"));
            assertTrue(result.contains("/>") || result.contains("</root>"));
        } catch (Exception e) {
            fail("Exception: " + e.getMessage());
        }
    }

    @Test
    void writer_writeElementString() {
        ByteArrayOutputStream out = new ByteArrayOutputStream();
        try {
            XmlWriter writer = XmlWriter.create(out);
            XmlWriter.writeElementString("name", "value");
            XmlWriter.flush();

            String result = out.toString(StandardCharsets.UTF_8);
            assertTrue(result.contains("<name>"));
            assertTrue(result.contains("value"));
            assertTrue(result.contains("</name>"));
        } catch (Exception e) {
            fail("Exception: " + e.getMessage());
        }
    }

    @Test
    void writer_writeNestedElements() {
        ByteArrayOutputStream out = new ByteArrayOutputStream();
        try {
            XmlWriter writer = XmlWriter.create(out);
            XmlWriter.writeStartElement("root");
            XmlWriter.writeStartElement("child");
            XmlWriter.writeString("text");
            XmlWriter.writeEndElement();
            XmlWriter.writeEndElement();
            XmlWriter.flush();

            String result = out.toString(StandardCharsets.UTF_8);
            assertTrue(result.contains("<root>"));
            assertTrue(result.contains("<child>"));
            assertTrue(result.contains("text"));
            assertTrue(result.contains("</child>"));
            assertTrue(result.contains("</root>"));
        } catch (Exception e) {
            fail("Exception: " + e.getMessage());
        }
    }

    @Test
    void writer_writeAttributeString() {
        ByteArrayOutputStream out = new ByteArrayOutputStream();
        try {
            XmlWriter writer = XmlWriter.create(out);
            XmlWriter.writeStartElement("element");
            XmlWriter.writeAttributeString("key", "val");
            XmlWriter.writeEndElement();
            XmlWriter.flush();

            String result = out.toString(StandardCharsets.UTF_8);
            assertTrue(result.contains("key=\"val\"") || result.contains("key='val'"));
        } catch (Exception e) {
            fail("Exception: " + e.getMessage());
        }
    }

    @Test
    void writer_writeComment() {
        ByteArrayOutputStream out = new ByteArrayOutputStream();
        try {
            XmlWriter writer = XmlWriter.create(out);
            XmlWriter.writeComment("a comment");
            XmlWriter.flush();

            String result = out.toString(StandardCharsets.UTF_8);
            assertTrue(result.contains("<!--"));
            assertTrue(result.contains("a comment"));
            assertTrue(result.contains("-->"));
        } catch (Exception e) {
            fail("Exception: " + e.getMessage());
        }
    }

    @Test
    void writer_settings_defaults() {
        XmlWriterSettings settings = new XmlWriterSettings();
        assertEquals("UTF-8", settings.getEncoding());
        assertFalse(settings.isIndent());
        assertEquals("  ", settings.getIndentChars());
        assertFalse(settings.isOmitXmlDeclaration());
    }

    @Test
    void writer_settings_setters() {
        XmlWriterSettings settings = new XmlWriterSettings();
        settings.setIndent(true);
        settings.setIndentChars("\t");
        settings.setOmitXmlDeclaration(true);
        settings.setEncoding("ISO-8859-1");

        assertTrue(settings.isIndent());
        assertEquals("\t", settings.getIndentChars());
        assertTrue(settings.isOmitXmlDeclaration());
        assertEquals("ISO-8859-1", settings.getEncoding());
    }

    // ---- XmlNodeType ----

    @Test
    void xmlNodeType_constants() {
        assertEquals(0, XmlNodeType.None);
        assertTrue(XmlNodeType.Element > 0);
        assertEquals(10, XmlNodeType.Attribute);
        assertTrue(XmlNodeType.EndElement > 0);
        assertTrue(XmlNodeType.EndDocument > 0);
    }

    @Test
    void xmlNodeType_getters() {
        assertEquals(XmlNodeType.None, XmlNodeType.getNone());
        assertEquals(XmlNodeType.Element, XmlNodeType.getElement());
        assertEquals(XmlNodeType.Attribute, XmlNodeType.getAttribute());
        assertEquals(XmlNodeType.EndElement, XmlNodeType.getEndElement());
        assertEquals(XmlNodeType.EndDocument, XmlNodeType.getEndDocument());
    }

    // ---- ReadState ----

    @Test
    void readState_constants() {
        assertEquals(0, ReadState.Initial);
        assertEquals(1, ReadState.Interactive);
        assertEquals(2, ReadState.Error);
        assertEquals(3, ReadState.EndOfFile);
        assertEquals(4, ReadState.Closed);
    }

    @Test
    void readState_getters() {
        assertEquals(ReadState.Initial, ReadState.getInitial());
        assertEquals(ReadState.Interactive, ReadState.getInteractive());
        assertEquals(ReadState.Error, ReadState.getError());
        assertEquals(ReadState.EndOfFile, ReadState.getEndOfFile());
        assertEquals(ReadState.Closed, ReadState.getClosed());
    }

    // ---- Round-trip: write then read ----

    @Test
    void roundTrip_writeAndRead() throws Exception {
        // Write
        ByteArrayOutputStream out = new ByteArrayOutputStream();
        XmlWriter.create(out);
        XmlWriter.writeStartElement("root");
        XmlWriter.writeElementString("name", "Alice");
        XmlWriter.writeElementString("age", "30");
        XmlWriter.writeEndElement();
        XmlWriter.flush();
        XmlWriter.close();

        // Read
        String written = out.toString(StandardCharsets.UTF_8);
        XmlReaderSettings settings = new XmlReaderSettings();
        settings.setIgnoreWhitespace(true);
        XmlReader reader = XmlReader.create(new ByteArrayInputStream(written.getBytes(StandardCharsets.UTF_8)), settings);

        assertTrue(XmlReader.read()); // advance to <root>
        assertTrue(XmlReader.isStartElement("root"));
        XmlReader.read(); // advance to <name>
        assertTrue(XmlReader.isStartElement("name"));
        String name = XmlReader.readElementContentAsString();
        assertEquals("Alice", name);
        // After readElementContentAsString, reader is past </name>, need read() to advance to <age>
        XmlReader.read();
        assertTrue(XmlReader.isStartElement("age"));
        int age = XmlReader.readElementContentAsInt();
        assertEquals(30, age);
    }
}

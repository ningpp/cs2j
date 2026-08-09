package io.github.ningpp.compat;

import org.junit.jupiter.api.Test;

import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.ArrayList;

import static org.junit.jupiter.api.Assertions.*;

class XmlLinqTest {
    @Test
    void loadDescendantsNameAndAttribute() throws Exception {
        Path file = Files.createTempFile("cs2j-xdoc", ".xml");
        Files.writeString(file,
                "<DirectedGraph><Nodes><Node Id=\"n1\" Label=\"A\" /></Nodes></DirectedGraph>",
                StandardCharsets.UTF_8);

        XDocument document = XDocument.load(file.toString());
        CSharpGenericIterable<XElement> descendants = document.descendants();
        ArrayList<XElement> collected = new ArrayList<>();
        for (XElement element : descendants) {
            collected.add(element);
        }

        XElement node = collected.stream()
                .filter(element -> element.getName().getLocalName().equals("Node"))
                .findFirst()
                .orElseThrow();

        assertEquals("n1", node.attribute("Id").getValue());
        assertNull(node.attribute("Missing"));
    }

    @Test
    void toXNameFactoryCreatesNameFromString() {
        // The converter emits XName.toXName("Id") for the C# implicit string->XName
        // conversion (e.g. nodeElement.Attribute("Id")), so the compat facade must
        // provide the factory.
        XName name = XName.toXName("Id");

        assertEquals("Id", name.getLocalName());
        assertEquals("Id", name.LocalName);
    }

    @Test
    void attributeAcceptsXNameFromToXNameFactory() throws Exception {
        Path file = Files.createTempFile("cs2j-xdoc", ".xml");
        Files.writeString(file,
                "<Root><Node Id=\"n1\" /></Root>",
                StandardCharsets.UTF_8);

        XDocument document = XDocument.load(file.toString());
        ArrayList<XElement> collected = new ArrayList<>();
        for (XElement element : document.descendants()) {
            collected.add(element);
        }
        XElement node = collected.stream()
                .filter(element -> element.getName().getLocalName().equals("Node"))
                .findFirst()
                .orElseThrow();

        XAttribute attr = node.attribute(XName.toXName("Id"));
        assertEquals("n1", attr.getValue());
    }
}
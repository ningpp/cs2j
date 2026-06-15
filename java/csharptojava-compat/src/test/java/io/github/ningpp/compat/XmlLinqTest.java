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
        ArrayList<XElement> descendants = new ArrayList<>();
        for (XElement element : document.descendants()) {
            descendants.add(element);
        }

        XElement node = descendants.stream()
                .filter(element -> element.getName().LocalName.equals("Node"))
                .findFirst()
                .orElseThrow();

        assertEquals("n1", node.attribute("Id").getValue());
        assertNull(node.attribute("Missing"));
    }
}

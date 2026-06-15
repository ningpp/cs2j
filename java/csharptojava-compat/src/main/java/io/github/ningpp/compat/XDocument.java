package io.github.ningpp.compat;

import org.w3c.dom.Document;
import org.w3c.dom.Element;
import org.w3c.dom.Node;

import javax.xml.parsers.DocumentBuilderFactory;
import java.io.File;
import java.util.ArrayList;

/** Minimal LINQ-to-XML document facade for converted System.Xml.Linq code. */
public final class XDocument {
    private final Document document;

    private XDocument(Document document) {
        this.document = document;
    }

    public static XDocument load(String filename) {
        try {
            DocumentBuilderFactory factory = DocumentBuilderFactory.newInstance();
            factory.setNamespaceAware(true);
            try {
                factory.setFeature("http://apache.org/xml/features/disallow-doctype-decl", true);
                factory.setFeature("http://xml.org/sax/features/external-general-entities", false);
                factory.setFeature("http://xml.org/sax/features/external-parameter-entities", false);
            } catch (Exception ignored) {
            }
            Document parsed = factory.newDocumentBuilder().parse(new File(filename));
            parsed.getDocumentElement().normalize();
            return new XDocument(parsed);
        } catch (Exception ex) {
            throw new XmlException("Failed to load XML document: " + filename, ex);
        }
    }

    public Iterable<XElement> descendants() {
        ArrayList<XElement> result = new ArrayList<>();
        collectDescendants(document.getDocumentElement(), result);
        return result;
    }

    private static void collectDescendants(Node node, ArrayList<XElement> result) {
        if (node instanceof Element element) {
            result.add(new XElement(element));
        }

        for (Node child = node.getFirstChild(); child != null; child = child.getNextSibling()) {
            collectDescendants(child, result);
        }
    }
}

package io.github.ningpp.compat;

import java.io.ByteArrayInputStream;
import java.io.StringReader;
import java.io.StringWriter;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.ArrayList;
import java.util.List;
import javax.xml.XMLConstants;
import javax.xml.parsers.DocumentBuilder;
import javax.xml.parsers.DocumentBuilderFactory;
import javax.xml.transform.OutputKeys;
import javax.xml.transform.Transformer;
import javax.xml.transform.TransformerFactory;
import javax.xml.transform.dom.DOMSource;
import javax.xml.transform.stream.StreamResult;
import org.w3c.dom.Document;
import org.w3c.dom.Element;
import org.w3c.dom.Node;
import org.xml.sax.InputSource;

/** Minimal System.Xml.Linq.XDocument-compatible wrapper backed by DOM. */
public final class XDocument {
    public final XElement Root;
    private final Document document;

    private XDocument(Document document) {
        this.document = document;
        this.Root = document.getDocumentElement() == null ? null : new XElement(document.getDocumentElement());
    }

    public static XDocument load(String filename) {
        try {
            DocumentBuilder builder = createBuilder();
            return new XDocument(builder.parse(Path.of(filename).toFile()));
        } catch (Exception e) {
            throw new RuntimeException(e);
        }
    }

    public static XDocument load(java.io.InputStream stream) {
        try {
            DocumentBuilder builder = createBuilder();
            return new XDocument(builder.parse(stream));
        } catch (Exception e) {
            throw new RuntimeException(e);
        }
    }

    public static XDocument parse(String xml) {
        try {
            DocumentBuilder builder = createBuilder();
            try (StringReader reader = new StringReader(xml)) {
                return new XDocument(builder.parse(new InputSource(reader)));
            }
        } catch (Exception first) {
            try {
                DocumentBuilder builder = createBuilder();
                byte[] bytes = xml.getBytes(StandardCharsets.UTF_8);
                return new XDocument(builder.parse(new ByteArrayInputStream(bytes)));
            } catch (Exception second) {
                throw new RuntimeException(first);
            }
        }
    }

    public XElement getRoot() {
        return Root;
    }

    public Iterable<XElement> descendants() {
        List<XElement> result = new ArrayList<>();
        Element root = document.getDocumentElement();
        if (root == null) {
            return result;
        }
        addDescendants(root, result);
        return result;
    }

    public Iterable<XElement> elements() {
        List<XElement> result = new ArrayList<>();
        Element root = document.getDocumentElement();
        if (root != null) {
            result.add(new XElement(root));
        }
        return result;
    }

    @Override
    public String toString() {
        return nodeToString(document, false);
    }

    static String nodeToString(Node node, boolean omitXmlDeclaration) {
        try {
            TransformerFactory factory = TransformerFactory.newInstance();
            try {
                factory.setFeature(XMLConstants.FEATURE_SECURE_PROCESSING, true);
            } catch (Exception ignored) {
            }
            Transformer transformer = factory.newTransformer();
            transformer.setOutputProperty(OutputKeys.OMIT_XML_DECLARATION, omitXmlDeclaration ? "yes" : "no");
            transformer.setOutputProperty(OutputKeys.INDENT, "yes");
            StringWriter writer = new StringWriter();
            transformer.transform(new DOMSource(node), new StreamResult(writer));
            return writer.toString();
        } catch (Exception e) {
            throw new RuntimeException(e);
        }
    }

    private static DocumentBuilder createBuilder() throws Exception {
        DocumentBuilderFactory factory = DocumentBuilderFactory.newInstance();
        factory.setNamespaceAware(true);
        try {
            factory.setFeature(XMLConstants.FEATURE_SECURE_PROCESSING, true);
            factory.setFeature("http://apache.org/xml/features/disallow-doctype-decl", true);
            factory.setFeature("http://xml.org/sax/features/external-general-entities", false);
            factory.setFeature("http://xml.org/sax/features/external-parameter-entities", false);
            factory.setXIncludeAware(false);
            factory.setExpandEntityReferences(false);
        } catch (Exception ignored) {
        }
        return factory.newDocumentBuilder();
    }

    private static void addDescendants(Element parent, List<XElement> result) {
        for (Node child = parent.getFirstChild(); child != null; child = child.getNextSibling()) {
            if (child instanceof Element childElement) {
                result.add(new XElement(childElement));
                addDescendants(childElement, result);
            }
        }
    }
}

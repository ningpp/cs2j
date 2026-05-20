package io.github.ningpp.compat;

import java.util.ArrayList;
import java.util.List;
import org.w3c.dom.Element;
import org.w3c.dom.NamedNodeMap;
import org.w3c.dom.Node;
import org.w3c.dom.NodeList;

/** Minimal System.Xml.Linq.XElement-compatible wrapper backed by DOM. */
public final class XElement {
    public final XName Name;
    private final Element element;

    XElement(Element element) {
        this.element = element;
        this.Name = createName(element);
    }

    public XName getName() {
        return Name;
    }

    private static XName createName(Element element) {
        String localName = element.getLocalName();
        if (localName == null || localName.isEmpty()) {
            localName = element.getTagName();
        }
        return new XName(localName, element.getNamespaceURI());
    }

    public XAttribute attribute(String name) {
        if (name == null) {
            return null;
        }
        if (element.hasAttribute(name)) {
            return new XAttribute(name, element.getAttribute(name));
        }

        NamedNodeMap attributes = element.getAttributes();
        for (int i = 0; i < attributes.getLength(); i++) {
            Node attr = attributes.item(i);
            String localName = attr.getLocalName();
            if (name.equals(localName) || name.equals(attr.getNodeName())) {
                return new XAttribute(name, attr.getNodeValue());
            }
        }
        return null;
    }

    public XAttribute attribute(XName name) {
        return name == null ? null : attribute(name.getLocalName());
    }

    public Iterable<XElement> elements() {
        List<XElement> result = new ArrayList<>();
        NodeList children = element.getChildNodes();
        for (int i = 0; i < children.getLength(); i++) {
            Node child = children.item(i);
            if (child instanceof Element childElement) {
                result.add(new XElement(childElement));
            }
        }
        return result;
    }

    public Iterable<XElement> descendants() {
        List<XElement> result = new ArrayList<>();
        addDescendants(element, result);
        return result;
    }

    Element unwrap() {
        return element;
    }

    @Override
    public String toString() {
        return XDocument.nodeToString(element, true);
    }

    private static void addDescendants(Element parent, List<XElement> result) {
        NodeList children = parent.getChildNodes();
        for (int i = 0; i < children.getLength(); i++) {
            Node child = children.item(i);
            if (child instanceof Element childElement) {
                result.add(new XElement(childElement));
                addDescendants(childElement, result);
            }
        }
    }
}

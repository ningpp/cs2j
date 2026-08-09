package io.github.ningpp.compat;

import org.w3c.dom.Attr;
import org.w3c.dom.Element;

/** Minimal LINQ-to-XML element facade for converted System.Xml.Linq code. */
public final class XElement {
    private final Element element;

    XElement(Element element) {
        this.element = element;
    }

    public XName getName() {
        return new XName(localName(element));
    }

    public XAttribute attribute(String name) {
        Attr attribute = element.getAttributeNode(name);
        if (attribute == null) {
            attribute = element.getAttributeNodeNS("*", name);
        }
        return attribute == null ? null : new XAttribute(localName(attribute), attribute.getValue());
    }

    /**
     * Overload for converted call sites that pass the C# XElement.Attribute(XName)
     * form; the converter emits XName.toXName("Id") for the implicit string->XName
     * conversion, so the attribute name arrives as an XName.
     */
    public XAttribute attribute(XName name) {
        return attribute(name.LocalName);
    }

    Element unwrap() {
        return element;
    }

    static String localName(org.w3c.dom.Node node) {
        String localName = node.getLocalName();
        return localName != null ? localName : node.getNodeName();
    }
}

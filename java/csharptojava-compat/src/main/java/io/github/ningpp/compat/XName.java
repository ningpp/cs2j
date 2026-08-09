package io.github.ningpp.compat;

/** Minimal LINQ-to-XML name facade for converted System.Xml.Linq code. */
public final class XName {
    public final String LocalName;

    public XName(String localName) {
        this.LocalName = localName;
    }

    /**
     * Mirrors the C# implicit conversion string -> XName (XName(string)).
     * The converter emits XName.toXName("Id") at call sites such as
     * XElement.attribute(XName) with a string literal argument.
     */
    public static XName toXName(String name) {
        return new XName(name);
    }

    public String getLocalName() {
        return LocalName;
    }
}
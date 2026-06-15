package io.github.ningpp.compat;

/** Minimal LINQ-to-XML attribute facade for converted System.Xml.Linq code. */
public final class XAttribute {
    private final String name;
    private final String value;

    XAttribute(String name, String value) {
        this.name = name;
        this.value = value;
    }

    public XName getName() {
        return new XName(name);
    }

    public String getValue() {
        return value;
    }
}

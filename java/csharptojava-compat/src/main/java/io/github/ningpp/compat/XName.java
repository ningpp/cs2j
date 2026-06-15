package io.github.ningpp.compat;

/** Minimal LINQ-to-XML name facade for converted System.Xml.Linq code. */
public final class XName {
    public final String LocalName;

    public XName(String localName) {
        this.LocalName = localName;
    }
}

package io.github.ningpp.compat;

/** Minimal System.Xml.Linq.XAttribute-compatible wrapper. */
public final class XAttribute {
    public final XName Name;
    public final String Value;
    private final XName name;
    private final String value;

    public XAttribute(String name, String value) {
        this(new XName(name), value);
    }

    public XAttribute(XName name, String value) {
        this.name = name == null ? new XName("") : name;
        this.value = value;
        this.Name = this.name;
        this.Value = this.value;
    }

    public XName getName() {
        return name;
    }

    public String getValue() {
        return value;
    }

    @Override
    public String toString() {
        return name + "=\"" + value + "\"";
    }
}

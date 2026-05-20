package io.github.ningpp.compat;

/** Minimal System.Xml.Linq.XName-compatible value. */
public final class XName {
    public final String LocalName;
    public final String NamespaceName;
    private final String localName;
    private final String namespaceName;

    public XName(String localName) {
        this(localName, "");
    }

    public XName(String localName, String namespaceName) {
        this.localName = localName == null ? "" : localName;
        this.namespaceName = namespaceName == null ? "" : namespaceName;
        this.LocalName = this.localName;
        this.NamespaceName = this.namespaceName;
    }

    public static XName get(String name) {
        return new XName(name);
    }

    public String getLocalName() {
        return localName;
    }

    public String getNamespaceName() {
        return namespaceName;
    }

    @Override
    public String toString() {
        return namespaceName.isEmpty() ? localName : "{" + namespaceName + "}" + localName;
    }
}

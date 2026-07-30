public enum AttributeProperties {
    DEFAULT(0),
    URI(1),
    BOOLEAN(2),
    NAME(4),
    _UNMAPPED(0);

    private final int value;
    private int unmappedValue;

    AttributeProperties(int v) {
        this.value = v;
    }

    public int getValue() {
        if (this == _UNMAPPED) return unmappedValue;
        return value;
    }
    public static AttributeProperties fromValue(int v) {
        for (AttributeProperties e : values()) { if (e != _UNMAPPED && e.value == v) return e; }
        _UNMAPPED.unmappedValue = v;
        return _UNMAPPED;
    }
    public static AttributeProperties fromValueUnchecked(int v) {
        for (AttributeProperties e : values()) { if (e != _UNMAPPED && e.value == v) return e; }
        _UNMAPPED.unmappedValue = v;
        return _UNMAPPED;
    }
}

public enum ElementProperties {
    DEFAULT(0),
    BOOL_PARENT(2),
    URI_PARENT(1),
    NAME_PARENT(4),
    _UNMAPPED(0);

    private final int value;
    private int unmappedValue;

    ElementProperties(int v) {
        this.value = v;
    }

    public int getValue() {
        if (this == _UNMAPPED) return unmappedValue;
        return value;
    }
    public static ElementProperties fromValue(int v) {
        for (ElementProperties e : values()) { if (e != _UNMAPPED && e.value == v) return e; }
        _UNMAPPED.unmappedValue = v;
        return _UNMAPPED;
    }
    public static ElementProperties fromValueUnchecked(int v) {
        for (ElementProperties e : values()) { if (e != _UNMAPPED && e.value == v) return e; }
        _UNMAPPED.unmappedValue = v;
        return _UNMAPPED;
    }
}

public class Tree {
    public int findCaseInsensitiveString(String s) {
        return 0;
    }
}

public class Writer {
    private AttributeProperties _currentAttributeProperties = AttributeProperties.DEFAULT;
    private ElementProperties currentElementProperties = ElementProperties.DEFAULT;
    private Tree attributePropertySearch;

    public void m(String localName) {
        _currentAttributeProperties = AttributeProperties.fromValue(attributePropertySearch.findCaseInsensitiveString(localName) & currentElementProperties.getValue());
    }
}


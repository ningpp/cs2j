public enum QueryProps {
    None(0x00),
    Position(0x01),
    Count(0x02),
    Cached(0x04),
    Reverse(0x08),
    Merge(0x10),
    _UNMAPPED(0);

    private final int value;
    private int unmappedValue;

    QueryProps(int v) {
        this.value = v;
    }

    public int getValue() {
        if (this == _UNMAPPED) return unmappedValue;
        return value;
    }
    public static QueryProps fromValue(int v) {
        for (QueryProps e : values()) { if (e != _UNMAPPED && e.value == v) return e; }
        _UNMAPPED.unmappedValue = v;
        return _UNMAPPED;
    }
    public static QueryProps fromValueUnchecked(int v) {
        for (QueryProps e : values()) { if (e != _UNMAPPED && e.value == v) return e; }
        _UNMAPPED.unmappedValue = v;
        return _UNMAPPED;
    }
}

public class ContextQuery {
    public QueryProps getProperties() {
        return QueryProps.fromValue(QueryProps.Merge.getValue() | QueryProps.Cached.getValue() | QueryProps.Position.getValue() | QueryProps.Count.getValue());
    }
}


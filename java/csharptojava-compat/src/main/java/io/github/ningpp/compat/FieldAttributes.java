package io.github.ningpp.compat;

/**
 * Stub for System.Reflection.FieldAttributes.
 */
public enum FieldAttributes {
    PrivateScope(0),
    Private(1),
    FamANDAssem(2),
    Assembly(3),
    Family(4),
    FamORAssem(5),
    Public(6),
    Static(16),
    InitOnly(32),
    Literal(64),
    NotSerialized(128),
    HasFieldMarshal(4096),
    HasDefault(32768),
    HasFieldRVA(256);

    private final int _value;

    FieldAttributes(int value) {
        this._value = value;
    }

    public int getValue() {
        return _value;
    }
}

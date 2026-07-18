package io.github.ningpp.compat;

/**
 * Stub for System.Reflection.PropertyAttributes.
 */
public enum PropertyAttributes {
    None(0),
    SpecialName(512),
    RTSpecialName(1024),
    HasDefault(4096),
    Reserved2(8192),
    Reserved3(16384),
    Reserved4(32768),
    ReservedMask(62464);

    private final int _value;

    PropertyAttributes(int value) {
        this._value = value;
    }

    public int getValue() {
        return _value;
    }
}

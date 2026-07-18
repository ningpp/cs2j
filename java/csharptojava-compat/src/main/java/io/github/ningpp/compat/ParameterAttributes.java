package io.github.ningpp.compat;

/**
 * Stub for System.Reflection.ParameterAttributes.
 */
public enum ParameterAttributes {
    None(0),
    In(1),
    Out(2),
    Lcid(4),
    Retval(8),
    Optional(16),
    HasDefault(4096),
    HasFieldMarshal(8192),
    Reserved3(3072),
    Reserved4(6144),
    ReservedMask(63488);

    private final int _value;

    ParameterAttributes(int value) {
        this._value = value;
    }

    public int getValue() {
        return _value;
    }
}

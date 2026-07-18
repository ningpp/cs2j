package io.github.ningpp.compat;

/**
 * Stub for System.Reflection.MethodAttributes.
 */
public enum MethodAttributes {
    PrivateScope(0),
    Private(1),
    FamANDAssem(2),
    Assembly(3),
    Family(4),
    FamORAssem(5),
    Public(6),
    Static(16),
    Final(32),
    Virtual(64),
    HideBySig(128),
    NewSlot(256),
    CheckAccessOnOverride(512),
    Abstract(1024),
    SpecialName(2048),
    PinvokeImpl(8192),
    UnmanagedExport(8),
    RTSpecialName(4096),
    HasSecurity(16384),
    RequireSecObject(32768);

    private final int _value;

    MethodAttributes(int value) {
        this._value = value;
    }

    public int getValue() {
        return _value;
    }
}

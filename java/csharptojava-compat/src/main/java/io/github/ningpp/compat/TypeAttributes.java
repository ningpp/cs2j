package io.github.ningpp.compat;

/**
 * Stub for System.Reflection.TypeAttributes.
 */
public enum TypeAttributes {
    NotPublic(0),
    Public(1),
    NestedPublic(2),
    NestedPrivate(3),
    NestedFamily(4),
    NestedAssembly(5),
    NestedFamANDAssem(6),
    NestedFamORAssem(7),
    AutoLayout(0),
    SequentialLayout(8),
    ExplicitLayout(16),
    Class(0),
    Interface(32),
    Abstract(128),
    Sealed(256),
    SpecialName(1024),
    Import(4096),
    Serializable(8192),
    WindowsRuntime(16384),
    UnicodeClass(65536),
    AutoClass(131072),
    CustomFormatClass(196608),
    HasSecurity(262144),
    BeforeFieldInit(1048576),
    RTSpecialName(2048),
    HasDefault(983040);

    private final int _value;

    TypeAttributes(int value) {
        this._value = value;
    }

    public int getValue() {
        return _value;
    }
}

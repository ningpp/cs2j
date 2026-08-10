package io.github.ningpp.compat;

/**
 * Replacement for System.TypeCode with the C# enum's explicit values, so the
 * converter's (int)typeCode cast (which emits getValue()) and fromValue()
 * round-trips work.
 */
public enum TypeCode {
    Empty(0),
    Object(1),
    DBNull(2),
    Boolean(3),
    Char(4),
    SByte(5),
    Byte(6),
    Int16(7),
    UInt16(8),
    Int32(9),
    UInt32(10),
    Int64(11),
    UInt64(12),
    Single(13),
    Double(14),
    Decimal(15),
    DateTime(16),
    String(18);

    private final int value;

    TypeCode(int value) {
        this.value = value;
    }

    public int getValue() {
        return value;
    }

    public static TypeCode fromValue(int value) {
        for (TypeCode code : values()) {
            if (code.value == value) {
                return code;
            }
        }
        throw new IllegalArgumentException("Invalid TypeCode value: " + value);
    }
}

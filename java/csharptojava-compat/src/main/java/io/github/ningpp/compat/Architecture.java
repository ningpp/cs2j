package io.github.ningpp.compat;

/**
 * Compat for System.Runtime.InteropServices.Architecture.
 * Constant names match the C# enum members; ordinal values match .NET.
 */
public enum Architecture {
    X86(0),
    X64(1),
    Arm(2),
    Arm64(3),
    Wasm(4),
    S390x(5),
    LoongArch64(6),
    Armv6(7),
    Ppc64le(8),
    RiscV64(9);

    private final int value;

    Architecture(int value) {
        this.value = value;
    }

    public int getValue() {
        return value;
    }

    public static Architecture fromValue(int value) {
        for (Architecture architecture : values()) {
            if (architecture.value == value) {
                return architecture;
            }
        }
        throw new IllegalArgumentException("Invalid Architecture value: " + value);
    }
}

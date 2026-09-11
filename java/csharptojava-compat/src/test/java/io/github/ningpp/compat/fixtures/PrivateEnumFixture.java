package io.github.ningpp.compat.fixtures;

/**
 * Holds a {@code private} nested enum in a package <em>other</em> than
 * {@code io.github.ningpp.compat}, mirroring converted code such as
 * {@code dotnet.text.json.Serialization.Tests.EnumConverterTests.MyCustomEnum}
 * (a C# {@code private enum} becomes a Java {@code private static enum}).
 *
 * <p>Reflection from {@link io.github.ningpp.compat.EnumHelper} — a different runtime
 * package — cannot access a public method declared on a private class, which is why the
 * helper has to open the accessor explicitly instead of relying on
 * {@code getClass().getMethod(...).invoke(...)}.</p>
 */
public final class PrivateEnumFixture {

    private PrivateEnumFixture() { }

    private enum PrivateValueEnum {
        First(1),
        Second(2),
        _UNMAPPED(0);

        private final int value;
        private int unmappedValue;

        PrivateValueEnum(int value) { this.value = value; }

        public int getValue() {
            return this == _UNMAPPED ? unmappedValue : value;
        }

        public static PrivateValueEnum fromValue(int v) {
            for (PrivateValueEnum e : values()) {
                if (e != _UNMAPPED && e.value == v) return e;
            }
            _UNMAPPED.unmappedValue = v;
            return _UNMAPPED;
        }
    }

    /** An enum constant whose declaring class is private. */
    public static Enum<?> first() { return PrivateValueEnum.First; }

    /** The sentinel carrying an unnamed value, with its raw value preserved. */
    public static Enum<?> unmapped(int rawValue) { return PrivateValueEnum.fromValue(rawValue); }

    /** Class token of the private enum, as converter-generated code would pass it. */
    public static Class<?> type() { return PrivateValueEnum.class; }
}

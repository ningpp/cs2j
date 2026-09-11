package io.github.ningpp.compat;

import io.github.ningpp.compat.fixtures.PrivateEnumFixture;
import org.junit.jupiter.api.Test;

import static org.junit.jupiter.api.Assertions.*;

/**
 * The converter emits a {@code getValue()} accessor on every enum constant it
 * generates, but the enum type itself may be non-public: a C# {@code private enum}
 * nested in a class becomes a Java {@code private static enum}, exactly like
 * {@code dotnet.text.json.Serialization.Tests.EnumConverterTests.MyCustomEnum}.
 *
 * <p>{@link EnumHelper} reads the underlying value reflectively from a different runtime
 * package, so it must cope with a public method declared on a non-public (private nested)
 * class — otherwise {@code Method.invoke} throws {@link IllegalAccessException} and enum
 * converters fail while warming up their name cache
 * ({@code ClassCastException: Cannot cast ... to a numeric value}).</p>
 */
class EnumHelperNonPublicEnumTest {

    @Test
    void toInt32_readsGetValueOfNonPublicEnumClass() {
        assertEquals(1, EnumHelper.toInt32(PrivateEnumFixture.first()));
    }

    @Test
    void toInt64_readsGetValueOfNonPublicEnumClass() {
        assertEquals(1L, EnumHelper.toInt64(PrivateEnumFixture.first()));
    }

    @Test
    void toInt32_preservesUnmappedValueOfNonPublicEnumClass() {
        assertEquals(-1, EnumHelper.toInt32(PrivateEnumFixture.unmapped(-1)));
    }

    @Test
    void namesAndValues_areReadableForNonPublicEnumClasses() {
        assertEquals(2, EnumHelper.getNames(PrivateEnumFixture.type()).length);
        assertEquals(2, EnumHelper.getValuesArray(PrivateEnumFixture.type()).length);
    }

    /**
     * {@code EnumConverter.write} reaches the underlying value through
     * {@code (int) Unsafe.as(value)}; with a non-public enum class the reflective read
     * used to fail silently and hand back the enum constant itself, which then threw
     * {@code ClassCastException: ...MyCustomEnum cannot be cast to ...Integer}.
     */
    @Test
    void unsafeAs_reinterpretsNonPublicEnumAsItsUnderlyingValue() {
        assertEquals(1, (int) Unsafe.as(PrivateEnumFixture.first()));
        assertEquals(-1, (int) Unsafe.as(PrivateEnumFixture.unmapped(-1)));
    }

    @Test
    void toObject_buildsConstantsOfNonPublicEnumClasses() {
        assertSame(PrivateEnumFixture.first(), EnumHelper.toObject(PrivateEnumFixture.type(), 1L));
        assertEquals(-1, EnumHelper.toInt32(EnumHelper.toObject(PrivateEnumFixture.type(), -1L)));
    }
}

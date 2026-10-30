package io.github.ningpp.compat;

import org.junit.jupiter.api.Test;
import static org.junit.jupiter.api.Assertions.*;

class EnumHelperTest {

    enum Color { Red, Green, Blue }

    enum Status { Open, Closed, Pending }

    // ---- ordinalOrMinusOne ----

    @Test
    void ordinalOrMinusOne_nullSentinel_returnsMinusOne() {
        // (DtdProcessing)(-1) is modeled as a null enum reference; reading it back
        // as int must reproduce the C# sentinel value.
        assertEquals(-1, EnumHelper.ordinalOrMinusOne(null));
    }

    @Test
    void ordinalOrMinusOne_enumConstant_returnsOrdinal() {
        assertEquals(0, EnumHelper.ordinalOrMinusOne(Color.Red));
        assertEquals(1, EnumHelper.ordinalOrMinusOne(Color.Green));
        assertEquals(2, EnumHelper.ordinalOrMinusOne(Color.Blue));
    }

    // ---- tryParse (case-sensitive, 3-arg) ----

    @Test
    void tryParse_caseSensitive_validName() {
        ObjectHolder<Color> holder = new ObjectHolder<>();
        assertTrue(EnumHelper.tryParse("Red", holder, Color.class));
        assertEquals(Color.Red, holder.value);
    }

    @Test
    void tryParse_caseSensitive_validName_lastConstant() {
        ObjectHolder<Color> holder = new ObjectHolder<>();
        assertTrue(EnumHelper.tryParse("Blue", holder, Color.class));
        assertEquals(Color.Blue, holder.value);
    }

    @Test
    void tryParse_caseSensitive_wrongCase_returnsFalse() {
        ObjectHolder<Color> holder = new ObjectHolder<>();
        assertFalse(EnumHelper.tryParse("red", holder, Color.class));
        assertNull(holder.value);
    }

    @Test
    void tryParse_caseSensitive_invalidName_returnsFalse() {
        ObjectHolder<Color> holder = new ObjectHolder<>();
        assertFalse(EnumHelper.tryParse("Yellow", holder, Color.class));
        assertNull(holder.value);
    }

    @Test
    void tryParse_caseSensitive_emptyString_returnsFalse() {
        ObjectHolder<Color> holder = new ObjectHolder<>();
        assertFalse(EnumHelper.tryParse("", holder, Color.class));
    }

    @Test
    void tryParse_caseSensitive_nullName_returnsFalse() {
        ObjectHolder<Color> holder = new ObjectHolder<>();
        assertFalse(EnumHelper.tryParse((String) null, holder, Color.class));
    }

    @Test
    void tryParse_caseSensitive_nullClass_returnsFalse() {
        ObjectHolder<Color> holder = new ObjectHolder<>();
        assertFalse(EnumHelper.tryParse("Red", holder, null));
    }

    // ---- tryParse (ignoreCase, 4-arg) ----

    @Test
    void tryParse_ignoreCase_validName_sameCase() {
        ObjectHolder<Color> holder = new ObjectHolder<>();
        assertTrue(EnumHelper.tryParse("Red", false, holder, Color.class));
        assertEquals(Color.Red, holder.value);
    }

    @Test
    void tryParse_ignoreCase_validName_differentCase_ignoreCaseTrue() {
        ObjectHolder<Color> holder = new ObjectHolder<>();
        assertTrue(EnumHelper.tryParse("red", true, holder, Color.class));
        assertEquals(Color.Red, holder.value);
    }

    @Test
    void tryParse_ignoreCase_validName_upperCase_ignoreCaseTrue() {
        ObjectHolder<Color> holder = new ObjectHolder<>();
        assertTrue(EnumHelper.tryParse("GREEN", true, holder, Color.class));
        assertEquals(Color.Green, holder.value);
    }

    @Test
    void tryParse_ignoreCase_validName_mixedCase_ignoreCaseTrue() {
        ObjectHolder<Color> holder = new ObjectHolder<>();
        assertTrue(EnumHelper.tryParse("bLuE", true, holder, Color.class));
        assertEquals(Color.Blue, holder.value);
    }

    @Test
    void tryParse_ignoreCase_wrongCase_ignoreCaseFalse_returnsFalse() {
        ObjectHolder<Color> holder = new ObjectHolder<>();
        assertFalse(EnumHelper.tryParse("red", false, holder, Color.class));
    }

    @Test
    void tryParse_ignoreCase_invalidName_ignoreCaseTrue_returnsFalse() {
        ObjectHolder<Color> holder = new ObjectHolder<>();
        assertFalse(EnumHelper.tryParse("Yellow", true, holder, Color.class));
    }

    @Test
    void tryParse_ignoreCase_nullName_returnsFalse() {
        ObjectHolder<Color> holder = new ObjectHolder<>();
        assertFalse(EnumHelper.tryParse(null, true, holder, Color.class));
    }

    @Test
    void tryParse_ignoreCase_nullClass_returnsFalse() {
        ObjectHolder<Color> holder = new ObjectHolder<>();
        assertFalse(EnumHelper.tryParse("Red", true, holder, null));
    }

    @Test
    void tryParse_ignoreCase_nonEnumClass_returnsFalse() {
        ObjectHolder<String> holder = new ObjectHolder<>();
        assertFalse(EnumHelper.tryParse("Red", true, holder, String.class));
    }

    // ---- tryParse with different enum types ----

    @Test
    void tryParse_differentEnumType_statusEnum() {
        ObjectHolder<Status> holder = new ObjectHolder<>();
        assertTrue(EnumHelper.tryParse("Open", holder, Status.class));
        assertEquals(Status.Open, holder.value);
    }

    @Test
    void tryParse_differentEnumType_statusEnum_ignoreCase() {
        ObjectHolder<Status> holder = new ObjectHolder<>();
        assertTrue(EnumHelper.tryParse("closed", true, holder, Status.class));
        assertEquals(Status.Closed, holder.value);
    }

    // ---- parse (case-sensitive, 2-arg) ----

    @Test
    void parse_caseSensitive_validName() {
        Object result = EnumHelper.parse(Color.class, "Green");
        assertEquals(Color.Green, result);
    }

    @Test
    void parse_caseSensitive_invalidName_throws() {
        assertThrows(IllegalArgumentException.class, () -> EnumHelper.parse(Color.class, "Yellow"));
    }

    @Test
    void parse_caseSensitive_nullName_throws() {
        assertThrows(IllegalArgumentException.class, () -> EnumHelper.parse(Color.class, null));
    }

    // ---- parse (ignoreCase, 3-arg) ----

    @Test
    void parse_ignoreCase_false_sameCase() {
        Object result = EnumHelper.parse(Color.class, "Blue", false);
        assertEquals(Color.Blue, result);
    }

    @Test
    void parse_ignoreCase_false_wrongCase_throws() {
        assertThrows(IllegalArgumentException.class, () -> EnumHelper.parse(Color.class, "blue", false));
    }

    @Test
    void parse_ignoreCase_true_differentCase() {
        Object result = EnumHelper.parse(Color.class, "red", true);
        assertEquals(Color.Red, result);
    }

    @Test
    void parse_ignoreCase_true_upperCase() {
        Object result = EnumHelper.parse(Color.class, "GREEN", true);
        assertEquals(Color.Green, result);
    }

    @Test
    void parse_ignoreCase_true_invalidName_throws() {
        assertThrows(IllegalArgumentException.class, () -> EnumHelper.parse(Color.class, "Yellow", true));
    }

    @Test
    void parse_ignoreCase_true_nullName_throws() {
        assertThrows(IllegalArgumentException.class, () -> EnumHelper.parse(Color.class, null, true));
    }

    @Test
    void parse_ignoreCase_true_nullClass_throws() {
        assertThrows(IllegalArgumentException.class, () -> EnumHelper.parse(null, "Red", true));
    }

    // ---- getValues ----

    @Test
    void getValues_returnsAllConstants() {
        Object[] values = EnumHelper.getValuesArray(Color.class);
        assertArrayEquals(new Object[]{Color.Red, Color.Green, Color.Blue}, values);
    }

    @Test
    void getValues_csharpArrayReturnsEnumConstants() {
        // C# Enum.GetValues returns an enum-typed array; converted foreach loops
        // iterate EnumHelper.getValues(...) with the enum element type.
        CSharpArray values = EnumHelper.getValues(Color.class);
        assertEquals(3, values.getLength());
        assertSame(Color.Red, values.getValue(0));
        assertSame(Color.Blue, values.getValue(2));
    }

    @Test
    void getValues_statusEnum() {
        Object[] values = EnumHelper.getValuesArray(Status.class);
        assertArrayEquals(new Object[]{Status.Open, Status.Closed, Status.Pending}, values);
    }

    enum WithUnmapped {
        A,
        B,
        _UNMAPPED
    }

    @Test
    void getValues_excludesSyntheticUnmappedConstant() {
        // The converter adds a synthetic _UNMAPPED constant to enums so numeric
        // values that have no named member can round-trip. C# Enum.GetValues does
        // not contain it, so compat must filter it out (JsonReaderOptions
        // validates CommentHandling against GetValues and rejects _UNMAPPED).
        CSharpArray values = EnumHelper.getValues(WithUnmapped.class);
        assertEquals(2, values.getLength());
        assertEquals(WithUnmapped.A, values.getValue(0));
        assertEquals(WithUnmapped.B, values.getValue(1));
        assertArrayEquals(new Object[]{WithUnmapped.A, WithUnmapped.B},
            EnumHelper.getValuesArray(WithUnmapped.class));
        assertArrayEquals(new String[]{"A", "B"},
            EnumHelper.getNames(WithUnmapped.class));
    }

    enum WithExplicitValue {
        A(10),
        B(20);

        private final int value;

        WithExplicitValue(int v) {
            this.value = v;
        }

        public int getValue() {
            return value;
        }
    }

    @Test
    void toInt32_toInt64_handlesEnumConstantsAndBoxedNumbers() {
        // C# unboxing casts like (int)boxedEnum work on enum constants; Java
        // enum constants are not Numbers, so EnumHelper must read getValue().
        assertEquals(10, EnumHelper.toInt32(WithExplicitValue.A));
        assertEquals(20L, EnumHelper.toInt64(WithExplicitValue.B));
        assertEquals(42, EnumHelper.toInt32(Integer.valueOf(42)));
        assertEquals(7L, EnumHelper.toInt64(Integer.valueOf(7)));
        assertEquals((byte) 10, EnumHelper.toInt32(WithExplicitValue.A));
    }
}

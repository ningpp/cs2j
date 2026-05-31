package io.github.ningpp.compat;

import org.junit.jupiter.api.Test;
import static org.junit.jupiter.api.Assertions.*;

class EnumHelperTest {

    enum Color { Red, Green, Blue }

    enum Status { Open, Closed, Pending }

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
        Object[] values = EnumHelper.getValues(Color.class);
        assertArrayEquals(new Object[]{Color.Red, Color.Green, Color.Blue}, values);
    }

    @Test
    void getValues_statusEnum() {
        Object[] values = EnumHelper.getValues(Status.class);
        assertArrayEquals(new Object[]{Status.Open, Status.Closed, Status.Pending}, values);
    }
}

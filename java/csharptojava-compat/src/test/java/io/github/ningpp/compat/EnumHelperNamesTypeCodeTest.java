package io.github.ningpp.compat;

import org.junit.jupiter.api.Test;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertTrue;

/**
 * EnumHelper.getNames mirrors System.Enum.GetNames (converter emits
 * EnumHelper.getNames for C# Enum.GetNames). TypeCode carries explicit C#
 * values so (int)typeCode casts work via getValue().
 */
public class EnumHelperNamesTypeCodeTest {

    @Test
    public void getNames_returnsConstantNames() {
        String[] names = EnumHelper.getNames(DayOfWeek.class);
        assertTrue(names.length >= 7);
        assertEquals("Sunday", names[0]);
        assertEquals("Monday", names[1]);
    }

    @Test
    public void typeCode_hasExplicitValuesAndGetValue() {
        assertEquals(9, TypeCode.Int32.getValue());
        assertEquals(18, TypeCode.String.getValue());
        assertEquals(TypeCode.Int32, TypeCode.fromValue(9));
    }
}

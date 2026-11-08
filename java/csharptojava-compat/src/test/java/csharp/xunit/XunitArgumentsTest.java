package csharp.xunit;

import org.junit.jupiter.api.Test;

import static org.junit.jupiter.api.Assertions.assertArrayEquals;
import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertSame;

/**
 * xUnit fills omitted trailing optional parameters of a theory row from the
 * declared default; JUnit needs the row to match the method arity exactly.
 */
public class XunitArgumentsTest {

    @Test
    public void padsShortRowWithTrailingDefaults() {
        Object[] row = {"json", 1, 2};

        Object[] padded = XunitArguments.pad(row, 3, 64);

        assertArrayEquals(new Object[] {"json", 1, 2, 64}, padded);
    }

    @Test
    public void keepsCompleteRowUnchanged() {
        Object[] row = {"json", 1, 2, 7};

        assertSame(row, XunitArguments.pad(row, 3, 64));
    }

    @Test
    public void fillsEveryMissingOptionalParameter() {
        Object[] row = {"json"};

        Object[] padded = XunitArguments.pad(row, 1, 2, 3);

        assertArrayEquals(new Object[] {"json", 2, 3}, padded);
    }

    /**
     * xUnit converts row values to the declared parameter type; JUnit does not.
     * Real chain: [InlineData(Threshold - 3)] is a long row for an int parameter,
     * and enum constants feed long parameters.
     */
    @Test
    public void coerceNarrowsLongRowToIntParameter() {
        Object[] row = XunitArguments.coerce(new Object[] {5L, "x"}, int.class, String.class);

        assertEquals(5, row[0]);
        assertEquals("x", row[1]);
    }

    @Test
    public void coerceUnboxesEnumConstantForLongParameter() {
        Object[] row = XunitArguments.coerce(
                new Object[] {io.github.ningpp.compat.TypeCode.Int32}, long.class);

        assertEquals(9L, row[0]);
    }

    @Test
    public void coerceLeavesMatchingObjectUntouched() {
        Object marker = new Object();

        Object[] row = XunitArguments.coerce(new Object[] {marker}, Object.class);

        assertSame(marker, row[0]);
    }
}

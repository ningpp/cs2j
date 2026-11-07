package csharp.xunit;

import org.junit.jupiter.api.Test;

import static org.junit.jupiter.api.Assertions.assertArrayEquals;
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
}

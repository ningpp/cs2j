package csharp.xunit;

import java.util.Arrays;

/**
 * JUnit 5 counterpart of xUnit's optional theory-parameter padding.
 *
 * <p>xUnit fills parameters a MemberData/ClassData row omits from the declared
 * default value ({@code int maxDepth = 64}); JUnit's {@code @ParameterizedTest}
 * supplies exactly the arguments of each row and fails the invocation with
 * {@code ParameterResolutionException} when the row is short. MemberData rows are
 * only known at run time, so the converted provider pads them through this
 * helper.</p>
 */
public final class XunitArguments {
    private XunitArguments() {
    }

    /**
     * Pads a theory row with the declared defaults of its trailing optional
     * parameters. A row that already supplies them is returned unchanged.
     *
     * @param row The row produced by the MemberData/ClassData provider.
     * @param firstOptionalIndex Index of the first optional parameter.
     * @param defaults Default expressions of the optional parameters, in order.
     * @return The row, padded to the method arity when it was short.
     */
    public static Object[] pad(Object[] row, int firstOptionalIndex, Object... defaults) {
        int arity = firstOptionalIndex + defaults.length;
        if (row.length >= arity) {
            return row;
        }
        Object[] padded = Arrays.copyOf(row, arity);
        for (int i = Math.max(row.length, firstOptionalIndex); i < arity; i++) {
            padded[i] = defaults[i - firstOptionalIndex];
        }
        return padded;
    }
}

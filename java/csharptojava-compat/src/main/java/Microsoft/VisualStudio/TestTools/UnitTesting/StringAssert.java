package Microsoft.VisualStudio.TestTools.UnitTesting;

/**
 * Minimal MSTest StringAssert compatibility surface used by generated tests.
 */
public final class StringAssert {

    private StringAssert() {
    }

    public static void startsWith(String value, String prefix) {
        startsWith(value, prefix, "StringAssert.StartsWith failed.");
    }

    public static void startsWith(String value, String prefix, String message) {
        if (value == null || !value.startsWith(prefix)) {
            throw new AssertionError(message + " Expected prefix <" + prefix + "> but was <" + value + ">.");
        }
    }

    public static void endsWith(String value, String suffix) {
        endsWith(value, suffix, "StringAssert.EndsWith failed.");
    }

    public static void endsWith(String value, String suffix, String message) {
        if (value == null || !value.endsWith(suffix)) {
            throw new AssertionError(message + " Expected suffix <" + suffix + "> but was <" + value + ">.");
        }
    }

    public static void contains(String value, String substring) {
        contains(value, substring, "StringAssert.Contains failed.");
    }

    public static void contains(String value, String substring, String message) {
        if (value == null || !value.contains(substring)) {
            throw new AssertionError(message + " Expected substring <" + substring + "> but was <" + value + ">.");
        }
    }
}

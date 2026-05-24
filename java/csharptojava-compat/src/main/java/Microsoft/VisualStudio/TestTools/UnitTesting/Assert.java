package Microsoft.VisualStudio.TestTools.UnitTesting;

import java.util.Objects;
import java.util.function.Supplier;
import io.github.ningpp.compat.CultureInfo;
import io.github.ningpp.compat.IEqualityComparer;
import io.github.ningpp.compat.ObjectHolder;

public final class Assert {
    private Assert() {}

    // ── AreSame ───────────────────────────────────────────────────────

    public static void areSame(Object expected, Object actual) {
        areSame(expected, actual, (String) null);
    }

    public static void areSame(Object expected, Object actual, String message) {
        if (expected != actual) {
            fail(format(message, "Expected references to be identical."));
        }
    }

    public static void areSame(Object expected, Object actual, String message, Object... args) {
        areSame(expected, actual, formatMessage(message, args));
    }

    // ── AreNotSame ────────────────────────────────────────────────────

    public static void areNotSame(Object notExpected, Object actual) {
        areNotSame(notExpected, actual, (String) null);
    }

    public static void areNotSame(Object notExpected, Object actual, String message) {
        if (notExpected == actual) {
            fail(format(message, "Expected references to be different."));
        }
    }

    public static void areNotSame(Object notExpected, Object actual, String message, Object... args) {
        areNotSame(notExpected, actual, formatMessage(message, args));
    }

    // ── AreEqual ──────────────────────────────────────────────────────
    // Object overloads (also serve as generic <T> due to erasure)

    public static void areEqual(Object expected, Object actual) {
        areEqual(expected, actual, (String) null);
    }

    public static void areEqual(Object expected, Object actual, String message) {
        if (!equals(expected, actual)) {
            fail(format(message, "Expected <" + expected + "> but was <" + actual + ">."));
        }
    }

    public static void areEqual(Object expected, Object actual, String message, Object... args) {
        areEqual(expected, actual, formatMessage(message, args));
    }

    // Double delta overloads

    public static void areEqual(double expected, double actual, double delta) {
        areEqual(expected, actual, delta, (String) null);
    }

    public static void areEqual(double expected, double actual, double delta, String message) {
        if (Double.isNaN(expected) && Double.isNaN(actual)) {
            return;
        }
        if (Math.abs(expected - actual) > delta) {
            fail(format(message, "Expected <" + expected + "> but was <" + actual + ">."));
        }
    }

    public static void areEqual(double expected, double actual, double delta, String message, Object... args) {
        areEqual(expected, actual, delta, formatMessage(message, args));
    }

    // Float delta overloads

    public static void areEqual(float expected, float actual, float delta) {
        areEqual(expected, actual, delta, (String) null);
    }

    public static void areEqual(float expected, float actual, float delta, String message) {
        if (Float.isNaN(expected) && Float.isNaN(actual)) {
            return;
        }
        if (Math.abs(expected - actual) > delta) {
            fail(format(message, "Expected <" + expected + "> but was <" + actual + ">."));
        }
    }

    public static void areEqual(float expected, float actual, float delta, String message, Object... args) {
        areEqual(expected, actual, delta, formatMessage(message, args));
    }

    // Long delta overloads

    public static void areEqual(long expected, long actual, long delta) {
        areEqual(expected, actual, delta, (String) null);
    }

    public static void areEqual(long expected, long actual, long delta, String message) {
        if (Math.abs(expected - actual) > delta) {
            fail(format(message, "Expected <" + expected + "> but was <" + actual + ">."));
        }
    }

    public static void areEqual(long expected, long actual, long delta, String message, Object... args) {
        areEqual(expected, actual, delta, formatMessage(message, args));
    }

    // String ignoreCase overloads (without CultureInfo)

    public static void areEqual(String expected, String actual, boolean ignoreCase) {
        areEqual(expected, actual, ignoreCase, CultureInfo.getCurrentCulture(), null);
    }

    public static void areEqual(String expected, String actual, boolean ignoreCase, String message) {
        areEqual(expected, actual, ignoreCase, CultureInfo.getCurrentCulture(), message);
    }

    public static void areEqual(String expected, String actual, boolean ignoreCase, String message, Object... args) {
        areEqual(expected, actual, ignoreCase, CultureInfo.getCurrentCulture(), formatMessage(message, args));
    }

    // String ignoreCase overloads with CultureInfo

    public static void areEqual(String expected, String actual, boolean ignoreCase, CultureInfo culture) {
        areEqual(expected, actual, ignoreCase, culture, (String) null);
    }

    public static void areEqual(String expected, String actual, boolean ignoreCase, CultureInfo culture, String message) {
        boolean equal = ignoreCase
            ? (expected == null ? actual == null : actual != null && expected.equalsIgnoreCase(actual))
            : Objects.equals(expected, actual);
        if (!equal) {
            fail(format(message, "Expected <" + expected + "> but was <" + actual + ">."));
        }
    }

    public static void areEqual(String expected, String actual, boolean ignoreCase, CultureInfo culture, String message, Object... args) {
        areEqual(expected, actual, ignoreCase, culture, formatMessage(message, args));
    }

    // IEqualityComparer overloads

    public static <T> void areEqual(T expected, T actual, IEqualityComparer<T> comparer) {
        areEqual(expected, actual, comparer, (String) null);
    }

    public static <T> void areEqual(T expected, T actual, IEqualityComparer<T> comparer, String message) {
        if (comparer.equals(expected, actual)) {
            return;
        }
        fail(format(message, "Expected <" + expected + "> but was <" + actual + ">."));
    }

    public static <T> void areEqual(T expected, T actual, IEqualityComparer<T> comparer, String message, Object... args) {
        areEqual(expected, actual, comparer, formatMessage(message, args));
    }

    // ── AreNotEqual ───────────────────────────────────────────────────

    public static void areNotEqual(Object notExpected, Object actual) {
        areNotEqual(notExpected, actual, (String) null);
    }

    public static void areNotEqual(Object notExpected, Object actual, String message) {
        if (equals(notExpected, actual)) {
            fail(format(message, "Did not expect <" + actual + ">."));
        }
    }

    public static void areNotEqual(Object notExpected, Object actual, String message, Object... args) {
        areNotEqual(notExpected, actual, formatMessage(message, args));
    }

    // Double delta overloads

    public static void areNotEqual(double notExpected, double actual, double delta) {
        areNotEqual(notExpected, actual, delta, (String) null);
    }

    public static void areNotEqual(double notExpected, double actual, double delta, String message) {
        if (Double.isNaN(notExpected) && Double.isNaN(actual)) {
            fail(format(message, "Did not expect <" + actual + ">."));
            return;
        }
        if (Math.abs(notExpected - actual) > delta) {
            return;
        }
        fail(format(message, "Did not expect <" + actual + ">."));
    }

    public static void areNotEqual(double notExpected, double actual, double delta, String message, Object... args) {
        areNotEqual(notExpected, actual, delta, formatMessage(message, args));
    }

    // Float delta overloads

    public static void areNotEqual(float notExpected, float actual, float delta) {
        areNotEqual(notExpected, actual, delta, (String) null);
    }

    public static void areNotEqual(float notExpected, float actual, float delta, String message) {
        if (Float.isNaN(notExpected) && Float.isNaN(actual)) {
            fail(format(message, "Did not expect <" + actual + ">."));
            return;
        }
        if (Math.abs(notExpected - actual) > delta) {
            return;
        }
        fail(format(message, "Did not expect <" + actual + ">."));
    }

    public static void areNotEqual(float notExpected, float actual, float delta, String message, Object... args) {
        areNotEqual(notExpected, actual, delta, formatMessage(message, args));
    }

    // Long delta overloads

    public static void areNotEqual(long notExpected, long actual, long delta) {
        areNotEqual(notExpected, actual, delta, (String) null);
    }

    public static void areNotEqual(long notExpected, long actual, long delta, String message) {
        if (Math.abs(notExpected - actual) > delta) {
            return;
        }
        fail(format(message, "Did not expect <" + actual + ">."));
    }

    public static void areNotEqual(long notExpected, long actual, long delta, String message, Object... args) {
        areNotEqual(notExpected, actual, delta, formatMessage(message, args));
    }

    // String ignoreCase overloads (without CultureInfo)

    public static void areNotEqual(String notExpected, String actual, boolean ignoreCase) {
        areNotEqual(notExpected, actual, ignoreCase, CultureInfo.getCurrentCulture(), null);
    }

    public static void areNotEqual(String notExpected, String actual, boolean ignoreCase, String message) {
        areNotEqual(notExpected, actual, ignoreCase, CultureInfo.getCurrentCulture(), message);
    }

    public static void areNotEqual(String notExpected, String actual, boolean ignoreCase, String message, Object... args) {
        areNotEqual(notExpected, actual, ignoreCase, CultureInfo.getCurrentCulture(), formatMessage(message, args));
    }

    // String ignoreCase overloads with CultureInfo

    public static void areNotEqual(String notExpected, String actual, boolean ignoreCase, CultureInfo culture) {
        areNotEqual(notExpected, actual, ignoreCase, culture, (String) null);
    }

    public static void areNotEqual(String notExpected, String actual, boolean ignoreCase, CultureInfo culture, String message) {
        boolean equal = ignoreCase
            ? (notExpected == null ? actual == null : actual != null && notExpected.equalsIgnoreCase(actual))
            : Objects.equals(notExpected, actual);
        if (equal) {
            fail(format(message, "Did not expect <" + actual + ">."));
        }
    }

    public static void areNotEqual(String notExpected, String actual, boolean ignoreCase, CultureInfo culture, String message, Object... args) {
        areNotEqual(notExpected, actual, ignoreCase, culture, formatMessage(message, args));
    }

    // IEqualityComparer overloads

    public static <T> void areNotEqual(T notExpected, T actual, IEqualityComparer<T> comparer) {
        areNotEqual(notExpected, actual, comparer, (String) null);
    }

    public static <T> void areNotEqual(T notExpected, T actual, IEqualityComparer<T> comparer, String message) {
        if (!comparer.equals(notExpected, actual)) {
            return;
        }
        fail(format(message, "Did not expect <" + actual + ">."));
    }

    public static <T> void areNotEqual(T notExpected, T actual, IEqualityComparer<T> comparer, String message, Object... args) {
        areNotEqual(notExpected, actual, comparer, formatMessage(message, args));
    }

    // ── Equals ────────────────────────────────────────────────────────
    // Handles mixed Number types (e.g., Integer vs Double) via doubleValue comparison.

    public static boolean equals(Object objA, Object objB) {
        if (Objects.equals(objA, objB)) {
            return true;
        }
        if (objA instanceof Number && objB instanceof Number) {
            double a = ((Number) objA).doubleValue();
            double b = ((Number) objB).doubleValue();
            if (Double.isNaN(a) && Double.isNaN(b)) {
                return true;
            }
            return a == b;
        }
        return false;
    }

    // ── IsTrue ────────────────────────────────────────────────────────

    public static void isTrue(boolean condition) {
        isTrue(condition, (String) null);
    }

    public static void isTrue(boolean condition, String message) {
        if (!condition) {
            fail(format(message, "Expected condition to be true."));
        }
    }

    public static void isTrue(boolean condition, String message, Object... args) {
        isTrue(condition, formatMessage(message, args));
    }

    public static void isTrue(Boolean condition) {
        isTrue(condition, (String) null);
    }

    public static void isTrue(Boolean condition, String message) {
        if (!Boolean.TRUE.equals(condition)) {
            fail(format(message, "Expected condition to be true."));
        }
    }

    public static void isTrue(Boolean condition, String message, Object... args) {
        isTrue(condition, formatMessage(message, args));
    }

    // ── IsFalse ───────────────────────────────────────────────────────

    public static void isFalse(boolean condition) {
        isFalse(condition, (String) null);
    }

    public static void isFalse(boolean condition, String message) {
        if (condition) {
            fail(format(message, "Expected condition to be false."));
        }
    }

    public static void isFalse(boolean condition, String message, Object... args) {
        isFalse(condition, formatMessage(message, args));
    }

    public static void isFalse(Boolean condition) {
        isFalse(condition, (String) null);
    }

    public static void isFalse(Boolean condition, String message) {
        if (!Boolean.FALSE.equals(condition)) {
            fail(format(message, "Expected condition to be false."));
        }
    }

    public static void isFalse(Boolean condition, String message, Object... args) {
        isFalse(condition, formatMessage(message, args));
    }

    // ── IsNull ────────────────────────────────────────────────────────

    public static void isNull(Object value) {
        isNull(value, (String) null);
    }

    public static void isNull(Object value, String message) {
        if (value != null) {
            fail(format(message, "Expected value to be null."));
        }
    }

    public static void isNull(Object value, String message, Object... args) {
        isNull(value, formatMessage(message, args));
    }

    // ── IsNotNull ─────────────────────────────────────────────────────

    public static void isNotNull(Object value) {
        isNotNull(value, (String) null);
    }

    public static void isNotNull(Object value, String message) {
        if (value == null) {
            fail(format(message, "Expected value to be non-null."));
        }
    }

    public static void isNotNull(Object value, String message, Object... args) {
        isNotNull(value, formatMessage(message, args));
    }

    // ── IsInstanceOfType ──────────────────────────────────────────────

    public static void isInstanceOfType(Object value, Class<?> expectedType) {
        isInstanceOfType(value, expectedType, (String) null);
    }

    public static void isInstanceOfType(Object value, Class<?> expectedType, String message) {
        if (expectedType == null) {
            fail(format(message, "Expected type cannot be null."));
            return;
        }
        if (value == null || !expectedType.isInstance(value)) {
            fail(format(message, "Expected value of type <" + expectedType.getName()
                + "> but was <" + (value == null ? "null" : value.getClass().getName()) + ">."));
        }
    }

    public static void isInstanceOfType(Object value, Class<?> expectedType, String message, Object... args) {
        isInstanceOfType(value, expectedType, formatMessage(message, args));
    }

    public static <T> void isInstanceOfType(Object value, Class<T> expectedType, ObjectHolder<T> instance) {
        isInstanceOfType(value, expectedType, instance, (String) null);
    }

    public static <T> void isInstanceOfType(Object value, Class<T> expectedType, ObjectHolder<T> instance, String message) {
        isInstanceOfType(value, expectedType, message);
        if (instance != null) {
            instance.value = expectedType.cast(value);
        }
    }

    public static <T> void isInstanceOfType(Object value, Class<T> expectedType, ObjectHolder<T> instance, String message, Object... args) {
        isInstanceOfType(value, expectedType, instance, formatMessage(message, args));
    }

    // Convenience overloads with type witness only (no Class<?> param needed for generic case)

    public static <T> void isInstanceOfType(Object value, ObjectHolder<T> instance) {
        isInstanceOfType(value, instance, (String) null);
    }

    public static <T> void isInstanceOfType(Object value, ObjectHolder<T> instance, String message) {
        if (instance == null) {
            fail(format(message, "Instance holder cannot be null."));
            return;
        }
        if (value == null) {
            fail(format(message, "Expected value of type <" + instance.value
                + "> but was null."));
            return;
        }
        try {
            @SuppressWarnings("unchecked")
            T casted = (T) value;
            instance.value = casted;
        } catch (ClassCastException e) {
            fail(format(message, "Expected value of type <" + (instance.value != null
                ? instance.value.getClass().getName() : "unknown")
                + "> but was <" + value.getClass().getName() + ">."));
        }
    }

    public static <T> void isInstanceOfType(Object value, ObjectHolder<T> instance, String message, Object... args) {
        isInstanceOfType(value, instance, formatMessage(message, args));
    }

    // ── IsNotInstanceOfType ───────────────────────────────────────────

    public static void isNotInstanceOfType(Object value, Class<?> wrongType) {
        isNotInstanceOfType(value, wrongType, (String) null);
    }

    public static void isNotInstanceOfType(Object value, Class<?> wrongType, String message) {
        if (wrongType == null) {
            fail(format(message, "Wrong type cannot be null."));
            return;
        }
        if (value != null && wrongType.isInstance(value)) {
            fail(format(message, "Did not expect value of type <" + wrongType.getName() + ">."));
        }
    }

    public static void isNotInstanceOfType(Object value, Class<?> wrongType, String message, Object... args) {
        isNotInstanceOfType(value, wrongType, formatMessage(message, args));
    }

    // ── ThrowsException ───────────────────────────────────────────────

    public static <T extends Throwable> T throwsException(Class<T> exceptionType, Runnable action) {
        return throwsException(exceptionType, action, (String) null);
    }

    public static <T extends Throwable> T throwsException(Class<T> exceptionType, Runnable action, String message) {
        try {
            action.run();
        } catch (Throwable e) {
            if (exceptionType.isInstance(e)) {
                return exceptionType.cast(e);
            }
            throw e;
        }
        fail(format(message, "No exception thrown. Expected <" + exceptionType.getName() + ">."));
        return null; // unreachable
    }

    public static <T extends Throwable> T throwsException(Class<T> exceptionType, Runnable action, String message, Object... args) {
        return throwsException(exceptionType, action, formatMessage(message, args));
    }

    public static <T extends Throwable> T throwsException(Class<T> exceptionType, Supplier<?> action) {
        return throwsException(exceptionType, action, (String) null);
    }

    public static <T extends Throwable> T throwsException(Class<T> exceptionType, Supplier<?> action, String message) {
        try {
            action.get();
        } catch (Throwable e) {
            if (exceptionType.isInstance(e)) {
                return exceptionType.cast(e);
            }
            throw e;
        }
        fail(format(message, "No exception thrown. Expected <" + exceptionType.getName() + ">."));
        return null; // unreachable
    }

    public static <T extends Throwable> T throwsException(Class<T> exceptionType, Supplier<?> action, String message, Object... args) {
        return throwsException(exceptionType, action, formatMessage(message, args));
    }

    // ── Fail ──────────────────────────────────────────────────────────

    public static void fail() {
        fail((String) null);
    }

    public static void fail(String message) {
        throw new UnitTestAssertException(message == null ? "Assertion failed." : message);
    }

    public static void fail(String message, Object... args) {
        fail(formatMessage(message, args));
    }

    // ── Inconclusive ──────────────────────────────────────────────────

    public static void inconclusive() {
        inconclusive((String) null);
    }

    public static void inconclusive(String message) {
        throw new AssertInconclusiveException(
            message == null ? "Test is inconclusive." : message);
    }

    public static void inconclusive(String message, Object... args) {
        inconclusive(formatMessage(message, args));
    }

    // ── ReplaceNullChars ──────────────────────────────────────────────

    public static String replaceNullChars(String input) {
        if (input == null) {
            return null;
        }
        return input.replace("\0", "\\0");
    }

    // ── Internal helpers ──────────────────────────────────────────────

    private static String format(String message, String fallback) {
        return message == null || message.isEmpty() ? fallback : message;
    }

    private static String formatMessage(String message, Object... args) {
        if (message == null || args == null || args.length == 0) {
            return message;
        }
        String format = message;
        for (int i = 0; i < args.length; i++) {
            format = format.replace("{" + i + "}", "%s");
        }
        return String.format(format, args);
    }
}

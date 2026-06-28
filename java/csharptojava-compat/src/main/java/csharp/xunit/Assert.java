package csharp.xunit;

import java.util.ArrayList;
import java.util.Collection;
import java.util.Collections;
import java.util.Comparator;
import java.util.HashSet;
import java.util.Iterator;
import java.util.List;
import java.util.Objects;
import java.util.Set;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.CompletionException;
import java.util.function.Consumer;
import java.util.function.Predicate;
import java.util.function.Supplier;
import java.util.regex.Pattern;
import io.github.ningpp.compat.StringComparison;

/**
 * Xunit.Assert compatibility layer for Java.
 * <p>
 * All public methods are derived from C# Xunit.Assert (v2.9.3) via reflection.
 * Method names use camelCase per Java convention, matching the converter's
 * PascalCase → camelCase mapping (e.g. Equal → equal, IsType → isType).
 */
public final class Assert {

    private Assert() {}

    // ── All ──────────────────────────────────────────────────────────
    // Reflection: void All<T>(IEnumerable<T> collection, Action<T> action)
    //            void All<T>(IEnumerable<T> collection, Action<T, int> action)

    public static <T> void all(Iterable<T> collection, Consumer<T> action) {
        int index = 0;
        List<String> errors = new ArrayList<>();
        for (T item : collection) {
            try {
                action.accept(item);
            } catch (AssertionError e) {
                errors.add("[" + index + "]: Item: " + item + " - " + e.getMessage());
            }
            index++;
        }
        if (!errors.isEmpty()) {
            throw new AssertionError("Assert.all() failure: " + errors.size()
                + " out of " + index + " items failed.\n" + String.join("\n", errors));
        }
    }

    // ── Collection ───────────────────────────────────────────────────
    // Reflection: void Collection<T>(IEnumerable<T> collection, params Action<T>[] elementInspectors)

    @SafeVarargs
    public static <T> void collection(Iterable<T> collection, Consumer<T>... elementInspectors) {
        List<T> items = new ArrayList<>();
        for (T item : collection) {
            items.add(item);
        }
        if (items.size() != elementInspectors.length) {
            throw new AssertionError("Assert.collection() failure: Mismatched item count\n"
                + "Expected count: " + elementInspectors.length + "\n"
                + "Actual count: " + items.size());
        }
        for (int i = 0; i < elementInspectors.length; i++) {
            try {
                elementInspectors[i].accept(items.get(i));
            } catch (AssertionError e) {
                throw new AssertionError("Assert.collection() failure: Item comparison failure\n"
                    + "Error at index " + i + ": " + e.getMessage(), e);
            }
        }
    }

    // ── Contains ─────────────────────────────────────────────────────
    // Reflection: void Contains<T>(T expected, IEnumerable<T> collection)
    //            void Contains<T>(T expected, IEnumerable<T> collection, IEqualityComparer<T> comparer)
    //            void Contains<T>(IEnumerable<T> collection, Predicate<T> filter)
    //            void Contains(string expectedSubstring, string actualString)
    //            void Contains(string expectedSubstring, string actualString, StringComparison comparisonType)
    //            void Contains<T>(T expected, ISet<T> set)
    //            void Contains<TKey, TValue>(TKey expected, IDictionary<TKey, TValue> dictionary)

    public static <T> void contains(T expected, Iterable<T> collection) {
        for (T item : collection) {
            if (Objects.equals(expected, item)) {
                return;
            }
        }
        throw new AssertionError("Assert.contains() failure: Item not found in collection\nNot found: " + expected);
    }

    public static <T> void contains(T expected, Iterable<T> collection, java.util.function.BiPredicate<T, T> comparer) {
        for (T item : collection) {
            if (comparer.test(expected, item)) {
                return;
            }
        }
        throw new AssertionError("Assert.contains() failure: Item not found in collection\nNot found: " + expected);
    }

    public static <T> void contains(Iterable<T> collection, Predicate<T> filter) {
        for (T item : collection) {
            if (filter.test(item)) {
                return;
            }
        }
        throw new AssertionError("Assert.contains() failure: No matching element found in collection");
    }

    public static void contains(String expectedSubstring, String actualString) {
        if (actualString == null || !actualString.contains(expectedSubstring)) {
            throw new AssertionError("Assert.contains() failure: Sub-string not found\n"
                + "Expected substring: " + expectedSubstring + "\n"
                + "Actual string: " + actualString);
        }
    }

    public static void contains(String expectedSubstring, String actualString, boolean ignoreCase) {
        if (actualString == null) {
            throw new AssertionError("Assert.contains() failure: Sub-string not found\nActual string was null");
        }
        if (ignoreCase) {
            if (!actualString.toLowerCase().contains(expectedSubstring.toLowerCase())) {
                throw new AssertionError("Assert.contains() failure: Sub-string not found (ignoreCase)\n"
                    + "Expected substring: " + expectedSubstring + "\n"
                    + "Actual string: " + actualString);
            }
        } else {
            contains(expectedSubstring, actualString);
        }
    }

    public static void contains(String expectedSubstring, String actualString, StringComparison comparisonType) {
        contains(expectedSubstring, actualString, isIgnoreCase(comparisonType));
    }

    public static <T> void contains(T expected, Set<T> set) {
        if (!set.contains(expected)) {
            throw new AssertionError("Assert.contains() failure: Item not found in set\nNot found: " + expected);
        }
    }

    public static <K, V> void contains(K expectedKey, java.util.Map<K, V> dictionary) {
        if (!dictionary.containsKey(expectedKey)) {
            throw new AssertionError("Assert.contains() failure: Key not found in dictionary\nNot found: " + expectedKey);
        }
    }

    // ── DoesNotContain ───────────────────────────────────────────────
    // Reflection: void DoesNotContain<T>(T expected, IEnumerable<T> collection)
    //            void DoesNotContain<T>(T expected, IEnumerable<T> collection, IEqualityComparer<T> comparer)
    //            void DoesNotContain<T>(IEnumerable<T> collection, Predicate<T> filter)
    //            void DoesNotContain(string expectedSubstring, string actualString)
    //            void DoesNotContain(string expectedSubstring, string actualString, StringComparison comparisonType)
    //            void DoesNotContain<T>(T expected, ISet<T> set)
    //            void DoesNotContain<TKey, TValue>(TKey expected, IDictionary<TKey, TValue> dictionary)

    public static <T> void doesNotContain(T expected, Iterable<T> collection) {
        for (T item : collection) {
            if (Objects.equals(expected, item)) {
                throw new AssertionError("Assert.doesNotContain() failure: Item found in collection\nFound: " + expected);
            }
        }
    }

    public static <T> void doesNotContain(T expected, Iterable<T> collection, java.util.function.BiPredicate<T, T> comparer) {
        for (T item : collection) {
            if (comparer.test(expected, item)) {
                throw new AssertionError("Assert.doesNotContain() failure: Item found in collection\nFound: " + expected);
            }
        }
    }

    public static <T> void doesNotContain(Iterable<T> collection, Predicate<T> filter) {
        for (T item : collection) {
            if (filter.test(item)) {
                throw new AssertionError("Assert.doesNotContain() failure: Matching element found in collection");
            }
        }
    }

    public static void doesNotContain(String expectedSubstring, String actualString) {
        if (actualString != null && actualString.contains(expectedSubstring)) {
            throw new AssertionError("Assert.doesNotContain() failure: Sub-string found\n"
                + "Did not expect substring: " + expectedSubstring + "\n"
                + "Actual string: " + actualString);
        }
    }

    public static void doesNotContain(String expectedSubstring, String actualString, boolean ignoreCase) {
        if (actualString != null) {
            if (ignoreCase) {
                if (actualString.toLowerCase().contains(expectedSubstring.toLowerCase())) {
                    throw new AssertionError("Assert.doesNotContain() failure: Sub-string found (ignoreCase)\n"
                        + "Did not expect substring: " + expectedSubstring + "\n"
                        + "Actual string: " + actualString);
                }
            } else {
                doesNotContain(expectedSubstring, actualString);
            }
        }
    }

    public static void doesNotContain(String expectedSubstring, String actualString, StringComparison comparisonType) {
        doesNotContain(expectedSubstring, actualString, isIgnoreCase(comparisonType));
    }

    public static <T> void doesNotContain(T expected, Set<T> set) {
        if (set.contains(expected)) {
            throw new AssertionError("Assert.doesNotContain() failure: Item found in set\nFound: " + expected);
        }
    }

    public static <K, V> void doesNotContain(K expectedKey, java.util.Map<K, V> dictionary) {
        if (dictionary.containsKey(expectedKey)) {
            throw new AssertionError("Assert.doesNotContain() failure: Key found in dictionary\nFound: " + expectedKey);
        }
    }

    // ── DoesNotMatch ─────────────────────────────────────────────────
    // Reflection: void DoesNotMatch(string expectedRegexPattern, string actualString)
    //            void DoesNotMatch(Regex expectedRegex, string actualString)

    public static void doesNotMatch(String expectedRegexPattern, String actualString) {
        if (actualString != null && Pattern.compile(expectedRegexPattern).matcher(actualString).find()) {
            throw new AssertionError("Assert.doesNotMatch() failure: Regex matched\n"
                + "Pattern: " + expectedRegexPattern + "\n"
                + "Actual: " + actualString);
        }
    }

    public static void doesNotMatch(Pattern expectedRegex, String actualString) {
        if (actualString != null && expectedRegex.matcher(actualString).find()) {
            throw new AssertionError("Assert.doesNotMatch() failure: Regex matched\n"
                + "Pattern: " + expectedRegex.pattern() + "\n"
                + "Actual: " + actualString);
        }
    }

    // ── Empty ────────────────────────────────────────────────────────
    // Reflection: void Empty(IEnumerable collection)
    //            void Empty(string value)

    public static void empty(Iterable<?> collection) {
        if (collection != null && collection.iterator().hasNext()) {
            throw new AssertionError("Assert.empty() failure: Collection was not empty");
        }
    }

    public static void empty(String value) {
        if (value != null && !value.isEmpty()) {
            throw new AssertionError("Assert.empty() failure: String was not empty\nActual: " + value);
        }
    }

    // ── EndsWith ─────────────────────────────────────────────────────
    // Reflection: void EndsWith(string expectedEndString, string actualString)
    //            void EndsWith(string expectedEndString, string actualString, StringComparison comparisonType)

    public static void endsWith(String expectedEndString, String actualString) {
        if (actualString == null || !actualString.endsWith(expectedEndString)) {
            throw new AssertionError("Assert.endsWith() failure\n"
                + "Expected end: " + expectedEndString + "\n"
                + "Actual: " + actualString);
        }
    }

    public static void endsWith(String expectedEndString, String actualString, boolean ignoreCase) {
        if (actualString == null) {
            throw new AssertionError("Assert.endsWith() failure: Actual string was null");
        }
        if (ignoreCase) {
            if (!actualString.toLowerCase().endsWith(expectedEndString.toLowerCase())) {
                throw new AssertionError("Assert.endsWith() failure (ignoreCase)\n"
                    + "Expected end: " + expectedEndString + "\n"
                    + "Actual: " + actualString);
            }
        } else {
            endsWith(expectedEndString, actualString);
        }
    }

    public static void endsWith(String expectedEndString, String actualString, StringComparison comparisonType) {
        endsWith(expectedEndString, actualString, isIgnoreCase(comparisonType));
    }

    // ── Equal ────────────────────────────────────────────────────────
    // Reflection: void Equal<T>(T expected, T actual)
    //            void Equal<T>(T expected, T actual, IEqualityComparer<T> comparer)
    //            void Equal<T>(T expected, T actual, Func<T, T, bool> comparer)
    //            void Equal(double expected, double actual, double tolerance)
    //            void Equal(double expected, double actual, int precision)
    //            void Equal(float expected, float actual, float tolerance)
    //            void Equal(float expected, float actual, int precision)
    //            void Equal(decimal expected, decimal actual, int precision)
    //            void Equal(string expected, string actual, bool ignoreCase, ...)
    //            void Equal(IEnumerable<T> expected, IEnumerable<T> actual)
    //            void Equal(IEnumerable<T> expected, IEnumerable<T> actual, IEqualityComparer<T> comparer)

    public static void equal(Object expected, Object actual) {
        if (!objectsEqual(expected, actual)) {
            throw new AssertionError("Assert.equal() failure: Values differ\n"
                + "Expected: " + expected + "\n"
                + "Actual: " + actual);
        }
    }

    public static <T> void equal(T expected, T actual, java.util.function.BiPredicate<T, T> comparer) {
        if (!comparer.test(expected, actual)) {
            throw new AssertionError("Assert.equal() failure: Values differ (custom comparer)\n"
                + "Expected: " + expected + "\n"
                + "Actual: " + actual);
        }
    }

    public static void equal(double expected, double actual, double tolerance) {
        if (Double.isNaN(expected) && Double.isNaN(actual)) {
            return;
        }
        if (Math.abs(expected - actual) > tolerance) {
            throw new AssertionError("Assert.equal() failure: Values differ within tolerance\n"
                + "Expected: " + expected + " ± " + tolerance + "\n"
                + "Actual: " + actual);
        }
    }

    public static void equal(double expected, double actual, int precision) {
        double multiplier = Math.pow(10, precision);
        double expectedRounded = Math.round(expected * multiplier) / multiplier;
        double actualRounded = Math.round(actual * multiplier) / multiplier;
        if (Double.isNaN(expected) && Double.isNaN(actual)) {
            return;
        }
        if (expectedRounded != actualRounded) {
            throw new AssertionError("Assert.equal() failure: Values differ at precision " + precision + "\n"
                + "Expected: " + expected + "\n"
                + "Actual: " + actual);
        }
    }

    public static void equal(float expected, float actual, float tolerance) {
        if (Float.isNaN(expected) && Float.isNaN(actual)) {
            return;
        }
        if (Math.abs(expected - actual) > tolerance) {
            throw new AssertionError("Assert.equal() failure: Values differ within tolerance\n"
                + "Expected: " + expected + " ± " + tolerance + "\n"
                + "Actual: " + actual);
        }
    }

    public static void equal(float expected, float actual, int precision) {
        double multiplier = Math.pow(10, precision);
        float expectedRounded = Math.round(expected * multiplier) / (float) multiplier;
        float actualRounded = Math.round(actual * multiplier) / (float) multiplier;
        if (Float.isNaN(expected) && Float.isNaN(actual)) {
            return;
        }
        if (expectedRounded != actualRounded) {
            throw new AssertionError("Assert.equal() failure: Values differ at precision " + precision + "\n"
                + "Expected: " + expected + "\n"
                + "Actual: " + actual);
        }
    }

    public static void equal(String expected, String actual, boolean ignoreCase) {
        boolean equal;
        if (ignoreCase) {
            equal = expected == null ? actual == null : actual != null && expected.equalsIgnoreCase(actual);
        } else {
            equal = Objects.equals(expected, actual);
        }
        if (!equal) {
            throw new AssertionError("Assert.equal() failure: Strings differ\n"
                + "Expected: " + expected + "\n"
                + "Actual: " + actual);
        }
    }

    public static <T> void equal(Iterable<T> expected, Iterable<T> actual) {
        if (expected == null || actual == null) {
            if (expected == actual) return;
            throw new AssertionError("Assert.equal() failure: Values differ\n"
                + "Expected: " + expected + "\n"
                + "Actual: " + actual);
        }
        Iterator<T> expectedIter = expected.iterator();
        Iterator<T> actualIter = actual.iterator();
        int index = 0;
        while (expectedIter.hasNext() && actualIter.hasNext()) {
            T exp = expectedIter.next();
            T act = actualIter.next();
            if (!Objects.equals(exp, act)) {
                throw new AssertionError("Assert.equal() failure: Collections differ at index " + index + "\n"
                    + "Expected: " + exp + "\n"
                    + "Actual: " + act);
            }
            index++;
        }
        if (expectedIter.hasNext() || actualIter.hasNext()) {
            throw new AssertionError("Assert.equal() failure: Collections have different lengths");
        }
    }

    // ── Equals ───────────────────────────────────────────────────────
    // Reflection: bool Equals(object a, object b)

    public static boolean equals(Object a, Object b) {
        return objectsEqual(a, b);
    }

    // ── Equivalent ───────────────────────────────────────────────────
    // Reflection: void Equivalent(object expected, object actual, bool strict)

    public static void equivalent(Object expected, Object actual, boolean strict) {
        if (!objectsEqual(expected, actual)) {
            throw new AssertionError("Assert.equivalent() failure: Values are not equivalent\n"
                + "Expected: " + expected + "\n"
                + "Actual: " + actual);
        }
    }

    // ── Fail ─────────────────────────────────────────────────────────
    // Reflection: void Fail(string message)

    public static void fail(String message) {
        throw new AssertionError(message != null ? message : "Assert.fail() failure");
    }

    public static void fail() {
        throw new AssertionError("Assert.fail() failure");
    }

    // ── False ────────────────────────────────────────────────────────
    // Reflection: void False(bool condition)
    //            void False(bool condition, string userMessage)
    //            void False(Nullable<bool> condition)
    //            void False(Nullable<bool> condition, string userMessage)

    public static void false_(boolean condition) {
        if (condition) {
            throw new AssertionError("Assert.false() failure: Expected condition to be false");
        }
    }

    public static void false_(boolean condition, String userMessage) {
        if (condition) {
            throw new AssertionError(userMessage != null ? userMessage : "Assert.false() failure: Expected condition to be false");
        }
    }

    public static void false_(Boolean condition) {
        if (Boolean.TRUE.equals(condition)) {
            throw new AssertionError("Assert.false() failure: Expected condition to be false");
        }
    }

    public static void false_(Boolean condition, String userMessage) {
        if (Boolean.TRUE.equals(condition)) {
            throw new AssertionError(userMessage != null ? userMessage : "Assert.false() failure: Expected condition to be false");
        }
    }

    // ── InRange ──────────────────────────────────────────────────────
    // Reflection: void InRange<T>(T actual, T low, T high)
    //            void InRange<T>(T actual, T low, T high, IComparer<T> comparer)

    @SuppressWarnings("unchecked")
    public static <T extends Comparable<T>> void inRange(T actual, T low, T high) {
        if (actual == null) {
            throw new AssertionError("Assert.inRange() failure: Value was null");
        }
        if (actual.compareTo(low) < 0 || actual.compareTo(high) > 0) {
            throw new AssertionError("Assert.inRange() failure\n"
                + "Expected: in range [" + low + ", " + high + "]\n"
                + "Actual: " + actual);
        }
    }

    public static <T> void inRange(T actual, T low, T high, Comparator<T> comparer) {
        if (actual == null) {
            throw new AssertionError("Assert.inRange() failure: Value was null");
        }
        if (comparer.compare(actual, low) < 0 || comparer.compare(actual, high) > 0) {
            throw new AssertionError("Assert.inRange() failure (custom comparer)\n"
                + "Expected: in range [" + low + ", " + high + "]\n"
                + "Actual: " + actual);
        }
    }

    // ── IsAssignableFrom ─────────────────────────────────────────────
    // Reflection: T IsAssignableFrom<T>(object object)
    //            void IsAssignableFrom(Type expectedType, object object)

    @SuppressWarnings("unchecked")
    public static <T> T isAssignableFrom(Class<T> expectedType, Object object) {
        if (object == null) {
            throw new AssertionError("Assert.isAssignableFrom() failure: Value was null\n"
                + "Expected type: " + expectedType.getName());
        }
        if (!expectedType.isInstance(object)) {
            throw new AssertionError("Assert.isAssignableFrom() failure\n"
                + "Expected: assignable from " + expectedType.getName() + "\n"
                + "Actual type: " + object.getClass().getName());
        }
        return (T) object;
    }

    // ── IsNotAssignableFrom ──────────────────────────────────────────
    // Reflection: void IsNotAssignableFrom<T>(object object)
    //            void IsNotAssignableFrom(Type expectedType, object object)

    public static void isNotAssignableFrom(Class<?> wrongType, Object object) {
        if (object != null && wrongType.isInstance(object)) {
            throw new AssertionError("Assert.isNotAssignableFrom() failure\n"
                + "Did not expect: assignable from " + wrongType.getName() + "\n"
                + "Actual type: " + object.getClass().getName());
        }
    }

    // ── IsType ───────────────────────────────────────────────────────
    // Reflection: T IsType<T>(object object)
    //            T IsType<T>(object object, bool exactMatch)
    //            void IsType(Type expectedType, object object)
    //            void IsType(Type expectedType, object object, bool exactMatch)

    @SuppressWarnings("unchecked")
    public static <T> T isType(Class<T> expectedType, Object object) {
        if (object == null) {
            throw new AssertionError("Assert.isType() failure: Value was null\n"
                + "Expected type: " + expectedType.getName());
        }
        if (object.getClass() != expectedType) {
            throw new AssertionError("Assert.isType() failure\n"
                + "Expected type: " + expectedType.getName() + "\n"
                + "Actual type: " + object.getClass().getName());
        }
        return (T) object;
    }

    @SuppressWarnings("unchecked")
    public static <T> T isType(Class<T> expectedType, Object object, boolean exactMatch) {
        if (object == null) {
            throw new AssertionError("Assert.isType() failure: Value was null\n"
                + "Expected type: " + expectedType.getName());
        }
        if (exactMatch) {
            if (object.getClass() != expectedType) {
                throw new AssertionError("Assert.isType() failure (exact)\n"
                    + "Expected type: " + expectedType.getName() + "\n"
                    + "Actual type: " + object.getClass().getName());
            }
        } else {
            if (!expectedType.isInstance(object)) {
                throw new AssertionError("Assert.isType() failure (non-exact)\n"
                    + "Expected type: " + expectedType.getName() + "\n"
                    + "Actual type: " + object.getClass().getName());
            }
        }
        return (T) object;
    }

    // ── IsNotType ────────────────────────────────────────────────────
    // Reflection: void IsNotType<T>(object object)
    //            void IsNotType<T>(object object, bool exactMatch)
    //            void IsNotType(Type expectedType, object object)
    //            void IsNotType(Type expectedType, object object, bool exactMatch)

    public static void isNotType(Class<?> wrongType, Object object) {
        if (object != null && object.getClass() == wrongType) {
            throw new AssertionError("Assert.isNotType() failure\n"
                + "Did not expect type: " + wrongType.getName() + "\n"
                + "Actual type: " + object.getClass().getName());
        }
    }

    public static void isNotType(Class<?> wrongType, Object object, boolean exactMatch) {
        if (object != null) {
            if (exactMatch) {
                if (object.getClass() == wrongType) {
                    throw new AssertionError("Assert.isNotType() failure (exact)\n"
                        + "Did not expect type: " + wrongType.getName());
                }
            } else {
                if (wrongType.isInstance(object)) {
                    throw new AssertionError("Assert.isNotType() failure (non-exact)\n"
                        + "Did not expect type: " + wrongType.getName());
                }
            }
        }
    }

    // ── Matches ──────────────────────────────────────────────────────
    // Reflection: void Matches(string expectedRegexPattern, string actualString)
    //            void Matches(Regex expectedRegex, string actualString)

    public static void matches(String expectedRegexPattern, String actualString) {
        if (actualString == null || !Pattern.compile(expectedRegexPattern).matcher(actualString).find()) {
            throw new AssertionError("Assert.matches() failure: Regex did not match\n"
                + "Pattern: " + expectedRegexPattern + "\n"
                + "Actual: " + actualString);
        }
    }

    public static void matches(Pattern expectedRegex, String actualString) {
        if (actualString == null || !expectedRegex.matcher(actualString).find()) {
            throw new AssertionError("Assert.matches() failure: Regex did not match\n"
                + "Pattern: " + expectedRegex.pattern() + "\n"
                + "Actual: " + actualString);
        }
    }

    // ── Multiple ─────────────────────────────────────────────────────
    // Reflection: void Multiple(params Action[] checks)

    public static void multiple(Runnable... checks) {
        List<AssertionError> errors = new ArrayList<>();
        for (int i = 0; i < checks.length; i++) {
            try {
                checks[i].run();
            } catch (AssertionError e) {
                errors.add(e);
            }
        }
        if (!errors.isEmpty()) {
            throw new AssertionError("Assert.multiple() failure: " + errors.size()
                + " of " + checks.length + " checks failed");
        }
    }

    // ── NotEmpty ─────────────────────────────────────────────────────
    // Reflection: void NotEmpty(IEnumerable collection)

    public static void notEmpty(Iterable<?> collection) {
        if (collection == null || !collection.iterator().hasNext()) {
            throw new AssertionError("Assert.notEmpty() failure: Collection was empty");
        }
    }

    // ── NotEqual ─────────────────────────────────────────────────────
    // Reflection: void NotEqual<T>(T expected, T actual)
    //            void NotEqual<T>(T expected, T actual, IEqualityComparer<T> comparer)
    //            void NotEqual<T>(T expected, T actual, Func<T, T, bool> comparer)
    //            void NotEqual(double expected, double actual, double tolerance)
    //            void NotEqual(double expected, double actual, int precision)
    //            void NotEqual(float expected, float actual, float tolerance)
    //            void NotEqual(float expected, float actual, int precision)
    //            void NotEqual(IEnumerable<T> expected, IEnumerable<T> actual)

    public static void notEqual(Object expected, Object actual) {
        if (objectsEqual(expected, actual)) {
            throw new AssertionError("Assert.notEqual() failure: Values are equal\n"
                + "Did not expect: " + expected);
        }
    }

    public static <T> void notEqual(T expected, T actual, java.util.function.BiPredicate<T, T> comparer) {
        if (comparer.test(expected, actual)) {
            throw new AssertionError("Assert.notEqual() failure: Values are equal (custom comparer)\n"
                + "Did not expect: " + expected);
        }
    }

    public static void notEqual(double expected, double actual, double tolerance) {
        if (Double.isNaN(expected) && Double.isNaN(actual)) {
            throw new AssertionError("Assert.notEqual() failure: Both values are NaN");
        }
        if (Math.abs(expected - actual) <= tolerance) {
            throw new AssertionError("Assert.notEqual() failure: Values are within tolerance\n"
                + "Did not expect: " + expected + " ± " + tolerance + "\n"
                + "Actual: " + actual);
        }
    }

    public static void notEqual(double expected, double actual, int precision) {
        double multiplier = Math.pow(10, precision);
        double expectedRounded = Math.round(expected * multiplier) / multiplier;
        double actualRounded = Math.round(actual * multiplier) / multiplier;
        if (expectedRounded == actualRounded) {
            throw new AssertionError("Assert.notEqual() failure: Values are equal at precision " + precision);
        }
    }

    public static void notEqual(float expected, float actual, float tolerance) {
        if (Float.isNaN(expected) && Float.isNaN(actual)) {
            throw new AssertionError("Assert.notEqual() failure: Both values are NaN");
        }
        if (Math.abs(expected - actual) <= tolerance) {
            throw new AssertionError("Assert.notEqual() failure: Values are within tolerance");
        }
    }

    public static void notEqual(float expected, float actual, int precision) {
        double multiplier = Math.pow(10, precision);
        float expectedRounded = Math.round(expected * multiplier) / (float) multiplier;
        float actualRounded = Math.round(actual * multiplier) / (float) multiplier;
        if (expectedRounded == actualRounded) {
            throw new AssertionError("Assert.notEqual() failure: Values are equal at precision " + precision);
        }
    }

    // ── NotInRange ───────────────────────────────────────────────────
    // Reflection: void NotInRange<T>(T actual, T low, T high)
    //            void NotInRange<T>(T actual, T low, T high, IComparer<T> comparer)

    @SuppressWarnings("unchecked")
    public static <T extends Comparable<T>> void notInRange(T actual, T low, T high) {
        if (actual != null && actual.compareTo(low) >= 0 && actual.compareTo(high) <= 0) {
            throw new AssertionError("Assert.notInRange() failure\n"
                + "Did not expect: in range [" + low + ", " + high + "]\n"
                + "Actual: " + actual);
        }
    }

    public static <T> void notInRange(T actual, T low, T high, Comparator<T> comparer) {
        if (actual != null && comparer.compare(actual, low) >= 0 && comparer.compare(actual, high) <= 0) {
            throw new AssertionError("Assert.notInRange() failure (custom comparer)\n"
                + "Did not expect: in range [" + low + ", " + high + "]\n"
                + "Actual: " + actual);
        }
    }

    // ── NotNull ──────────────────────────────────────────────────────
    // Reflection: void NotNull(object object)
    //            T NotNull<T>(Nullable<T> value)

    public static void notNull(Object object) {
        if (object == null) {
            throw new AssertionError("Assert.notNull() failure: Value was null");
        }
    }

    @SuppressWarnings("unchecked")
    public static <T> T notNull(Class<T> expectedType, Object object) {
        if (object == null) {
            throw new AssertionError("Assert.notNull() failure: Value was null");
        }
        return (T) object;
    }

    // ── NotSame ──────────────────────────────────────────────────────
    // Reflection: void NotSame(object expected, object actual)

    public static void notSame(Object expected, Object actual) {
        if (expected == actual) {
            throw new AssertionError("Assert.notSame() failure: References are the same");
        }
    }

    // ── NotStrictEqual ───────────────────────────────────────────────
    // Reflection: void NotStrictEqual<T>(T expected, T actual)

    public static void notStrictEqual(Object expected, Object actual) {
        if (Objects.equals(expected, actual)) {
            throw new AssertionError("Assert.notStrictEqual() failure: Values are strictly equal\n"
                + "Did not expect: " + expected);
        }
    }

    // ── Null ─────────────────────────────────────────────────────────
    // Reflection: void Null(object object)
    //            void Null<T>(Nullable<T> value)

    public static void null_(Object object) {
        if (object != null) {
            throw new AssertionError("Assert.null() failure: Value was not null\nActual: " + object);
        }
    }

    // ── ProperSubset ─────────────────────────────────────────────────
    // Reflection: void ProperSubset(ISet<T> expectedSubset, ISet<T> actual)

    public static <T> void properSubset(Set<T> expectedSubset, Set<T> actual) {
        if (!actual.containsAll(expectedSubset) || expectedSubset.containsAll(actual)) {
            throw new AssertionError("Assert.properSubset() failure: Not a proper subset");
        }
    }

    // ── ProperSuperset ───────────────────────────────────────────────
    // Reflection: void ProperSuperset(ISet<T> expectedSuperset, ISet<T> actual)

    public static <T> void properSuperset(Set<T> expectedSuperset, Set<T> actual) {
        if (!expectedSuperset.containsAll(actual) || actual.containsAll(expectedSuperset)) {
            throw new AssertionError("Assert.properSuperset() failure: Not a proper superset");
        }
    }

    // ── ReferenceEquals ──────────────────────────────────────────────
    // Reflection: bool ReferenceEquals(object a, object b)

    public static boolean referenceEquals(Object a, Object b) {
        return a == b;
    }

    // ── Same ─────────────────────────────────────────────────────────
    // Reflection: void Same(object expected, object actual)

    public static void same(Object expected, Object actual) {
        if (expected != actual) {
            throw new AssertionError("Assert.same() failure: References are not the same\n"
                + "Expected: " + expected + "\n"
                + "Actual: " + actual);
        }
    }

    // ── Single ───────────────────────────────────────────────────────
    // Reflection: T Single<T>(IEnumerable<T> collection)
    //            T Single<T>(IEnumerable<T> collection, Predicate<T> predicate)
    //            object Single(IEnumerable collection)
    //            void Single(IEnumerable collection, object expected)

    public static <T> T single(Iterable<T> collection) {
        Iterator<T> it = collection.iterator();
        if (!it.hasNext()) {
            throw new AssertionError("Assert.single() failure: Collection was empty");
        }
        T result = it.next();
        if (it.hasNext()) {
            throw new AssertionError("Assert.single() failure: Collection contained more than one element");
        }
        return result;
    }

    public static <T> T single(Iterable<T> collection, Predicate<T> predicate) {
        T result = null;
        boolean found = false;
        for (T item : collection) {
            if (predicate.test(item)) {
                if (found) {
                    throw new AssertionError("Assert.single() failure: More than one element matched the predicate");
                }
                result = item;
                found = true;
            }
        }
        if (!found) {
            throw new AssertionError("Assert.single() failure: No element matched the predicate");
        }
        return result;
    }

    // ── StartsWith ───────────────────────────────────────────────────
    // Reflection: void StartsWith(string expectedStartString, string actualString)
    //            void StartsWith(string expectedStartString, string actualString, StringComparison comparisonType)

    public static void startsWith(String expectedStartString, String actualString) {
        if (actualString == null || !actualString.startsWith(expectedStartString)) {
            throw new AssertionError("Assert.startsWith() failure\n"
                + "Expected start: " + expectedStartString + "\n"
                + "Actual: " + actualString);
        }
    }

    public static void startsWith(String expectedStartString, String actualString, boolean ignoreCase) {
        if (actualString == null) {
            throw new AssertionError("Assert.startsWith() failure: Actual string was null");
        }
        if (ignoreCase) {
            if (!actualString.toLowerCase().startsWith(expectedStartString.toLowerCase())) {
                throw new AssertionError("Assert.startsWith() failure (ignoreCase)\n"
                    + "Expected start: " + expectedStartString + "\n"
                    + "Actual: " + actualString);
            }
        } else {
            startsWith(expectedStartString, actualString);
        }
    }

    public static void startsWith(String expectedStartString, String actualString, StringComparison comparisonType) {
        startsWith(expectedStartString, actualString, isIgnoreCase(comparisonType));
    }

    // ── StrictEqual ──────────────────────────────────────────────────
    // Reflection: void StrictEqual<T>(T expected, T actual)

    public static void strictEqual(Object expected, Object actual) {
        if (!Objects.equals(expected, actual)) {
            throw new AssertionError("Assert.strictEqual() failure: Values differ\n"
                + "Expected: " + expected + "\n"
                + "Actual: " + actual);
        }
    }

    // ── Subset ───────────────────────────────────────────────────────
    // Reflection: void Subset(ISet<T> expectedSubset, ISet<T> actual)

    public static <T> void subset(Set<T> expectedSubset, Set<T> actual) {
        if (!actual.containsAll(expectedSubset)) {
            throw new AssertionError("Assert.subset() failure: Not a subset");
        }
    }

    // ── Superset ─────────────────────────────────────────────────────
    // Reflection: void Superset(ISet<T> expectedSuperset, ISet<T> actual)

    public static <T> void superset(Set<T> expectedSuperset, Set<T> actual) {
        if (!expectedSuperset.containsAll(actual)) {
            throw new AssertionError("Assert.superset() failure: Not a superset");
        }
    }

    // ── Throws ───────────────────────────────────────────────────────
    // Reflection: T Throws<T>(Action testCode)
    //            T Throws<T>(Func<object> testCode)
    //            T Throws<T>(string paramName, Action testCode)
    //            Exception Throws(Type exceptionType, Action testCode)

    @SuppressWarnings("unchecked")
    public static <T extends Throwable> T throws_(Class<T> exceptionType, Runnable testCode) {
        try {
            testCode.run();
        } catch (Throwable t) {
            if (exceptionType == t.getClass()) {
                return (T) t;
            }
            if (exceptionType.isInstance(t)) {
                throw new AssertionError("Assert.throws() failure: Exception type was not exact match\n"
                    + "Expected: " + exceptionType.getName() + "\n"
                    + "Actual: " + t.getClass().getName());
            }
            throw new AssertionError("Assert.throws() failure: Wrong exception type\n"
                + "Expected: " + exceptionType.getName() + "\n"
                + "Actual: " + t.getClass().getName(), t);
        }
        throw new AssertionError("Assert.throws() failure: No exception thrown\n"
            + "Expected: " + exceptionType.getName());
    }

    public static <T extends Throwable> T throws_(Class<T> exceptionType, java.util.function.Supplier<?> testCode) {
        return throws_(exceptionType, (Runnable) testCode::get);
    }

    public static <T extends Throwable> T throws_(String paramName, Class<T> exceptionType, Runnable testCode) {
        T ex = throws_(exceptionType, testCode);
        // Verify paramName matches if the exception has a parameter name
        return ex;
    }

    public static Throwable throwsType(Class<?> exceptionType, Runnable testCode) {
        try {
            testCode.run();
        } catch (Throwable t) {
            if (exceptionType.isInstance(t)) {
                return t;
            }
            throw new AssertionError("Assert.throws() failure: Wrong exception type\n"
                + "Expected: " + exceptionType.getName() + "\n"
                + "Actual: " + t.getClass().getName(), t);
        }
        throw new AssertionError("Assert.throws() failure: No exception thrown\n"
            + "Expected: " + exceptionType.getName());
    }

    public static Throwable throwsType(Class<?> exceptionType, java.util.function.Supplier<?> testCode) {
        return throwsType(exceptionType, (Runnable) testCode::get);
    }

    // ── ThrowsAny ────────────────────────────────────────────────────
    // Reflection: T ThrowsAny<T>(Action testCode)
    //            T ThrowsAny<T>(Func<object> testCode)

    @SuppressWarnings("unchecked")
    public static <T extends Throwable> T throwsAny(Class<T> exceptionType, Runnable testCode) {
        try {
            testCode.run();
        } catch (Throwable t) {
            if (exceptionType.isInstance(t)) {
                return (T) t;
            }
            throw new AssertionError("Assert.throwsAny() failure: Wrong exception type\n"
                + "Expected: " + exceptionType.getName() + " (or subclass)\n"
                + "Actual: " + t.getClass().getName(), t);
        }
        throw new AssertionError("Assert.throwsAny() failure: No exception thrown\n"
            + "Expected: " + exceptionType.getName() + " (or subclass)");
    }

    // ── ThrowsAsync ──────────────────────────────────────────────────
    // Reflection: Task<T> ThrowsAsync<T>(Func<Task> testCode)

    public static <T extends Throwable> CompletableFuture<T> throwsAsync(
        Class<T> exceptionType,
        Supplier<? extends CompletableFuture<?>> testCode) {
        CompletableFuture<?> future;
        try {
            future = testCode.get();
        } catch (Throwable t) {
            return CompletableFuture.completedFuture(assertExactThrowable(exceptionType, t, "throwsAsync"));
        }

        if (future == null) {
            return failedFuture(new AssertionError("Assert.throwsAsync() failure: No task returned\n"
                + "Expected: " + exceptionType.getName()));
        }

        return future.handle((ignored, failure) -> {
            var thrown = unwrapCompletionException(failure);
            if (thrown == null) {
                throw new AssertionError("Assert.throwsAsync() failure: No exception thrown\n"
                    + "Expected: " + exceptionType.getName());
            }
            return assertExactThrowable(exceptionType, thrown, "throwsAsync");
        });
    }

    // ── True ─────────────────────────────────────────────────────────
    // Reflection: void True(bool condition)
    //            void True(bool condition, string userMessage)
    //            void True(Nullable<bool> condition)
    //            void True(Nullable<bool> condition, string userMessage)

    public static void true_(boolean condition) {
        if (!condition) {
            throw new AssertionError("Assert.true() failure: Expected condition to be true");
        }
    }

    public static void true_(boolean condition, String userMessage) {
        if (!condition) {
            throw new AssertionError(userMessage != null ? userMessage : "Assert.true() failure: Expected condition to be true");
        }
    }

    public static void true_(Boolean condition) {
        if (!Boolean.TRUE.equals(condition)) {
            throw new AssertionError("Assert.true() failure: Expected condition to be true");
        }
    }

    public static void true_(Boolean condition, String userMessage) {
        if (!Boolean.TRUE.equals(condition)) {
            throw new AssertionError(userMessage != null ? userMessage : "Assert.true() failure: Expected condition to be true");
        }
    }

    // ── Distinct ─────────────────────────────────────────────────────
    // Reflection: void Distinct<T>(IEnumerable<T> collection)
    //            void Distinct<T>(IEnumerable<T> collection, IEqualityComparer<T> comparer)

    public static <T> void distinct(Iterable<T> collection) {
        Set<T> seen = new HashSet<>();
        Set<T> duplicates = new HashSet<>();
        for (T item : collection) {
            if (!seen.add(item)) {
                duplicates.add(item);
            }
        }
        if (!duplicates.isEmpty()) {
            throw new AssertionError("Assert.distinct() failure: Duplicate items found: " + duplicates);
        }
    }

    // ── Internal helpers ─────────────────────────────────────────────

    private static boolean objectsEqual(Object a, Object b) {
        if (Objects.equals(a, b)) {
            return true;
        }
        if (a instanceof Number && b instanceof Number) {
            double da = ((Number) a).doubleValue();
            double db = ((Number) b).doubleValue();
            if (Double.isNaN(da) && Double.isNaN(db)) {
                return true;
            }
            return da == db;
        }
        return false;
    }

    private static boolean isIgnoreCase(StringComparison comparisonType) {
        return comparisonType == StringComparison.CurrentCultureIgnoreCase
            || comparisonType == StringComparison.InvariantCultureIgnoreCase
            || comparisonType == StringComparison.OrdinalIgnoreCase;
    }

    private static Throwable unwrapCompletionException(Throwable throwable) {
        if (throwable instanceof CompletionException && throwable.getCause() != null) {
            return throwable.getCause();
        }
        return throwable;
    }

    private static <T extends Throwable> T assertExactThrowable(
        Class<T> exceptionType,
        Throwable thrown,
        String assertName) {
        if (exceptionType == thrown.getClass()) {
            return exceptionType.cast(thrown);
        }
        if (exceptionType.isInstance(thrown)) {
            throw new AssertionError("Assert." + assertName + "() failure: Exception type was not exact match\n"
                + "Expected: " + exceptionType.getName() + "\n"
                + "Actual: " + thrown.getClass().getName(), thrown);
        }
        throw new AssertionError("Assert." + assertName + "() failure: Wrong exception type\n"
            + "Expected: " + exceptionType.getName() + "\n"
            + "Actual: " + thrown.getClass().getName(), thrown);
    }

    private static <T> CompletableFuture<T> failedFuture(Throwable throwable) {
        CompletableFuture<T> future = new CompletableFuture<>();
        future.completeExceptionally(throwable);
        return future;
    }
}

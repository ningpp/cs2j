package io.github.ningpp.compat;

import java.util.ArrayList;
import java.util.List;
import java.util.Objects;

/** Minimal NUnit-style CollectionAssert surface used by converted test code. */
public final class CollectionAssert {
    private CollectionAssert() {
    }

    public static void areEqual(Object expected, Object actual) {
        areEqual(expected, actual, null);
    }

    public static void areEqual(Object expected, Object actual, String message) {
        if (expected != null && actual != null
            && (expected.getClass().isArray() || actual.getClass().isArray())) {
            List<Object> left = arrayOrList(expected);
            List<Object> right = arrayOrList(actual);
            if (!Objects.equals(left, right)) {
                throw new AssertionError(
                    (message == null ? "" : message + " ")
                    + "Expected: " + left + " but was: " + right);
            }
            return;
        }
        if (!Objects.equals(expected, actual)) {
            throw new AssertionError(
                (message == null ? "" : message + " ")
                + "Expected: " + expected + " but was: " + actual);
        }
    }

    private static List<Object> arrayOrList(Object value) {
        if (value == null) {
            return null;
        }
        if (value.getClass().isArray()) {
            int length = java.lang.reflect.Array.getLength(value);
            List<Object> result = new ArrayList<>(length);
            for (int i = 0; i < length; i++) {
                result.add(java.lang.reflect.Array.get(value, i));
            }
            return result;
        }
        if (value instanceof Iterable<?> iterable) {
            return toList(iterable);
        }
        return List.of(value);
    }

    /**
     * NUnit {@code CollectionAssert.AreEquivalent}: order-insensitive comparison
     * with element multiplicity. Arrays (including primitive arrays) and
     * Iterables are flattened to lists first.
     */
    public static void areEquivalent(Object expected, Object actual) {
        areEquivalent(expected, actual, null);
    }

    public static void areEquivalent(Object expected, Object actual, String message) {
        List<Object> left = flatten(expected);
        List<Object> right = flatten(actual);
        if (!sameMultiset(left, right)) {
            throw new AssertionError(
                (message == null ? "" : message + " ")
                + "Expected: " + left + " but was: " + right);
        }
    }

    /**
     * NUnit {@code CollectionAssert.AreEquivalent(expected, actual, message,
     * params object[])}: the trailing arguments are format arguments for
     * {@code message}.
     */
    public static void areEquivalent(
            Object expected, Object actual, String message, Object... parameters) {
        areEquivalent(expected, actual, formatMessage(message, parameters));
    }

    private static String formatMessage(String message, Object[] parameters) {
        if (message == null || parameters == null || parameters.length == 0) {
            return message;
        }
        try {
            // NUnit messages are .NET composite format strings (`{0}`), not Java's `%s`.
            return StringHelper.formatCs(message, parameters);
        } catch (RuntimeException e) {
            // Not a valid format string (plain text with braces/percent signs):
            // keep the raw text rather than masking the assertion result.
            return message;
        }
    }

    /** NUnit {@code CollectionAssert.AreNotEqual}: ordered element comparison. */
    public static void areNotEqual(Object expected, Object actual) {
        areNotEqual(expected, actual, null);
    }

    public static void areNotEqual(Object expected, Object actual, String message) {
        List<Object> left = flatten(expected);
        List<Object> right = flatten(actual);
        if (Objects.equals(left, right)) {
            throw new AssertionError(
                (message == null ? "" : message + " ")
                + "Expected collections to differ but both were: " + left);
        }
    }

    /** NUnit {@code CollectionAssert.Contains}. */
    public static void contains(Iterable<?> collection, Object item) {
        contains(collection, item, null);
    }

    public static void contains(Iterable<?> collection, Object item, String message) {
        boolean found = false;
        if (collection != null) {
            for (Object value : collection) {
                if (Objects.equals(value, item)) {
                    found = true;
                    break;
                }
            }
        }
        if (!found) {
            throw new AssertionError(
                (message == null ? "" : message + " ")
                + "Expected collection to contain: " + item);
        }
    }

    private static List<Object> flatten(Object value) {
        if (value == null) {
            return new ArrayList<>();
        }
        if (value.getClass().isArray()) {
            int length = java.lang.reflect.Array.getLength(value);
            List<Object> result = new ArrayList<>(length);
            for (int i = 0; i < length; i++) {
                result.add(java.lang.reflect.Array.get(value, i));
            }
            return result;
        }
        if (value instanceof Iterable<?> iterable) {
            return toList(iterable);
        }
        List<Object> single = new ArrayList<>(1);
        single.add(value);
        return single;
    }

    private static boolean sameMultiset(List<Object> left, List<Object> right) {
        if (left.size() != right.size()) {
            return false;
        }
        java.util.Map<Object, Integer> counts = new java.util.HashMap<>();
        for (Object item : left) {
            counts.merge(item, 1, Integer::sum);
        }
        for (Object item : right) {
            Integer count = counts.get(item);
            if (count == null || count == 0) {
                return false;
            }
            counts.put(item, count - 1);
        }
        return true;
    }

    public static void areEqual(Iterable<?> expected, Iterable<?> actual) {
        areEqual(expected, actual, null);
    }

    public static void areEqual(Iterable<?> expected, Iterable<?> actual, String message) {
        List<Object> left = toList(expected);
        List<Object> right = toList(actual);
        if (!Objects.equals(left, right)) {
            throw new AssertionError(
                (message == null ? "" : message + " ")
                + "Expected: " + left + " but was: " + right);
        }
    }

    private static List<Object> toList(Iterable<?> values) {
        List<Object> result = new ArrayList<>();
        if (values != null) {
            for (Object value : values) {
                result.add(value);
            }
        }
        return result;
    }
}

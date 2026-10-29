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

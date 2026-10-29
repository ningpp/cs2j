package io.github.ningpp.compat;

import java.util.ArrayList;
import java.util.List;
import java.util.Objects;

/** Minimal NUnit-style CollectionAssert surface used by converted test code. */
public final class CollectionAssert {
    private CollectionAssert() {
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

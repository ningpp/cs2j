package Microsoft.VisualStudio.TestTools.UnitTesting;

import java.util.ArrayList;
import java.util.List;
import java.util.Objects;

public final class CollectionAssert {
    private CollectionAssert() {}

    public static void areEqual(Iterable<?> expected, Iterable<?> actual) {
        areEqual(expected, actual, null);
    }

    public static void areEqual(Iterable<?> expected, Iterable<?> actual, String message) {
        var expectedList = toList(expected);
        var actualList = toList(actual);
        if (!Objects.equals(expectedList, actualList)) {
            throw new AssertionError(message == null || message.isEmpty()
                ? "Expected collections to be equal."
                : message);
        }
    }

    public static void areEquivalent(Iterable<?> expected, Iterable<?> actual) {
        areEquivalent(expected, actual, null);
    }

    public static void areEquivalent(Iterable<?> expected, Iterable<?> actual, String message) {
        var expectedList = toList(expected);
        var actualList = toList(actual);
        if (!areMultisetsEqual(expectedList, actualList)) {
            throw new AssertionError(message == null || message.isEmpty()
                ? "Expected collections to be equivalent."
                : message);
        }
    }

    public static void contains(Iterable<?> collection, Object element) {
        for (Object item : collection) {
            if (Objects.equals(item, element)) {
                return;
            }
        }
        throw new AssertionError("Expected collection to contain the specified element.");
    }

    public static void areNotEqual(Iterable<?> notExpected, Iterable<?> actual) {
        if (Objects.equals(toList(notExpected), toList(actual))) {
            throw new AssertionError("Expected collections to be different.");
        }
    }

    private static boolean areMultisetsEqual(List<Object> expected, List<Object> actual) {
        if (expected.size() != actual.size()) {
            return false;
        }

        List<Object> remaining = new ArrayList<>(actual);
        for (Object item : expected) {
            if (!remaining.remove(item)) {
                return false;
            }
        }
        return true;
    }

    private static List<Object> toList(Iterable<?> source) {
        ArrayList<Object> values = new ArrayList<>();
        for (Object item : source) {
            values.add(item);
        }
        return values;
    }
}

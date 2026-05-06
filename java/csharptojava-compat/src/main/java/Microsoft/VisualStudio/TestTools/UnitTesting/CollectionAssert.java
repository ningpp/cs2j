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

    private static List<Object> toList(Iterable<?> source) {
        ArrayList<Object> values = new ArrayList<>();
        for (Object item : source) {
            values.add(item);
        }
        return values;
    }
}

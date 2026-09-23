package io.github.ningpp.compat;

import org.junit.jupiter.api.Test;
import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertThrows;
import static org.junit.jupiter.api.Assertions.assertTrue;

/**
 * C# enumerates a default ImmutableArray&lt;T&gt; via GetEnumerator(), which throws
 * InvalidOperationException because the backing array was never initialized (Json.NET
 * fails the same way) — the serializer must surface that throw instead of writing an
 * empty array (ImmutableCollectionsTests.serializeDefaultArray pins it as
 * InvalidOperationException → IllegalStateException).
 */
class ImmutableArrayDefaultInstanceTest {

    @Test
    void defaultInstanceIterationThrows() {
        ImmutableArray<String> array = new ImmutableArray<>();
        assertTrue(array.getIsDefault());
        assertThrows(IllegalStateException.class, array::iterator);
    }

    @Test
    void defaultInstanceSpliteratorThrows() {
        ImmutableArray<String> array = new ImmutableArray<>();
        assertThrows(IllegalStateException.class, array::spliterator);
    }

    @Test
    void defaultInstanceThrowsThroughSerializerEnumerationPath() {
        // ImmutableEnumerableOfTConverter enumerates exactly like this:
        // CSharpGenericIterable.fromAny(value).iterator()
        ImmutableArray<String> array = new ImmutableArray<>();
        CSharpGenericIterable<?> iterable = CSharpGenericIterable.fromAny(array);
        assertThrows(IllegalStateException.class, iterable::iterator);
    }

    @Test
    void nonDefaultInstanceIteratesElements() {
        ImmutableArray<String> array = ImmutableArray.create("One", "II", "3");
        int count = 0;
        for (String item : array) {
            count++;
        }
        assertEquals(3, count);
    }
}

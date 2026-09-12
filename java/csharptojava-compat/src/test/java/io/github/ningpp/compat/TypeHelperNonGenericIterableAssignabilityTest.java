package io.github.ningpp.compat;

import org.junit.jupiter.api.Test;

import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertTrue;

/**
 * .NET's {@code IEnumerable<T>} derives from the non-generic {@code IEnumerable}, so a type that
 * implements the non-generic compat collection tree is also an {@code IEnumerable<T>}.
 *
 * <p>{@code IEnumerableConverterFactory.CanConvert} asks exactly
 * {@code isAssignableFrom(CSharpGenericIterable.class, type)}. Before the bridge a non-generic
 * collection such as {@code CSharpObjQueue} (which implements {@code CSharpIterable<Object>})
 * answered false, so no collection converter claimed it and the queue serialized as a POCO:
 * {@code {"Count":2,"SyncRoot":{},...}} instead of {@code [1,2]}.</p>
 */
class TypeHelperNonGenericIterableAssignabilityTest {

    @Test
    void nonGenericCollection_IsAssignableToGenericIterable() {
        assertTrue(TypeHelper.isAssignableFrom(CSharpGenericIterable.class, CSharpObjQueue.class));
        assertTrue(TypeHelper.isAssignableFrom(CSharpGenericIterable.class, CSharpObjStack.class));
        // The interface itself answers true as well (it did before this change).
        assertTrue(TypeHelper.isAssignableFrom(CSharpGenericIterable.class, CSharpIterable.class));
    }

    @Test
    void unrelatedTypes_AreStillNotAssignable() {
        assertFalse(TypeHelper.isAssignableFrom(CSharpGenericIterable.class, String.class));
        assertFalse(TypeHelper.isAssignableFrom(CSharpGenericIterable.class, Integer.class));
    }
}

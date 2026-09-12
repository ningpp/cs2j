package io.github.ningpp.compat;

import org.junit.jupiter.api.Test;

import static org.junit.jupiter.api.Assertions.*;

/**
 * The extension-data path asks whether a member type is assignable from a type that could
 * not be resolved (null), and the port used to dereference it: 42 extension-data failures
 * pointed at TypeHelper.isAssignableFrom with a NullPointerException. C# reflection never
 * gets a null there, so the tolerant answer for the erased Java model is false.
 */
class TypeHelperNullArgumentTest {

    @Test
    void isAssignableFrom_toleratesNullArguments() {
        assertFalse(TypeHelper.isAssignableFrom(CSharpList.class, null));
        assertFalse(TypeHelper.isAssignableFrom(null, CSharpList.class));
        assertFalse(TypeHelper.isAssignableFrom(null, null));
    }

    @Test
    void isAssignableFrom_stillAnswersForRealTypes() {
        assertTrue(TypeHelper.isAssignableFrom(CSharpGenericIterable.class, CSharpList.class));
        assertFalse(TypeHelper.isAssignableFrom(String.class, Integer.class));
    }
}
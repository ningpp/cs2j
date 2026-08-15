package io.github.ningpp.compat;

import static org.junit.jupiter.api.Assertions.assertFalse;

import org.junit.jupiter.api.Test;

/// Java class objects are always concrete: a generic class literal such as
/// CSharpList.class represents a runtime type whose type arguments are erased,
/// not an open C# generic definition. TypeHelper.getContainsGenericParameters
/// must therefore not reject every generic Java class.
class TypeHelperGenericParameterTest {
    static class Generic<T> {
    }

    @Test
    void concreteJavaClassesDoNotContainGenericParameters() {
        assertFalse(TypeHelper.getContainsGenericParameters(Generic.class));
        assertFalse(TypeHelper.getContainsGenericParameters(CSharpList.class));
        assertFalse(TypeHelper.getContainsGenericParameters(CSharpGenericIList.class));
    }
}

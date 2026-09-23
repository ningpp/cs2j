package io.github.ningpp.compat;

import org.junit.jupiter.api.AfterEach;
import org.junit.jupiter.api.Test;

import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertTrue;

/**
 * Mirrors the generated {@code JsonClassInfo.determineExtensionDataProperty} gate for
 * {@code [JsonExtensionData]} members: {@code TypeHelper.isExtensionDataDictionaryType} is
 * called with the member's DECLARED type — the erased class {@code PropertyInfo.getPropertyType()}
 * returned — once for the declared type and once for the runtime type. Each test reflects a
 * member first (the generated cacheMember loop calls {@code getPropertyType()} on every member
 * before the gate runs), so the member's closed signature is published exactly the way the
 * generated flow publishes it.
 *
 * <p>.NET validates the CLOSED type ({@code typeof(IDictionary<string, object>).IsAssignableFrom}
 * / {@code typeof(IDictionary<string, JsonElement>).IsAssignableFrom}). Before the fix the port
 * answered on the raw class alone: a generic holder's {@code T Overflow} erased to Object and was
 * refused ("does not match the required signature" — 12 parameterized ExtensionDataTests errors),
 * while {@code Dictionary<string, int>} passed the shape test and the two invalid-dictionary
 * tests saw no exception at all.</p>
 */
class TypeHelperExtensionDataDictionaryTest {

    @AfterEach
    void dropRecordedMemberState() {
        // The generated flow builds one class info at a time on the calling thread; the tests
        // reuse the same raw classes across fixtures, so drop the member snapshot between them
        // the same way a fresh operation starts clean.
        TypeHelper.clearThreadRegistrations();
    }

    /** A C# generic holder: {@code ClassWithExtensionData<T>.Overflow} erases to Object. */
    static class ClassWithExtensionData<T> {
        @CompatJsonExtensionData public T getOverflow() { return null; }
        public void setOverflow(T value) { }
    }

    /** Dictionary<string, int> — .NET rejects the closed value type. */
    static class ClassWithInvalidExtensionProperty {
        @CompatJsonExtensionData public CSharpDictionary<String, Integer> getMyOverflow() { return null; }
        public void setMyOverflow(CSharpDictionary<String, Integer> value) { }
    }

    /** Dictionary<string, object> — the canonical valid form. */
    static class ClassWithValidExtensionProperty {
        @CompatJsonExtensionData public CSharpDictionary<String, Object> getMyOverflow() { return null; }
        public void setMyOverflow(CSharpDictionary<String, Object> value) { }
    }

    /** CustomOverflowDictionary<object> : Dictionary<string, object> — single-parameter subtype. */
    static class CustomOverflowDictionary<T> extends CSharpDictionary<String, T> {
    }

    static class ClassWithCustomOverflowExtensionProperty {
        @CompatJsonExtensionData public CustomOverflowDictionary<Object> getOverflow() { return null; }
        public void setOverflow(CustomOverflowDictionary<Object> value) { }
    }

    /** Dictionary<int, object> — a non-string key is invalid in .NET. */
    static class ClassWithIntegerKeyedExtensionProperty {
        @CompatJsonExtensionData public CSharpDictionary<Integer, Object> getOverflow() { return null; }
        public void setOverflow(CSharpDictionary<Integer, Object> value) { }
    }

    /** A list is not an IDictionary<string, T>. */
    static class ClassWithListExtensionProperty {
        @CompatJsonExtensionData public CSharpList<String> getOverflow() { return null; }
        public void setOverflow(CSharpList<String> value) { }
    }

    /** ImmutableDictionary<string, object> — accepted (realized as IImmutableDictionary). */
    static class ClassWithImmutableExtensionProperty {
        @CompatJsonExtensionData public ImmutableDictionary<String, Object> getMyOverflow() { return null; }
        public void setMyOverflow(ImmutableDictionary<String, Object> value) { }
    }

    private static Class<?> declaredPropertyType(Class<?> holder, String getterName) {
        // Reflection walk of the generated flow: PropertyInfo.getProperties pairs the accessors
        // and getPropertyType() publishes the member's closed signature before the gate reads it.
        for (PropertyInfo property : PropertyInfo.getProperties(holder)) {
            if (property.getGetMethod() != null && property.getGetMethod().getName().equals(getterName)) {
                return property.getPropertyType();
            }
        }
        throw new IllegalStateException("getter not found: " + getterName);
    }

    @Test
    void erasedTypeVariableMember_isAccepted() {
        Class<?> declared = declaredPropertyType(ClassWithExtensionData.class, "getOverflow");
        assertTrue(declared == Object.class, "the C# type variable must erase to Object");
        assertTrue(TypeHelper.isExtensionDataDictionaryType(declared),
            "the erased holder property must pass the gate; .NET validated the closed type");
    }

    @Test
    void invalidClosedDictionary_isRejected() {
        Class<?> declared = declaredPropertyType(ClassWithInvalidExtensionProperty.class, "getMyOverflow");
        assertFalse(TypeHelper.isExtensionDataDictionaryType(declared),
            "Dictionary<string, int> must be rejected like .NET's closed-type check");
    }

    @Test
    void validClosedDictionary_isAccepted() {
        Class<?> declared = declaredPropertyType(ClassWithValidExtensionProperty.class, "getMyOverflow");
        assertTrue(TypeHelper.isExtensionDataDictionaryType(declared),
            "Dictionary<string, object> is the canonical extension data type");
    }

    @Test
    void singleParameterSubtype_isAccepted() {
        Class<?> declared = declaredPropertyType(ClassWithCustomOverflowExtensionProperty.class, "getOverflow");
        assertTrue(TypeHelper.isExtensionDataDictionaryType(declared),
            "CustomOverflowDictionary<object> declares Dictionary<string, object> in its supertype");
    }

    @Test
    void integerKeyedDictionary_isRejected() {
        Class<?> declared = declaredPropertyType(ClassWithIntegerKeyedExtensionProperty.class, "getOverflow");
        assertFalse(TypeHelper.isExtensionDataDictionaryType(declared),
            "Dictionary<int, object> is not an IDictionary<string, T>");
    }

    @Test
    void nonDictionaryMember_isRejected() {
        Class<?> declared = declaredPropertyType(ClassWithListExtensionProperty.class, "getOverflow");
        assertFalse(TypeHelper.isExtensionDataDictionaryType(declared),
            "a list member is not extension data");
    }

    @Test
    void immutableDictionary_isAccepted() {
        Class<?> declared = declaredPropertyType(ClassWithImmutableExtensionProperty.class, "getMyOverflow");
        assertTrue(TypeHelper.isExtensionDataDictionaryType(declared),
            "ImmutableDictionary<string, object> must stay accepted (IDictionary with throwing mutators in .NET)");
    }

    @Test
    void nullType_isRejected() {
        assertFalse(TypeHelper.isExtensionDataDictionaryType(null));
    }
}

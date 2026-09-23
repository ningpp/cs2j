package io.github.ningpp.compat;

import org.junit.jupiter.api.Test;

import java.lang.reflect.Constructor;
import java.math.BigInteger;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.List;

import static org.junit.jupiter.api.Assertions.*;

/**
 * Bridges the member shapes the cs2j transpiler emits for the C# constructs exercised by
 * {@code dotnet.text.json.Serialization.Tests.PropertyVisibilityTests}. Each mimic class
 * reproduces the exact generated Java shape; the assertions state the .NET reflection
 * answer the generated serializer relies on.
 */
class PropertyVisibilityBridgeTest {

    // ── TypeHelper.getProperty must surface the setter ─────────────────────────

    /** Shape of ClassWithClassProperty_IgnoreConditionWhenWritingDefault. */
    static class WithIgnoredWhenWritingDefaultProperty {
        private int int1;
        private String myString = "DefaultString";
        private int int2;

        public int getInt1() { return int1; }
        public void setInt1(int value) { int1 = value; }

        @CompatJsonIgnore(2)
        public String getMyString() { return myString; }
        public void setMyString(String value) { this.myString = value; }

        public int getInt2() { return int2; }
        public void setInt2(int value) { int2 = value; }
    }

    @Test
    void getProperty_findsUsableSetter() {
        PropertyInfo property = TypeHelper.getProperty(WithIgnoredWhenWritingDefaultProperty.class, "MyString");
        assertNotNull(property, "C# Type.GetProperty answers the property");
        assertEquals("MyString", property.getName());

        Object obj = new WithIgnoredWhenWritingDefaultProperty();
        assertEquals("DefaultString", property.getValue(obj));
        // The generated test does exactly this: type.GetProperty("MyString").SetValue(obj, null).
        assertDoesNotThrow(() -> property.setValue(obj, null));
        assertNull(property.getValue(obj));
    }

    // ── DeclaredOnly must restrict the member walk to the declaring type ───────

    /** Shape of ClassWithHiddenByNewSlotIntProperty / ClassWithNewSlotDecimalField. */
    @SuppressWarnings("unused")
    static class BaseWithMyNumericProperty {
        private int myNumeric = 1;

        public int getMyNumeric() { return myNumeric; }
        public void setMyNumeric(int value) { this.myNumeric = value; }
    }

    /** C#: [JsonInclude] public new long MyNumeric = 5 — a field hiding the base property. */
    @SuppressWarnings("unused")
    static class DerivedWithNewSlotMyNumericField extends BaseWithMyNumericProperty {
        public long MyNumeric = 5L;
    }

    private static final int CLASS_INFO_FLAGS =
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

    @Test
    void getProperties_declaredOnly_returnsOnlyDeclaredMembers() {
        // .NET GetProperties(DeclaredOnly) on the derived type answers no property at all:
        // MyNumeric is declared on the BASE type. The inherited listing made the base
        // property win the serializer's name cache before the derived field was seen
        // (PropertyVisibilityTests.serialize_NewSlotPublicField_ConflictWithBasePublicProperty
        // serialized 1 instead of 1.5).
        PropertyInfo[] declared = PropertyInfo.getProperties(DerivedWithNewSlotMyNumericField.class, CLASS_INFO_FLAGS);
        assertEquals(0, declared.length, "derived type declares no properties");
        assertEquals(1, PropertyInfo.getProperties(BaseWithMyNumericProperty.class, CLASS_INFO_FLAGS).length);
    }

    /** Reproduces JsonClassInfo's per-level caching order: properties first, then fields,
     *  walking from the most derived type; the first member cached under a JSON name wins. */
    private static MemberInfo firstCachedMemberForName(Class<?> type, String jsonName) {
        for (Class<?> current = type; current != null && current != Object.class; current = current.getSuperclass()) {
            for (PropertyInfo propertyInfo : PropertyInfo.getProperties(current, CLASS_INFO_FLAGS)) {
                if (jsonName.equals(propertyInfo.getName())) {
                    return propertyInfo;
                }
            }
            for (FieldInfo fieldInfo : TypeHelper.getFields(current, CLASS_INFO_FLAGS)) {
                if (jsonName.equals(fieldInfo.getName())) {
                    return fieldInfo;
                }
            }
        }
        return null;
    }

    @Test
    void newSlotField_winsTheNameCacheOverHiddenBaseProperty() {
        MemberInfo cached = firstCachedMemberForName(DerivedWithNewSlotMyNumericField.class, "MyNumeric");
        assertTrue(cached instanceof FieldInfo, ".NET caches the derived new-slot field");
        assertEquals(5L, ((FieldInfo) cached).getValue(new DerivedWithNewSlotMyNumericField()));
    }

    // ── Constructor enumeration on closed java.base types ──────────────────────

    @Test
    void getConstructors_enumeratesTypesWithInaccessibleConstructors() {
        // BigInteger's private constructors live in the non-open java.base module; C#
        // Type.GetConstructors() has no equivalent failure mode and reports every
        // constructor. The enumeration must survive and include non-public ctors.
        ConstructorInfo[] ctors = assertDoesNotThrow(() -> TypeHelper.getConstructors(BigInteger.class));
        assertTrue(ctors.length > 0);
        assertTrue(Arrays.stream(ctors).anyMatch(ConstructorInfo::getIsPublic));
        assertTrue(Arrays.stream(ctors).anyMatch(c -> !c.getIsPublic()), "non-public ctors are reported");
    }

    @Test
    void constructorInfo_canWrapInaccessibleConstructor() {
        for (Constructor<?> ctor : BigInteger.class.getDeclaredConstructors()) {
            assertDoesNotThrow(() -> new ConstructorInfo(ctor));
        }
    }

    // ── The Object.getClass() collision rename (C# property `Class`) ───────────

    /** Shape of ClassUsingIgnoreWhenWritingDefaultAttribute: C# property `Class` became
     *  getter getClassValue (Object.getClass collision) + setter setClass, with no
     *  @CompatPropertyName to carry the original name. */
    static class WithClassRenamedProperty {
        private String classValue;

        @CompatJsonIgnore(2)
        public String getClassValue() { return classValue; }
        public void setClass(String value) { this.classValue = value; }
    }

    @Test
    void objectGetCollisionRename_recoversCsharpPropertyNameAndSetter() {
        List<PropertyInfo> props = new ArrayList<>(
            Arrays.asList(PropertyInfo.getProperties(WithClassRenamedProperty.class)));
        assertEquals(1, props.size(), "exactly one C# property");
        assertEquals("Class", props.get(0).getName(), "the C# name, not the renamed getter's");

        assertNotNull(props.get(0).getSetMethod(), "setClass pairs with getClassValue");

        Object obj = new WithClassRenamedProperty();
        props.get(0).setValue(obj, "round-tripped");
        assertEquals("round-tripped", props.get(0).getValue(obj));
    }
}

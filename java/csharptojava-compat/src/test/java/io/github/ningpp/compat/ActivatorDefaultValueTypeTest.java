package io.github.ningpp.compat;

import org.junit.jupiter.api.Test;
import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertNotNull;
import static org.junit.jupiter.api.Assertions.assertSame;
import static org.junit.jupiter.api.Assertions.assertThrows;

/**
 * C# {@code Activator.CreateInstance(Type)} answers {@code default(T)} for a value type,
 * which has no parameterless constructor to reflect over: {@code CreateInstance(typeof(bool))}
 * boxes {@code false}. Generated reflection code walks property types through
 * {@code PropertyInfo.getPropertyType()}, which boxes primitives — so a C# {@code bool}
 * property arrives here as {@code Boolean.class} (OptionsTests.getFullyPopulatedOptionsInstance),
 * and C# enums are value types whose default is the 0 value.
 */
class ActivatorDefaultValueTypeTest {

    enum SampleEnum {
        First, Second
    }

    static class NoDefaultCtor {
        public NoDefaultCtor(int value) {
        }
    }

    @Test
    void boxedBooleanDefaultsToFalse() {
        assertEquals(Boolean.FALSE, Activator.createInstance(Boolean.class));
    }

    @Test
    void primitiveBooleanDefaultsToFalse() {
        assertEquals(Boolean.FALSE, (Boolean) Activator.createInstance(boolean.class));
    }

    @Test
    void boxedNumericWrappersDefaultToZero() {
        assertEquals(0, (int) (Integer) Activator.createInstance(Integer.class));
        assertEquals(0L, (long) (Long) Activator.createInstance(Long.class));
        assertEquals(0, (byte) (Byte) Activator.createInstance(Byte.class));
        assertEquals(0, (short) (Short) Activator.createInstance(Short.class));
        assertEquals(0, (char) (Character) Activator.createInstance(Character.class));
        assertEquals(0f, (float) (Float) Activator.createInstance(Float.class), 0.0f);
        assertEquals(0.0, (double) (Double) Activator.createInstance(Double.class), 0.0);
    }

    @Test
    void primitiveIntDefaultsToZero() {
        assertEquals(0, (int) (Integer) Activator.createInstance(int.class));
    }

    @Test
    void enumDefaultsToFirstDeclaredConstant() {
        assertSame(SampleEnum.First, Activator.createInstance(SampleEnum.class));
    }

    @Test
    void referenceTypeStillUsesParameterlessConstructor() {
        assertNotNull(Activator.createInstance(Object.class));
    }

    @Test
    void classWithoutParameterlessConstructorStillThrows() {
        assertThrows(RuntimeException.class, () -> Activator.createInstance(NoDefaultCtor.class));
    }
}

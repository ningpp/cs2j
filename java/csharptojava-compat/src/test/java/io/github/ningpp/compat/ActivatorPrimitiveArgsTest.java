package io.github.ningpp.compat;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertNotNull;

import org.junit.jupiter.api.Test;

class ActivatorPrimitiveArgsTest {
    static class C {
        public C(int value, String text) {
        }
    }

    @Test
    void boxedIntegerMatchesPrimitiveIntConstructorParam() {
        Object instance = Activator.createInstance(C.class, new Object[] { 1, "x" });
        assertNotNull(instance);
    }

    /**
     * Converter classes receive one injected Class&lt;?&gt; constructor parameter per C#
     * generic parameter, but a call site only forwards the type arguments it knows (e.g.
     * ObjectConverterFactory forwards MakeGenericType(typeToConvert) while
     * SmallObjectWithParameterizedConstructorConverter&lt;,,,,&gt; declares five Class
     * parameters). The extra tokens are never read by the generated converters, so the
     * helper fills them with Object.class instead of failing to match the constructor --
     * while the first token (the converted type) stays exact.
     */
    static class FiveTokenConverter {
        final Class<?> tClass;
        final Class<?> tArg0Class;
        final Class<?> tArg1Class;
        final Class<?> tArg2Class;
        final Class<?> tArg3Class;

        public FiveTokenConverter(Class<?> tClass, Class<?> tArg0Class, Class<?> tArg1Class,
                                  Class<?> tArg2Class, Class<?> tArg3Class) {
            this.tClass = tClass;
            this.tArg0Class = tArg0Class;
            this.tArg1Class = tArg1Class;
            this.tArg2Class = tArg2Class;
            this.tArg3Class = tArg3Class;
        }
    }

    @Test
    void fewerTypeArgumentsThanClassParameters_PadsWithObjectAndKeepsFirstToken() {
        Object instance = Activator.createInstanceForGenericType(
            FiveTokenConverter.class, 0, null, String.class);

        FiveTokenConverter converter = (FiveTokenConverter) instance;
        assertEquals(String.class, converter.tClass);
        assertEquals(Object.class, converter.tArg0Class);
        assertEquals(Object.class, converter.tArg1Class);
        assertEquals(Object.class, converter.tArg2Class);
        assertEquals(Object.class, converter.tArg3Class);
    }

    static class TwoTokenConverter {
        final Class<?> tClass;
        final Class<?> tKeyClass;

        public TwoTokenConverter(Class<?> tClass, Class<?> tKeyClass) {
            this.tClass = tClass;
            this.tKeyClass = tKeyClass;
        }
    }

    @Test
    void moreTypeArgumentsThanConstructorParameters_UsesPrefix() {
        Object instance = Activator.createInstanceForGenericType(
            TwoTokenConverter.class, 0, null, String.class, Integer.class, Boolean.class);

        TwoTokenConverter converter = (TwoTokenConverter) instance;
        assertEquals(String.class, converter.tClass);
        assertEquals(Integer.class, converter.tKeyClass);
    }
}

package io.github.ningpp.compat;

import org.junit.jupiter.api.Test;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertNotEquals;
import static org.junit.jupiter.api.Assertions.assertSame;

class CSharpNumericTypeTokenTest {

    @Test
    void csharpNumericMarkersPreserveDistinctRuntimeTypeIdentity() {
        Class<?> byteType = CSharpByte.class;
        Class<?> intType = int.class;

        assertSame(CSharpByte.class, byteType);
        assertNotEquals(intType, byteType);
        assertNotEquals(CSharpByte.class, CSharpSByte.class);
        assertNotEquals(CSharpUInt16.class, CSharpUInt32.class);
        assertNotEquals(CSharpUInt32.class, CSharpUInt64.class);
    }

    @Test
    void javaByteClassDenormalizesToSByteToken() {
        // The converter maps C# byte -> Java int/Integer and C# sbyte -> Java
        // byte/Byte, so a java.lang.Byte surfaced by reflection over generated
        // code can only represent a C# sbyte.  Denormalization must therefore
        // resolve Byte.class to CSharpSByte.class (SByteConverter handles Byte
        // values), not CSharpByte.class (ByteConverter expects Integer).
        assertSame(CSharpSByte.class, TypeHelper.denormalizeToCSharpTypeToken(Byte.class));
    }

    @Test
    void dictionaryLookupByJavaByteClassFindsSByteConverterRegistration() {
        // Mirrors JsonSerializerOptions converter lookup: converters are
        // registered under C# type tokens, but property types obtained via
        // reflection arrive as Java wrapper classes.
        CSharpDictionary<Class, String> converters = new CSharpDictionary<>();
        converters.put(CSharpByte.class, "ByteConverter");
        converters.put(CSharpSByte.class, "SByteConverter");
        assertEquals("SByteConverter", converters.getChecked(Byte.class));
    }
}

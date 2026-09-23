package io.github.ningpp.compat;

import static org.junit.jupiter.api.Assertions.assertEquals;

import org.junit.jupiter.api.Test;

/**
 * JsonTextWriter.writeValue(Double) must render with .NET's invariant G format
 * (shortest round-trip): "1.7976931348623157E+308", "4". The generated
 * Utf8JsonWriterTests build their EXPECTED strings through this writer and
 * compare them against the actual Utf8JsonWriter output, so the two writers
 * must produce identical double text.
 */
public class JsonTextWriterDoubleTest {

    private static String writeValueText(Object value) {
        java.io.StringWriter stringWriter = new java.io.StringWriter();
        JsonTextWriter writer = new JsonTextWriter(new CSharpTextWriter(stringWriter));
        writer.writeValue(value);
        writer.flush();
        return stringWriter.toString();
    }

    @Test
    public void writeValue_double_usesDotNetGFormat() {
        assertEquals("4", writeValueText(4.0d));
        assertEquals("0", writeValueText(0.0d));
        assertEquals("1970097990", writeValueText(1970097990.0d));
        assertEquals("1.7976931348623157E+308", writeValueText(Double.MAX_VALUE));
        assertEquals("-1.7976931348623157E+308", writeValueText(-Double.MAX_VALUE));
        assertEquals("1E-05", writeValueText(0.00001d));
    }

    @Test
    public void writeValue_otherNumbersKeepJavaText() {
        assertEquals("1234567", writeValueText(1234567));
        assertEquals("1234567", writeValueText(1234567L));
        assertEquals("true", writeValueText(Boolean.TRUE));
        assertEquals("\"text\"", writeValueText("text"));
    }
}

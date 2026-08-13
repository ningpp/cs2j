package io.github.ningpp.compat;

/**
 * Compat for Newtonsoft.Json.JsonTextWriter — minimal JSON writer used by the
 * converted tests' JsonTestHelper (writeStartObject/Array, writePropertyName,
 * writeValue, writeComment, flush, close).
 */
public class JsonTextWriter implements AutoCloseable {
    private final CSharpTextWriter writer;

    public JsonTextWriter(CSharpTextWriter writer) {
        this.writer = writer;
    }

    public void writeStartObject() {
        writer.print('{');
    }

    public void writeEndObject() {
        writer.print('}');
    }

    public void writeStartArray() {
        writer.print('[');
    }

    public void writeEndArray() {
        writer.print(']');
    }

    public void writePropertyName(String name) {
        writer.print('"');
        writer.print(name);
        writer.print("\":");
    }

    public void writeValue(String value) {
        if (value == null) {
            writer.print("null");
            return;
        }
        writer.print('"');
        writer.print(value);
        writer.print('"');
    }

    public void writeValue(Object value) {
        if (value == null) {
            writer.print("null");
            return;
        }
        if (value instanceof String s) {
            writeValue(s);
            return;
        }
        if (value instanceof Boolean b) {
            writer.print(b ? "true" : "false");
            return;
        }
        if (value instanceof Number n) {
            writer.print(n.toString());
            return;
        }
        writer.print('"');
        writer.print(value.toString());
        writer.print('"');
    }

    public void writeComment(String text) {
        writer.print("/*");
        writer.print(text);
        writer.print("*/");
    }

    public void writeNull() {
        writer.print("null");
    }

    public void writeRaw(String rawJson) {
        writer.print(rawJson);
    }

    public void flush() {
        writer.flush();
    }

    @Override
    public void close() {
        writer.flush();
    }
}

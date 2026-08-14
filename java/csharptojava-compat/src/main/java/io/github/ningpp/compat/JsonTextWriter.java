package io.github.ningpp.compat;

/**
 * Compat for Newtonsoft.Json.JsonTextWriter — minimal JSON writer used by the
 * converted tests' JsonTestHelper (writeStartObject/Array, writePropertyName,
 * writeValue, writeComment, flush, close).
 */
public class JsonTextWriter implements AutoCloseable {
    private final CSharpTextWriter writer;
    private final java.util.ArrayDeque<Character> containerStack = new java.util.ArrayDeque<>();
    private StringEscapeHandling stringEscapeHandling = StringEscapeHandling.Default;

    public JsonTextWriter(CSharpTextWriter writer) {
        this.writer = writer;
    }

    public void writeStartObject() {
        writer.print('{');
        containerStack.push('{');
    }

    public void writeEndObject() {
        writer.print('}');
        if (!containerStack.isEmpty() && containerStack.peek() == '{') {
            containerStack.pop();
        }
    }

    public void writeStartArray() {
        writer.print('[');
        containerStack.push('[');
    }

    public void writeEndArray() {
        writer.print(']');
        if (!containerStack.isEmpty() && containerStack.peek() == '[') {
            containerStack.pop();
        }
    }

    /** C# JsonWriter.WriteEnd() — closes the current container. */
    public void writeEnd() {
        if (!containerStack.isEmpty()) {
            char open = containerStack.pop();
            writer.print(open == '{' ? '}' : ']');
        }
    }

    public void writePropertyName(String name) {
        writer.print('"');
        writer.print(name);
        writer.print("\":");
    }

    /** C# JsonWriter.WritePropertyName(string, bool escape). */
    public void writePropertyName(String name, boolean escape) {
        writePropertyName(name);
    }

    /** C# JsonWriter.StringEscapeHandling. */
    public void setStringEscapeHandling(StringEscapeHandling value) {
        this.stringEscapeHandling = value;
    }

    public StringEscapeHandling getStringEscapeHandling() {
        return stringEscapeHandling;
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

    /** Mirrors Newtonsoft.Json.Formatting. */
    public enum Formatting {
        None,
        Indented
    }

    private Formatting formatting = Formatting.None;

    public void setFormatting(Formatting value) {
        this.formatting = value;
    }

    public Formatting getFormatting() {
        return formatting;
    }

    /** Mirrors JsonWriter.WriteToken(JsonReader) — drains the reader into this writer. */
    public void writeToken(JsonTextReader reader, boolean writeChildren) {
        while (reader.read()) {
            JsonToken token = reader.getTokenType();
            Object value = reader.getValue();
            switch (token) {
                case StartObject:
                    writeStartObject();
                    break;
                case EndObject:
                    writeEndObject();
                    break;
                case StartArray:
                    writeStartArray();
                    break;
                case EndArray:
                    writeEndArray();
                    break;
                case PropertyName:
                    writePropertyName(value == null ? "" : value.toString());
                    break;
                case String:
                    writeValue(value == null ? "" : value.toString());
                    break;
                case Integer:
                case Float:
                case Raw:
                case Boolean:
                    writeValue(value);
                    break;
                case Null:
                    writeNull();
                    break;
                default:
                    break;
            }
        }
    }

    public void flush() {
        writer.flush();
    }

    @Override
    public void close() {
        writer.flush();
    }
}

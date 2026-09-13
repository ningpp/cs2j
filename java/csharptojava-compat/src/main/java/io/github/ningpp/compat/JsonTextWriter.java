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
    private Formatting formatting = Formatting.None;
    private int depth = 0;
    private boolean containerHasItems = false;
    private boolean afterPropertyName = false;

    public JsonTextWriter(CSharpTextWriter writer) {
        this.writer = writer;
    }

    private void writeIndentation() {
        if (formatting == Formatting.Indented) {
            writer.print(System.lineSeparator());
            for (int i = 0; i < depth; i++) {
                writer.print("  ");
            }
        }
    }

    private void writeItemIndentation() {
        if (formatting == Formatting.Indented && depth > 0) {
            writer.print(System.lineSeparator());
            for (int i = 0; i < depth; i++) {
                writer.print("  ");
            }
        }
    }

    private void writeBeforeValue() {
        if (afterPropertyName) {
            // Value of the property just written: no comma, keep on the same line.
            if (formatting == Formatting.Indented) {
                writer.print(' ');
            }
            afterPropertyName = false;
            return;
        }
        if (containerHasItems) {
            writer.print(',');
        }
        writeItemIndentation();
        containerHasItems = true;
    }

    private void writeBeforeContainerStart() {
        if (afterPropertyName) {
            if (formatting == Formatting.Indented) {
                writer.print(' ');
            }
            afterPropertyName = false;
        } else {
            if (containerHasItems) {
                writer.print(',');
            }
            writeItemIndentation();
        }
    }

    private void writeEndContainer(char open) {
        if (depth > 0) {
            depth--;
        }
        if (containerHasItems) {
            writeIndentation();
        }
        writer.print(open == '{' ? '}' : ']');
        if (!containerStack.isEmpty()) {
            containerStack.pop();
        }
        containerHasItems = true;
        afterPropertyName = false;
    }

    public void writeStartObject() {
        writeBeforeContainerStart();
        writer.print('{');
        containerStack.push('{');
        depth++;
        containerHasItems = false;
    }

    public void writeEndObject() {
        writeEndContainer('{');
    }

    public void writeStartArray() {
        writeBeforeContainerStart();
        writer.print('[');
        containerStack.push('[');
        depth++;
        containerHasItems = false;
    }

    public void writeEndArray() {
        writeEndContainer('[');
    }

    /** C# JsonWriter.WriteEnd() — closes the current container. */
    public void writeEnd() {
        if (!containerStack.isEmpty()) {
            char open = containerStack.peek();
            writeEndContainer(open);
        }
    }

    public void writePropertyName(String name) {
        if (containerHasItems) {
            writer.print(',');
        }
        writeItemIndentation();
        writer.print('"');
        writer.print(escapeJsonString(name));
        writer.print("\":");
        containerHasItems = true;
        afterPropertyName = true;
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
        writeBeforeValue();
        if (value == null) {
            writer.print("null");
            return;
        }
        writer.print('"');
        writer.print(escapeJsonString(value));
        writer.print('"');
    }

    public void writeValue(Object value) {
        if (value == null) {
            writeBeforeValue();
            writer.print("null");
            return;
        }
        if (value instanceof String s) {
            writeValue(s);
            return;
        }
        if (value instanceof CSharpDateTime dt) {
            writeBeforeValue();
            writer.print('"');
            writer.print(formatIsoDateTime(dt.getYear(), dt.getMonth(), dt.getDay(), dt.getHour(), dt.getMinute(), dt.getSecond(), dt.getMillisecond(), dt.getKind()));
            writer.print('"');
            return;
        }
        if (value instanceof CSharpDateTimeOffset dto) {
            writeBeforeValue();
            writer.print('"');
            CSharpTimeSpan offset = dto.getOffset();
            int offsetSeconds = (int) (offset.getTicks() / CSharpTimeSpan.TICKS_PER_SECOND);
            String sign = offsetSeconds < 0 ? "-" : "+";
            int absSeconds = Math.abs(offsetSeconds);
            writer.print(formatIsoDateTime(dto.getYear(), dto.getMonth(), dto.getDay(), dto.getHour(), dto.getMinute(), dto.getSecond(), dto.getMillisecond(), null));
            writer.print(sign);
            writer.print(pad2(absSeconds / 3600));
            writer.print(':');
            writer.print(pad2((absSeconds % 3600) / 60));
            writer.print('"');
            return;
        }
        writeBeforeValue();
        if (value instanceof Boolean b) {
            writer.print(b ? "true" : "false");
            return;
        }
        if (value instanceof Number n) {
            writer.print(n.toString());
            return;
        }
        if (value instanceof Decimal dec) {
            // C# decimal is a value type, so the compat Decimal wrapper does not extend
            // java.lang.Number; without this branch it fell through to the "quote anything
            // unknown" fallback and Newtonsoft-rendered expected JSON contained
            // ["1234567.0", ...] where Newtonsoft itself writes a bare 1234567.0.
            writer.print(dec.toString());
            return;
        }
        if (value instanceof byte[] bytes) {
            // Newtonsoft writes byte[] as a Base64 string. Write it in place: delegating to
            // writeValue(String) would call writeBeforeValue() a SECOND time (it already ran
            // above), which emits a stray separator and turns the object into
            // {"message":,"AQIDBAUG"} - the expected string of every
            // Utf8JsonWriterTests#writeBase64String theory row (12 tests).
            writer.print('"');
            writer.print(escapeJsonString(java.util.Base64.getEncoder().encodeToString(bytes)));
            writer.print('"');
            return;
        }
        writer.print('"');
        writer.print(value.toString());
        writer.print('"');
    }

    /**
     * Mirrors Newtonsoft.Json DateTimeUtils.WriteDateTimeString: ISO 8601
     * yyyy-MM-ddTHH:mm:ss, with sub-second digits only when non-zero (trailing
     * zeros trimmed), and Z for DateTimeKind.Utc.
     */
    private static String formatIsoDateTime(int year, int month, int day, int hour, int minute, int second, int millisecond, DateTimeKind kind) {
        StringBuilder sb = new StringBuilder(32);
        sb.append(pad4(year)).append('-').append(pad2(month)).append('-').append(pad2(day))
          .append('T').append(pad2(hour)).append(':').append(pad2(minute)).append(':').append(pad2(second));
        if (millisecond != 0) {
            sb.append('.').append(String.format("%03d", millisecond));
        }
        if (kind == DateTimeKind.Utc) {
            sb.append('Z');
        }
        return sb.toString();
    }

    private static String pad2(int v) {
        return v < 10 ? "0" + v : Integer.toString(v);
    }

    private static String pad4(int v) {
        return v < 10 ? "000" + v : v < 100 ? "00" + v : v < 1000 ? "0" + v : Integer.toString(v);
    }

    /**
     * Escapes a JSON string the same way Utf8JsonWriter does: quote as \\u0022,
     * backslash as \\, \\b \\f \\n \\r \\t short escapes, other control characters
     * and HTML-sensitive ASCII characters as uppercase \\uXXXX.
     */
    private String escapeJsonString(String s) {
        boolean escapeHtml = stringEscapeHandling == StringEscapeHandling.EscapeHtml;
        // Newtonsoft's StringEscapeHandling.EscapeNonAscii writes every non-ASCII UTF-16 code unit
        // as a backslash-u escape. It was never honoured here, so the expected string of
        // Utf8JsonWriterTests#highSurrogateMissingGetsReplaced (built with EscapeNonAscii) came out
        // with the raw astral characters while the writer under test - whose default encoder escapes
        // them - produced the surrogate escapes, and the four theory rows failed.
        boolean escapeNonAscii = stringEscapeHandling == StringEscapeHandling.EscapeNonAscii;
        boolean needsEscape = false;
        for (int i = 0; i < s.length(); i++) {
            char ch = s.charAt(i);
            if (ch == '"' || ch == '\\' || ch < 0x20
                || (escapeNonAscii && ch > 0x7F) || (escapeHtml && isHtmlSensitive(ch))) {
                needsEscape = true;
                break;
            }
        }
        if (!needsEscape) {
            return s;
        }
        StringBuilder sb = new StringBuilder(s.length() + 16);
        for (int i = 0; i < s.length(); i++) {
            char ch = s.charAt(i);
            switch (ch) {
                case '"':
                    sb.append("\\u0022");
                    break;
                case '\\':
                    sb.append("\\\\");
                    break;
                case '\b':
                    sb.append("\\b");
                    break;
                case '\f':
                    sb.append("\\f");
                    break;
                case '\n':
                    sb.append("\\n");
                    break;
                case '\r':
                    sb.append("\\r");
                    break;
                case '\t':
                    sb.append("\\t");
                    break;
                default:
                    if (ch < 0x20 || (escapeNonAscii && ch > 0x7F) || (escapeHtml && isHtmlSensitive(ch))) {
                        sb.append(String.format("\\u%04X", (int) ch));
                    } else {
                        sb.append(ch);
                    }
            }
        }
        return sb.toString();
    }

    private static boolean isHtmlSensitive(char ch) {
        return ch == '<' || ch == '>' || ch == '&' || ch == '\'' || ch == '+' || ch == '`';
    }

    public void writeComment(String text) {
        // Comments are not JSON items: no comma separator and they do not
        // contribute to the container item state (matches Newtonsoft/Utf8JsonWriter).
        writeItemIndentation();
        writer.print("/*");
        writer.print(text);
        writer.print("*/");
    }

    public void writeNull() {
        writeBeforeValue();
        writer.print("null");
    }

    public void writeRaw(String rawJson) {
        writeBeforeValue();
        writer.print(rawJson);
    }

    /** Mirrors Newtonsoft.Json.Formatting. */
    public enum Formatting {
        None,
        Indented
    }

    public void setFormatting(Formatting value) {
        this.formatting = value;
    }

    public Formatting getFormatting() {
        return formatting;
    }

    /** Mirrors JsonWriter.WriteToken(JsonReader, bool) — writes the reader's CURRENT token only. */
    public void writeToken(JsonTextReader reader, boolean writeChildren) {
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

    public void flush() {
        writer.flush();
    }

    @Override
    public void close() {
        writer.flush();
    }
}

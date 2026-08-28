package io.github.ningpp.compat;

import java.io.IOException;

/**
 * Compat for Newtonsoft.Json.JsonTextReader — minimal streaming JSON reader used
 * by the converted tests' JsonTestHelper. Parses tokens via a simple hand-rolled
 * scanner over the compat TextReader.
 */
public class JsonTextReader implements AutoCloseable {
    private final TextReader reader;
    private int current = -1;
    private boolean pushedBack;
    private JsonToken tokenType = JsonToken.None;
    private Object value;
    private FloatParseHandling floatParseHandling = FloatParseHandling.Double;
    private final StringBuilder sb = new StringBuilder();

    public JsonTextReader(TextReader reader) {
        this.reader = reader;
    }

    private int peekChar() {
        if (pushedBack) {
            return current;
        }
        // TextReader.peek() is non-consuming; do not mark it as pushed back.
        return reader.peek();
    }

    private int nextChar() {
        if (pushedBack) {
            pushedBack = false;
            return current;
        }
        return reader.read();
    }

    private void skipWhitespace() {
        while (peekChar() >= 0) {
            int c = peekChar();
            if (c == ' ' || c == '\t' || c == '\n' || c == '\r') {
                nextChar();
            } else {
                break;
            }
        }
    }

    public boolean read() {
        try {
            skipWhitespace();
            int c = peekChar();
            if (c < 0) {
                tokenType = JsonToken.None;
                value = null;
                return false;
            }
            switch (c) {
                case '{':
                    nextChar();
                    tokenType = JsonToken.StartObject;
                    value = null;
                    break;
                case '}':
                    nextChar();
                    tokenType = JsonToken.EndObject;
                    value = null;
                    break;
                case '[':
                    nextChar();
                    tokenType = JsonToken.StartArray;
                    value = null;
                    break;
                case ']':
                    nextChar();
                    tokenType = JsonToken.EndArray;
                    value = null;
                    break;
                case ',':
                case ':':
                    // Structural separators are not tokens in Newtonsoft.Json;
                    // skip and advance to the next real token.
                    nextChar();
                    return read();
                case '"':
                    nextChar();
                    String s = readStringLiteral();
                    // Peek ahead: if the next non-whitespace char is ':', this is a
                    // property name, not a string value.
                    skipWhitespace();
                    if (peekChar() == ':') {
                        nextChar(); // consume ':'
                        tokenType = JsonToken.PropertyName;
                    } else {
                        tokenType = JsonToken.String;
                    }
                    value = s;
                    break;
                case 't':
                    if (matchKeyword("true")) {
                        tokenType = JsonToken.Boolean;
                        value = Boolean.TRUE;
                    } else {
                        tokenType = JsonToken.Raw;
                        value = readRawToken();
                    }
                    break;
                case 'f':
                    if (matchKeyword("false")) {
                        tokenType = JsonToken.Boolean;
                        value = Boolean.FALSE;
                    } else {
                        tokenType = JsonToken.Raw;
                        value = readRawToken();
                    }
                    break;
                case 'n':
                    if (matchKeyword("null")) {
                        tokenType = JsonToken.Null;
                        value = null;
                    } else {
                        tokenType = JsonToken.Raw;
                        value = readRawToken();
                    }
                    break;
                default:
                    tokenType = JsonToken.Raw;
                    value = readRawToken();
                    break;
            }
            return true;
        } catch (IOException e) {
            throw new RuntimeException(e);
        }
    }

    /** Attempts to match a literal keyword (true/false/null) from the current position. */
    private boolean matchKeyword(String keyword) throws IOException {
        for (int i = 0; i < keyword.length(); i++) {
            if (peekChar() != keyword.charAt(i)) {
                return false;
            }
            nextChar();
        }
        int next = peekChar();
        return next < 0 || next == ',' || next == '}' || next == ']' || next == ' ' || next == '\t' || next == '\n' || next == '\r';
    }

    private String readStringLiteral() throws IOException {
        sb.setLength(0);
        while (true) {
            int c = nextChar();
            if (c < 0) {
                break;
            }
            if (c == '"') {
                break;
            }
            if (c == '\\') {
                int esc = nextChar();
                switch (esc) {
                    case 'n':
                        sb.append('\n');
                        break;
                    case 'r':
                        sb.append('\r');
                        break;
                    case 't':
                        sb.append('\t');
                        break;
                    case 'b':
                        sb.append('\b');
                        break;
                    case 'f':
                        sb.append('\f');
                        break;
                    case 'u':
                        int hex = 0;
                        for (int i = 0; i < 4; i++) {
                            int h = nextChar();
                            hex = (hex << 4) | Character.digit(h, 16);
                        }
                        sb.append((char) hex);
                        break;
                    default:
                        sb.append((char) esc);
                        break;
                }
            } else {
                sb.append((char) c);
            }
        }
        return sb.toString();
    }

    private Object readRawToken() throws IOException {
        sb.setLength(0);
        while (peekChar() >= 0) {
            int c = peekChar();
            if (c == ',' || c == '}' || c == ']' || c == ' ' || c == '\t' || c == '\n' || c == '\r') {
                break;
            }
            sb.append((char) nextChar());
        }
        String raw = sb.toString();
        if (raw.equals("true")) {
            return Boolean.TRUE;
        }
        if (raw.equals("false")) {
            return Boolean.FALSE;
        }
        if (raw.equals("null")) {
            return null;
        }
        return raw;
    }

    public JsonToken getTokenType() {
        return tokenType;
    }

    /**
     * Mirrors JsonReader.Skip() — advances the reader past the current value
     * (including nested objects/arrays).
     */
    public void skip() {
        int depth = 0;
        do {
            if (!read()) {
                return;
            }
            JsonToken t = getTokenType();
            switch (t) {
                case StartObject:
                case StartArray:
                    depth++;
                    break;
                case EndObject:
                case EndArray:
                    depth--;
                    break;
                default:
                    break;
            }
        } while (depth > 0);
    }

    public Object getValue() {
        return value;
    }

    private int maxDepth = 64;
    private int depth;

    /** C# JsonReader.MaxDepth — setter used by the converted tests. */
    public void setMaxDepth(int maxDepth) {
        this.maxDepth = maxDepth;
    }

    public int getMaxDepth() {
        return maxDepth;
    }

    /** C# JsonReader.Depth — current nesting depth. */
    public int getDepth() {
        return depth;
    }

    public void setFloatParseHandling(FloatParseHandling handling) {
        floatParseHandling = handling;
    }

    public FloatParseHandling getFloatParseHandling() {
        return floatParseHandling;
    }

    @Override
    public void close() {
        try {
            reader.close();
        } catch (Exception e) {
            throw new RuntimeException(e);
        }
    }
}

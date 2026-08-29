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
                case '/':
                    // Newtonsoft.Json supports /* */ block comments and // line
                    // comments; they are surfaced as JsonToken.Comment.
                    nextChar();
                    int c2 = peekChar();
                    if (c2 == '*') {
                        nextChar();
                        StringBuilder sb = new StringBuilder();
                        while (true) {
                            int cc = nextChar();
                            if (cc < 0) {
                                break;
                            }
                            if (cc == '*' && peekChar() == '/') {
                                nextChar();
                                break;
                            }
                            sb.append((char) cc);
                        }
                        tokenType = JsonToken.Comment;
                        value = sb.toString();
                    } else if (c2 == '/') {
                        nextChar();
                        StringBuilder sb = new StringBuilder();
                        while (true) {
                            int cc = nextChar();
                            if (cc < 0 || cc == '\n' || cc == '\r') {
                                break;
                            }
                            sb.append((char) cc);
                        }
                        tokenType = JsonToken.Comment;
                        value = sb.toString();
                    } else {
                        tokenType = JsonToken.Raw;
                        value = readRawToken();
                    }
                    break;
                case '{':
                    nextChar();
                    tokenType = JsonToken.StartObject;
                    value = null;
                    // Newtonsoft JsonReader.Depth: container start/end tokens
                    // report the depth BEFORE entering; value tokens report the
                    // enclosing container depth (getDepth() subtracts for the
                    // just-opened container).
                    depth++;
                    break;
                case '}':
                    nextChar();
                    tokenType = JsonToken.EndObject;
                    value = null;
                    if (depth > 0) {
                        depth--;
                    }
                    break;
                case '[':
                    nextChar();
                    tokenType = JsonToken.StartArray;
                    value = null;
                    depth++;
                    break;
                case ']':
                    nextChar();
                    tokenType = JsonToken.EndArray;
                    value = null;
                    if (depth > 0) {
                        depth--;
                    }
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
                    if (c == '-' || (c >= '0' && c <= '9')) {
                        // Newtonsoft JsonToken.Integer/Float for numeric values.
                        String num = readNumberLiteral();
                        if (num.indexOf('.') >= 0 || num.indexOf('e') >= 0 || num.indexOf('E') >= 0) {
                            tokenType = JsonToken.Float;
                            value = Double.parseDouble(num);
                        } else {
                            tokenType = JsonToken.Integer;
                            value = Long.parseLong(num);
                        }
                    } else {
                        tokenType = JsonToken.Raw;
                        value = readRawToken();
                    }
                    break;
            }
            return true;
        } catch (IOException e) {
            throw new RuntimeException(e);
        }
    }

    /** Reads a numeric literal starting at the current position (including optional leading '-'). */
    private String readNumberLiteral() throws IOException {
        StringBuilder sb = new StringBuilder();
        while (true) {
            int c = peekChar();
            if ((c >= '0' && c <= '9') || c == '-' || c == '+' || c == '.' || c == 'e' || c == 'E') {
                sb.append((char) c);
                nextChar();
            } else {
                break;
            }
        }
        return sb.toString();
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
    /** Mirrors Newtonsoft JsonReader.Skip() — skips the children of the current token. */
    public void skip() {
        if (getTokenType() == JsonToken.PropertyName) {
            if (!read()) {
                return;
            }
        }
        if (getTokenType() == JsonToken.StartObject || getTokenType() == JsonToken.StartArray) {
            int depth = getDepth();
            do {
                if (!read()) {
                    return;
                }
            } while (depth < getDepth());
        }
        // For all other token types (including None and scalar values) the
        // reader does not move.
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
        // Newtonsoft reports the depth BEFORE entering a container for the
        // container's own Start/End token; the field already counts the open
        // container, so subtract one for Start tokens.
        if (tokenType == JsonToken.StartObject || tokenType == JsonToken.StartArray) {
            return Math.max(depth - 1, 0);
        }
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

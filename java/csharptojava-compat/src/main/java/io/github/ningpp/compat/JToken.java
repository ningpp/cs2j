package io.github.ningpp.compat;

import java.util.ArrayList;
import java.util.List;

/**
 * Compat for Newtonsoft.Json.JToken — minimal round-trip token: readFrom(reader)
 * materializes the JSON text, writeTo(writer) re-emits it. Supports the indexer
 * (get(String)), iteration over arrays/objects and the static conversion helpers
 * (toString/toInt/toBoolean/toDouble) used by the converted tests.
 */
public class JToken implements Iterable<JToken> {
    private final String raw;

    private JToken(String raw) {
        this.raw = raw;
    }

    public static JToken readFrom(JsonTextReader reader) {
        String all = readAll(reader);
        return new JToken(all);
    }

    private static String readAll(JsonTextReader reader) {
        // Re-serialize what the reader consumed: simplest faithful approach is to
        // drain the reader and rebuild compact JSON. The reader exposes raw values
        // per token; reconstruct a compact representation.
        StringBuilder sb = new StringBuilder();
        boolean first = true;
        boolean inObject = false;
        boolean inArray = false;
        boolean firstInContainer = true;
        String pendingProperty = null;
        while (reader.read()) {
            JsonToken t = reader.getTokenType();
            Object v = reader.getValue();
            switch (t) {
                case StartObject:
                    if (pendingProperty != null) {
                        sb.append('"').append(pendingProperty).append("\":");
                        pendingProperty = null;
                    }
                    sb.append('{');
                    inObject = true;
                    firstInContainer = true;
                    break;
                case EndObject:
                    sb.append('}');
                    inObject = false;
                    break;
                case StartArray:
                    sb.append('[');
                    inArray = true;
                    firstInContainer = true;
                    break;
                case EndArray:
                    sb.append(']');
                    inArray = false;
                    break;
                case PropertyName:
                    sb.append('"').append(v).append("\":");
                    break;
                case String:
                    sb.append('"').append(v).append('"');
                    break;
                case Integer:
                case Float:
                case Raw:
                case Boolean:
                case Null:
                    if (v == null) {
                        sb.append("null");
                    } else {
                        sb.append(v.toString());
                    }
                    break;
                default:
                    break;
            }
        }
        return sb.toString();
    }

    public void writeTo(JsonTextWriter writer) {
        writer.writeRaw(raw);
    }

    /** C# JToken indexer — obj["propertyName"] returns the child token or null. */
    public JToken get(String propertyName) {
        if (raw == null || raw.length() < 3) {
            return null;
        }
        String key = "\"" + propertyName + "\"";
        int idx = raw.indexOf(key);
        if (idx < 0) {
            return null;
        }
        int colon = raw.indexOf(':', idx + key.length());
        if (colon < 0) {
            return null;
        }
        int start = colon + 1;
        while (start < raw.length() && (raw.charAt(start) == ' ' || raw.charAt(start) == '\t')) {
            start++;
        }
        int end = valueEnd(raw, start);
        if (end < 0) {
            return null;
        }
        return new JToken(raw.substring(start, end));
    }

    private static int valueEnd(String text, int start) {
        char c = text.charAt(start);
        if (c == '"') {
            int i = start + 1;
            while (i < text.length()) {
                char ch = text.charAt(i);
                if (ch == '\\') {
                    i += 2;
                    continue;
                }
                if (ch == '"') {
                    return i + 1;
                }
                i++;
            }
            return -1;
        }
        if (c == '{' || c == '[') {
            char open = c;
            char close = c == '{' ? '}' : ']';
            int depth = 0;
            boolean inString = false;
            for (int i = start; i < text.length(); i++) {
                char ch = text.charAt(i);
                if (inString) {
                    if (ch == '\\') {
                        i++;
                    } else if (ch == '"') {
                        inString = false;
                    }
                    continue;
                }
                if (ch == '"') {
                    inString = true;
                } else if (ch == open) {
                    depth++;
                } else if (ch == close) {
                    depth--;
                    if (depth == 0) {
                        return i + 1;
                    }
                }
            }
            return -1;
        }
        int i = start;
        while (i < text.length() && text.charAt(i) != ',' && text.charAt(i) != '}'
               && text.charAt(i) != ']' && text.charAt(i) != ' ' && text.charAt(i) != '\t'
               && text.charAt(i) != '\n' && text.charAt(i) != '\r') {
            i++;
        }
        return i;
    }

    /** C# (string)JToken — the string value (without quotes), or the raw text for other types. */
    public static String toString(JToken token) {
        if (token == null || token.raw == null) {
            return null;
        }
        String rawValue = token.raw;
        if (rawValue.length() >= 2 && rawValue.charAt(0) == '"' && rawValue.charAt(rawValue.length() - 1) == '"') {
            StringBuilder sb = new StringBuilder();
            for (int i = 1; i < rawValue.length() - 1; i++) {
                char ch = rawValue.charAt(i);
                if (ch == '\\' && i + 1 < rawValue.length() - 1) {
                    char esc = rawValue.charAt(++i);
                    switch (esc) {
                        case 'n': sb.append('\n'); break;
                        case 'r': sb.append('\r'); break;
                        case 't': sb.append('\t'); break;
                        case 'b': sb.append('\b'); break;
                        case 'f': sb.append('\f'); break;
                        case 'u':
                            if (i + 4 < rawValue.length() - 1) {
                                sb.append((char) Integer.parseInt(rawValue.substring(i + 1, i + 5), 16));
                                i += 4;
                            }
                            break;
                        default: sb.append(esc); break;
                    }
                } else {
                    sb.append(ch);
                }
            }
            return sb.toString();
        }
        return rawValue;
    }

    /** C# (int)JToken — parses the numeric value. */
    public static int toInt(JToken token) {
        return Integer.parseInt(toString(token).trim());
    }

    /** C# (bool)JToken — parses the boolean value. */
    public static boolean toBoolean(JToken token) {
        return Boolean.parseBoolean(toString(token).trim());
    }

    /** C# (double)JToken — parses the numeric value. */
    public static double toDouble(JToken token) {
        return Double.parseDouble(toString(token).trim());
    }

    /** Iterates the elements of a JSON array token (C# foreach over a JArray). */
    @Override
    public java.util.Iterator<JToken> iterator() {
        List<JToken> elements = new ArrayList<>();
        if (raw != null && raw.length() >= 2 && raw.charAt(0) == '[' && raw.charAt(raw.length() - 1) == ']') {
            String inner = raw.substring(1, raw.length() - 1);
            int i = 0;
            int n = inner.length();
            while (i < n) {
                while (i < n && (inner.charAt(i) == ' ' || inner.charAt(i) == '\t' || inner.charAt(i) == ',')) {
                    i++;
                }
                if (i >= n) {
                    break;
                }
                int end = valueEnd(inner, i);
                if (end < 0) {
                    break;
                }
                elements.add(new JToken(inner.substring(i, end)));
                i = end;
            }
        }
        return elements.iterator();
    }

    @Override
    public String toString() {
        return raw;
    }
}

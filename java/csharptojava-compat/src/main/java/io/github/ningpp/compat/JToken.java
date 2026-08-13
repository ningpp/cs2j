package io.github.ningpp.compat;

/**
 * Compat for Newtonsoft.Json.JToken — minimal round-trip token: readFrom(reader)
 * materializes the JSON text, writeTo(writer) re-emits it.
 */
public class JToken {
    private String raw;

    private JToken(String raw) {
        this.raw = raw;
    }

    public static JToken readFrom(JsonTextReader reader) {
        StringBuilder sb = new StringBuilder();
        String all = readAll(reader);
        JToken token = new JToken(all);
        return token;
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

    @Override
    public String toString() {
        return raw;
    }
}

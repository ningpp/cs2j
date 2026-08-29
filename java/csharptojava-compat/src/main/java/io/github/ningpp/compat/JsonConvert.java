package io.github.ningpp.compat;

/**
 * Compat for Newtonsoft.Json.JsonConvert — minimal JSON scalar deserialization
 * used by the converted tests (null / string / boolean / number values).
 */
public class JsonConvert {
    private JsonConvert() {
    }

    public static Object deserializeObject(String json) {
        String t = json == null ? "null" : json.trim();
        if (t.equals("null")) {
            return null;
        }
        if (t.equals("true")) {
            return Boolean.TRUE;
        }
        if (t.equals("false")) {
            return Boolean.FALSE;
        }
        if (t.length() >= 2 && t.startsWith("\"") && t.endsWith("\"")) {
            String inner = t.substring(1, t.length() - 1);
            // Newtonsoft parses ISO 8601 date strings into DateTime values, not
            // plain strings (JValue auto-detection). Match that so
            // Assert.Equal(newtonsoftObj, systemTextJsonObj) compares equal
            // CSharpDateTime instances instead of a String vs DateTime mismatch.
            io.github.ningpp.compat.ObjectHolder<CSharpDateTime> dtHolder = new io.github.ningpp.compat.ObjectHolder<>();
            if (CSharpDateTime.tryParse(inner, dtHolder)) {
                return dtHolder.value;
            }
            return inner.replace("\\\"", "\"").replace("\\\\", "\\");
        }
        try {
            return Integer.parseInt(t);
        } catch (NumberFormatException ignored) {
        }
        try {
            return Long.parseLong(t);
        } catch (NumberFormatException ignored) {
        }
        try {
            return Double.parseDouble(t);
        } catch (NumberFormatException ignored) {
        }
        return null;
    }

    public static Object deserializeObject(String json, Class type) {
        return deserializeObject(json);
    }

    public static Object deserializeObject(String json, Class type, Object settings) {
        return deserializeObject(json);
    }

    public static String serializeObject(Object value) {
        if (value == null) {
            return "null";
        }
        if (value instanceof Boolean) {
            return value.toString();
        }
        if (value instanceof Number) {
            return value.toString();
        }
        if (value instanceof String) {
            String s = (String) value;
            return "\"" + s.replace("\\", "\\\\").replace("\"", "\\\"") + "\"";
        }
        return "\"" + value.toString() + "\"";
    }

    public static String serializeObject(Object value, Object settings) {
        return serializeObject(value);
    }
}

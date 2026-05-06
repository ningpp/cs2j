package io.github.ningpp.compat;

import org.junit.jupiter.api.Test;
import java.util.LinkedHashMap;
import java.util.Map;
import static org.junit.jupiter.api.Assertions.*;

/**
 * Tests for JsonSerializer and JsonSerializerOptions.
 */
class JsonSerializerTest {

    // ---- JsonSerializer.serialize ----

    @Test
    void serialize_simpleMap() {
        Map<String, Object> map = new LinkedHashMap<>();
        map.put("name", "Alice");
        map.put("age", 30);

        String json = JsonSerializer.serialize(map);
        assertNotNull(json);
        assertTrue(json.contains("\"name\""));
        assertTrue(json.contains("\"Alice\""));
        assertTrue(json.contains("\"age\""));
        assertTrue(json.contains("30"));
    }

    @Test
    void serialize_withOptions() {
        Map<String, Object> map = new LinkedHashMap<>();
        map.put("key", "value");

        JsonSerializerOptions options = new JsonSerializerOptions();
        options.setWriteIndented(true);

        String json = JsonSerializer.serialize(map, options);
        assertNotNull(json);
        assertTrue(json.contains("\"key\""));
        assertTrue(json.contains("\"value\""));
    }

    @Test
    void serialize_stringValue() {
        Map<String, String> map = new LinkedHashMap<>();
        map.put("greeting", "hello world");

        String json = JsonSerializer.serialize(map);
        assertTrue(json.contains("\"greeting\""));
        assertTrue(json.contains("\"hello world\""));
    }

    @Test
    void serialize_numberValue() {
        Map<String, Integer> map = new LinkedHashMap<>();
        map.put("count", 42);

        String json = JsonSerializer.serialize(map);
        assertTrue(json.contains("42"));
    }

    @Test
    void serialize_booleanValue() {
        Map<String, Boolean> map = new LinkedHashMap<>();
        map.put("active", true);

        String json = JsonSerializer.serialize(map);
        assertTrue(json.contains("true"));
    }

    @Test
    void serialize_nullValue() {
        Map<String, Object> map = new LinkedHashMap<>();
        map.put("nothing", null);

        String json = JsonSerializer.serialize(map);
        assertTrue(json.contains("null"));
    }

    @Test
    void serialize_emptyMap() {
        Map<String, Object> map = new LinkedHashMap<>();
        String json = JsonSerializer.serialize(map);
        assertNotNull(json);
        assertEquals("{}", json);
    }

    // ---- JsonSerializer.deserialize ----

    @Test
    void deserialize_toMap() {
        String json = "{\"name\":\"Bob\",\"score\":95}";
        Map<?, ?> result = JsonSerializer.deserialize(json, Map.class);
        assertNotNull(result);
        assertEquals("Bob", result.get("name"));
        assertEquals(95, result.get("score"));
    }

    @Test
    void deserialize_stringValue() {
        String json = "{\"key\":\"hello\"}";
        Map<?, ?> result = JsonSerializer.deserialize(json, Map.class);
        assertEquals("hello", result.get("key"));
    }

    @Test
    void deserialize_booleanValue() {
        String json = "{\"flag\":true}";
        Map<?, ?> result = JsonSerializer.deserialize(json, Map.class);
        assertEquals(true, result.get("flag"));
    }

    @Test
    void deserialize_emptyObject() {
        String json = "{}";
        Map<?, ?> result = JsonSerializer.deserialize(json, Map.class);
        assertNotNull(result);
        assertTrue(result.isEmpty());
    }

    @Test
    void deserialize_toObjectClass() {
        String json = "{\"x\":1}";
        Object result = JsonSerializer.deserialize(json);
        assertNotNull(result);
    }

    // ---- Round-trip: serialize then deserialize ----

    @Test
    void roundTrip_map() {
        Map<String, Object> original = new LinkedHashMap<>();
        original.put("name", "Charlie");
        original.put("age", 25);
        original.put("active", true);

        String json = JsonSerializer.serialize(original);
        Map<?, ?> restored = JsonSerializer.deserialize(json, Map.class);

        assertEquals("Charlie", restored.get("name"));
        assertEquals(25, restored.get("age"));
        assertEquals(true, restored.get("active"));
    }

    // ---- JsonSerializerOptions ----

    @Test
    void options_defaultValues() {
        JsonSerializerOptions options = new JsonSerializerOptions();
        assertFalse(options.isWriteIndented());
    }

    @Test
    void options_setWriteIndented() {
        JsonSerializerOptions options = new JsonSerializerOptions();
        options.setWriteIndented(true);
        assertTrue(options.isWriteIndented());
        options.setWriteIndented(false);
        assertFalse(options.isWriteIndented());
    }

    // ---- Error handling ----

    @Test
    void deserialize_invalidJson_throws() {
        assertThrows(RuntimeException.class, () -> {
            JsonSerializer.deserialize("{invalid json", Map.class);
        });
    }
}

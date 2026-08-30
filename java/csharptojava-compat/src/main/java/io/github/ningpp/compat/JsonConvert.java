package io.github.ningpp.compat;

import com.fasterxml.jackson.core.JsonParser;
import com.fasterxml.jackson.databind.BeanDescription;
import com.fasterxml.jackson.databind.DeserializationConfig;
import com.fasterxml.jackson.databind.DeserializationFeature;
import com.fasterxml.jackson.databind.JavaType;
import com.fasterxml.jackson.databind.JsonNode;
import com.fasterxml.jackson.databind.ObjectMapper;
import com.fasterxml.jackson.databind.PropertyNamingStrategies;
import com.fasterxml.jackson.databind.deser.BeanDeserializerBuilder;
import com.fasterxml.jackson.databind.deser.BeanDeserializerModifier;
import com.fasterxml.jackson.databind.introspect.BeanPropertyDefinition;
import java.util.ArrayList;
import java.util.Iterator;
import java.util.List;

/**
 * Compat for Newtonsoft.Json.JsonConvert — uses Jackson for JSON deserialization.
 * When a Class token is provided, deserializes into that concrete type; otherwise
 * into generic structures (Map/List/scalar).
 *
 * Test data uses MIXED property naming: some files use snake_case (C#
 * SnakeCaseNamingStrategy), others use camelCase. The mapper accepts both by
 * registering a BeanDeserializerModifier that adds a SECOND property binding
 * for the snake_case form of each camelCased Java field.
 */
public class JsonConvert {
    private JsonConvert() {
    }

    private static volatile ObjectMapper _mapper = createDefaultMapper();

    private static ObjectMapper createDefaultMapper() {
        ObjectMapper mapper = new ObjectMapper()
                .configure(DeserializationFeature.FAIL_ON_UNKNOWN_PROPERTIES, false)
                .configure(DeserializationFeature.USE_BIG_DECIMAL_FOR_FLOATS, true)
                .setPropertyNamingStrategy(PropertyNamingStrategies.SNAKE_CASE);
        // BeanDeserializerModifier must be registered via a SimpleModule —
        // ObjectMapper.setDeserializerModifier does not exist in Jackson 2.x.
        com.fasterxml.jackson.databind.module.SimpleModule module =
                new com.fasterxml.jackson.databind.module.SimpleModule();
        module.setDeserializerModifier(new SnakeCaseDualModifier());
        mapper.registerModule(module);
        return mapper;
    }

    public static void setObjectMapper(ObjectMapper m) {
        _mapper = m != null ? m : createDefaultMapper();
    }

    public static ObjectMapper getObjectMapper() {
        return _mapper;
    }

    public static Object deserializeObject(String json) {
        return convertToGeneric(json);
    }

    public static Object deserializeObject(String json, Object typeOrSettings) {
        if (typeOrSettings instanceof Class<?>) {
            try {
                return _mapper.readValue(json, (Class<?>) typeOrSettings);
            } catch (Exception e) {
                throw new RuntimeException("JSON deserialize error: " + e.getMessage(), e);
            }
        }
        return convertToGeneric(json);
    }

    public static Object deserializeObject(String json, Object type, Object settings) {
        if (type instanceof Class<?>) {
            try {
                return _mapper.readValue(json, (Class<?>) type);
            } catch (Exception e) {
                throw new RuntimeException("JSON deserialize error: " + e.getMessage(), e);
            }
        }
        return convertToGeneric(json);
    }

    public static Object deserializeList(String json, Class<?> elementClass) {
        if (json == null) {
            return null;
        }
        String t = json.trim();
        if (t.isEmpty() || t.equals("null")) {
            return null;
        }
        try {
            JavaType listType = _mapper.getTypeFactory()
                    .constructCollectionType(CSharpList.class, elementClass);
            return _mapper.readValue(t, listType);
        } catch (Exception e) {
            throw new RuntimeException("JSON deserialize list error: " + e.getMessage(), e);
        }
    }

    private static Object convertToGeneric(String json) {
        if (json == null) {
            return null;
        }
        String t = json.trim();
        if (t.isEmpty() || t.equals("null")) {
            return null;
        }
        try {
            JsonNode root = _mapper.readTree(t);
            return convertNode(root);
        } catch (Exception e) {
            throw new RuntimeException("JSON parse error: " + e.getMessage(), e);
        }
    }

    private static Object convertNode(JsonNode node) {
        if (node == null || node.isNull()) {
            return null;
        }
        if (node.isTextual()) {
            String s = node.asText();
            CSharpDateTime dt = tryParseDateTime(s);
            if (dt != null) {
                return dt;
            }
            return s;
        }
        if (node.isBoolean()) {
            return node.asBoolean();
        }
        if (node.isNumber()) {
            JsonParser.NumberType nt = node.numberType();
            if (nt == JsonParser.NumberType.INT) {
                return node.asInt();
            }
            if (nt == JsonParser.NumberType.LONG) {
                return node.asLong();
            }
            if (nt == JsonParser.NumberType.DOUBLE || nt == JsonParser.NumberType.FLOAT
                    || nt == JsonParser.NumberType.BIG_DECIMAL) {
                return node.asDouble();
            }
            if (nt == JsonParser.NumberType.BIG_INTEGER) {
                return node.asLong();
            }
            return node.numberValue();
        }
        if (node.isArray()) {
            int sz = node.size();
            CSharpList<Object> list = new CSharpList<>(sz);
            for (int i = 0; i < sz; i++) {
                list.add(convertNode(node.get(i)));
            }
            return list;
        }
        if (node.isObject()) {
            CSharpDictionary<String, Object> dict = new CSharpDictionary<>();
            Iterator<String> it = node.fieldNames();
            while (it.hasNext()) {
                String key = it.next();
                dict.put(key, convertNode(node.get(key)));
            }
            return dict;
        }
        return node.asText();
    }

    private static CSharpDateTime tryParseDateTime(String s) {
        if (s == null || s.length() < 10) {
            return null;
        }
        if (!Character.isDigit(s.charAt(0)) || s.charAt(4) != '-') {
            return null;
        }
        ObjectHolder<CSharpDateTime> holder = new ObjectHolder<>();
        if (CSharpDateTime.tryParse(s, holder)) {
            return holder.value;
        }
        return null;
    }

    public static String serializeObject(Object value) {
        return serializeObjectInternal(value);
    }

    public static String serializeObject(Object value, Object settings) {
        return serializeObjectInternal(value);
    }

    private static String serializeObjectInternal(Object value) {
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
        if (value instanceof CSharpDateTime) {
            return "\"" + value.toString() + "\"";
        }
        return "\"" + value.toString() + "\"";
    }

    private static String camelToSnake(String s) {
        if (s == null || s.isEmpty()) {
            return s;
        }
        StringBuilder sb = new StringBuilder();
        for (int i = 0; i < s.length(); i++) {
            char c = s.charAt(i);
            if (Character.isUpperCase(c)) {
                if (i > 0) {
                    sb.append('_');
                }
                sb.append(Character.toLowerCase(c));
            } else {
                sb.append(c);
            }
        }
        return sb.toString();
    }

    /**
     * For each bean property, add a SECOND property binding for the camelCase
     * form of the name. The SNAKE_CASE naming strategy already provides the
     * snake_case binding; this modifier adds the camelCase binding so that
     * JSON files using either naming convention are deserialized correctly.
     */
    static class SnakeCaseDualModifier extends BeanDeserializerModifier {
        @Override
        public BeanDeserializerBuilder updateBuilder(DeserializationConfig config,
                BeanDescription beanDesc, BeanDeserializerBuilder builder) {
            // builder.getProperties() returns Iterator<SettableBeanProperty> in
            // Jackson 2.x. Collect them so we can iterate twice.
            final List<com.fasterxml.jackson.databind.deser.SettableBeanProperty> orig =
                    new ArrayList<>();
            for (java.util.Iterator<com.fasterxml.jackson.databind.deser.SettableBeanProperty> it =
                         builder.getProperties(); it.hasNext(); ) {
                orig.add(it.next());
            }
            for (com.fasterxml.jackson.databind.deser.SettableBeanProperty prop : orig) {
                String snakeName = prop.getName(); // JSON name after strategy (snake_case)
                if (snakeName == null) {
                    continue;
                }
                // Derive Java field name from the underlying member.
                String javaName = null;
                if (prop.getMember() != null) {
                    String memberName = prop.getMember().getName();
                    if (memberName != null) {
                        // For getter/setter methods, strip the prefix
                        if ((memberName.startsWith("get") || memberName.startsWith("set"))
                                && memberName.length() > 3) {
                            javaName = Character.toLowerCase(memberName.charAt(3)) + memberName.substring(4);
                        } else {
                            javaName = memberName; // Field name
                        }
                    }
                }
                if (javaName == null || javaName.equals(snakeName)) {
                    continue;
                }
                // Check if the camelCase JSON name is already in the builder
                boolean alreadyHas = false;
                for (com.fasterxml.jackson.databind.deser.SettableBeanProperty p : orig) {
                    if (javaName.equals(p.getName())) {
                        alreadyHas = true;
                        break;
                    }
                }
                if (!alreadyHas) {
                    builder.addOrReplaceProperty(prop.withSimpleName(javaName), false);
                }
            }
            return builder;
        }
    }
}

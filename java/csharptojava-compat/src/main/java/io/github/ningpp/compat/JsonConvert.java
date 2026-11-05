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
import java.lang.reflect.Array;
import java.util.ArrayList;
import java.util.HashSet;
import java.util.Iterator;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.Set;

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

    /**
     * Re-types a generic structure that a generic JSON helper answered for
     * {@code JsonConvert.DeserializeObject<T>}. The helper's {@code (T)} cast is
     * erased in Java (a generic method cannot name {@code T}), so it returns the
     * generic {@code CSharpDictionary}/{@code CSharpList<Object>} shape; the
     * caller knows the closed type and converts it through Jackson here.
     *
     * @param value the generic value returned by the helper
     * @param rawClass the closed raw class (CSharpList for List-family targets)
     * @param elementClasses optional element class for one-level collections
     */
    public static Object convertToType(Object value, Class<?> rawClass, Class<?>... elementClasses) {
        if (value == null || rawClass == null) {
            return value;
        }
        try {
            JavaType target = elementClasses == null || elementClasses.length == 0
                    ? _mapper.getTypeFactory().constructType(rawClass)
                    : _mapper.getTypeFactory().constructCollectionType(
                            rawClass.asSubclass(java.util.Collection.class), elementClasses[0]);
            return _mapper.convertValue(toPlainValue(value), target);
        } catch (Exception e) {
            throw new RuntimeException("JSON retype error: " + e.getMessage(), e);
        }
    }

    /**
     * Rewrites compat containers into plain JDK containers before Jackson gets to
     * see them.
     *
     * <p>Passing a {@link CSharpDictionary} straight to {@code convertValue} made
     * Jackson serialize it as a bean; {@link CSharpGenericIDictionary#getSyncRoot()}
     * returns {@code this}, so the bean property {@code sync_root} closed a
     * self-reference and every retype failed with
     * "Direct self-reference leading to cycle". Plain {@code LinkedHashMap}/{@code
     * ArrayList} have no such bean properties and keep the JSON member order.</p>
     */
    private static Object toPlainValue(Object value) {
        if (value == null) {
            return null;
        }
        if (value instanceof CSharpGenericIDictionary<?, ?> dict) {
            LinkedHashMap<String, Object> out = new LinkedHashMap<>();
            for (CSharpKeyValuePair<?, ?> pair : dict) {
                out.put(String.valueOf(pair.getKey()), toPlainValue(pair.getValue()));
            }
            return out;
        }
        if (value instanceof Map<?, ?> map) {
            LinkedHashMap<String, Object> out = new LinkedHashMap<>();
            for (Map.Entry<?, ?> entry : map.entrySet()) {
                out.put(String.valueOf(entry.getKey()), toPlainValue(entry.getValue()));
            }
            return out;
        }
        if (value instanceof CSharpKeyValuePair<?, ?> pair) {
            LinkedHashMap<String, Object> out = new LinkedHashMap<>();
            out.put("Key", toPlainValue(pair.getKey()));
            out.put("Value", toPlainValue(pair.getValue()));
            return out;
        }
        if (value.getClass().isArray()) {
            int length = Array.getLength(value);
            ArrayList<Object> out = new ArrayList<>(length);
            for (int i = 0; i < length; i++) {
                out.add(toPlainValue(Array.get(value, i)));
            }
            return out;
        }
        if (value instanceof Iterable<?> iterable) {
            ArrayList<Object> out = new ArrayList<>();
            for (Object element : iterable) {
                out.add(toPlainValue(element));
            }
            return out;
        }
        return value;
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
     * For each bean property, add extra bindings for every naming convention the
     * JSON test data uses. The SNAKE_CASE naming strategy already provides the
     * snake_case binding; this modifier also registers the member's own spelling
     * and its lowerCamelCase form.
     *
     * <p>The lowerCamel alias is what makes the C#-shaped PascalCase fields (for
     * example {@code public int NodeCount;}) accept a {@code nodeCount} key: the
     * member name is {@code NodeCount}, so the existing alias only covered
     * {@code NodeCount} and {@code node_count}, leaving {@code NodeCount} at its
     * default {@code 0}.</p>
     */
    static class SnakeCaseDualModifier extends BeanDeserializerModifier {
        @Override
        public BeanDeserializerBuilder updateBuilder(DeserializationConfig config,
                BeanDescription beanDesc, BeanDeserializerBuilder builder) {
            // builder.getProperties() returns Iterator<SettableBeanProperty> in
            // Jackson 2.x. Collect them so we can iterate twice.
            final List<com.fasterxml.jackson.databind.deser.SettableBeanProperty> orig =
                    new ArrayList<>();
            final Set<String> known = new HashSet<>();
            for (java.util.Iterator<com.fasterxml.jackson.databind.deser.SettableBeanProperty> it =
                         builder.getProperties(); it.hasNext(); ) {
                com.fasterxml.jackson.databind.deser.SettableBeanProperty prop = it.next();
                orig.add(prop);
                known.add(prop.getName());
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
                            javaName = memberName; // Field name (may be PascalCase)
                        }
                    }
                }
                if (javaName == null) {
                    continue;
                }
                addAlias(builder, prop, snakeName, javaName, known);
                addAlias(builder, prop, snakeName, lowerCamelCase(javaName), known);
            }
            return builder;
        }

        private static void addAlias(BeanDeserializerBuilder builder,
                com.fasterxml.jackson.databind.deser.SettableBeanProperty prop,
                String canonicalName, String alias, Set<String> known) {
            if (alias == null || alias.isEmpty() || alias.equals(canonicalName)
                    || known.contains(alias)) {
                return;
            }
            builder.addOrReplaceProperty(prop.withSimpleName(alias), false);
            known.add(alias);
        }

        /**
         * Same mangling Jackson's LOWER_CAMEL_CASE strategy applies: lower-case the
         * leading run of upper-case characters ({@code NodeCount} → {@code nodeCount},
         * {@code URL} → {@code url}).
         */
        private static String lowerCamelCase(String name) {
            if (name == null || name.isEmpty()) {
                return name;
            }
            char c0 = name.charAt(0);
            char c1 = Character.toLowerCase(c0);
            if (c0 == c1) {
                return name;
            }
            StringBuilder sb = new StringBuilder(name.length());
            sb.append(c1);
            for (int i = 1; i < name.length(); i++) {
                char c = name.charAt(i);
                char lower = Character.toLowerCase(c);
                if (c == lower) {
                    sb.append(name, i, name.length());
                    break;
                }
                sb.append(lower);
            }
            return sb.toString();
        }
    }
}

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
import java.util.Map;

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
        return serializeObjectInternal(value, null);
    }

    public static String serializeObject(Object value, Object settings) {
        return serializeObjectInternal(value, settings instanceof JsonSerializerSettings s ? s : null);
    }

    /**
     * Newtonsoft.Json parity serializer. ReferenceHandlerTests uses this as its
     * parity oracle: serializeObject(value, preserveSettings) must produce the
     * same JSON as the serializer under test, i.e. real reference metadata
     * ($id/$ref/$values with ids in discovery order) under
     * PreserveReferencesHandling.All — not a quoted toString() of the value.
     */
    private static String serializeObjectInternal(Object value, JsonSerializerSettings settings) {
        if (value == null) {
            return "null";
        }
        if (value instanceof Boolean || value instanceof Number) {
            return value.toString();
        }
        if (isScalar(value)) {
            return quoteString(value.toString());
        }
        StringBuilder sb = new StringBuilder();
        writeValue(sb, value, new WriteSession(settings));
        return sb.toString();
    }

    /**
     * Values that are never reference-tracked and always serialize as JSON
     * strings: strings, chars, dates and enums (Newtonsoft serializes enums as
     * their name by default).
     */
    private static boolean isScalar(Object value) {
        return value instanceof String
                || value instanceof Character
                || value instanceof Enum
                || value instanceof CSharpDateTime;
    }

    private static String quoteString(String s) {
        StringBuilder sb = new StringBuilder(s.length() + 2);
        appendQuoted(sb, s);
        return sb.toString();
    }

    private static void appendQuoted(StringBuilder sb, String s) {
        sb.append('"');
        escapeString(sb, s);
        sb.append('"');
    }

    /** Newtonsoft Default string escaping: quote, backslash and control characters. */
    private static void escapeString(StringBuilder sb, String s) {
        for (int i = 0; i < s.length(); i++) {
            char c = s.charAt(i);
            switch (c) {
                case '"':
                    sb.append("\\\"");
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
                    if (c < 0x20) {
                        sb.append(String.format("\\u%04x", (int) c));
                    } else {
                        sb.append(c);
                    }
                    break;
            }
        }
    }

    /** Per-serialize state: reference tracking plus the settings in effect. */
    private static final class WriteSession {
        private final JsonSerializerSettings settings;
        private final PreserveReferencesHandling preserveReferences;
        private final ReferenceLoopHandling referenceLoop;
        private final java.util.IdentityHashMap<Object, String> referenceIds = new java.util.IdentityHashMap<>();
        private final java.util.IdentityHashMap<Object, Boolean> onStack = new java.util.IdentityHashMap<>();
        private int nextId;

        WriteSession(JsonSerializerSettings settings) {
            this.settings = settings;
            this.preserveReferences = settings != null
                    ? settings.preserveReferencesHandling
                    : PreserveReferencesHandling.None;
            this.referenceLoop = settings != null
                    ? settings.referenceLoopHandling
                    : ReferenceLoopHandling.Error;
        }

        boolean tracks(Object value) {
            switch (preserveReferences) {
                case All:
                    return true;
                case Objects:
                    return !(value instanceof Iterable) && !(value instanceof Map)
                            && !value.getClass().isArray();
                case Arrays:
                    return value instanceof Iterable || value instanceof Map
                            || value.getClass().isArray();
                default:
                    return false;
            }
        }

        /** Already serialized once: write a $ref instead of recursing. */
        String seenId(Object value) {
            return referenceIds.get(value);
        }

        String assignId(Object value) {
            nextId++;
            String id = Integer.toString(nextId);
            referenceIds.put(value, id);
            return id;
        }

        /** Whether {@code value} is an ancestor of the value currently being written. */
        boolean isOnStack(Object value) {
            return onStack.containsKey(value);
        }

        void enter(Object value) {
            onStack.put(value, Boolean.TRUE);
        }

        void exit(Object value) {
            onStack.remove(value);
        }

        void writeReference(StringBuilder sb, String id) {
            sb.append('{');
            appendQuoted(sb, "$ref");
            sb.append(':');
            appendQuoted(sb, id);
            sb.append('}');
        }
    }

    private static void writeValue(StringBuilder sb, Object value, WriteSession session) {
        if (value == null) {
            sb.append("null");
            return;
        }
        if (value instanceof Boolean || value instanceof Number) {
            sb.append(value);
            return;
        }
        if (isScalar(value)) {
            appendQuoted(sb, value.toString());
            return;
        }

        if (session.tracks(value)) {
            String seen = session.seenId(value);
            if (seen != null) {
                session.writeReference(sb, seen);
                return;
            }
        } else if (session.isOnStack(value)) {
            // Without reference metadata a cycle cannot be serialized.
            if (session.referenceLoop == ReferenceLoopHandling.Ignore) {
                sb.append("null");
                return;
            }
            if (session.referenceLoop != ReferenceLoopHandling.Serialize) {
                throw new RuntimeException("Self referencing loop detected");
            }
        }

        String id = session.tracks(value) ? session.assignId(value) : null;

        if (value instanceof Map) {
            writeDictionary(sb, (Map<?, ?>) value, id, session);
            return;
        }
        if (value instanceof CSharpGenericIDictionary) {
            writeDictionary(sb, (CSharpGenericIDictionary<?, ?>) value, id, session);
            return;
        }
        if (isContainer(value)) {
            writeArray(sb, value, id, session);
            return;
        }
        writeObject(sb, value, id, session);
    }

    private static boolean isContainer(Object value) {
        return value instanceof Iterable || value.getClass().isArray();
    }

    private static void writeDictionary(StringBuilder sb, Map<?, ?> map, String id, WriteSession session) {
        session.enter(map);
        writeDictionaryEntries(sb, map.entrySet()::iterator, id, session, map);
        session.exit(map);
    }

    /**
     * CSharpDictionary and friends are not java.util.Map — they are generic
     * C#-shaped dictionaries iterating CSharpKeyValuePair entries. Write them
     * as a JSON object (key order = insertion order), not a $values array.
     */
    private static void writeDictionary(StringBuilder sb, CSharpGenericIDictionary<?, ?> dictionary,
            String id, WriteSession session) {
        session.enter(dictionary);
        writeDictionaryEntries(sb, dictionary::iterator, id, session, dictionary);
        session.exit(dictionary);
    }

    private interface EntrySource {
        Iterator<?> iterator();
    }

    private static void writeDictionaryEntries(StringBuilder sb, EntrySource source, String id,
            WriteSession session, Object owner) {
        sb.append('{');
        boolean first = true;
        if (id != null) {
            sb.append("\"$id\":\"").append(id).append('"');
            first = false;
        }
        Iterator<?> entries = source.iterator();
        while (entries.hasNext()) {
            Object entry = entries.next();
            Object key;
            Object entryValue;
            if (entry instanceof Map.Entry<?, ?> mapEntry) {
                key = mapEntry.getKey();
                entryValue = mapEntry.getValue();
            } else if (entry instanceof CSharpKeyValuePair<?, ?> pair) {
                key = pair.getKey();
                entryValue = pair.getValue();
            } else {
                continue;
            }
            if (!first) {
                sb.append(',');
            }
            first = false;
            appendQuoted(sb, key == null ? "" : String.valueOf(key));
            sb.append(':');
            writeValue(sb, entryValue, session);
        }
        sb.append('}');
    }

    private static void writeArray(StringBuilder sb, Object value, String id, WriteSession session) {
        session.enter(value);
        sb.append('{');
        boolean first = true;
        if (id != null) {
            sb.append("\"$id\":\"").append(id).append('"');
            first = false;
        }
        if (!first) {
            sb.append(',');
        }
        appendQuoted(sb, "$values");
        sb.append(":[");
        first = true;
        if (value instanceof Iterable) {
            for (Object item : (Iterable<?>) value) {
                if (!first) {
                    sb.append(',');
                }
                first = false;
                writeValue(sb, item, session);
            }
        } else {
            int length = java.lang.reflect.Array.getLength(value);
            for (int i = 0; i < length; i++) {
                if (!first) {
                    sb.append(',');
                }
                first = false;
                writeValue(sb, java.lang.reflect.Array.get(value, i), session);
            }
        }
        sb.append("]}");
        session.exit(value);
    }

    /** Getters for declared non-static fields, in declaration order — the C# public property shape. */
    private static List<java.lang.reflect.Method> readableGetters(Class<?> type) {
        List<java.lang.reflect.Method> getters = new ArrayList<>();
        for (Class<?> c = type; c != null && c != Object.class; c = c.getSuperclass()) {
            for (java.lang.reflect.Field field : c.getDeclaredFields()) {
                int modifiers = field.getModifiers();
                if (java.lang.reflect.Modifier.isStatic(modifiers) || field.isSynthetic()) {
                    continue;
                }
                String fieldName = field.getName();
                if (fieldName.isEmpty()) {
                    continue;
                }
                String capitalized = Character.toUpperCase(fieldName.charAt(0)) + fieldName.substring(1);
                java.lang.reflect.Method getter = findGetter(type, "get" + capitalized);
                if (getter == null) {
                    getter = findGetter(type, "is" + capitalized);
                }
                if (getter != null) {
                    getters.add(getter);
                }
                // No getter for the field: not a serializable property.
            }
        }
        return getters;
    }

    private static java.lang.reflect.Method findGetter(Class<?> type, String name) {
        try {
            java.lang.reflect.Method getter = type.getMethod(name);
            if (getter.getParameterCount() == 0
                    && !java.lang.reflect.Modifier.isStatic(getter.getModifiers())) {
                getter.setAccessible(true);
                return getter;
            }
        } catch (NoSuchMethodException e) {
            // Fall through.
        }
        return null;
    }

    private static void writeObject(StringBuilder sb, Object value, String id, WriteSession session) {
        session.enter(value);
        sb.append('{');
        boolean first = true;
        if (id != null) {
            sb.append("\"$id\":\"").append(id).append('"');
            first = false;
        }
        for (java.lang.reflect.Method getter : readableGetters(value.getClass())) {
            if (!first) {
                sb.append(',');
            }
            first = false;
            appendQuoted(sb, propertyName(getter));
            sb.append(':');
            Object propertyValue;
            try {
                propertyValue = getter.invoke(value);
            } catch (Exception e) {
                throw new RuntimeException("JSON serialize error: " + e.getMessage(), e);
            }
            writeValue(sb, propertyValue, session);
        }
        sb.append('}');
        session.exit(value);
    }

    /** Property name from the getter, honouring @CompatPropertyName like PropertyInfo does. */
    private static String propertyName(java.lang.reflect.Method getter) {
        CompatPropertyName compatName = getter.getAnnotation(CompatPropertyName.class);
        if (compatName != null) {
            return compatName.value();
        }
        String name = getter.getName();
        if (name.startsWith("get") && name.length() > 3) {
            return name.substring(3);
        }
        if (name.startsWith("is") && name.length() > 2) {
            return name.substring(2);
        }
        return name;
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

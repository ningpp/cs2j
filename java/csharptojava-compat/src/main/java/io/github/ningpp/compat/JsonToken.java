package io.github.ningpp.compat;

/**
 * Compat for Newtonsoft.Json.JsonToken.
 */
public enum JsonToken {
    None(0),
    StartObject(1),
    StartArray(2),
    StartConstructor(3),
    PropertyName(4),
    Comment(5),
    Raw(6),
    Integer(7),
    Float(8),
    String(9),
    Boolean(10),
    Null(11),
    Undefined(12),
    EndObject(13),
    EndArray(14),
    EndConstructor(15),
    Date(16),
    Bytes(17);

    private final int value;

    JsonToken(int value) {
        this.value = value;
    }

    public int getValue() {
        return value;
    }

    public static JsonToken fromValue(int value) {
        for (JsonToken token : values()) {
            if (token.value == value) {
                return token;
            }
        }
        return None;
    }
}

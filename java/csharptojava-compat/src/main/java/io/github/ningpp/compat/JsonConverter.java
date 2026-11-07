package io.github.ningpp.compat;

/**
 * Marker for Newtonsoft.Json.JsonConverter / JsonConverter&lt;T&gt;. Converter
 * output only needs the erased type token (e.g. CSharpArray.empty(...)), the
 * serialization work is carried by the compat JToken/JsonConvert surface.
 */
public interface JsonConverter<T> {
}

package io.github.ningpp.compat;

import java.lang.annotation.ElementType;
import java.lang.annotation.Retention;
import java.lang.annotation.RetentionPolicy;
import java.lang.annotation.Target;

/**
 * Marks a type that carried C# {@code [JsonNumberHandling(flags)]}, carrying the flags so the
 * generated {@code JsonNumberHandlingAttribute} can be materialized from it.
 *
 * <p>System.Text.Json attributes are generated as classes, not annotations, so
 * {@code TypeHelper.getCustomAttributes(type, attributeType, inherit)} cannot see them. The
 * converter emits this compat annotation instead and the compat lookup constructs the
 * attribute reflectively - the same route that made {@code [JsonIgnore]} and
 * {@code [JsonExtensionData]} work.</p>
 */
@Retention(RetentionPolicy.RUNTIME)
@Target({ElementType.TYPE, ElementType.METHOD, ElementType.FIELD})
public @interface CompatJsonNumberHandling {
    /** C# {@code JsonNumberHandling} flags value (Strict = 0, AllowReadingFromString = 1, ...). */
    int value();
}

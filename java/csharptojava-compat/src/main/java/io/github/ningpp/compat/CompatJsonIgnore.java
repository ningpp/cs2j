package io.github.ningpp.compat;

import java.lang.annotation.ElementType;
import java.lang.annotation.Retention;
import java.lang.annotation.RetentionPolicy;
import java.lang.annotation.Target;

/**
 * Marks a member that carried C# {@code [JsonIgnore]}.
 *
 * <p>System.Text.Json attributes are generated as classes, not annotations, so
 * {@code CustomAttributeExtensions.getCustomAttribute} cannot see them. The converter emits
 * this compat annotation instead and the compat lookup materializes the generated attribute
 * class reflectively - the same route that made {@code [JsonExtensionData]} work.</p>
 */
@Retention(RetentionPolicy.RUNTIME)
@Target({ElementType.METHOD, ElementType.FIELD})
public @interface CompatJsonIgnore {
    /**
     * C# {@code JsonIgnoreAttribute.Condition}. The generated attribute class cannot carry it —
     * the converter drops the object initializer, so it always answers its default of
     * {@code Always}. A bare {@code [JsonIgnore]} is {@code Always} (= 1), the default here;
     * without the value a {@code [JsonIgnore(Condition = Never)]} member stayed "always ignore"
     * and was dropped from the output (four tests measured {@code {}} where
     * {@code {"MyString":"Hello"}} was expected).
     */
    int value() default 1;
}

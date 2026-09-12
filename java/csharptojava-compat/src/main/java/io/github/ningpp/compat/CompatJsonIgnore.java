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
}

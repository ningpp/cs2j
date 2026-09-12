package io.github.ningpp.compat;

import java.lang.annotation.ElementType;
import java.lang.annotation.Retention;
import java.lang.annotation.RetentionPolicy;
import java.lang.annotation.Target;

/**
 * Marks a member that carried C# {@code [JsonInclude]}.
 *
 * <p>System.Text.Json attributes are generated as classes, not annotations, so
 * {@code JsonPropertyInfo.GetAttribute&lt;JsonIncludeAttribute&gt;} cannot see them. The converter
 * emits this compat annotation instead and the compat lookup materializes the generated
 * attribute class from it - the same route that made {@code [JsonIgnore]} and
 * {@code [JsonExtensionData]} work.</p>
 *
 * <p>Unlike those two this one is not only about serialization behaviour: the SDK validates
 * that a member annotated with {@code [JsonInclude]} is public, and throws
 * {@code InvalidOperationException} otherwise
 * ({@code JsonClassInfo.ThrowInvalidOperationException_JsonIncludeOnNonPublicInvalid}). Without
 * the marker the check reads "no attribute" and the invalid member is silently skipped instead
 * of failing.</p>
 */
@Retention(RetentionPolicy.RUNTIME)
@Target({ElementType.METHOD, ElementType.FIELD})
public @interface CompatJsonInclude {
}

package io.github.ningpp.compat;

import java.lang.annotation.ElementType;
import java.lang.annotation.Retention;
import java.lang.annotation.RetentionPolicy;
import java.lang.annotation.Target;

/**
 * Carries the converter type a C# member or type named with {@code [JsonConverter]}, including when
 * the source wrote a DERIVED attribute class.
 *
 * <p>System.Text.Json attributes are generated as classes, not annotations, so
 * {@code CustomAttributeExtensions.getCustomAttribute} cannot see them and the converter emits this
 * compat annotation instead (the same route as {@code @CompatJsonIgnore} /
 * {@code @CompatJsonPropertyName}). The converter resolves the converter type by walking the C#
 * attribute's inheritance chain, so
 * {@code class InvalidConverterAttribute : JsonConverterAttribute { ... : base(typeof(int)) }}
 * is recorded as {@code typeof(int)} - the base constructor argument - not as the derived class.</p>
 *
 * <p>Without it the attribute never reaches the runtime, so the "does not derive from JsonConverter
 * or have a public parameterless constructor" and "is not compatible with the type" validations in
 * {@code JsonSerializerOptions.getConverterFromAttribute} never run. Measured: every
 * {@code CustomConverterTests} negative test asserting {@code InvalidOperationException} on a bad
 * {@code [JsonConverter]} failed with "No exception thrown".</p>
 */
@Retention(RetentionPolicy.RUNTIME)
@Target({ElementType.METHOD, ElementType.FIELD, ElementType.TYPE})
public @interface CompatJsonConverter {
    /** The C# converter type the attribute names, resolved through its inheritance chain. */
    Class<?> value();
}
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
    /**
     * The C# converter type the attribute names, resolved through its inheritance chain.
     *
     * <p>{@code Void.class} is the sentinel for C# {@code [JsonConverter(null)]} - an attribute that
     * is PRESENT but names no converter type, which is how a derived attribute takes over creation
     * through {@code createConverter}. A defaulted annotation value cannot be null, and dropping the
     * marker entirely would make the attribute indistinguishable from an absent one, so the null
     * case needs its own spelling. Measured: CustomConverterTests.PocoWithNullConverter.MyInt
     * carries {@code [NullConverter]} ({@code : base(null)}) and the serialize that must throw
     * InvalidOperationException did not.</p>
     */
    Class<?> value() default Void.class;
}
package io.github.ningpp.compat;

import java.lang.annotation.ElementType;
import java.lang.annotation.Retention;
import java.lang.annotation.RetentionPolicy;
import java.lang.annotation.Target;

/**
 * Carries the name a member had in C# {@code [JsonPropertyName("...")]}.
 *
 * <p>System.Text.Json attributes are generated as classes, not annotations, so
 * {@code CustomAttributeExtensions.getCustomAttribute} cannot see them and the converter emits this
 * compat annotation instead (the same route as {@code @CompatJsonIgnore} /
 * {@code @CompatJsonInclude}). Unlike those, this attribute has a VALUE, which the lookup reads to
 * build the generated {@code JsonPropertyNameAttribute}.</p>
 *
 * <p>Without it the C# name never reaches the runtime at all: measured on
 * {@code PropertyVisibilityTests.ClassWithPropertyNamingConflictWhichThrows}, whose
 * {@code ConflictingString} property is declared {@code [JsonPropertyName("MyString")]} — the
 * generated Java carried no annotation, so the property stayed "conflictingString", no name
 * collision was detected, and the twelve tests that assert
 * {@code InvalidOperationException} on such a collision all failed ("No exception thrown").</p>
 */
@Retention(RetentionPolicy.RUNTIME)
@Target({ElementType.METHOD, ElementType.FIELD})
public @interface CompatJsonPropertyName {
    /** The JSON property name from the C# attribute. */
    String value();
}

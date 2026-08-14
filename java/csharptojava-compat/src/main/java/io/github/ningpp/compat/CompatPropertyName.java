package io.github.ningpp.compat;

import java.lang.annotation.ElementType;
import java.lang.annotation.Retention;
import java.lang.annotation.RetentionPolicy;
import java.lang.annotation.Target;

/**
 * Records the C# property name on a Java getter/setter whose method name had to
 * be disambiguated (e.g. a C# `new` property hiding a base property of an
 * incompatible type is emitted as getX_newSlot/setX_newSlot because Java
 * forbids same-name methods with unrelated return types). PropertyInfo reads
 * this annotation so the property keeps its original name for reflection and
 * serialization metadata.
 */
@Retention(RetentionPolicy.RUNTIME)
@Target({ElementType.METHOD})
public @interface CompatPropertyName {
    String value();
}

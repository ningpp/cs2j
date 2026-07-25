package io.github.ningpp.compat.xml;

import java.lang.annotation.ElementType;
import java.lang.annotation.Retention;
import java.lang.annotation.RetentionPolicy;
import java.lang.annotation.Target;

/**
 * Marks a field or property that should be ignored during XML serialization.
 * Mirrors System.Xml.Serialization.XmlIgnoreAttribute.
 */
@Retention(RetentionPolicy.RUNTIME)
@Target({ElementType.FIELD, ElementType.METHOD})
public @interface XmlIgnore {
}

package io.github.ningpp.compat.xml;

import java.lang.annotation.ElementType;
import java.lang.annotation.Retention;
import java.lang.annotation.RetentionPolicy;
import java.lang.annotation.Target;

/**
 * Marks a field or property that should be serialized as an XML attribute.
 * Mirrors System.Xml.Serialization.XmlAttributeAttribute.
 */
@Retention(RetentionPolicy.RUNTIME)
@Target({ElementType.FIELD, ElementType.METHOD})
public @interface XmlAttribute {
    String name() default "";
}

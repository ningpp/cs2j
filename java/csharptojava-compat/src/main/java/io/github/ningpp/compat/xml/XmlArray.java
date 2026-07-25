package io.github.ningpp.compat.xml;

import java.lang.annotation.ElementType;
import java.lang.annotation.Retention;
import java.lang.annotation.RetentionPolicy;
import java.lang.annotation.Target;

/**
 * Specifies the wrapper element name for an array property.
 * Mirrors System.Xml.Serialization.XmlArrayAttribute.
 */
@Retention(RetentionPolicy.RUNTIME)
@Target({ElementType.FIELD, ElementType.METHOD})
public @interface XmlArray {
    String elementName() default "";
}

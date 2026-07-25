package io.github.ningpp.compat.xml;

import java.lang.annotation.ElementType;
import java.lang.annotation.Retention;
import java.lang.annotation.RetentionPolicy;
import java.lang.annotation.Target;

/**
 * Specifies the element name for items inside an XML array.
 * Mirrors System.Xml.Serialization.XmlArrayItemAttribute.
 */
@Retention(RetentionPolicy.RUNTIME)
@Target({ElementType.FIELD, ElementType.METHOD})
public @interface XmlArrayItem {
    String elementName() default "";
}

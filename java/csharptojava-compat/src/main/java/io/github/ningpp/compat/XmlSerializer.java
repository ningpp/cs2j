package io.github.ningpp.compat;

import io.github.ningpp.compat.xml.XmlArray;
import io.github.ningpp.compat.xml.XmlArrayItem;
import io.github.ningpp.compat.xml.XmlAttribute;
import io.github.ningpp.compat.xml.XmlIgnore;

import java.beans.IntrospectionException;
import java.beans.Introspector;
import java.beans.PropertyDescriptor;
import java.io.StringReader;
import java.io.StringWriter;
import java.lang.reflect.Array;
import java.lang.reflect.Field;
import java.lang.reflect.Method;
import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;

import javax.xml.parsers.DocumentBuilder;
import javax.xml.parsers.DocumentBuilderFactory;
import javax.xml.stream.XMLOutputFactory;
import javax.xml.stream.XMLStreamWriter;

import org.w3c.dom.Document;
import org.w3c.dom.Element;
import org.w3c.dom.NamedNodeMap;
import org.w3c.dom.Node;
import org.w3c.dom.NodeList;

/**
 * Compatibility replacement for System.Xml.Serialization.XmlSerializer.
 * Supports simple object graphs, arrays, and the XML serialization annotations
 * in the io.github.ningpp.compat.xml package.
 */
public class XmlSerializer {

    private final Class<?> type;

    public XmlSerializer(Class<?> type) {
        this.type = type;
    }

    /**
     * Serializes the given object to the supplied CSharpTextWriter.
     */
    public void serialize(CSharpTextWriter writer, Object value) {
        if (value == null) {
            return;
        }
        try {
            StringWriter buffer = new StringWriter();
            XMLStreamWriter xml = XMLOutputFactory.newInstance().createXMLStreamWriter(buffer);
            xml.writeStartDocument("UTF-8", "1.0");
            serializeObject(xml, value, rootName(value.getClass()));
            xml.writeEndDocument();
            xml.flush();
            xml.close();
            writer.write(buffer.toString());
        } catch (Exception e) {
            throw new RuntimeException(e);
        }
    }

    /**
     * Deserializes an object of the configured type from the supplied TextReader.
     */
    public Object deserialize(TextReader reader) {
        try {
            String xml = reader.readToEnd();
            DocumentBuilderFactory factory = DocumentBuilderFactory.newInstance();
            factory.setNamespaceAware(false);
            DocumentBuilder builder = factory.newDocumentBuilder();
            Document doc = builder.parse(new org.xml.sax.InputSource(new StringReader(xml)));
            Element root = doc.getDocumentElement();
            return deserializeObject(root, type);
        } catch (Exception e) {
            throw new RuntimeException(e);
        }
    }

    private void serializeObject(XMLStreamWriter xml, Object value, String elementName) throws Exception {
        xml.writeStartElement(elementName);

        Map<String, PropertyMeta> properties = getProperties(value.getClass());
        for (Map.Entry<String, PropertyMeta> entry : properties.entrySet()) {
            PropertyMeta meta = entry.getValue();
            if (meta.isIgnored) {
                continue;
            }

            Object propValue = meta.read(value);
            if (propValue == null) {
                continue;
            }

            if (meta.isAttribute) {
                xml.writeAttribute(meta.elementName, String.valueOf(propValue));
                continue;
            }

            if (propValue.getClass().isArray()) {
                xml.writeStartElement(meta.elementName);
                int length = Array.getLength(propValue);
                for (int i = 0; i < length; i++) {
                    Object item = Array.get(propValue, i);
                    if (item != null) {
                        serializeSimpleValue(xml, item, meta.arrayItemName);
                    }
                }
                xml.writeEndElement();
            } else {
                serializeSimpleValue(xml, propValue, meta.elementName);
            }
        }

        xml.writeEndElement();
    }

    private void serializeSimpleValue(XMLStreamWriter xml, Object value, String elementName) throws Exception {
        xml.writeStartElement(elementName);
        xml.writeCharacters(String.valueOf(value));
        xml.writeEndElement();
    }

    private Object deserializeObject(Element element, Class<?> targetType) throws Exception {
        Object instance = targetType.getDeclaredConstructor().newInstance();
        Map<String, PropertyMeta> properties = getProperties(targetType);

        // Handle attributes
        NamedNodeMap attributes = element.getAttributes();
        for (int i = 0; i < attributes.getLength(); i++) {
            Node attr = attributes.item(i);
            String attrName = attr.getNodeName();
            for (PropertyMeta meta : properties.values()) {
                if (meta.isAttribute && meta.elementName.equals(attrName)) {
                    meta.write(instance, convertValue(attr.getNodeValue(), meta.type));
                    break;
                }
            }
        }

        // Handle child elements
        NodeList children = element.getChildNodes();
        for (int i = 0; i < children.getLength(); i++) {
            Node child = children.item(i);
            if (child.getNodeType() != Node.ELEMENT_NODE) {
                continue;
            }
            Element childElement = (Element) child;
            String childName = childElement.getNodeName();

            for (PropertyMeta meta : properties.values()) {
                if (meta.isIgnored || meta.isAttribute) {
                    continue;
                }
                if (!meta.elementName.equals(childName)) {
                    continue;
                }
                if (meta.type.isArray()) {
                    Class<?> componentType = meta.type.getComponentType();
                    List<Object> items = new ArrayList<>();
                    NodeList itemNodes = childElement.getChildNodes();
                    for (int j = 0; j < itemNodes.getLength(); j++) {
                        Node itemNode = itemNodes.item(j);
                        if (itemNode.getNodeType() != Node.ELEMENT_NODE) {
                            continue;
                        }
                        Element itemElement = (Element) itemNode;
                        if (itemElement.getNodeName().equals(meta.arrayItemName)) {
                            items.add(deserializeSimpleValue(itemElement, componentType));
                        }
                    }
                    Object array = Array.newInstance(componentType, items.size());
                    for (int j = 0; j < items.size(); j++) {
                        Array.set(array, j, items.get(j));
                    }
                    meta.write(instance, array);
                } else {
                    meta.write(instance, deserializeSimpleValue(childElement, meta.type));
                }
                break;
            }
        }

        return instance;
    }

    private Object deserializeSimpleValue(Element element, Class<?> targetType) {
        String text = element.getTextContent();
        return convertValue(text, targetType);
    }

    private Object convertValue(String value, Class<?> targetType) {
        if (targetType == String.class) {
            return value;
        }
        if (targetType == int.class || targetType == Integer.class) {
            return Integer.parseInt(value);
        }
        if (targetType == boolean.class || targetType == Boolean.class) {
            return Boolean.parseBoolean(value);
        }
        if (targetType == long.class || targetType == Long.class) {
            return Long.parseLong(value);
        }
        if (targetType == double.class || targetType == Double.class) {
            return Double.parseDouble(value);
        }
        if (targetType == float.class || targetType == Float.class) {
            return Float.parseFloat(value);
        }
        if (targetType == short.class || targetType == Short.class) {
            return Short.parseShort(value);
        }
        if (targetType == byte.class || targetType == Byte.class) {
            return Byte.parseByte(value);
        }
        if (targetType == char.class || targetType == Character.class) {
            return value.isEmpty() ? '\0' : value.charAt(0);
        }
        return value;
    }

    private Map<String, PropertyMeta> getProperties(Class<?> clazz) {
        Map<String, PropertyMeta> result = new LinkedHashMap<>();
        Map<String, Field> fields = new LinkedHashMap<>();
        for (Class<?> current = clazz; current != null && current != Object.class; current = current.getSuperclass()) {
            for (Field field : current.getDeclaredFields()) {
                if (!fields.containsKey(field.getName())) {
                    fields.put(field.getName(), field);
                }
            }
        }

        try {
            for (PropertyDescriptor pd : Introspector.getBeanInfo(clazz).getPropertyDescriptors()) {
                String name = pd.getName();
                if ("class".equals(name)) {
                    continue;
                }
                Method read = pd.getReadMethod();
                Method write = pd.getWriteMethod();
                if (read == null) {
                    continue;
                }

                Field field = fields.get(name);
                PropertyMeta meta = new PropertyMeta();
                meta.name = name;
                meta.type = pd.getPropertyType();
                meta.readMethod = read;
                meta.writeMethod = write;

                XmlAttribute xmlAttr = getAnnotation(read, write, field, XmlAttribute.class);
                XmlIgnore xmlIgnore = getAnnotation(read, write, field, XmlIgnore.class);
                XmlArray xmlArray = getAnnotation(read, write, field, XmlArray.class);
                XmlArrayItem xmlArrayItem = getAnnotation(read, write, field, XmlArrayItem.class);

                if (xmlIgnore != null) {
                    meta.isIgnored = true;
                }
                if (xmlAttr != null) {
                    meta.isAttribute = true;
                    meta.elementName = xmlAttr.name().isEmpty() ? name : xmlAttr.name();
                } else {
                    meta.elementName = xmlArray != null && !xmlArray.elementName().isEmpty()
                            ? xmlArray.elementName()
                            : name;
                    meta.arrayItemName = xmlArrayItem != null && !xmlArrayItem.elementName().isEmpty()
                            ? xmlArrayItem.elementName()
                            : "Item";
                }

                result.put(name, meta);
            }
        } catch (IntrospectionException e) {
            throw new RuntimeException(e);
        }

        return result;
    }

    private <A extends java.lang.annotation.Annotation> A getAnnotation(Method read, Method write, Field field, Class<A> annotationType) {
        A a = read.getAnnotation(annotationType);
        if (a != null) return a;
        if (write != null) {
            a = write.getAnnotation(annotationType);
            if (a != null) return a;
        }
        if (field != null) {
            a = field.getAnnotation(annotationType);
            if (a != null) return a;
        }
        return null;
    }

    private String rootName(Class<?> clazz) {
        return clazz.getSimpleName();
    }

    private static class PropertyMeta {
        String name;
        Class<?> type;
        Method readMethod;
        Method writeMethod;
        boolean isAttribute;
        boolean isIgnored;
        String elementName;
        String arrayItemName = "Item";

        Object read(Object target) {
            try {
                return readMethod.invoke(target);
            } catch (Exception e) {
                throw new RuntimeException(e);
            }
        }

        void write(Object target, Object value) {
            if (writeMethod == null) {
                return;
            }
            try {
                writeMethod.invoke(target, value);
            } catch (Exception e) {
                throw new RuntimeException(e);
            }
        }
    }
}

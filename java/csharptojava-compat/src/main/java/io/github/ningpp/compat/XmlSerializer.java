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
import java.lang.reflect.Modifier;
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

        // C# 公共字段：转换产物保留为 Java public 字段（属性则落 C11 访问器方法对）。
        // XmlSerializer 在 .NET 里同样序列化公共字段，这里必须直接按字段枚举，
        // 否则字段形态的 POCO 序列化结果为空（真实链 serializer-tests 4 例）。
        for (Field field : fields.values()) {
            int mod = field.getModifiers();
            if (Modifier.isStatic(mod) || field.isSynthetic() || !Modifier.isPublic(mod)) {
                continue;
            }
            PropertyMeta meta = new PropertyMeta();
            meta.name = field.getName();
            meta.type = field.getType();
            meta.field = field;
            // 同名 C11 访问器（get_X/set_X → X()/X(v)）优先，保留封装形态的读写语义。
            Method getter = findAccessorGetter(clazz, field.getName());
            if (getter != null) {
                meta.readMethod = getter;
            }
            Method setter = findAccessorSetter(clazz, field.getName(), field.getType());
            if (setter != null) {
                meta.writeMethod = setter;
            }
            applyXmlAnnotations(meta, meta.readMethod, meta.writeMethod, field);
            result.put(meta.name, meta);
        }

        try {
            for (PropertyDescriptor pd : Introspector.getBeanInfo(clazz).getPropertyDescriptors()) {
                String name = pd.getName();
                if ("class".equals(name)) {
                    continue;
                }
                if (result.containsKey(name)) {
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
                meta.field = field;
                applyXmlAnnotations(meta, read, write, field);

                result.put(name, meta);
            }
        } catch (IntrospectionException e) {
            throw new RuntimeException(e);
        }

        return result;
    }

    /** Maps [XmlAttribute]/[XmlIgnore]/[XmlArray]/[XmlArrayItem] onto one property. */
    private void applyXmlAnnotations(PropertyMeta meta, Method read, Method write, Field field) {
        XmlAttribute xmlAttr = getAnnotation(read, write, field, XmlAttribute.class);
        XmlIgnore xmlIgnore = getAnnotation(read, write, field, XmlIgnore.class);
        XmlArray xmlArray = getAnnotation(read, write, field, XmlArray.class);
        XmlArrayItem xmlArrayItem = getAnnotation(read, write, field, XmlArrayItem.class);

        if (xmlIgnore != null) {
            meta.isIgnored = true;
        }
        if (xmlAttr != null) {
            meta.isAttribute = true;
            meta.elementName = xmlAttr.name().isEmpty() ? meta.name : xmlAttr.name();
        } else {
            meta.elementName = xmlArray != null && !xmlArray.elementName().isEmpty()
                    ? xmlArray.elementName()
                    : meta.name;
            meta.arrayItemName = xmlArrayItem != null && !xmlArrayItem.elementName().isEmpty()
                    ? xmlArrayItem.elementName()
                    : "Item";
        }
    }

    /**
     * The C11 getter half of a converted C# property ({@code get_X} → accessor method
     * {@code X()}) for a backing field, or null. Converted auto-properties keep the
     * field name in camelCase, so the accessor name is the capitalized field name.
     */
    private static Method findAccessorGetter(Class<?> clazz, String fieldName) {
        String getter = capitalize(fieldName);
        for (Method m : clazz.getMethods()) {
            if (Modifier.isStatic(m.getModifiers()) || m.getParameterCount() != 0) continue;
            if (m.getReturnType() == void.class || !m.getName().equals(getter)) continue;
            return m;
        }
        return null;
    }

    private static Method findAccessorSetter(Class<?> clazz, String fieldName, Class<?> type) {
        String setter = capitalize(fieldName);
        for (Method m : clazz.getMethods()) {
            if (Modifier.isStatic(m.getModifiers()) || m.getParameterCount() != 1) continue;
            if (m.getReturnType() != void.class || !m.getName().equals(setter)) continue;
            if (m.getParameterTypes()[0].isAssignableFrom(type)
                || isBoxCompatible(m.getParameterTypes()[0], type)) {
                return m;
            }
        }
        return null;
    }

    private static String capitalize(String name) {
        return name.isEmpty() ? name : Character.toUpperCase(name.charAt(0)) + name.substring(1);
    }

    private static boolean isBoxCompatible(Class<?> parameterType, Class<?> valueType) {
        if (parameterType.isAssignableFrom(valueType)) return true;
        return (parameterType == Integer.class && (valueType == int.class || valueType == Integer.class))
            || (parameterType == Long.class && (valueType == long.class || valueType == Long.class))
            || (parameterType == Short.class && (valueType == short.class || valueType == Short.class))
            || (parameterType == Byte.class && (valueType == byte.class || valueType == Byte.class))
            || (parameterType == Character.class && (valueType == char.class || valueType == Character.class))
            || (parameterType == Boolean.class && (valueType == boolean.class || valueType == Boolean.class))
            || (parameterType == Double.class && (valueType == double.class || valueType == Double.class))
            || (parameterType == Float.class && (valueType == float.class || valueType == Float.class));
    }

    private <A extends java.lang.annotation.Annotation> A getAnnotation(Method read, Method write, Field field, Class<A> annotationType) {
        A a = read != null ? read.getAnnotation(annotationType) : null;
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
        /** Backing/public field of a converted C# field-or-property (null for bean-only shapes). */
        Field field;
        boolean isAttribute;
        boolean isIgnored;
        String elementName;
        String arrayItemName = "Item";

        Object read(Object target) {
            try {
                if (readMethod != null) {
                    return readMethod.invoke(target);
                }
                return field.get(target);
            } catch (Exception e) {
                throw new RuntimeException(e);
            }
        }

        void write(Object target, Object value) {
            if (writeMethod == null && field == null) {
                return;
            }
            try {
                if (writeMethod != null) {
                    writeMethod.invoke(target, value);
                } else {
                    field.set(target, value);
                }
            } catch (Exception e) {
                throw new RuntimeException(e);
            }
        }
    }
}

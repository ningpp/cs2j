package io.github.ningpp.compat;

import io.github.ningpp.compat.xml.XmlArray;
import io.github.ningpp.compat.xml.XmlArrayItem;
import io.github.ningpp.compat.xml.XmlAttribute;
import io.github.ningpp.compat.xml.XmlIgnore;
import org.junit.jupiter.api.Test;

import java.io.StringReader;
import java.io.StringWriter;

import static org.junit.jupiter.api.Assertions.*;

/**
 * Tests for the compat XmlSerializer implementation.
 */
class XmlSerializerTest {

    public static class Person {
        private String name;
        private int age;

        public String getName() { return name; }
        public void setName(String name) { this.name = name; }
        public int getAge() { return age; }
        public void setAge(int age) { this.age = age; }
    }

    public static class AttributedItem {
        @XmlAttribute(name = "value")
        private String value;

        public String getValue() { return value; }
        public void setValue(String value) { this.value = value; }
    }

    public static class WithIgnoredProperty {
        private String included;
        @XmlIgnore
        private String ignored;

        public String getIncluded() { return included; }
        public void setIncluded(String included) { this.included = included; }
        public String getIgnored() { return ignored; }
        public void setIgnored(String ignored) { this.ignored = ignored; }
    }

    public static class ItemList {
        @XmlArray(elementName = "Items")
        @XmlArrayItem(elementName = "Item")
        private String[] items;

        public String[] getItems() { return items; }
        public void setItems(String[] items) { this.items = items; }
    }

    @Test
    void serializeAndDeserialize_simpleClass() {
        Person person = new Person();
        person.setName("Alice");
        person.setAge(30);

        String xml = serialize(Person.class, person);
        assertTrue(xml.contains("Alice"));
        assertTrue(xml.contains("30"));

        Person restored = deserialize(Person.class, xml);
        assertEquals("Alice", restored.getName());
        assertEquals(30, restored.getAge());
    }

    @Test
    void serialize_usesXmlAttribute() {
        AttributedItem item = new AttributedItem();
        item.setValue("v1");

        String xml = serialize(AttributedItem.class, item);
        assertTrue(xml.contains("value=\"v1\""), "Expected attribute serialization but got: " + xml);
    }

    @Test
    void serialize_ignoresXmlIgnoreProperty() {
        WithIgnoredProperty item = new WithIgnoredProperty();
        item.setIncluded("yes");
        item.setIgnored("ThisValueMustNotAppear");

        String xml = serialize(WithIgnoredProperty.class, item);
        assertTrue(xml.contains("yes"));
        assertFalse(xml.contains("ThisValueMustNotAppear"), "Expected ignored property to be omitted but got: " + xml);
    }

    @Test
    void serializeAndDeserialize_array() {
        ItemList list = new ItemList();
        list.setItems(new String[] { "a", "b", "c" });

        String xml = serialize(ItemList.class, list);
        assertTrue(xml.contains("<Items>"));
        assertTrue(xml.contains("<Item>a</Item>"));

        ItemList restored = deserialize(ItemList.class, xml);
        assertArrayEquals(new String[] { "a", "b", "c" }, restored.getItems());
    }

    private <T> String serialize(Class<T> type, T value) {
        XmlSerializer serializer = new XmlSerializer(type);
        StringWriter writer = new StringWriter();
        serializer.serialize(new CSharpTextWriter(writer), value);
        return writer.toString();
    }

    private <T> T deserialize(Class<T> type, String xml) {
        XmlSerializer serializer = new XmlSerializer(type);
        Object result = serializer.deserialize(new TextReader(new StringReader(xml)));
        return type.cast(result);
    }
}

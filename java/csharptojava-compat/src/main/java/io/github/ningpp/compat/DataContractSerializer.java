package io.github.ningpp.compat;

import java.io.StringReader;
import java.io.StringWriter;

/**
 * Minimal replacement for System.Runtime.Serialization.DataContractSerializer.
 * Provides readObject / writeObject signatures that match converted C# code patterns.
 * The actual serialization is a stub — real implementations should use a proper
 * XML serialization framework (e.g. Jackson XML or JAXB).
 */
public class DataContractSerializer {

    private final Class<?> type;

    public DataContractSerializer(Class<?> type) {
        this.type = type;
    }

    /**
     * Deserializes an object from the given XmlReader.
     * Stub implementation — reads element content as string and returns it.
     */
    public Object readObject(XmlReader reader, boolean verifyObjectName) {
        // Stub: return the inner text as the deserialized object
        String content = XmlReader.readElementContentAsString();
        return content;
    }

    /**
     * Serializes an object to the given XmlWriter.
     * Stub implementation — writes the object's toString() as a string element.
     */
    public void writeObject(XmlWriter writer, Object graph) {
        if (graph != null) {
            XmlWriter.writeString(graph.toString());
        }
    }

    /**
     * Serializes an object to the given XmlWriter with a specified root element name.
     */
    public void writeObject(XmlWriter writer, Object graph, String rootName) {
        if (rootName != null) {
            XmlWriter.writeStartElement(rootName);
        }
        writeObject(writer, graph);
        if (rootName != null) {
            XmlWriter.writeEndElement();
        }
    }
}

package io.github.ningpp.compat;

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

    public Object readObject(Object reader, boolean verifyObjectName) {
        return null;
    }

    public void writeObject(Object writer, Object graph) {
    }

}

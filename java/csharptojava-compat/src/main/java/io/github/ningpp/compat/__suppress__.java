package io.github.ningpp.compat;

/**
 * Placeholder type emitted by the converter for C# types that have no Java
 * counterpart (e.g. System.Runtime.Serialization.StreamingContext and
 * System.Runtime.Serialization.ISerializable). Kept deliberately minimal so
 * that generated code compiles; operations on it fail with
 * PlatformNotSupportedException, matching the behavior of the .NET surface
 * it stands in for.
 *
 * <p>It doubles as the converter's stand-in for
 * {@code System.Runtime.Serialization.SerializationInfo}: generated
 * {@code OnDeserialization}/{@code GetObjectData} members call
 * {@code getInt32}/{@code getValue}/{@code addValue} on it.</p>
 */
public final class __suppress__ {

    public __suppress__() {
    }

    /**
     * Mirrors the SerializationInfo(Type, IFormatterConverter) constructor.
     */
    public __suppress__(Class<?> type, Object converter) {
    }

    /**
     * Mirrors the SerializationInfo(Type, IFormatterConverter) constructor for
     * call sites whose converter argument is itself a compat placeholder.
     */
    public __suppress__(Class<?> type, __suppress__ converter) {
    }

    /**
     * Mirrors ISerializable.GetObjectData(SerializationInfo, StreamingContext).
     */
    public void getObjectData(Object info, __suppress__ context) {
        throw new PlatformNotSupportedException();
    }

    /**
     * Mirrors SerializationInfo.GetInt32(string).
     */
    public int getInt32(String name) {
        throw new PlatformNotSupportedException();
    }

    /**
     * Mirrors SerializationInfo.GetInt64(string).
     */
    public long getInt64(String name) {
        throw new PlatformNotSupportedException();
    }

    /**
     * Mirrors SerializationInfo.GetString(string).
     */
    public String getString(String name) {
        throw new PlatformNotSupportedException();
    }

    /**
     * Mirrors SerializationInfo.GetBoolean(string).
     */
    public boolean getBoolean(String name) {
        throw new PlatformNotSupportedException();
    }

    /**
     * Mirrors SerializationInfo.GetValue(string, Type).
     *
     * @param <T> The type of the value to read.
     * @param name The name of the value to read.
     * @param type The type of the value to read.
     * @return Nothing; always throws.
     */
    public <T> T getValue(String name, Class<T> type) {
        throw new PlatformNotSupportedException();
    }

    /**
     * Mirrors SerializationInfo.GetValue(string, Type) for array types such as
     * {@code KeyValuePair<K,V>[]}, whose {@code Class} literal is not statically
     * typed as {@code Class<T[]>}.
     */
    public Object getValue(String name, Object type) {
        throw new PlatformNotSupportedException();
    }

    /**
     * Mirrors SerializationInfo.AddValue(string, object).
     */
    public void addValue(String name, Object value) {
        throw new PlatformNotSupportedException();
    }

    /**
     * Mirrors SerializationInfo.AddValue(string, object, Type).
     */
    public void addValue(String name, Object value, Class<?> type) {
        throw new PlatformNotSupportedException();
    }

    /**
     * Mirrors SerializationInfo.SetType(Type).
     */
    public void setType(Class<?> type) {
        throw new PlatformNotSupportedException();
    }

    /**
     * Mirrors SerializationInfo.FullTypeName.
     */
    public String getFullTypeName() {
        throw new PlatformNotSupportedException();
    }

    /**
     * Mirrors SerializationInfo.AssemblyName.
     */
    public String getAssemblyName() {
        throw new PlatformNotSupportedException();
    }
}

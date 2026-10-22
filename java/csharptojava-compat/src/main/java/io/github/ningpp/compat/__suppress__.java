package io.github.ningpp.compat;

/**
 * Stand-in for serialization-only BCL types that map to the {@code __suppress__}
 * sentinel in TypeMappings.json (System.Runtime.Serialization's ISerializable /
 * SerializationInfo / StreamingContext). Members whose signatures reference
 * these types are dropped during conversion, but generated code may still
 * mention the sentinel in casts ({@code ((ISerializable)r).GetObjectData(...)}),
 * so it must be an interface: javac rejects casts from a class to an unrelated
 * class, while class-to-interface casts always compile.
 *
 * <p>Every operation throws {@link PlatformNotSupportedException}, matching
 * the behavior of the .NET surfaces it stands in for.</p>
 */
public interface __suppress__ {

    /**
     * Mirrors ISerializable.GetObjectData(SerializationInfo, StreamingContext):
     * binary serialization is not supported on this platform.
     */
    default void getObjectData(__suppress__ info, __suppress__ context) {
        throw new PlatformNotSupportedException();
    }

    /** Mirrors ISerializable.GetObjectData(SerializationInfo, StreamingContext). */
    default void getObjectData(Object info, __suppress__ context) {
        throw new PlatformNotSupportedException();
    }

    /** Mirrors SerializationInfo.GetInt32(string). */
    default int getInt32(String name) {
        throw new PlatformNotSupportedException();
    }

    /** Mirrors SerializationInfo.GetInt64(string). */
    default long getInt64(String name) {
        throw new PlatformNotSupportedException();
    }

    /** Mirrors SerializationInfo.GetString(string). */
    default String getString(String name) {
        throw new PlatformNotSupportedException();
    }

    /** Mirrors SerializationInfo.GetBoolean(string). */
    default boolean getBoolean(String name) {
        throw new PlatformNotSupportedException();
    }

    /** Mirrors SerializationInfo.GetValue(string, Type). */
    default <T> T getValue(String name, Class<T> type) {
        throw new PlatformNotSupportedException();
    }

    /** Mirrors SerializationInfo.GetValue(string, Type) for untyped Class literals. */
    default Object getValue(String name, Object type) {
        throw new PlatformNotSupportedException();
    }

    /** Mirrors SerializationInfo.AddValue(string, object). */
    default void addValue(String name, Object value) {
        throw new PlatformNotSupportedException();
    }

    /** Mirrors SerializationInfo.AddValue(string, object, Type). */
    default void addValue(String name, Object value, Class<?> type) {
        throw new PlatformNotSupportedException();
    }

    /** Mirrors SerializationInfo.SetType(Type). */
    default void setType(Class<?> type) {
        throw new PlatformNotSupportedException();
    }

    /** Mirrors SerializationInfo.FullTypeName. */
    default String getFullTypeName() {
        throw new PlatformNotSupportedException();
    }

    /** Mirrors SerializationInfo.AssemblyName. */
    default String getAssemblyName() {
        throw new PlatformNotSupportedException();
    }
}

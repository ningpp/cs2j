package io.github.ningpp.compat;

/**
 * Placeholder type emitted by the converter for C# types that have no Java
 * counterpart (e.g. System.Runtime.Serialization.StreamingContext and
 * System.Runtime.Serialization.ISerializable). Kept deliberately minimal so
 * that generated code compiles; operations on it fail with
 * PlatformNotSupportedException, matching the behavior of the .NET surface
 * it stands in for.
 */
public final class __suppress__ {

    public __suppress__() {
    }

    /**
     * Mirrors ISerializable.GetObjectData(SerializationInfo, StreamingContext).
     */
    public void getObjectData(Object info, __suppress__ context) {
        throw new PlatformNotSupportedException();
    }
}
